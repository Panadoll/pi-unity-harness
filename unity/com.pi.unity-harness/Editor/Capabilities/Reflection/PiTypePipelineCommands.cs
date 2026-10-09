#if PI_UNITY_PIPELINE
using System;
using System.Linq;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters
{
    internal static class PiTypePipelineCommands
    {
        [CliCommand("type_get_json_schema", "Build a basic JSON schema for a CLR/Unity type")]
        public static string TypeGetJsonSchema(
            [CliArg("type_name", "Full or short type name", Required = true)] string typeName,
            [CliArg("include_non_public", "Include non-public members")] bool includeNonPublic = false)
        {
            var type = PiPipelineSupport.FindType(typeName);
            if (type == null)
                throw new ArgumentException("Type not found: " + typeName);

            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
            if (includeNonPublic)
                flags |= System.Reflection.BindingFlags.NonPublic;

            var properties = type.GetProperties(flags)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Select(property => new { name = property.Name, type = property.PropertyType.FullName, canRead = property.CanRead, canWrite = property.CanWrite })
                .ToArray();
            var fields = type.GetFields(flags)
                .Select(field => new { name = field.Name, type = field.FieldType.FullName, isPublic = field.IsPublic })
                .ToArray();

            return PiPipelineSupport.Ok(new
            {
                type = type.FullName,
                schema = new
                {
                    title = type.Name,
                    type = "object",
                    properties,
                    fields,
                },
            });
        }
    }
}
#endif
