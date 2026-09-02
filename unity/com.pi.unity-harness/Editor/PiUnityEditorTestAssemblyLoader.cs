using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// Package test assemblies use autoReferenced=false, so TestRunnerApi.RetrieveTestList
    /// (which only sees AppDomain-loaded assemblies) can miss them until something loads the DLL.
    /// </summary>
    [InitializeOnLoad]
    public static class PiUnityEditorTestAssemblyLoader
    {
        static PiUnityEditorTestAssemblyLoader()
        {
            EditorApplication.delayCall += () => TryLoadEditorTestAssemblies();
        }

        /// <summary>
        /// Loads compiled Editor test assemblies into the current AppDomain.
        /// Returns the names that were newly loaded or already present.
        /// </summary>
        public static IReadOnlyList<string> TryLoadEditorTestAssemblies()
        {
            var loadedNames = new List<string>();
            UnityEditor.Compilation.Assembly[] assemblies;
            try
            {
                assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PiUnityEditorTestAssemblyLoader] CompilationPipeline.GetAssemblies failed: " + ex.Message);
                return loadedNames;
            }

            foreach (UnityEditor.Compilation.Assembly assembly in assemblies)
            {
                if (assembly == null || string.IsNullOrEmpty(assembly.outputPath) || !IsEditorTestAssembly(assembly))
                    continue;

                try
                {
                    if (IsLoaded(assembly.name))
                    {
                        loadedNames.Add(assembly.name);
                        continue;
                    }

                    string path = Path.GetFullPath(assembly.outputPath);
                    if (!File.Exists(path))
                        continue;

                    System.Reflection.Assembly.LoadFrom(path);
                    loadedNames.Add(assembly.name);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PiUnityEditorTestAssemblyLoader] Failed to load " + assembly.name + ": " + ex.Message);
                }
            }

            return loadedNames;
        }

        private static bool IsEditorTestAssembly(UnityEditor.Compilation.Assembly assembly)
        {
            if (!string.IsNullOrEmpty(assembly.name) &&
                assembly.name.IndexOf("Tests", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string[] defines = assembly.defines;
            if (defines == null)
                return false;
            for (int i = 0; i < defines.Length; i++)
            {
                if (defines[i] == "UNITY_INCLUDE_TESTS")
                    return true;
            }

            return false;
        }

        private static bool IsLoaded(string assemblyName)
        {
            System.Reflection.Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                if (string.Equals(loaded[i].GetName().Name, assemblyName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
