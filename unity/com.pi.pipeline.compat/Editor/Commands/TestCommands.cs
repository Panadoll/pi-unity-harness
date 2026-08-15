using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Pipeline.Commands;
using Unity.Pipeline.Editor.Testing;
using Unity.Pipeline.Models;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.Pipeline.Editor.Commands
{
    /// <summary>
    /// Commands for running Unity tests programmatically
    /// </summary>
    public static class TestCommands
    {
        /// <summary>
        /// Execute Unity tests with filtering options.
        /// Synchronous by default (blocks until completion), async mode available with --async-tests flag.
        /// </summary>
        [CliCommand("run_tests", "Execute Unity tests with filtering options", MainThreadRequired = true)]
        public static async Task<TestExecutionResponse> RunTests(
            [CliArg("mode", "Test mode: all, editor, playmode (default: all)")] string mode = "all",
            [CliArg("filter", "Test name filter pattern (case-insensitive partial match)")] string filter = "",
            [CliArg("filter_type", "Filter type: testName, assembly, category (default: testName)")] string filterType = "testName",
            [CliArg("include_explicit", "Include tests marked with [Explicit] attribute")] bool includeExplicit = false,
            [CliArg("async_tests", "Run asynchronously - return immediately, poll /test-status for results")] bool asyncTests = false,
            [CliArg("timeout", "Test execution timeout in seconds (default: 300)")] int timeout = 300,
            [CliArg("dirty_action", "Dirty scene policy before running tests: save / discard / abort (default abort)")] string dirtyAction = "abort")
        {
            // 启动测试前应用 dirtyAction 脏场景策略（与 harness 的 DirtyScenePolicy 语义一致；
            // compat 为独立 fork，无法引用 harness 程序集，此处实现最小等价逻辑）。
            string policyError = ApplyDirtyScenePolicy(dirtyAction);
            if (policyError != null)
            {
                return new TestExecutionResponse
                {
                    Success = false,
                    Command = "run_tests",
                    Error = policyError,
                    ExecutedAt = DateTime.UtcNow
                };
            }

            // Return the full structured response (including failures and the Error field on a
            // failed run) rather than throwing: the server now awaits this Task and serializes the
            // unwrapped response, so the client receives complete, structured reporting. Throwing
            // would only surface an opaque message and discard the per-test results.
            return await PipelineTestRunner.ExecuteTestsAsync(
                mode,
                filter,
                filterType,
                includeExplicit,
                asyncTests,
                timeout);
        }

        /// <summary>
        /// List all available tests without executing any of them.
        /// Enumerates the test tree via TestRunnerApi.RetrieveTestList for the requested mode(s).
        /// </summary>
        [CliCommand("list_tests", "List all available tests (EditMode and/or PlayMode) without running them", MainThreadRequired = true)]
        public static async Task<TestListResponse> ListTests(
            [CliArg("mode", "Test mode: all, editor, playmode (default: all)")] string mode = "all")
        {
            try
            {
                if (!TryGetModes(mode, out var modes))
                {
                    return new TestListResponse
                    {
                        Success = false,
                        Command = "list_tests",
                        Error = $"Invalid mode '{mode}'. Use 'editor', 'playmode', or 'all'.",
                        ExecutedAt = DateTime.UtcNow
                    };
                }

                var tests = new List<TestListItem>();
                foreach (var testMode in modes)
                {
                    tests.AddRange(await RetrieveTestsAsync(testMode));
                }

                return new TestListResponse
                {
                    Success = true,
                    Command = "list_tests",
                    Mode = modes.Count == 2 ? "All" : modes[0].ToString(),
                    Count = tests.Count,
                    Tests = tests,
                    Message = $"Found {tests.Count} test(s).",
                    ExecutedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TestCommands] list_tests failed: {ex.Message}");
                return new TestListResponse
                {
                    Success = false,
                    Command = "list_tests",
                    Error = "Failed to list tests",
                    ErrorDetails = ex.ToString(),
                    ExecutedAt = DateTime.UtcNow
                };
            }
        }

        /// <summary>
        /// Get current test status for async test execution
        /// </summary>
        [CliCommand("test_status", "Get status of running async test execution", MainThreadRequired = false)]
        public static string GetTestStatus()
        {
            var status = PipelineTestRunner.GetTestStatus();
            return status ?? "{\"status\":\"no_tests\",\"message\":\"No test run in progress\"}";
        }

        /// <summary>
        /// Cancel running test execution
        /// </summary>
        [CliCommand("cancel_tests", "Cancel running test execution", MainThreadRequired = true)]
        public static object CancelTests()
        {
            return PipelineTestRunner.CancelTests();
        }

        /// <summary>
        /// 解析 dirtyAction 参数并对当前所有已打开场景应用策略（run_tests 专用）。
        /// 语义与 harness DirtyScenePolicy 一致：abort=有脏场景即报错（默认）、save=先保存
        /// （untitled 场景无法保存则报错）、discard=静默丢弃后继续。
        /// 返回 null 表示可继续；返回非 null 为错误信息。
        /// </summary>
        private static string ApplyDirtyScenePolicy(string dirtyAction)
        {
            if (string.IsNullOrEmpty(dirtyAction))
                dirtyAction = "abort";
            dirtyAction = dirtyAction.Trim().ToLowerInvariant();
            if (dirtyAction != "abort" && dirtyAction != "save" && dirtyAction != "discard")
                return $"dirtyAction \"{dirtyAction}\" 无效（run_tests）。可选值：save / discard / abort（默认 abort）。";

            var openScenes = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
                openScenes.Add(SceneManager.GetSceneAt(i));
            var dirtyScenes = openScenes.Where(s => s.IsValid() && s.isDirty).ToList();
            if (dirtyScenes.Count == 0)
                return null;

            if (dirtyAction == "discard")
                return null; // 脚本化操作会静默丢弃未保存修改，不弹模态对话框

            if (dirtyAction == "abort")
            {
                var names = string.Join(", ", dirtyScenes.Select(s => "场景 '" + s.name + "'"));
                return $"{names} 有未保存修改，abort 策略拒绝执行 run_tests。请先保存场景，或指定 dirtyAction=save / discard。";
            }

            // save：先保存脏场景再继续；untitled 场景无法保存，按 abort 处理并报错
            var untitled = dirtyScenes.Where(s => string.IsNullOrEmpty(s.path)).ToList();
            if (untitled.Count > 0)
            {
                var names = string.Join(", ", untitled.Select(s => "场景 '" + s.name + "'（untitled）"));
                return $"无法以 save 策略处理 run_tests：{names} 从未保存（无路径）。请先保存或改用 discard。";
            }

            foreach (var scene in dirtyScenes)
            {
                if (!EditorSceneManager.SaveScene(scene))
                    return $"保存场景失败（run_tests）：{scene.name} ({scene.path})。";
            }
            return null;
        }

        /// <summary>
        /// Resolve a mode string to the concrete TestMode(s) to enumerate.
        /// </summary>
        private static bool TryGetModes(string mode, out List<TestMode> modes)
        {
            switch (mode?.ToLowerInvariant())
            {
                case null:
                case "":
                case "all":
                    modes = new List<TestMode> { TestMode.EditMode, TestMode.PlayMode };
                    return true;
                case "editor":
                case "editmode":
                    modes = new List<TestMode> { TestMode.EditMode };
                    return true;
                case "playmode":
                case "play":
                    modes = new List<TestMode> { TestMode.PlayMode };
                    return true;
                default:
                    modes = null;
                    return false;
            }
        }

        /// <summary>
        /// Retrieve the test list for a single mode. RetrieveTestList enumerates the test tree (no
        /// execution) and invokes the callback on the main thread; we bridge it to a Task.
        /// </summary>
        private static Task<List<TestListItem>> RetrieveTestsAsync(TestMode testMode)
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var tcs = new TaskCompletionSource<List<TestListItem>>();

            api.RetrieveTestList(testMode, root =>
            {
                try
                {
                    var items = new List<TestListItem>();
                    CollectLeafTests(root, testMode, items);
                    tcs.SetResult(items);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        /// <summary>
        /// Walk the test tree and collect leaf tests (actual test methods, not suites/fixtures).
        /// </summary>
        private static void CollectLeafTests(ITestAdaptor test, TestMode testMode, List<TestListItem> items)
        {
            if (test == null)
                return;

            if (!test.HasChildren && !test.IsSuite)
            {
                items.Add(new TestListItem
                {
                    FullName = test.FullName,
                    Mode = testMode.ToString(),
                    Assembly = test.TypeInfo?.Assembly?.GetName()?.Name,
                    Categories = test.Categories?.ToList() ?? new List<string>(),
                    Explicit = test.RunState == RunState.Explicit
                });
                return;
            }

            if (test.Children != null)
            {
                foreach (var child in test.Children)
                    CollectLeafTests(child, testMode, items);
            }
        }
    }

    /// <summary>
    /// Response for the list_tests command: the available tests, without execution results.
    /// </summary>
    [Serializable]
    public class TestListResponse : CommandExecutionResponse
    {
        public string Mode { get; set; }       // EditMode, PlayMode, or All
        public int Count { get; set; }
        public List<TestListItem> Tests { get; set; } = new List<TestListItem>();
    }

    /// <summary>
    /// A single available test (no run state / outcome — this is a listing, not a result).
    /// </summary>
    [Serializable]
    public class TestListItem
    {
        public string FullName { get; set; }
        public string Mode { get; set; }
        public string Assembly { get; set; }
        public List<string> Categories { get; set; } = new List<string>();
        public bool Explicit { get; set; }
    }
}
