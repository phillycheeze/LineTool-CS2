﻿// <copyright file="LineToolSystem.cs" company="algernon (K. Algernon A. Sheppard)">
// Copyright (c) algernon (K. Algernon A. Sheppard). All rights reserved.
// </copyright>

namespace LineTool
{
    using System.Reflection;
    using Colossal.Entities;
    using Colossal.Logging;
    using Game.Common;
    using Game.Input;
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
        // Previewing.
        private readonly NativeList<Entity> _previewEntities = new (Allocator.Persistent);
        private readonly NativeList<TooltipInfo> _tooltips = new (8, Allocator.Persistent);

        // Line calculations.
        private readonly NativeList<PointData> _points = new (Allocator.Persistent);
        private bool _fixedPreview = false;
        private float3 _fixedPos;
        private Random _random = new ();

        // Cursor.
        private ControlPoint _raycastPoint;
        private float3 _previousPos;
        private Entity _cursorEntity = Entity.Null;

        // Prefab selection.
        private ObjectPrefab _selectedPrefab;
        private Entity _selectedEntity = Entity.Null;
        private int _originalXP;

        // References.
        private ILog _log;
        private TerrainSystem _terrainSystem;
        private TerrainHeightData _terrainHeightData;
        private OverlayRenderSystem.Buffer _overlayBuffer;

        // Input actions.
        private ProxyAction _applyAction;
        private ProxyAction _cancelAction;
        private InputAction _fixedPreviewAction;

        // Mode.
        private LineMode _currentMode;
        private LineModeBase _mode;

        // Tool settings.
        private float _spacing = 20f;
        private bool _randomRotation = false;
        private int _rotation = 0;
        private bool _dirty = false;

        // Tree Controller integration.
        private ToolBaseSystem _treeControllerTool;
        private PropertyInfo _nextTreeState = null;

        /// <summary>
        /// Gets the tool's ID string.
        /// </summary>
        public override string toolID => "Line Tool";

        /// <summary>
        /// Gets or sets the line spacing.
        /// </summary>
        internal float Spacing
        {
            get => _spacing;

            set
            {
                _spacing = value;
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
        /// Gets or sets the rotation setting.
        /// </summary>
        internal int Rotation
        {
            get => _rotation;
            set
            {
                _rotation = value;
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
                        _mode = new StraightMode(_mode);
                        break;
                    case LineMode.SimpleCurve:
                        _mode = new SimpleCurveMode(_mode);
                        break;
                    case LineMode.Circle:
                        _mode = new CircleMode(_mode);
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
                _selectedPrefab = value as ObjectPrefab;

                // Update selected entity;
                if (_selectedPrefab is null)
                {
                    _selectedEntity = Entity.Null;
                }
                else
                {
                    // Get selected entity.
                    _selectedEntity = m_PrefabSystem.GetEntity(_selectedPrefab);

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
        public override PrefabBase GetPrefab() => null; // TODO:_selectedPrefab;

        /// <summary>
        /// Sets the prefab selected by this tool.
        /// </summary>
        /// <param name="prefab">Prefab to set.</param>
        /// <returns><c>true</c> if a prefab is currently selected, otherwise <c>false</c>.</returns>
        public override bool TrySetPrefab(PrefabBase prefab)
        {
            // Check for eligible prefab.
            if (prefab is ObjectPrefab objectPrefab)
            {
                // Eligible - set it.
                SelectedPrefab = objectPrefab;
                return true;
            }

            // If we got here this isn't an eligible prefab.
            return false;
        }

        /// <summary>
        /// Refreshes all displayed prefabs to align with current Tree Control settings.
        /// </summary>
        internal void RefreshTreeControl()
        {
            // Update cursor entity.
            ResetTreeState(_cursorEntity);

            // Update all previewed trees.
            for (int i = 0; i < _previewEntities.Length; ++i)
            {
                ResetTreeState(_previewEntities[i]);
            }

            // Set dirty flag.
            _dirty = true;
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

            // Set default mode.
            _currentMode = LineMode.Straight;
            _mode = new StraightMode();

            // Set actions.
            _applyAction = InputManager.instance.FindAction("Tool", "Apply");
            _cancelAction = InputManager.instance.FindAction("Tool", "Mouse Cancel");

            // Enable fixed preview control.
            _fixedPreviewAction = new ("LineTool-FixPreview");
            _fixedPreviewAction.AddCompositeBinding("ButtonWithOneModifier").With("Modifier", "<Keyboard>/shift").With("Button", "<Mouse>/leftButton");
            _fixedPreviewAction.Enable();

            // Enable hotkey.
            InputAction hotKey = new ("LineTool-Hotkey");
            hotKey.AddCompositeBinding("ButtonWithOneModifier").With("Modifier", "<Keyboard>/ctrl").With("Button", "<Keyboard>/l");
            hotKey.performed += EnableTool;
            hotKey.Enable();

            // Try to get tree controller tool.
            if (World.GetExistingSystemManaged<ToolSystem>().tools.Find(x => x.toolID.Equals("Tree Controller Tool")) is ToolBaseSystem treeControllerTool)
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
            // Clear tooltips.
            _tooltips.Clear();

            // Don't do anything if no selected prefab.
            if (_selectedPrefab is null)
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

                // Check for and perform any cancellation.
                if (_cancelAction.WasPressedThisFrame())
                {
                    // Reset current mode settings.
                    _mode.Reset();

                    // Revert previewing.
                    foreach (Entity previewEntity in _previewEntities)
                    {
                        EntityManager.AddComponent<Deleted>(previewEntity);
                    }

                    _previewEntities.Clear();

                    return inputDeps;
                }

                // If no cancellation, handle any fixed preview action.
                else if (_fixedPreviewAction.WasPressedThisFrame())
                {
                    _fixedPreview = true;
                    _fixedPos = position;
                }

                // Handle apply action if no other actions.
                else if (_applyAction.WasPressedThisFrame())
                {
                    // Were we in fixed state?
                    if (_fixedPreview)
                    {
                        // Yes - cancel fixed preview.
                        _fixedPreview = false;
                    }

                    // Handle click.
                    if (_mode.HandleClick(position))
                    {
                        // We're placing items - remove highlighting.
                        foreach (Entity previewEntity in _previewEntities)
                        {
                            EntityManager.RemoveComponent<Highlighted>(previewEntity);
                            EntityManager.AddComponent<Updated>(previewEntity);
                        }

                        // Clear preview.
                        _previewEntities.Clear();

                        // Reset tool mode.
                        _mode.ItemsPlaced(position);

                        return inputDeps;
                    }
                }

                // Update cursor entity if we haven't got an initial position set.
                if (!_mode.HasStart)
                {
                    // Create cursor entity if none yet exists.
                    if (_cursorEntity == Entity.Null)
                    {
                        _cursorEntity = CreateEntity();

                        // Highlight cursor entity.
                        EntityManager.AddComponent<Highlighted>(_cursorEntity);
                    }

                    // Update cursor entity position.
                    EntityManager.SetComponentData(_cursorEntity, new Transform { m_Position = position, m_Rotation = GetEffectiveRotation(position) });
                    EntityManager.AddComponent<Updated>(_cursorEntity);

                    // Ensure cursor entity tree state.
                    EnsureTreeState(_cursorEntity);

                    return inputDeps;
                }
                else if (_cursorEntity != Entity.Null)
                {
                    // Cancel cursor entity.
                    EntityManager.AddComponent<Deleted>(_cursorEntity);
                    _cursorEntity = Entity.Null;
                }
            }

            // Render any overlay.
            _mode.DrawOverlay(position, _overlayBuffer, _tooltips);

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
            _mode.CalculatePoints(position, Spacing, _rotation, _points, ref _terrainHeightData);

            // Step along length and place objects.
            int count = 0;
            foreach (PointData thisPoint in _points)
            {
                UnityEngine.Random.InitState((int)(thisPoint.Position.x + thisPoint.Position.y + thisPoint.Position.z));

                // Create transform component.
                Transform transformData = new ()
                {
                    m_Position = thisPoint.Position,
                    m_Rotation = _randomRotation ? GetEffectiveRotation(thisPoint.Position) : thisPoint.Rotation,
                };

                // Create new entity if required.
                if (count >= _previewEntities.Length)
                {
                    // Create new entity.
                    Entity newEntity = CreateEntity();

                    // Set entity location.
                    EntityManager.SetComponentData(newEntity, transformData);
                    EntityManager.AddComponent<Highlighted>(newEntity);
                    _previewEntities.Add(newEntity);
                }
                else
                {
                    // Otherwise, use existing entity.
                    EntityManager.SetComponentData(_previewEntities[count], transformData);
                    EntityManager.AddComponent<Updated>(_previewEntities[count]);

                    // Ensure any trees are still adults.
                    EnsureTreeState(_previewEntities[count]);
                }

                // Increment distance.
                ++count;
            }

            // Clear any excess entities.
            if (count < _previewEntities.Length)
            {
                int startCount = count;
                while (count < _previewEntities.Length)
                {
                    EntityManager.AddComponent<Deleted>(_previewEntities[count++]);
                }

                // Remove excess range from list
                _previewEntities.RemoveRange(startCount, count - startCount);
            }

            return inputDeps;
        }

        /// <summary>
        /// Called when the tool starts running.
        /// </summary>
        protected override void OnStartRunning()
        {
            _log.Debug("OnStartRunning");
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
            _log.Debug("OnStopRunning");

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

            // Revert previewing.
            foreach (Entity previewEntity in _previewEntities)
            {
                EntityManager.AddComponent<Deleted>(previewEntity);
            }

            // Clear previewed entity buffer.
            _previewEntities.Clear();

            // Restore original prefab XP, if we changed it.
            if (_originalXP != 0 && EntityManager.TryGetComponent(_selectedEntity, out PlaceableObjectData placeableData))
            {
                placeableData.m_XPReward = _originalXP;
                EntityManager.SetComponentData(_selectedEntity, placeableData);
                _originalXP = 0;
            }

            base.OnStopRunning();
        }

        /// <summary>
        /// Called when the system is destroyed.
        /// </summary>
        protected override void OnDestroy()
        {
            // Dispose of unmanaged lists.
            _previewEntities.Dispose();
            _points.Dispose();
            _tooltips.Dispose();

            base.OnDestroy();
        }

        /// <summary>
        /// Enables the tool (called by hotkey action).
        /// </summary>
        /// <param name="context">Callback context.</param>
        private void EnableTool(InputAction.CallbackContext context)
        {
            // Activate this tool if it isn't already active.
            if (m_ToolSystem.activeTool != this)
            {
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
                    m_Growth = 128,
                };

                EntityManager.SetComponentData(newEntity, treeData);
            }

            return newEntity;
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
                _random.InitState((uint)(math.abs(position.x) + math.abs(position.y) + math.abs(position.z)) * 1000);
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
            // Ensure any trees are still adults.
            if (EntityManager.TryGetComponent<Tree>(entity, out Tree tree))
            {
                if (tree.m_Growth != 128)
                {
                    tree.m_State = GetTreeState();
                    tree.m_Growth = 128;
                    EntityManager.SetComponentData(entity, tree);
                }
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
    }
}
