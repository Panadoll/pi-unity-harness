#if PI_UNITY_PIPELINE
using System.Collections.Generic;

namespace Pi.UnityHarness.Editor.Capabilities.Pipeline.Models
{
    internal class PiPipelineAddComponentResponse
    {
        public List<PiPipelineComponentData> AddedComponents { get; set; } = new List<PiPipelineComponentData>();
        public List<string> Messages { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiPipelineComponentListResult
    {
        public string[] Items { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
    }

    internal class PiPipelineGetComponentResponse
    {
        public PiPipelineObjectRefData Reference { get; set; }
        public int Index { get; set; }
        public PiPipelineComponentData Component { get; set; }
        public Dictionary<string, object> Fields { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public object View { get; set; }
    }

    internal class PiPipelineModifyComponentResponse
    {
        public bool Success { get; set; }
        public PiPipelineObjectRefData Reference { get; set; }
        public int Index { get; set; }
        public PiPipelineComponentData Component { get; set; }
        public string[] Logs { get; set; }
    }

    internal class PiPipelineCopyAssetsResponse
    {
        public List<PiPipelineAssetData> CopiedAssets { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiPipelineCreateFolderInput
    {
        public string Path { get; set; }
    }

    internal class PiPipelineCreateFolderResponse
    {
        public List<string> CreatedFolders { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiPipelineDeleteAssetsResponse
    {
        public List<string> DeletedPaths { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiPipelineDestroyComponentsResponse
    {
        public List<PiPipelineComponentData> DestroyedComponents { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiPipelineDestroyGameObjectResult
    {
        public bool Success { get; set; }
        public PiPipelineGameObjectData GameObject { get; set; }
        public string Error { get; set; }
    }

    internal class PiPipelineEditorStatsData
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

    internal class PiPipelineModifyObjectResponse
    {
        public bool Success { get; set; }
        public PiPipelineObjectRefData Reference { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public string[] Logs { get; set; }
    }

    internal class PiPipelineMoveAssetsResponse
    {
        public List<string> MovedPaths { get; set; }
        public List<string> Errors { get; set; }
    }

    internal class PiPipelinePackageData
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string Source { get; set; }
        public string Category { get; set; }
        public string ResolvedPath { get; set; }
    }

    internal class PiPipelinePackageSearchResult
    {
        public int Count { get; set; }
        public List<PiPipelinePackageData> Packages { get; set; }
    }

    internal class PiPipelinePassData
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    internal class PiPipelineShaderData
    {
        public string Name { get; set; }
        public long InstanceId { get; set; }
        public string AssetPath { get; set; }
        public int PropertyCount { get; set; }
        public List<PiPipelineShaderPropertyData> Properties { get; set; }
    }

    internal class PiPipelineShaderPropertyData
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
    }

    internal class PiPipelineShaderMessageData
    {
        public string Severity { get; set; }
        public string Message { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
    }

    internal class PiPipelineSubshaderData
    {
        public int Index { get; set; }
        public int PassCount { get; set; }
    }

    internal class PiPipelineToolInfoData
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool MainThreadRequired { get; set; }
        public bool RuntimeOnly { get; set; }
    }

    internal class PiPipelineToolInputData
    {
        public string Name { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    internal class PiPipelineToolToggleInput
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
    }

    internal class PiPipelineToolToggleResult
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
        public string Message { get; set; }
    }

    internal class PiPipelineUnloadSceneResult
    {
        public bool Unloaded { get; set; }
        public string Path { get; set; }
        public string Error { get; set; }
    }
}
#endif
