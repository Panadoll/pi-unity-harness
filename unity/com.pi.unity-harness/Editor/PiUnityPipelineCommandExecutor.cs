using System;
using System.Text;
using System.IO;
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
        private static readonly Dictionary<string, string> SidecarMutability = LoadSidecarMutability();
        private static bool s_sidecarWarningLogged;
        private static readonly HashSet<string> ForbiddenCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "eval",
            "recompile",
            "recompile_status",
        };
        private const int MaxCommandSuggestions = 5;
        private const int MaxSuggestionTextLength = 96;
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
                CommandInfo command = FindCommand(commandName);
                if (command == null)
                {
                    string message = BuildCommandNotFoundMessage(commandName, CommandRegistry.DiscoverCommands());
                    completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "command_not_found", message));
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
                    object server = FindLivePipelineServer();
                    rawResult = InvokeBoundCommand(command, boundParameters, server, timeoutMs);
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
        public static void ExecuteJob(string jobId, string commandName, string parametersJson, int timeoutMs)
        {
#if PI_UNITY_PIPELINE
            if (string.IsNullOrEmpty(jobId) || !PiUnityBridge.TryStartPipelineJob(jobId))
                return;

            Task.Run(async () =>
            {
                try
                {
                    object server = FindLivePipelineServer();
                    if (server == null)
                        throw new InvalidOperationException("Pipeline server is not available for detached command execution.");

                    CommandInfo command = CommandRegistry.DiscoverCommands()
                        .FirstOrDefault(c => string.Equals(c.Name, commandName, StringComparison.Ordinal));
                    if (command == null)
                        throw new InvalidOperationException(BuildCommandNotFoundMessage(commandName, CommandRegistry.DiscoverCommands()));
                    string forbiddenReason = GetForbiddenReason(command);
                    if (forbiddenReason != null)
                        throw new InvalidOperationException(forbiddenReason);

                    JObject parameters;
                    if (!TryParseParameters(parametersJson, out parameters, out string parseError))
                        throw new ArgumentException(parseError);

                    object registry = GetPropertyValue(server, "JobRegistry");
                    if (registry == null)
                        throw new MissingMemberException("Unity Pipeline JobRegistry is unavailable.");
                    MethodInfo tryCreate = registry.GetType().GetMethod("TryCreate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (tryCreate == null)
                        throw new MissingMethodException("Unity Pipeline JobRegistry.TryCreate is unavailable.");
                    // 先验证入口，避免创建无法执行的官方 queued 记录。
                    MethodInfo runJob = server.GetType().BaseType?.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "RunJobDetached" && m.GetParameters().Length == 3);
                    if (runJob == null)
                        throw new MissingMethodException("Unity Pipeline RunJobDetached is unavailable.");
                    Type requestType = runJob.GetParameters()[2].ParameterType;
                    object request = Activator.CreateInstance(requestType, true);
                    SetPropertyValue(request, "Command", commandName);
                    SetPropertyValue(request, "Parameters", parameters);
                    SetPropertyValue(request, "Job", true);
                    SetPropertyValue(request, "Timeout", timeoutMs > 0 ? (int?)timeoutMs : null);
                    object[] createArgs = { commandName, null };
                    if (!(bool)tryCreate.Invoke(registry, createArgs))
                        throw new InvalidOperationException("Unity Pipeline detached job queue is full.");
                    object record = createArgs[1];
                    // 非主线程命令可能在 Invoke 内同步阻塞；监控必须独立运行。
                    Task officialJob = (Task)runJob.Invoke(server, new[] { record, command, request });
                    await MonitorOfficialJob(officialJob, server, registry, record, jobId).ConfigureAwait(false);
                    JObject status = ReadOfficialJobStatus(server, record);
                    string state = status.Value<string>("state") ?? "failed";
                    if (string.Equals(state, "completed", StringComparison.OrdinalIgnoreCase))
                        PiUnityBridge.CompleteJson(jobId, SuccessCommandJson(jobId, commandName, status["result"]));
                    else
                        PiUnityBridge.CompleteJson(jobId, PiUnityJsonHelper.ErrorJson(jobId, OfficialJobErrorType(state, status), OfficialJobError(status)));
                }
                catch (Exception ex)
                {
                    Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    PiUnityBridge.CompleteJson(jobId, PiUnityJsonHelper.ErrorJson(jobId, "command_error", FormatException(commandName, inner)));
                }
            });
#else
            PiUnityBridge.CompleteJson(jobId, PiUnityJsonHelper.ErrorJson(jobId, "pipeline_unavailable", "com.unity.pipeline is not installed or PI_UNITY_PIPELINE is not defined."));
#endif
        }
#if PI_UNITY_PIPELINE
        internal static CommandInfo FindCommand(string name)
        {
            return CommandRegistry.DiscoverCommands().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        }

        /// <summary>
        /// 主线程同步调用。不走 server.ExecuteCommand：它对后台命令做 Task.Run + await，续体要回主线程，
        /// 主线程在这里等它就会让 Editor 永久卡死。
        /// </summary>
        internal static object InvokeCommand(CommandInfo command, JObject parameters)
        {
            if (!TryBindParameters(command, parameters, out object[] values, out string error))
                throw new ArgumentException(error);
            object result = InvokeCommandMethod(command, values);
            Task task = result as Task;
            if (task == null)
                return result;
            if (!task.IsCompleted)
                throw new InvalidOperationException("Pipeline command '" + command.Name + "' is asynchronous; use InvokeCommandAsync instead of blocking the Unity main thread.");
            return CompletedTaskResult(task);
        }

        internal static async Task<object> InvokeCommandAsync(CommandInfo command, JObject parameters)
        {
            if (!TryBindParameters(command, parameters, out object[] values, out string error))
                throw new ArgumentException(error);
            object result = InvokeBoundCommand(command, values, FindLivePipelineServer(), 60000);
            Task task = result as Task;
            if (task == null)
                return result;
            await task;
            return CompletedTaskResult(task);
        }

        private static object CompletedTaskResult(Task task)
        {
            task.GetAwaiter().GetResult();
            PropertyInfo resultProperty = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            return resultProperty == null ? null : resultProperty.GetValue(task, null);
        }

        private static object InvokeBoundCommand(CommandInfo command, object[] values, object server, int timeoutMs)
        {
            MethodInfo execute = server?.GetType().BaseType?.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "ExecuteCommand" && m.GetParameters().Length == 3);
            if (execute != null)
                return execute.Invoke(server, new object[] { command, values, timeoutMs > 0 ? timeoutMs : 60000 });
            return InvokeCommandMethod(command, values);
        }

        private static object InvokeCommandMethod(CommandInfo command, object[] values)
        {
            PropertyInfo target = typeof(CommandInfo).GetProperty("Target", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return command.Method.Invoke(target?.GetValue(command, null), values);
        }

        private static object FindFieldOrPropertyValue(object value, string name)
        {
            if (value == null) return null;
            FieldInfo field = value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(value);
            return GetPropertyValue(value, name);
        }

        private static object FindLivePipelineServer()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type startup = assembly.GetType("Unity.Pipeline.Editor.PipelineServerStartup", false);
                PropertyInfo property = startup?.GetProperty("Server", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                object server = property?.GetValue(null, null);
                if (server != null) return server;
            }
            return null;
        }

        private static async Task MonitorOfficialJob(Task job, object server, object registry, object record, string jobId)
        {
            MethodInfo requestCancel = registry.GetType().GetMethod("RequestCancel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            bool cancellationForwarded = false;
            while (!job.IsCompleted)
            {
                if (!cancellationForwarded && PiUnityBridge.IsPipelineJobCancellationRequested(jobId) && requestCancel != null)
                {
                    try
                    {
                        object id = FindFieldOrPropertyValue(record, "Id");
                        object[] cancelArgs = { id, null };
                        cancellationForwarded = (bool)requestCancel.Invoke(registry, cancelArgs);
                    }
                    catch { }
                }
                if (ReferenceEquals(FindFieldOrPropertyValue(registry, "CurrentRunning"), record))
                    ReportOfficialProgress(server, jobId);
                await Task.WhenAny(job, Task.Delay(50)).ConfigureAwait(false);
            }
            await job.ConfigureAwait(false);
        }

        private static JObject ReadOfficialJobStatus(object server, object record)
        {
            MethodInfo build = server.GetType().BaseType?.GetMethod("BuildJobResponse", BindingFlags.Instance | BindingFlags.NonPublic);
            if (build == null)
                throw new MissingMethodException("Unity Pipeline BuildJobResponse is unavailable.");
            string json = JsonConvert.SerializeObject(build.Invoke(server, new object[] { record, null }));
            return JObject.Parse(json);
        }

        private static void ReportOfficialProgress(object server, string jobId)
        {
            try
            {
                PropertyInfo progressProperty = server.GetType().BaseType?.GetProperty("Progress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object progress = progressProperty?.GetValue(server, null);
                object snapshot = progress?.GetType().GetProperty("Current", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(progress, null);
                if (snapshot == null) return;
                Type type = snapshot.GetType();
                if (!(bool)(type.GetField("HasReport")?.GetValue(snapshot) ?? false)) return;
                JObject value = new JObject
                {
                    ["title"] = ToJToken(type.GetField("Title")?.GetValue(snapshot)),
                    ["info"] = ToJToken(type.GetField("Info")?.GetValue(snapshot)),
                    ["current"] = ToJToken(type.GetField("Current")?.GetValue(snapshot)),
                    ["total"] = ToJToken(type.GetField("Total")?.GetValue(snapshot)),
                    ["progress"] = ToJToken(type.GetField("Progress01")?.GetValue(snapshot)),
                };
                PiUnityBridge.ReportPipelineJobProgress(jobId, value.ToString(Formatting.None));
            }
            catch { }
        }

        private static object GetPropertyValue(object value, string name)
        {
            return value?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value, null);
        }

        private static void SetPropertyValue(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null) throw new MissingMemberException(target.GetType().FullName, name);
            property.SetValue(target, value, null);
        }

        private static string OfficialJobError(JObject status)
        {
            return status.Value<string>("errorDetails") ?? status.Value<string>("error") ?? "Pipeline detached job failed.";
        }

        private static string OfficialJobErrorType(string state, JObject status)
        {
            if (string.Equals(state, "canceled", StringComparison.OrdinalIgnoreCase)) return "canceled";
            return string.IsNullOrEmpty(status.Value<string>("error")) ? "command_error" : status.Value<string>("error");
        }
#endif


#if PI_UNITY_PIPELINE
        internal static bool IsCommandVisible(CommandInfo command)
        {
            return command != null && command.RuntimeOnly == false && !ForbiddenCommands.Contains(command.Name);
        }

        internal static string GetMutability(CommandInfo command, out string source)
        {
            source = "default";
            if (command == null)
                return "write";
            PiCommandPolicyAttribute attribute = command.Method.GetCustomAttributes(typeof(PiCommandPolicyAttribute), true).FirstOrDefault() as PiCommandPolicyAttribute;
            if (attribute != null)
            {
                source = "attribute";
                return NormalizeMutability(attribute.Mutability);
            }
            if (SidecarMutability.TryGetValue(command.Name, out string sidecar))
            {
                source = "sidecar";
                return NormalizeMutability(sidecar);
            }
            return "write";
        }

        private static string NormalizeMutability(string value)
        {
            return value == "read" || value == "destructive" ? value : "write";
        }

        private static Dictionary<string, string> LoadSidecarMutability()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string path = Path.Combine(Directory.GetParent(UnityEngine.Application.dataPath).FullName, "Packages/com.pi.unity-harness/Editor/Pipeline/command-policy.json");
                if (!File.Exists(path)) return values;
                JObject root = JObject.Parse(File.ReadAllText(path));
                foreach (var property in root.Properties())
                    values[property.Name] = (string)property.Value["mutability"] ?? "write";
            }
            catch (Exception ex)
            {
                if (!s_sidecarWarningLogged)
                {
                    s_sidecarWarningLogged = true;
                    Debug.LogWarning("[PiUnityHarness] command-policy.json ignored: " + ex.Message);
                }
            }
            return values;
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

        internal static string BuildCommandNotFoundMessage(string commandName, IEnumerable<CommandInfo> commands)
        {
            string displayedName = TruncateSuggestionText(commandName ?? string.Empty);
            List<string> suggestions = GetCommandSuggestions(commandName, commands);
            string message = "No pipeline command named '" + displayedName + "'.";
            return suggestions.Count == 0
                ? message + " Use 'pi-unity list-commands' to see available commands."
                : message + " Did you mean: [" + string.Join(", ", suggestions.Select(TruncateSuggestionText)) + "]?";
        }

        internal static List<string> GetCommandSuggestions(string commandName, IEnumerable<CommandInfo> commands)
        {
            string query = NormalizeSuggestionText(commandName);
            if (query.Length == 0 || commands == null)
                return new List<string>();

            int distanceThreshold = Math.Min(4, Math.Max(2, query.Length / 6));
            return commands
                .Where(IsCommandVisible)
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.Ordinal)
                .Select(name =>
                {
                    string candidate = NormalizeSuggestionText(name);
                    bool candidateStartsWithQuery = candidate.StartsWith(query, StringComparison.Ordinal);
                    bool queryStartsWithCandidate = query.StartsWith(candidate, StringComparison.Ordinal);
                    int distance = BoundedEditDistance(query, candidate);
                    return new
                    {
                        Name = name,
                        PrefixRank = candidateStartsWithQuery ? 0 : (queryStartsWithCandidate ? 1 : 2),
                        Distance = distance,
                        Relevant = candidateStartsWithQuery || queryStartsWithCandidate || distance <= distanceThreshold,
                    };
                })
                .Where(s => s.Relevant)
                .OrderBy(s => s.PrefixRank)
                .ThenBy(s => s.Distance)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .Take(MaxCommandSuggestions)
                .Select(s => s.Name)
                .ToList();
        }

        private static string NormalizeSuggestionText(string value)
        {
            string text = value ?? string.Empty;
            if (text.Length > MaxSuggestionTextLength)
                text = text.Substring(0, MaxSuggestionTextLength);
            return text.ToLowerInvariant();
        }

        private static string TruncateSuggestionText(string value)
        {
            if (value.Length <= MaxSuggestionTextLength)
                return value;
            return value.Substring(0, MaxSuggestionTextLength) + "...";
        }

        private static int BoundedEditDistance(string left, string right)
        {
            int[] previous = new int[right.Length + 1];
            int[] current = new int[right.Length + 1];
            for (int j = 0; j <= right.Length; j++)
                previous[j] = j;

            for (int i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= right.Length; j++)
                {
                    int substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
                }
                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[right.Length];
        }

        private static JObject BuildCommandJson(CommandInfo command)
        {
            JToken schema = JValue.CreateNull();
            try
            {
                Type generatorType = typeof(CommandInfo).Assembly.GetType("Unity.Pipeline.Commands.JsonSchemaGenerator");
                MethodInfo generate = generatorType?.GetMethod("GenerateCommandSchema", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                string schemaText = generate?.Invoke(null, new object[] { command }) as string;
                if (!string.IsNullOrEmpty(schemaText))
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
                    ["jsonType"] = JsonTypeFor(parameter.ParameterType),
                    ["required"] = parameter.Required,
                    ["defaultValue"] = ToJToken(parameter.DefaultValue),
                });
            }

            JObject policy = BuildPolicy(command);
            if (schema is JObject schemaObject)
                schemaObject["x-command-metadata"] = policy.DeepClone();
            return new JObject
            {
                ["name"] = command.Name,
                ["description"] = command.Description,
                ["mainThreadRequired"] = command.MainThreadRequired,
                ["runtimeOnly"] = command.RuntimeOnly,
                ["policy"] = policy,
                ["schema"] = schema,
                ["parameters"] = parameters,
            };
        }

        private static string JsonTypeFor(Type type)
        {
            if (type == typeof(bool)) return "boolean";
            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)) return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
            if (type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) return "array";
            return "string";
        }

        private static JObject BuildPolicy(CommandInfo command)
        {
            string source;
            string mutability = GetMutability(command, out source);
            return new JObject
            {
                ["mutability"] = mutability,
                ["thread"] = command.MainThreadRequired ? "main" : "any",
                ["runtime"] = command.RuntimeOnly ? "runtime" : "editor",
                ["source"] = source,
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
