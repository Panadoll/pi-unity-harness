#if PI_UNITY_PIPELINE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands.Data;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static partial class PiMcpPipelineCommands
    {
        [CliCommand("package_add", "Add a Unity package")]
        public static Task<string> PackageAdd([CliArg("package_id", "Package id or git URL")] string packageId)
        {
            var request = Client.Add(packageId);
            return AwaitPackageRequest(request, "package_add", () => request.Result);
        }

        [CliCommand("package_list", "List Unity packages")]
        public static Task<string> PackageList([CliArg("offline_mode", "Use offline mode")] bool offlineMode = false)
        {
            var request = Client.List(offlineMode, true);
            return AwaitPackageRequest(request, "package_list", () => request.Result);
        }

        [CliCommand("package_remove", "Remove a Unity package")]
        public static Task<string> PackageRemove([CliArg("package_name", "Package name")] string packageName)
        {
            var request = Client.Remove(packageName);
            return AwaitPackageRequest(request, "package_remove", () => packageName);
        }

        [CliCommand("package_search", "Search Unity packages")]
        public static async Task<string> PackageSearch([CliArg("query", "Search query")] string query)
        {
            var request = Client.SearchAll(false);
            await WaitForPackageRequest(request);
            var results = request.Result
                .Where(package => string.IsNullOrWhiteSpace(query) || package.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || package.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(ToPackageData)
                .ToList();
            return PiMcpPipelineSupport.Ok(new PiMcpPackageSearchResult { Count = results.Count, Packages = results });
        }
    }
}
#endif
