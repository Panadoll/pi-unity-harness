#if PI_UNITY_PIPELINE
using System.Collections.Generic;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands.Data
{
    internal class PiMcpAddComponentResponse
    {
        public List<PiMcpComponentData> AddedComponents { get; set; } = new List<PiMcpComponentData>();
        public List<string> Messages { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiMcpComponentListResult
    {
        public string[] Items { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
    }

    internal class PiMcpGetComponentResponse
    {
        public PiMcpObjectRefData Reference { get; set; }
        public int Index { get; set; }
        public PiMcpComponentData Component { get; set; }
        public Dictionary<string, object> Fields { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public object View { get; set; }
    }

    internal class PiMcpModifyComponentResponse
    {
        public bool Success { get; set; }
        public PiMcpObjectRefData Reference { get; set; }
        public int Index { get; set; }
        public PiMcpComponentData Component { get; set; }
        public string[] Logs { get; set; }
    }

    internal class PiMcpCopyAssetsResponse
    {
        public List<PiMcpAssetData> CopiedAssets { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiMcpCreateFolderInput
    {
        public string Path { get; set; }
    }

    internal class PiMcpCreateFolderResponse
    {
        public List<string> CreatedFolders { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiMcpDeleteAssetsResponse
    {
        public List<string> DeletedPaths { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiMcpDestroyComponentsResponse
    {
        public List<PiMcpComponentData> DestroyedComponents { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiMcpDestroyGameObjectResult
    {
        public bool Success { get; set; }
        public PiMcpGameObjectData GameObject { get; set; }
        public string Error { get; set; }
    }

    internal class PiMcpEditorStatsData
    {
        public bool IsPlaying { get; set; }
        public bool IsPlayingOrWillChangePlaymode { get; set; }
        public bool IsPaused { get; set; }
        public bool IsCompiling { get; set; }
        public bool IsUpdating { get; set; }
        public string ApplicationPath { get; set; }
        public string UnityVersion { get; set; }
        public double TimeSinceStartup { get; set; }
    }

    internal class PiMcpModifyObjectResponse
    {
        public bool Success { get; set; }
        public PiMcpObjectRefData Reference { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public string[] Logs { get; set; }
    }

    internal class PiMcpMoveAssetsResponse
    {
        public List<string> MovedPaths { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiMcpPackageData
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string Source { get; set; }
        public string Category { get; set; }
        public string ResolvedPath { get; set; }
    }

    internal class PiMcpPackageSearchResult
    {
        public int Count { get; set; }
        public List<PiMcpPackageData> Packages { get; set; }
    }

    internal class PiMcpPassData
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    internal class PiMcpShaderData
    {
        public string Name { get; set; }
        public long InstanceId { get; set; }
        public string AssetPath { get; set; }
        public int PropertyCount { get; set; }
        public List<PiMcpShaderPropertyData> Properties { get; set; }
    }

    internal class PiMcpShaderPropertyData
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
    }

    internal class PiMcpShaderMessageData
    {
        public string Severity { get; set; }
        public string Message { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
    }

    internal class PiMcpSubshaderData
    {
        public int Index { get; set; }
        public int PassCount { get; set; }
    }

    internal class PiMcpToolInfoData
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool MainThreadRequired { get; set; }
        public bool RuntimeOnly { get; set; }
    }

    internal class PiMcpToolInputData
    {
        public string Name { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    internal class PiMcpToolToggleInput
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
    }

    internal class PiMcpToolToggleResult
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
        public string Message { get; set; }
    }

    internal class PiMcpUnloadSceneResult
    {
        public bool Unloaded { get; set; }
        public string Path { get; set; }
        public string Error { get; set; }
    }
}
#endif
