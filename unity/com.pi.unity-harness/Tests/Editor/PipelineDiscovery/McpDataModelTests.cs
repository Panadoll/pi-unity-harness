#if PI_UNITY_PIPELINE
using System;
using System.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands.Data;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class McpDataModelTests
    {
        [Test]
        public void MigratedDataModels_ExposeUnityMcpResponseShapes()
        {
            Type[] models =
            {
                typeof(PiMcpAddComponentResponse),
                typeof(PiMcpComponentListResult),
                typeof(PiMcpCopyAssetsResponse),
                typeof(PiMcpCreateFolderInput),
                typeof(PiMcpCreateFolderResponse),
                typeof(PiMcpDeleteAssetsResponse),
                typeof(PiMcpDestroyComponentsResponse),
                typeof(PiMcpDestroyGameObjectResult),
                typeof(PiMcpEditorStatsData),
                typeof(PiMcpGetComponentResponse),
                typeof(PiMcpModifyComponentResponse),
                typeof(PiMcpModifyObjectResponse),
                typeof(PiMcpMoveAssetsResponse),
                typeof(PiMcpPackageData),
                typeof(PiMcpPackageSearchResult),
                typeof(PiMcpPassData),
                typeof(PiMcpSelectionData),
                typeof(PiMcpShaderData),
                typeof(PiMcpShaderMessageData),
                typeof(PiMcpShaderPropertyData),
                typeof(PiMcpSubshaderData),
                typeof(PiMcpToolInfoData),
                typeof(PiMcpToolInputData),
                typeof(PiMcpToolToggleInput),
                typeof(PiMcpToolToggleResult),
                typeof(PiMcpUnloadSceneResult),
            };

            foreach (var model in models)
                Assert.IsTrue(model.GetFields().Length + model.GetProperties().Length > 0, model.FullName);
        }

        [Test]
        public void PackageData_MatchesUnityMcpCoreFields()
        {
            var names = typeof(PackageData).GetProperties().Select(property => property.Name).ToArray();

            CollectionAssert.Contains(names, "Name");
            CollectionAssert.Contains(names, "DisplayName");
            CollectionAssert.Contains(names, "Version");
            CollectionAssert.Contains(names, "Description");
            CollectionAssert.Contains(names, "Source");
            CollectionAssert.Contains(names, "Category");
        }

        [Test]
        public void UnityMcpAliasTypes_AreAvailableForOriginalModelNames()
        {
            Type[] aliases =
            {
                typeof(AddComponentResponse),
                typeof(ComponentListResult),
                typeof(CopyAssetsResponse),
                typeof(CreateFolderInput),
                typeof(CreateFolderResponse),
                typeof(DeleteAssetsResponse),
                typeof(DestroyComponentsResponse),
                typeof(DestroyGameObjectResult),
                typeof(EditorStatsData),
                typeof(GetComponentResponse),
                typeof(ModifyComponentResponse),
                typeof(ModifyObjectResponse),
                typeof(MoveAssetsResponse),
                typeof(PackageData),
                typeof(PackageSearchResult),
                typeof(PassData),
                typeof(SelectionData),
                typeof(ShaderData),
                typeof(ShaderMessageData),
                typeof(ShaderPropertyData),
                typeof(SubshaderData),
                typeof(ToolInfoData),
                typeof(ToolInputData),
                typeof(ToolToggleInput),
                typeof(ToolToggleResult),
                typeof(UnloadSceneResult),
            };

            foreach (var alias in aliases)
                Assert.IsNotNull(alias.BaseType, alias.Name);
        }
    }
}
#endif
