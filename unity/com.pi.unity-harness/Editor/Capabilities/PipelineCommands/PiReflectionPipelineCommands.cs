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
        [CliCommand("reflection_method_call", "Call a method via reflection")]
        public static string ReflectionMethodCall([CliArg("type_name", "Type name")] string typeName, [CliArg("method_name", "Method name")] string methodName, [CliArg("args_json", "JSON array of arguments")] string argsJson = null, [CliArg("instance_id", "Optional target instance id")] long instanceId = 0)
        {
            var type = PiMcpPipelineSupport.FindType(typeName);
            if (type == null)
                throw new ArgumentException("Type not found: " + typeName);
            var args = string.IsNullOrWhiteSpace(argsJson) ? new Newtonsoft.Json.Linq.JArray() : Newtonsoft.Json.Linq.JArray.Parse(argsJson);
            var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
                .Where(method => method.Name == methodName && method.GetParameters().Length == args.Count)
                .ToArray();
            if (methods.Length == 0)
                throw new ArgumentException("Method not found: " + methodName);
            var methodInfo = methods[0];
            object target = null;
            if (!methodInfo.IsStatic)
                target = PiMcpPipelineSupport.ObjectFromId(instanceId);
            var parameters = methodInfo.GetParameters();
            var values = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                values[i] = args[i].ToObject(parameters[i].ParameterType);
            object result = methodInfo.Invoke(target, values);
            return PiMcpPipelineSupport.Ok(new { typeName = type.FullName, methodName, result });
        }

        [CliCommand("reflection_method_find", "Find methods via reflection")]
        public static string ReflectionMethodFind([CliArg("type_name", "Type name filter")] string typeName = null, [CliArg("method_name", "Method name filter")] string methodName = null, [CliArg("limit", "Maximum results")] int limit = 100)
        {
            var matches = new List<object>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); } catch { continue; }
                foreach (var type in types)
                {
                    if (!string.IsNullOrWhiteSpace(typeName) && type.FullName.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
                    {
                        if (!string.IsNullOrWhiteSpace(methodName) && method.Name.IndexOf(methodName, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        matches.Add(new { type = type.FullName, method = method.Name, isStatic = method.IsStatic, parameters = method.GetParameters().Select(p => new { p.Name, parameterType = p.ParameterType.FullName }).ToArray(), returnType = method.ReturnType.FullName });
                        if (matches.Count >= (limit <= 0 ? 100 : limit))
                            return PiMcpPipelineSupport.Ok(new { count = matches.Count, methods = matches });
                    }
                }
            }
            return PiMcpPipelineSupport.Ok(new { count = matches.Count, methods = matches });
        }
    }
}
#endif
