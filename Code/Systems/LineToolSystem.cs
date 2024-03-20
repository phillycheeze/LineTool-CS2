﻿// <copyright file="LineToolSystem.cs" company="algernon (K. Algernon A. Sheppard)">
// Copyright (c) algernon (K. Algernon A. Sheppard). All rights reserved.
// Licensed under the Apache Licence, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// See LICENSE.txt file in the project root for full license information.
// </copyright>

namespace LineTool
{
    using System;
    using System.Reflection;
    using Colossal.Entities;
    using Colossal.Logging;
    using Colossal.Mathematics;
    using Colossal.Serialization.Entities;
    using Game;
    using Game.Areas;
    using Game.Buildings;
    using Game.City;
    using Game.Common;
    using Game.Input;
    using Game.Net;
    using Game.Objects;
    using Game.Prefabs;
    using Game.Rendering;
    using Game.Simulation;
    using Game.Tools;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Mathematics;
    using UnityEngine.InputSystem;
    using static Game.Rendering.GuideLinesSystem;
    using Random = Unity.Mathematics.Random;
    using Transform = Game.Objects.Transform;
    using Tree = Game.Objects.Tree;

    /// <summary>
    /// Line tool system.
    /// </summary>
    public sealed partial class LineToolSystem : ObjectToolBaseSystem
    {
        // Native buffers.
        private NativeList<TooltipInfo> _tooltips;
        private NativeList<PointData> _points;

        // Line calculations.
        private bool _fixedPreview = false;
        private float3 _fixedPos;
        private Random _random = new ();

        // Cursor.
        private ControlPoint _raycastPoint;
        private float3 _previousPos;
        private Entity _cursorEntity = Entity.Null;

        // Prefab selection.
        private ToolBaseSystem _previousTool = null;
        private ObjectGeometryPrefab _selectedPrefab;
        private Entity _selectedEntity = Entity.Null;
        private int _originalXP;
        private Bounds1 _zBounds;

        // References.
        private ILog _log;
        private TerrainSystem _terrainSystem;
        private TerrainHeightData _terrainHeightData;
        private OverlayRenderSystem.Buffer _overlayBuffer;
        private CityConfigurationSystem _cityConfigurationSystem;

        // Input actions.
        private ProxyAction _applyAction;
        private ProxyAction _cancelAction;
        private InputAction _fixedPreviewAction;
        private InputAction _keepBuildingAction;

        // Mode.
        private LineBase _mode;
        private LineMode _currentMode = LineMode.Point;
        private DragMode _dragMode = DragMode.None;

        // Tool settings.
        private SpacingMode _spacingMode = SpacingMode.Manual;
        private float _spacing = 20f;
        private bool _randomRotation = false;
        private int _rotation = 0;
        private float _randomSpacing = 0f;
        private float _randomOffset = 0f;
        private bool _dirty = false;

        // Tree Controller integration.
        private ToolBaseSystem _treeControllerTool;
        private PropertyInfo _nextTreeState = null;

        /// <summary>
        /// Point dragging mode.
        /// </summary>
        internal enum DragMode
        {
            /// <summary>
            /// No dragging.
            /// </summary>
            None = 0,

            /// <summary>
            /// Dragging the line's start position.
            /// </summary>
            StartPos,

            /// <summary>
            /// Dragging the line's end position.
            /// </summary>
            EndPos,

            /// <summary>
            /// Dragging the line's elbow position.
            /// </summary>
            ElbowPos,
        }

        /// <summary>
        /// Gets the tool's ID string.
        /// </summary>
        public override string toolID => "Line Tool";

        /// <summary>
        /// Gets or sets the effective line spacing.
        /// </summary>
        internal float Spacing
        {
            get => _spacing;

            set
            {
                // Don't allow spacing to be set smaller than the smallest side of zBounds.
                _spacing = (float)Math.Round(math.max(value, math.max(math.abs(_zBounds.max), math.abs(_zBounds.min) + 0.1f)), 1);
                _dirty = true;
            }
        }

        /// <summary>
        /// Gets the effective spacing value, taking into account fence mode.
        /// </summary>
        internal float EffectiveSpacing => _spacingMode == SpacingMode.FenceMode ? _zBounds.max - _zBounds.min : _spacing;

        /// <summary>
        /// Gets or sets the current spacing mode.
        /// </summary>
        internal SpacingMode CurrentSpacingMode
        {
            get => _spacingMode;
            set
            {
                _spacingMode = value;
                _dirty = true;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether random rotation is active.
        /// </summary>
        internal bool RandomRotation
        {
            get => _randomRotation;
            set
            {
                _randomRotation = value;
                _dirty = true;
            }
        }

        /// <summary>
        /// Gets or sets the random spacing offset maximum.
        /// </summary>
        internal float RandomSpacing
        {
            get => _randomSpacing;
            set
            {
                _randomSpacing = value;
                _dirty = true;
            }
        }

        /// <summary>
        /// Gets or sets the random lateral offset maximum.
        /// </summary>
        internal float RandomOffset
        {
            get => _randomOffset;
            set
            {
                _randomOffset = value;
                _dirty = true;
            }
        }

        /// <summary>
        /// Gets or sets the rotation setting.
        /// </summary>
        internal int Rotation
        {
            get => _rotation;
            set
            {
                _rotation = value;

                // Bounds checks.
                if (_rotation >= 360)
                {
                    _rotation -= 360;
                }

                if (_rotation < 0)
                {
                    _rotation += 360;
                }

                _dirty = true;
            }
        }

        /// <summary>
        /// Gets the tooltip list.
        /// </summary>
        internal NativeList<TooltipInfo> Tooltips => _tooltips;

        /// <summary>
        /// Gets or sets the current line mode.
        /// </summary>
        internal LineMode Mode
        {
            get => _currentMode;

            set
            {
                // Don't do anything if no change.
                if (value == _currentMode)
                {
                    return;
                }

                // Apply updated tool mode.
                switch (value)
                {
                    case LineMode.Straight:
                        _mode = new StraightLine(_mode);
                        break;
                    case LineMode.SimpleCurve:
                        _mode = new SimpleCurve(_mode);
                        break;
                    case LineMode.Circle:
                        _mode = new Circle(_mode);
                        break;
                }

                // Update mode.
                _currentMode = value;
            }
        }

        /// <summary>
        /// Gets the currently selected entity.
        /// </summary>
        internal Entity SelectedEntity => _selectedEntity;

        /// <summary>
        /// Sets the currently selected prefab.
        /// </summary>
        private PrefabBase SelectedPrefab
        {
            set
            {
                _selectedPrefab = value as ObjectGeometryPrefab;

                // Update selected entity.
                if (_selectedPrefab is null)
                {
                    // No valid entity selected.
                    _selectedEntity = Entity.Null;
                }
                else
                {
                    // Get selected entity.
                    _selectedEntity = m_PrefabSystem.GetEntity(_selectedPrefab);

                    // Check bounds.
                    _zBounds.min = 0;
                    _zBounds.max = 0;
                    foreach (ObjectMeshInfo mesh in _selectedPrefab.m_Meshes)
                    {
                        if (mesh.m_Mesh is RenderPrefab renderPrefab)
                        {
                            // Update bounds if either of the z extents of this mesh exceed the previous extent.
                            _zBounds.min = math.min(_zBounds.min, renderPrefab.bounds.z.min);
                            _zBounds.max = math.max(_zBounds.max, renderPrefab.bounds.z.max);
                        }
                    }

                    // Reduce any XP to zero while we're using the tool.
                    SaveXP();
                }
            }
        }

        /// <summary>
        /// Called when the raycast is initialized.
        /// </summary>
        public override void InitializeRaycast()
        {
            base.InitializeRaycast();

            // Set raycast mask.
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain;
        }

        /// <summary>
        /// Gets the prefab selected by this tool.
        /// </summary>
        /// <returns>C<c>null</c>.</returns>
        public override PrefabBase GetPrefab() => _selectedPrefab;

        /// <summary>
        /// Sets the prefab selected by this tool.
        /// </summary>
        /// <param name="prefab">Prefab to set.</param>
        /// <returns><c>false</c>.</returns>
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        /// <summary>
        /// Elevation-up key handler; used to increment spacing.
        /// </summary>
        public override void ElevationUp() => Spacing = _spacing + 1;

        /// <summary>
        /// Elevation-down key handler; used to decrement spacing.
        /// </summary>
        public override void ElevationDown() => Spacing = _spacing - 1;

        /// <summary>
        /// Gets the snap mask for this tool.
        /// </summary>
        /// <param name="onMask">Snap on mask.</param>
        /// <param name="offMask">Snap off mask.</param>
        public override void GetAvailableSnapMask(out Snap onMask, out Snap offMask)
        {
            base.GetAvailableSnapMask(out onMask, out offMask);
            onMask |= Snap.ContourLines;
            offMask |= Snap.ContourLines;
        }

        /// <summary>
        /// Enables the tool (called by hotkey action).
        /// </summary>
        internal void EnableTool()
        {
            // Activate this tool if it isn't already active.
            if (m_ToolSystem.activeTool != this)
            {
                _previousTool = m_ToolSystem.activeTool;

                // Check for valid prefab selection before continuing.
                SelectedPrefab = World.GetOrCreateSystemManaged<ObjectToolSystem>().prefab;
                if (_selectedPrefab != null)
                {
                    // Valid prefab selected - switch to this tool.
                    m_ToolSystem.selected = Entity.Null;
                    m_ToolSystem.activeTool = this;
                }
            }
        }

        /// <summary>
        /// Restores the previously-used tool.
        /// </summary>
        internal void RestorePreviousTool()
        {
            if (_previousTool is not null)
            {
                m_ToolSystem.activeTool = _previousTool;
                _currentMode = LineMode.Point;
            }
            else
            {
                _log.Error("null tool set when restoring previous tool");
            }
        }

        /// <summary>
        /// Called when the system is created.
        /// </summary>
        protected override void OnCreate()
        {
            base.OnCreate();

            // Set log.
            _log = Mod.Instance.Log;

            // Get system references.
            _terrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            _overlayBuffer = World.GetOrCreateSystemManaged<OverlayRenderSystem>().GetBuffer(out var _);
            _cityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();

            // Create buffers.
            _tooltips = new (8, Allocator.Persistent);
            _points = new (Allocator.Persistent);

            // Set default mode.
            _mode = new StraightLine();

            // Set actions.
            _applyAction = InputManager.instance.FindAction("Tool", "Apply");
            _cancelAction = InputManager.instance.FindAction("Tool", "Mouse Cancel");

            // Enable fixed preview control.
            _fixedPreviewAction = new ("LineTool-FixPreview");
            _fixedPreviewAction.AddCompositeBinding("ButtonWithOneModifier").With("Modifier", "<Keyboard>/ctrl").With("Button", "<Mouse>/leftButton");
            _fixedPreviewAction.Enable();

            // Enable keep building action.
            _keepBuildingAction = new ("LineTool-KeepBuilding");
            _keepBuildingAction.AddCompositeBinding("ButtonWithOneModifier").With("Modifier", "<Keyboard>/shift").With("Button", "<Mouse>/leftButton");
            _keepBuildingAction.Enable();
        }

        /// <summary>
        /// Called by the game when loading is complete.
        /// </summary>
        /// <param name="purpose">Loading purpose.</param>
        /// <param name="mode">Current game mode.</param>
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Try to get tree controller tool.
            if (World.GetOrCreateSystemManaged<ToolSystem>().tools.Find(x => x.toolID.Equals("Tree Controller Tool")) is ToolBaseSystem treeControllerTool)
            {
                // Found it - attempt to reflect NextTreeState property getter.
                _log.Info("found tree controller");
                _nextTreeState = treeControllerTool.GetType().GetProperty("NextTreeState");
                if (_nextTreeState is not null)
                {
                    _treeControllerTool = treeControllerTool;
                    _log.Info("reflected NextTreeState");
                }
            }
            else
            {
                _log.Info("tree controller tool not found");
            }
        }

        /// <summary>
        /// Called every tool update.
        /// </summary>
        /// <param name="inputDeps">Input dependencies.</param>
        /// <returns>Job handle.</returns>
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            // Clear state.
            applyMode = ApplyMode.Clear;
            _tooltips.Clear();

            // Don't do anything if no selected prefab or we're in point mode.
            if (_selectedPrefab is null || _currentMode == LineMode.Point)
            {
                return inputDeps;
            }

            // Check for valid raycast.
            float3 position = _fixedPreview ? _fixedPos : _previousPos;
            if (GetRaycastResult(out _raycastPoint))
            {
                // Valid raycast - update position.
                position = _fixedPreview ? _fixedPos : _raycastPoint.m_HitPosition;

                // Calculate terrain height.
                _terrainHeightData = _terrainSystem.GetHeightData();
                position.y = TerrainUtils.SampleHeight(ref _terrainHeightData, position);

                // Handle any dragging.
                if (_dragMode != DragMode.None)
                {
                    if (_applyAction.WasReleasedThisFrame() || _fixedPreviewAction.WasReleasedThisFrame())
                    {
                        // Cancel dragging.
                        _dragMode = DragMode.None;
                    }
                    else
                    {
                        // Drag end point.
                        if (_dragMode == DragMode.EndPos)
                        {
                            position = _raycastPoint.m_HitPosition;
                            _fixedPos = position;
                        }
                        else
                        {
                            // Handle dragging for other points via line mode instance.
                            _mode.HandleDrag(_dragMode, _raycastPoint.m_HitPosition);
                        }

                        _dirty = true;
                    }
                }

                // Check for and perform any cancellation.
                if (_cancelAction.WasPressedThisFrame())
                {
                    // Reset current mode settings.
                    _mode.Reset();
                    _dragMode = DragMode.None;

                    return inputDeps;
                }

                // If no cancellation, handle any fixed preview action if we're ready to place.
                else if (_fixedPreviewAction.WasPressedThisFrame() && _mode.HasAllPoints)
                {
                    // Are we already in fixed preview mode?
                    if (_fixedPreview)
                    {
                        // Already in fixed preview mode - check for dragging hits.
                        _dragMode = _mode.CheckDragHit(_raycastPoint.m_HitPosition);
                        if (_dragMode != DragMode.None)
                        {
                            // If dragging, has started, then we're done here.
                            return inputDeps;
                        }
                    }
                    else
                    {
                        // Activate fixed preview mode and fix current position.
                        _fixedPreview = true;
                        _fixedPos = position;
                    }
                }

                // Handle apply action if no other actions.
                else if (_applyAction.WasPressedThisFrame() || _keepBuildingAction.WasPressedThisFrame())
                {
                    // Were we in fixed state?
                    if (_fixedPreview)
                    {
                        // Check for dragging hits.
                        _dragMode = _mode.CheckDragHit(_raycastPoint.m_HitPosition);
                        if (_dragMode != DragMode.None)
                        {
                            // If dragging, has started, then we're done here.
                            return inputDeps;
                        }

                        // Yes - cancel fixed preview.
                        _fixedPreview = false;
                    }

                    // Handle click.
                    if (_mode.HandleClick(position))
                    {
                        // We're placing items.
                        applyMode = ApplyMode.Apply;

                        // Perform post-placement.
                        _mode.ItemsPlaced(position);

                        // Reset tool mode if we're not building continuously.
                        if (!_keepBuildingAction.WasPressedThisFrame())
                        {
                            _mode.Reset();
                        }

                        return inputDeps;
                    }
                }

                // Update cursor entity if we haven't got an initial position set.
                if (!_mode.HasStart)
                {
                    // Don't update if the cursor hasn't moved.
                    if (position.x != _previousPos.x || position.z != _previousPos.z)
                    {
                        // Delete any existing cursor entity and create a new one.
                        if (_cursorEntity != Entity.Null)
                        {
                            EntityManager.AddComponent<Deleted>(_cursorEntity);
                        }

                        _cursorEntity = CreateEntity();

                        // Highlight cursor entity.
                        EntityManager.AddComponent<Highlighted>(_cursorEntity);

                        // Update cursor entity position.
                        EntityManager.SetComponentData(_cursorEntity, new Transform { m_Position = position, m_Rotation = GetEffectiveRotation(position) });
                        EntityManager.AddComponent<BatchesUpdated>(_cursorEntity);

                        // Ensure cursor entity tree state.
                        EnsureTreeState(_cursorEntity);

                        // Update previous position.
                        _previousPos = position;
                    }

                    return inputDeps;
                }
                else if (_cursorEntity != Entity.Null)
                {
                    // Cancel cursor entity.
                    EntityManager.AddComponent<Deleted>(_cursorEntity);
                    _cursorEntity = Entity.Null;
                }
            }
            else
            {
                // No valid raycast - hide cursor.
                if (_cursorEntity != Entity.Null)
                {
                    EntityManager.AddComponent<Deleted>(_cursorEntity);
                }
            }

            // Render any overlay.
            _mode.DrawOverlay(_overlayBuffer, _tooltips);

            // Overlay control points.
            if (_fixedPreview)
            {
                _mode.DrawPointOverlays(_overlayBuffer);
            }

            // Check for position change or update needed.
            if (!_dirty && position.x == _previousPos.x && position.z == _previousPos.y)
            {
                // No update needed.
                return inputDeps;
            }

            // Update stored position and clear dirty flag.
            _previousPos = position;
            _dirty = false;

            // If we got here we're (re)calculating points.
            _points.Clear();
            _mode.CalculatePoints(position, _spacingMode, EffectiveSpacing, RandomSpacing, RandomOffset, _rotation, _zBounds, _points, ref _terrainHeightData);


            // Step along length and place preview objects.
            foreach (PointData thisPoint in _points)
            {
                UnityEngine.Random.InitState((int)(thisPoint.Position.x + thisPoint.Position.y + thisPoint.Position.z));

                // Create transform component.
                Transform transformData = new ()
                {
                    m_Position = thisPoint.Position,
                    m_Rotation = _randomRotation ? GetEffectiveRotation(thisPoint.Position) : thisPoint.Rotation,
                };

                // Create entity.
                CreateDefinitions(
                    _selectedEntity,
                    thisPoint.Position,
                    _randomRotation ? GetEffectiveRotation(thisPoint.Position) : thisPoint.Rotation);
            }

            return inputDeps;
        }

        /// <summary>
        /// Called when the tool starts running.
        /// </summary>
        protected override void OnStartRunning()
        {
            _log.Info("OnStartRunning");
            base.OnStartRunning();

            // Ensure apply action is enabled.
            _applyAction.shouldBeEnabled = true;
            _cancelAction.shouldBeEnabled = true;

            // Clear any previous raycast result.
            _raycastPoint = default;

            // Reset any previously-stored starting position.
            _mode.Reset();

            // Clear any applications.
            applyMode = ApplyMode.Clear;
        }

        /// <summary>
        /// Called when the tool stops running.
        /// </summary>
        protected override void OnStopRunning()
        {
            _log.Info("OnStopRunning");

            // Clear tooltips.
            _tooltips.Clear();

            // Disable apply action.
            _applyAction.shouldBeEnabled = false;
            _cancelAction.shouldBeEnabled = false;

            // Cancel cursor entity.
            if (_cursorEntity != Entity.Null)
            {
                EntityManager.AddComponent<Deleted>(_cursorEntity);
                _cursorEntity = Entity.Null;
            }

            // Restore prefab XP.
            RestoreXP();

            // Reset state.
            _mode.Reset();

            base.OnStopRunning();
        }

        /// <summary>
        /// Called when the system is destroyed.
        /// </summary>
        protected override void OnDestroy()
        {
            // Dispose of unmanaged lists.
            _tooltips.Dispose();
            _points.Dispose();

            base.OnDestroy();
        }

        /// <summary>
        /// Creates a new copy of the currently selected entity.
        /// </summary>
        /// <returns>New entity.</returns>
        private Entity CreateEntity()
        {
            // Create new entity.
            ObjectData componentData = EntityManager.GetComponentData<ObjectData>(_selectedEntity);
            Entity newEntity = EntityManager.CreateEntity(componentData.m_Archetype);

            // Set prefab and transform.
            EntityManager.SetComponentData(newEntity, new PrefabRef(_selectedEntity));

            // Set tree growth to adult if this is a tree.
            if (EntityManager.HasComponent<Tree>(newEntity))
            {
                Tree treeData = new ()
                {
                    m_State = GetTreeState(),
                    m_Growth = 0,
                };

                EntityManager.SetComponentData(newEntity, treeData);
            }

            return newEntity;
        }

        /// <summary>
        /// Reduces XP gain for the current object to zero and records the original value.
        /// </summary>
        private void SaveXP()
        {
            // Reduce any XP to zero while we're using the tool.
            if (EntityManager.TryGetComponent(_selectedEntity, out PlaceableObjectData placeableData))
            {
                _originalXP = placeableData.m_XPReward;
                placeableData.m_XPReward = 0;
                EntityManager.SetComponentData(_selectedEntity, placeableData);
            }
            else
            {
                _originalXP = 0;
            }
        }

        /// <summary>
        /// Restores the selected prefab's original XP gain.
        /// </summary>
        private void RestoreXP()
        {
            // Restore original prefab XP, if we changed it.
            if (_originalXP != 0 && _selectedEntity != Entity.Null && EntityManager.TryGetComponent(_selectedEntity, out PlaceableObjectData placeableData))
            {
                placeableData.m_XPReward = _originalXP;
                EntityManager.SetComponentData(_selectedEntity, placeableData);
                _originalXP = 0;
            }
        }

        /// <summary>
        /// Gets the effective object rotation depending on current settings.
        /// </summary>
        /// <param name="position">Object position (to seed random number generator).</param>
        /// <returns>Effective rotation quaternion according to current settings.</returns>
        private quaternion GetEffectiveRotation(float3 position)
        {
            int rotation = _rotation;

            // Override fixed rotation with a random value if we're using random rotation.
            if (_randomRotation)
            {
                // Use position to init RNG.
                _random.InitState((uint)(math.abs(position.x) + math.abs(position.y) + math.abs(position.z)) * 10000);
                rotation = _random.NextInt(360);
            }

            // Generate return quaternion.
            return quaternion.Euler(0, math.radians(rotation), 0);
        }

        /// <summary>
        /// Ensures any previewed trees have the correct age group.
        /// This resolves an issue where previewed trees will have their age group reset if they ever get blocked while previewing.
        /// </summary>
        /// <param name="entity">Entity to check.</param>
        private void EnsureTreeState(Entity entity)
        {
            // Ensure any trees have been assigned the correct age.
            if (EntityManager.TryGetComponent<Tree>(entity, out Tree tree))
            {
                tree.m_State = GetTreeState();
                tree.m_Growth = 0;
                EntityManager.SetComponentData(entity, tree);
            }
        }

        /// <summary>
        /// Gets the tree state to apply to the next created tree.
        /// Uses Tree Controller to determine this if available, otherwise returns <see cref="TreeState.Adult"/>.
        /// </summary>
        /// <returns>Tree state to apply.</returns>
        private TreeState GetTreeState()
        {
            if (_treeControllerTool is null)
            {
                // Use this if Tree Controller is unavailable.
                return TreeState.Adult;
            }
            else
            {
                // Tree controller state.
                return (TreeState)_nextTreeState.GetValue(_treeControllerTool);
            }
        }

        /// <summary>
        /// Resets a tree to current tree state settings.
        /// </summary>
        /// <param name="entity">Tree entity.</param>
        private void ResetTreeState(Entity entity)
        {
            if (entity != Entity.Null)
            {
                if (EntityManager.TryGetComponent<Tree>(entity, out Tree tree))
                {
                    tree.m_State = GetTreeState();
                    EntityManager.SetComponentData(entity, tree);
                }
            }
        }

        /// <summary>
        /// Creates temporary object definitions for previewing.
        /// </summary>
        /// <param name="objectPrefab">Object prefab entity.</param>
        /// <param name="position">Entity position.</param>
        /// <param name="rotation">Entity rotation.</param>
        private void CreateDefinitions(Entity objectPrefab, float3 position, quaternion rotation)
        {
            CreateDefinitions definitions = default;
            definitions.m_EditorMode = m_ToolSystem.actionMode.IsEditor();
            definitions.m_LefthandTraffic = _cityConfigurationSystem.leftHandTraffic;
            definitions.m_ObjectPrefab = objectPrefab;
            definitions.m_Theme = _cityConfigurationSystem.defaultTheme;
            definitions.m_RandomSeed = RandomSeed.Next();
            definitions.m_ControlPoint = new () { m_Position = position, m_Rotation = rotation };
            definitions.m_AttachmentPrefab = default;
            definitions.m_OwnerData = SystemAPI.GetComponentLookup<Owner>(true);
            definitions.m_TransformData = SystemAPI.GetComponentLookup<Transform>(true);
            definitions.m_AttachedData = SystemAPI.GetComponentLookup<Attached>(true);
            definitions.m_LocalTransformCacheData = SystemAPI.GetComponentLookup<LocalTransformCache>(true);
            definitions.m_ElevationData = SystemAPI.GetComponentLookup<Game.Objects.Elevation>(true);
            definitions.m_BuildingData = SystemAPI.GetComponentLookup<Building>(true);
            definitions.m_LotData = SystemAPI.GetComponentLookup<Game.Buildings.Lot>(true);
            definitions.m_EdgeData = SystemAPI.GetComponentLookup<Edge>(true);
            definitions.m_NodeData = SystemAPI.GetComponentLookup<Game.Net.Node>(true);
            definitions.m_CurveData = SystemAPI.GetComponentLookup<Curve>(true);
            definitions.m_NetElevationData = SystemAPI.GetComponentLookup<Game.Net.Elevation>(true);
            definitions.m_OrphanData = SystemAPI.GetComponentLookup<Orphan>(true);
            definitions.m_UpgradedData = SystemAPI.GetComponentLookup<Upgraded>(true);
            definitions.m_CompositionData = SystemAPI.GetComponentLookup<Composition>(true);
            definitions.m_AreaClearData = SystemAPI.GetComponentLookup<Clear>(true);
            definitions.m_AreaSpaceData = SystemAPI.GetComponentLookup<Space>(true);
            definitions.m_AreaLotData = SystemAPI.GetComponentLookup<Game.Areas.Lot>(true);
            definitions.m_EditorContainerData = SystemAPI.GetComponentLookup<Game.Tools.EditorContainer>(true);
            definitions.m_PrefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true);
            definitions.m_PrefabNetObjectData = SystemAPI.GetComponentLookup<NetObjectData>(true);
            definitions.m_PrefabBuildingData = SystemAPI.GetComponentLookup<BuildingData>(true);
            definitions.m_PrefabAssetStampData = SystemAPI.GetComponentLookup<AssetStampData>(true);
            definitions.m_PrefabBuildingExtensionData = SystemAPI.GetComponentLookup<BuildingExtensionData>(true);
            definitions.m_PrefabSpawnableObjectData = SystemAPI.GetComponentLookup<SpawnableObjectData>(true);
            definitions.m_PrefabObjectGeometryData = SystemAPI.GetComponentLookup<ObjectGeometryData>(true);
            definitions.m_PrefabPlaceableObjectData = SystemAPI.GetComponentLookup<PlaceableObjectData>(true);
            definitions.m_PrefabAreaGeometryData = SystemAPI.GetComponentLookup<AreaGeometryData>(true);
            definitions.m_PrefabBuildingTerraformData = SystemAPI.GetComponentLookup<BuildingTerraformData>(true);
            definitions.m_PrefabCreatureSpawnData = SystemAPI.GetComponentLookup<CreatureSpawnData>(true);
            definitions.m_PlaceholderBuildingData = SystemAPI.GetComponentLookup<PlaceholderBuildingData>(true);
            definitions.m_PrefabNetGeometryData = SystemAPI.GetComponentLookup<NetGeometryData>(true);
            definitions.m_PrefabCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true);
            definitions.m_SubObjects = SystemAPI.GetBufferLookup<Game.Objects.SubObject>(true);
            definitions.m_CachedNodes = SystemAPI.GetBufferLookup<LocalNodeCache>(true);
            definitions.m_InstalledUpgrades = SystemAPI.GetBufferLookup<InstalledUpgrade>(true);
            definitions.m_SubNets = SystemAPI.GetBufferLookup<Game.Net.SubNet>(true);
            definitions.m_ConnectedEdges = SystemAPI.GetBufferLookup<ConnectedEdge>(true);
            definitions.m_SubAreas = SystemAPI.GetBufferLookup<Game.Areas.SubArea>(true);
            definitions.m_AreaNodes = SystemAPI.GetBufferLookup<Game.Areas.Node>(true);
            definitions.m_AreaTriangles = SystemAPI.GetBufferLookup<Triangle>(true);
            definitions.m_PrefabSubObjects = SystemAPI.GetBufferLookup<Game.Prefabs.SubObject>(true);
            definitions.m_PrefabSubNets = SystemAPI.GetBufferLookup<Game.Prefabs.SubNet>(true);
            definitions.m_PrefabSubLanes = SystemAPI.GetBufferLookup<Game.Prefabs.SubLane>(true);
            definitions.m_PrefabSubAreas = SystemAPI.GetBufferLookup<Game.Prefabs.SubArea>(true);
            definitions.m_PrefabSubAreaNodes = SystemAPI.GetBufferLookup<SubAreaNode>(true);
            definitions.m_PrefabPlaceholderElements = SystemAPI.GetBufferLookup<PlaceholderObjectElement>(true);
            definitions.m_PrefabRequirementElements = SystemAPI.GetBufferLookup<ObjectRequirementElement>(true);
            definitions.m_PrefabServiceUpgradeBuilding = SystemAPI.GetBufferLookup<ServiceUpgradeBuilding>(true);
            definitions.m_WaterSurfaceData = m_WaterSystem.GetSurfaceData(out var deps);
            definitions.m_TerrainHeightData = m_TerrainSystem.GetHeightData();
            definitions.m_CommandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            definitions.Execute();
        }
    }
}
