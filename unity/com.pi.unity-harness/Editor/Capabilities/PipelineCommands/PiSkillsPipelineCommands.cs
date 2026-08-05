#if PI_UNITY_PIPELINE
using System;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static partial class PiMcpPipelineCommands
    {
        [CliCommand("skills_create", "Create a markdown skill file under Assets/PiSkills")]
        public static string SkillsCreate(
            [CliArg("name", "Skill name")] string name,
            [CliArg("description", "Skill description")] string description = null,
            [CliArg("body", "Skill markdown body")] string body = null,
            [CliArg("folder", "Target folder under project")] string folder = "Assets/PiSkills")
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Skill name is required.");

            string safeName = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-').ToArray()).Trim('-');
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "skill";

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string relativePath = (folder.TrimEnd('/', '\\') + "/" + safeName + ".md").Replace('\\', '/');
            string fullPath = Path.Combine(projectRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

            string content = "# " + name + "\n\n" +
                (string.IsNullOrWhiteSpace(description) ? string.Empty : description + "\n\n") +
                (string.IsNullOrWhiteSpace(body) ? "Describe when and how to use this Unity skill.\n" : body.TrimEnd() + "\n");
            File.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();
            return PiMcpPipelineSupport.Ok(new { created = true, path = relativePath, name, description });
        }

        [CliCommand("skills_generate", "Generate a markdown summary of available Unity pipeline commands")]
        public static string SkillsGenerate(
            [CliArg("folder", "Target folder under project")] string folder = "Assets/PiSkills",
            [CliArg("name", "Generated skill file name")] string name = "unity-pipeline-tools")
        {
            var commands = Unity.Pipeline.Commands.CommandRegistry.DiscoverCommands()
                .Where(Pi.UnityHarness.Editor.PiUnityPipelineCommandExecutor.IsCommandVisible)
                .OrderBy(command => command.Name)
                .Select(command => "- `" + command.Name + "` - " + command.Description)
                .ToArray();
            string body = "Use these Unity Pipeline commands through `unity_pipeline`.\n\n" + string.Join("\n", commands);
            return SkillsCreate(name, "Generated summary of Unity Pipeline commands.", body, folder);
        }
    }
}
#endif
