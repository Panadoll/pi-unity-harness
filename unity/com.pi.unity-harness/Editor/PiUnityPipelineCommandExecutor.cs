using System;
using System.Text;
#if PI_UNITY_PIPELINE
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;
#endif

namespace Pi.UnityHarness.Editor
{
    internal static class PiUnityPipelineCommandExecutor
    {
#if PI_UNITY_PIPELINE
        private static readonly HashSet<string> ForbiddenCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "eval",
            "recompile",
            "recompile_status",
        };
        // Pure in-memory state: not persisted across domain reloads. On beforeAssemblyReload each
        // request is answered with a timeout error and cleared, because managed Tasks cannot be
        // resumed after reload (contrast: PiUnityTestCoordinator uses SessionState across reloads).
        private static readonly List<PendingTask> PendingTasks = new List<PendingTask>();
        private static bool s_updateRegistered;

        static PiUnityPipelineCommandExecutor()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            for (int i = PendingTasks.Count - 1; i >= 0; i--)
            {
                PendingTask pending = PendingTasks[i];
                try
                {
                    pending.CompleteJson(pending.RequestId, PiUnityJsonHelper.ErrorJson(pending.RequestId, "timeout", "Domain reload — pipeline command '" + pending.CommandName + "' aborted"));
                }
                catch { /* Best-effort notification; do not block the reload */ }
            }
            PendingTasks.Clear();
            if (s_updateRegistered)
            {
                s_updateRegistered = false;
                EditorApplication.update -= OnUpdate;
            }
        }
#endif

        public static string BuildListCommandsResponse(string replyTo)
        {
#if PI_UNITY_PIPELINE
            try
            {
                JArray commands = new JArray();
                foreach (CommandInfo command in CommandRegistry.DiscoverCommands().OrderBy(c => c.Name))
                {
                    if (!IsCommandVisible(command))
                        continue;
                    commands.Add(BuildCommandJson(command));
                }

                JObject result = new JObject
                {
                    ["typeName"] = "command_list",
                    ["pipelineAvailable"] = true,
                    ["commands"] = commands,
                    ["count"] = commands.Count,
                };
                return PiUnityJsonHelper.SuccessJson(replyTo, result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                return PiUnityJsonHelper.ErrorJson(replyTo, "command_error", "list_commands failed: " + ex.Message);
            }
#else
            return PiUnityJsonHelper.SuccessJson(replyTo, "{\"typeName\":\"command_list\",\"pipelineAvailable\":false,\"commands\":[],\"count\":0}");
#endif
        }

        public static void ExecuteCommand(string requestId, string commandName, string parametersJson, int timeoutMs, Action<string, string> completeJson)
        {
#if PI_UNITY_PIPELINE
            // Idempotent wrapper: ensures each request completes at most once (guards against a second response from a catch path).
            completeJson = MakeCompleteOnce(completeJson);

            if (string.IsNullOrWhiteSpace(commandName))
            {
                completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "usage", "missing pipeline command name"));
                return;
            }

            try
            {
                CommandInfo command = CommandRegistry.DiscoverCommands().FirstOrDefault(c => string.Equals(c.Name, commandName, StringComparison.Ordinal));
                if (command == null)
                {
                    string available = string.Join(", ", CommandRegistry.DiscoverCommands().Where(IsCommandVisible).Select(c => c.Name).OrderBy(n => n));
                    completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "command_not_found", "No pipeline command named '" + commandName + "'. Available: [" + available + "]"));
                    return;
                }

                string forbiddenReason = GetForbiddenReason(command);
                if (forbiddenReason != null)
                {
                    completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "command_forbidden", forbiddenReason));
                    return;
                }

                JObject parameters;
                if (!TryParseParameters(parametersJson, out parameters, out string parseError))
                {
                    completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "parameter_error", parseError));
                    return;
                }

                if (string.Equals(command.Name, "run_tests", StringComparison.Ordinal) && IsSynchronousRunTests(command, parameters))
                {
                    PiUnityTestCoordinator.StartRunTests(requestId, parameters.ToString(Formatting.None), timeoutMs, completeJson);
                    return;
                }

                object[] boundParameters;
                if (!TryBindParameters(command, parameters, out boundParameters, out string bindError))
                {
                    completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "parameter_error", bindError));
                    return;
                }

                object rawResult;
                try
                {
                    rawResult = command.Method.Invoke(null, boundParameters);
                }
                catch (TargetInvocationException ex)
                {
                    Exception inner = ex.InnerException ?? ex;
                    completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "command_error", FormatException(commandName, inner)));
                    return;
                }

                if (rawResult is Task task)
                {
                    QueueTask(requestId, commandName, task, timeoutMs, completeJson);
                    return;
                }

                completeJson(requestId, SuccessCommandJson(requestId, commandName, rawResult));
            }
            catch (Exception ex)
            {
                // completeJson is idempotently wrapped: if the try block already responded, this call is ignored.
                completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "command_error", FormatException(commandName, ex)));
            }
#else
            completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "pipeline_unavailable", "com.unity.pipeline is not installed or PI_UNITY_PIPELINE is not defined."));
#endif
        }

#if PI_UNITY_PIPELINE
        internal static bool IsCommandVisible(CommandInfo command)
        {
            return command != null && command.RuntimeOnly == false && !ForbiddenCommands.Contains(command.Name);
        }

        internal static string GetForbiddenReason(CommandInfo command)
        {
            if (command.RuntimeOnly)
                return "Command '" + command.Name + "' is runtime-only and cannot run inside the Editor pipe bridge.";

            if (string.Equals(command.Name, "eval", StringComparison.Ordinal))
                return "Pipeline eval is disabled on the pipe bridge; use native unity_eval instead.";

            if (string.Equals(command.Name, "recompile", StringComparison.Ordinal))
                return "Pipeline recompile is disabled on the pipe bridge; use native unity_recompile for blocking reload-stable compilation.";

            if (string.Equals(command.Name, "recompile_status", StringComparison.Ordinal))
                return "Pipeline recompile_status is disabled on the pipe bridge; unity_recompile returns the final result directly, and unity_status can observe compiler state.";

            return null;
        }

        private static JObject BuildCommandJson(CommandInfo command)
        {
            JToken schema = JValue.CreateNull();
            try
            {
                string schemaText = JsonSchemaGenerator.GenerateCommandSchema(command);
                if (!string.IsNullOrWhiteSpace(schemaText))
                    schema = JObject.Parse(schemaText);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PiUnityHarness] pipeline schema generation failed for " + command.Name + ": " + ex.Message);
            }

            JArray parameters = new JArray();
            foreach (CommandParameterInfo parameter in command.Parameters)
            {
                parameters.Add(new JObject
                {
                    ["name"] = parameter.Name,
                    ["description"] = parameter.Description,
                    ["type"] = parameter.ParameterType.Name,
                    ["typeFullName"] = parameter.ParameterType.FullName,
                    ["required"] = parameter.Required,
                    ["defaultValue"] = ToJToken(parameter.DefaultValue),
                });
            }

            return new JObject
            {
                ["name"] = command.Name,
                ["description"] = command.Description,
                ["mainThreadRequired"] = command.MainThreadRequired,
                ["runtimeOnly"] = command.RuntimeOnly,
                ["schema"] = schema,
                ["parameters"] = parameters,
            };
        }

        internal static bool TryParseParameters(string parametersJson, out JObject parameters, out string error)
        {
            parameters = null;
            error = null;

            string text = string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson;
            try
            {
                JToken token = JToken.Parse(text);
                parameters = token as JObject;
                if (parameters == null)
                {
                    error = "parametersJson must be a JSON object";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "invalid parametersJson: " + ex.Message;
                return false;
            }
        }

        internal static bool TryBindParameters(CommandInfo command, JObject parametersJson, out object[] values, out string error)
        {
            values = new object[command.Parameters.Count];
            error = null;

            for (int i = 0; i < command.Parameters.Count; i++)
            {
                CommandParameterInfo parameter = command.Parameters[i];
                JToken token = null;
                bool hasValue = parametersJson != null && parametersJson.TryGetValue(parameter.Name, out token) && token != null && token.Type != JTokenType.Null;

                if (!hasValue)
                {
                    if (parameter.Required)
                    {
                        error = "Required parameter '" + parameter.Name + "' is missing";
                        return false;
                    }

                    values[i] = parameter.DefaultValue ?? (parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null);
                    continue;
                }

                try
                {
                    object value = token.ToObject(parameter.ParameterType);
                    if (parameter.Required && value is string text && string.IsNullOrEmpty(text))
                    {
                        error = "Required parameter '" + parameter.Name + "' is empty";
                        return false;
                    }
                    values[i] = value;
                }
                catch (Exception ex)
                {
                    error = "Failed to convert parameter '" + parameter.Name + "' to " + parameter.ParameterType.Name + ": " + ex.Message;
                    return false;
                }
            }

            return true;
        }

        private static bool IsSynchronousRunTests(CommandInfo command, JObject parameters)
        {
            if (!string.Equals(command.Name, "run_tests", StringComparison.Ordinal))
                return false;

            if (parameters == null)
                return true;

            JToken token;
            if (!parameters.TryGetValue("async_tests", out token) || token == null || token.Type == JTokenType.Null)
                return true;

            try
            {
                return !token.ToObject<bool>();
            }
            catch
            {
                return false;
            }
        }

        private static Action<string, string> MakeCompleteOnce(Action<string, string> completeJson)
        {
            int completed = 0;
            return (id, json) =>
            {
                if (System.Threading.Interlocked.Exchange(ref completed, 1) == 0)
                    completeJson(id, json);
            };
        }

        private static void QueueTask(string requestId, string commandName, Task task, int timeoutMs, Action<string, string> completeJson)
        {
            int effectiveTimeoutMs = timeoutMs > 0 ? timeoutMs : 30000;
            PendingTasks.Add(new PendingTask
            {
                RequestId = requestId,
                CommandName = commandName,
                Task = task,
                StartedAtUtc = DateTime.UtcNow,
                TimeoutMs = effectiveTimeoutMs,
                CompleteJson = completeJson,
            });
            EnsureUpdateRegistered();
        }

        private static void EnsureUpdateRegistered()
        {
            if (s_updateRegistered)
                return;
            s_updateRegistered = true;
            EditorApplication.update += OnUpdate;
        }

        private static void OnUpdate()
        {
            for (int i = PendingTasks.Count - 1; i >= 0; i--)
            {
                PendingTask pending = PendingTasks[i];
                double elapsedMs = (DateTime.UtcNow - pending.StartedAtUtc).TotalMilliseconds;
                if (elapsedMs > pending.TimeoutMs)
                {
                    PendingTasks.RemoveAt(i);
                    pending.CompleteJson(pending.RequestId, PiUnityJsonHelper.ErrorJson(pending.RequestId, "timeout", "pipeline command '" + pending.CommandName + "' exceeded " + pending.TimeoutMs + "ms"));
                    continue;
                }

                if (!pending.Task.IsCompleted)
                    continue;

                PendingTasks.RemoveAt(i);
                CompleteTask(pending);
            }

            if (PendingTasks.Count == 0 && s_updateRegistered)
            {
                s_updateRegistered = false;
                EditorApplication.update -= OnUpdate;
            }
        }

        private static void CompleteTask(PendingTask pending)
        {
            if (pending.Task.IsCanceled)
            {
                pending.CompleteJson(pending.RequestId, PiUnityJsonHelper.ErrorJson(pending.RequestId, "cancelled", "pipeline command '" + pending.CommandName + "' was cancelled"));
                return;
            }

            if (pending.Task.IsFaulted)
            {
                Exception ex = pending.Task.Exception != null ? pending.Task.Exception.GetBaseException() : null;
                pending.CompleteJson(pending.RequestId, PiUnityJsonHelper.ErrorJson(pending.RequestId, "command_error", FormatException(pending.CommandName, ex ?? pending.Task.Exception)));
                return;
            }

            object result = null;
            try
            {
                PropertyInfo resultProperty = pending.Task.GetType().GetProperty("Result");
                if (resultProperty != null)
                    result = resultProperty.GetValue(pending.Task, null);
            }
            catch (Exception ex)
            {
                pending.CompleteJson(pending.RequestId, PiUnityJsonHelper.ErrorJson(pending.RequestId, "command_error", "Failed to read Task result for '" + pending.CommandName + "': " + ex.Message));
                return;
            }

            pending.CompleteJson(pending.RequestId, SuccessCommandJson(pending.RequestId, pending.CommandName, result));
        }

        private static string SuccessCommandJson(string replyTo, string commandName, object value)
        {
            string output = SerializeOutput(value);
            JObject result = new JObject
            {
                ["output"] = output,
                ["typeName"] = "pipeline_command",
                ["command"] = commandName,
                ["valueTypeName"] = value == null ? null : value.GetType().FullName,
            };

            if (value is string)
            {
                JToken parsed;
                if (TryParseJsonToken(output, out parsed))
                    result["value"] = parsed;
            }
            else
            {
                result["value"] = ToJToken(value);
            }

            return PiUnityJsonHelper.SuccessJson(replyTo, result.ToString(Formatting.None));
        }

        private static string SerializeOutput(object value)
        {
            if (value == null)
                return string.Empty;
            if (value is string text)
                return text;
            if (value is JToken token)
                return token.ToString(Formatting.None);
            return JsonConvert.SerializeObject(value, Formatting.None);
        }

        private static JToken ToJToken(object value)
        {
            if (value == null)
                return JValue.CreateNull();
            try
            {
                return JToken.FromObject(value);
            }
            catch
            {
                return value.ToString();
            }
        }

        private static bool TryParseJsonToken(string text, out JToken token)
        {
            token = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            try
            {
                token = JToken.Parse(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string FormatException(string commandName, Exception ex)
        {
            if (ex == null)
                return "Command '" + commandName + "' failed";
            return "Command '" + commandName + "' failed: " + ex.GetType().Name + ": " + ex.Message;
        }

        private sealed class PendingTask
        {
            public string RequestId;
            public string CommandName;
            public Task Task;
            public DateTime StartedAtUtc;
            public int TimeoutMs;
            public Action<string, string> CompleteJson;
        }
#endif

    }
}
