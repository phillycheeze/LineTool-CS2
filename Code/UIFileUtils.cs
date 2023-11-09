﻿// <copyright file="UIFileUtils.cs" company="algernon (K. Algernon A. Sheppard)">
// Copyright (c) algernon (K. Algernon A. Sheppard). All rights reserved.
// </copyright>

namespace LineTool
{
    using System;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using cohtml.Net;
    using Colossal.IO.AssetDatabase;

    /// <summary>
    /// Utilities for loading and parsing UI related files (css, html, css).
    /// </summary>
    internal static class UIFileUtils
    {
        // Mod assembly path cache.
        private static string s_assemblyPath = null;

        /// <summary>
        /// Gets the mod directory file path of the currently executing mod assembly.
        /// </summary>
        public static string AssemblyPath
        {
            get
            {
                // Update cached path if the existing one is invalid.
                if (string.IsNullOrWhiteSpace(s_assemblyPath))
                {
                    // No path cached - find current executable asset.
                    string assemblyName = Assembly.GetExecutingAssembly().FullName;
                    ExecutableAsset modAsset = AssetDatabase.global.GetAsset(SearchFilter<ExecutableAsset>.ByCondition(x => x.definition?.FullName == assemblyName));
                    if (modAsset is null)
                    {
                        Logging.LogError("mod executable asset not found");
                        return null;
                    }

                    // Update cached path.
                    s_assemblyPath = Path.GetDirectoryName(modAsset.GetMeta().path);
                }

                // Return cached path.
                return s_assemblyPath;
            }
        }

        /// <summary>
        /// Executes JavaScript in the given View.
        /// </summary>
        /// <param name="view"><see cref="View"/> to execute in.</param>
        /// <param name="script">Script to execute.</param>
        internal static void ExecuteScript(View view, string script)
        {
            // Null check.
            if (!string.IsNullOrEmpty(script))
            {
                view?.ExecuteScript(script);
            }
        }

        /// <summary>
        /// Reads all UI files from the specified mod sub-directory.
        /// </summary>
        /// <param name="uiView">View to insert into.</param>
        /// <param name="directoryName">Mod sub-directory.</param>
        internal static void ReadUIFiles(View uiView, string directoryName)
        {
            try
            {
                // Get sub-directory.
                string directoryPath = Path.Combine(AssemblyPath, directoryName);
                if (!Directory.Exists(directoryPath))
                {
                    Logging.LogError("unable to locate UI file directory ", directoryPath);
                    return;
                }

                // Load css files.
                foreach (string filename in Directory.GetFiles(directoryPath, "*.css"))
                {
                    ExecuteScript(uiView, ReadCSS(filename));
                }

                // Load HTML files.
                foreach (string filename in Directory.GetFiles(directoryPath, "*.html"))
                {
                    ExecuteScript(uiView, ReadHTML(filename));
                }

                // Load JavaScript files.
                foreach (string filename in Directory.GetFiles(directoryPath, "*.js"))
                {
                    ExecuteScript(uiView, ReadJS(filename));
                }
            }
            catch (Exception e)
            {
                Logging.LogException(e, "exception reading UI files");
            }
        }

        /// <summary>
        /// Load CSS from a UI file.
        /// </summary>
        /// <param name="fileName">UI file name to read.</param>
        /// <returns>JavaScript text embedding the CSS (<c>null</c> if empty or error).</returns>
        private static string ReadCSS(string fileName)
        {
            try
            {
                // Attempt to read file.
                string css = ReadUIFile(fileName);

                // Don't do anything if file wasn't read.
                if (!string.IsNullOrEmpty(css))
                {
                    // Return JavaScript code with CSS embedded.
                    return $"var style = document.createElement('style'); style.type = 'text/css'; style.innerHTML = \"{EscapeToJavaScript(css)}\"; document.head.appendChild(style);";
                }
            }
            catch (Exception e)
            {
                Logging.LogException(e, "exception reading CSS file ", fileName);
            }

            // If we got here, something went wrong.; return null.
            return null;
        }

        /// <summary>
        /// Load HTML from a UI file.
        /// </summary>
        /// <param name="fileName">UI file name to read.</param>
        /// <returns>JavaScript text embedding the HTML (<c>null</c> if empty or error).</returns>
        private static string ReadHTML(string fileName)
        {
            try
            {
                // Attempt to read file.
                string html = ReadUIFile(fileName);

                // Don't do anything if file wasn't read.
                if (!string.IsNullOrEmpty(html))
                {
                    // Return JavaScript code with HTML embedded.
                    return $"var div = document.createElement('div'); div.innerHTML = \"{EscapeToJavaScript(html)}\"; document.body.appendChild(div);";
                }
            }
            catch (Exception e)
            {
                Logging.LogException(e, "exception reading CSS file ", fileName);
            }

            // If we got here, something went wrong.; return null.
            return null;
        }

        /// <summary>
        /// Load and execute JavaScript from a UI file.
        /// </summary>>
        /// <param name="fileName">UI file name to read.</param>
        /// <returns>JavaScript as <see cref="string"/> (<c>null</c> if empty or error).</returns>
        private static string ReadJS(string fileName)
        {
            try
            {
                // Attempt to read file.
                string js = ReadUIFile(fileName);

                // Don't do anything if file wasn't read.
                if (!string.IsNullOrEmpty(js))
                {
                    // Return JavaScript code with HTML embedded.
                    return js;
                }
            }
            catch (Exception e)
            {
                Logging.LogException(e, "exception reading CSS file ", fileName);
            }

            // If we got here, something went wrong.; return null.
            return null;
        }

        /// <summary>
        /// Reads a UI text file.
        /// </summary>
        /// <param name="fileName">UI file name to read.</param>
        /// <returns>File contents (<c>null</c> if none or error).</returns>
        private static string ReadUIFile(string fileName)
        {
            try
            {
                // Check that file exists.
                if (File.Exists(fileName))
                {
                    // Read file.
                    return File.ReadAllText(fileName);
                }
            }
            catch (Exception e)
            {
                Logging.LogException(e, "exception reading UI file ", fileName);
            }

            // If we got here, something went wrong.
            return null;
        }

        /// <summary>
        /// Escapes HTML input for in-lining into JavaScript.
        /// </summary>
        /// <param name="sourceString">HTML source.</param>
        /// <returns>Escaped HTML.</returns>
        private static string EscapeToJavaScript(string sourceString)
        {
            // Create output StringBuilder.
            int length = sourceString.Length;
            StringBuilder stringBuilder = new (length * 2);

            // Iterate through each char.
            int index = -1;
            while (++index < length)
            {
                char ch = sourceString[index];

                // Just skip line breaks.
                if (ch == '\n' || ch == '\r')
                {
                    continue;
                }

                // Escape any double or single quotes.
                if (ch == '"' || ch == '\'')
                {
                    stringBuilder.Append('\\');
                }

                // Add character to output.
                stringBuilder.Append(ch);
            }

            return stringBuilder.ToString();
        }
    }
}
