using System;
using System.Collections.Generic;
using System.Reflection;

namespace Pi.UnityHarness.Editor.Capabilities.Input
{
    public static partial class HarnessInput
    {
        private static readonly Dictionary<string, MethodInfo> s_backendMethods = new Dictionary<string, MethodInfo>();
        private static PropertyInfo s_lastDispatchResultProperty;

        private static string InvokeBackend(string methodName, object[] args, Func<string> success)
        {
            try
            {
                InvokeBackendVoid(methodName, args);
                return success();
            }
            catch (Exception ex)
            {
                return ExceptionToJson(ex);
            }
        }

        private static void InvokeBackendVoid(string methodName, params object[] args)
        {
            var method = GetBackendMethod(methodName);
            if (method == null)
                throw new InvalidOperationException("Input backend method not found: " + methodName);

            try
            {
                method.Invoke(null, args);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private static bool TryInvokeBackendVoid(string methodName, out string errorJson, params object[] args)
        {
            try
            {
                InvokeBackendVoid(methodName, args);
                errorJson = null;
                return true;
            }
            catch (Exception ex)
            {
                errorJson = ExceptionToJson(ex);
                return false;
            }
        }

        private static bool InvokeBackendBool(string methodName)
        {
            var method = GetBackendMethod(methodName);
            if (method == null)
                return false;

            try
            {
                object result = method.Invoke(null, null);
                return result is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        private static MethodInfo GetBackendMethod(string methodName)
        {
            if (s_backendMethods.TryGetValue(methodName, out var cached))
                return cached;

            var method = BackendType?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            if (method != null)
                s_backendMethods[methodName] = method;
            return method;
        }

        private static PropertyInfo GetLastDispatchResultProperty()
        {
            if (s_lastDispatchResultProperty != null)
                return s_lastDispatchResultProperty;

            s_lastDispatchResultProperty = BackendType?.GetProperty("LastDispatchResult",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return s_lastDispatchResultProperty;
        }

        /// <summary>Read the most recent dispatch result from the backend via reflection.</summary>
        private static string GetDispatchResultJson()
        {
            try
            {
                var prop = GetLastDispatchResultProperty();
                if (prop == null)
                    return InputJson.Object()
                        .Bool("attempted", false)
                        .String("error", "dispatch_result_unavailable")
                        .ToString();

                object result = prop.GetValue(null);
                if (result == null)
                    return InputJson.Object()
                        .Bool("attempted", false)
                        .String("error", "dispatch_result_unavailable")
                        .ToString();

                // Read fields via reflection to avoid cross-assembly struct dependency
                var resultType = result.GetType();
                bool attempted = (bool)resultType.GetField("Attempted").GetValue(result);
                bool dispatched = (bool)resultType.GetField("Dispatched").GetValue(result);
                string topHit = resultType.GetField("TopHitName").GetValue(result) as string;
                string handler = resultType.GetField("HandlerName").GetValue(result) as string;
                string error = resultType.GetField("Error").GetValue(result) as string;

                var json = InputJson.Object()
                    .Bool("attempted", attempted)
                    .Bool("dispatched", dispatched);
                if (topHit != null)
                    json.String("top_hit", topHit);
                if (handler != null)
                    json.String("handler", handler);
                if (error != null)
                    json.String("error", error);
                return json.ToString();
            }
            catch (Exception ex)
            {
                return InputJson.Object()
                    .Bool("attempted", false)
                    .String("error", ex.Message)
                    .ToString();
            }
        }

        private static string BuildPointerDispatchResult(string action, float x, float y,
            string button, string dispatchJson, string error)
        {
            bool dispatched = IsDispatchSuccessful(dispatchJson);
            var json = InputJson.Object()
                .String("status", dispatched ? "succeeded" : "failed")
                .String("action", action)
                .Number("x", x)
                .Number("y", y)
                .String("button", button);

            if (!dispatched)
                json.String("error", error).String("error_type", "runtime");

            return json.Raw("dispatch", dispatchJson).ToString();
        }

        private static string BuildScrollDispatchResult(float x, float y, float deltaX, float deltaY,
            string dispatchJson)
        {
            bool dispatched = IsDispatchSuccessful(dispatchJson);
            var json = InputJson.Object()
                .String("status", dispatched ? "succeeded" : "failed")
                .String("action", "scroll")
                .Number("x", x)
                .Number("y", y)
                .Number("delta_x", deltaX)
                .Number("delta_y", deltaY);

            if (!dispatched)
                json.String("error", "EventSystem dispatch did not reach a scroll handler")
                    .String("error_type", "runtime");

            return json.Raw("dispatch", dispatchJson).ToString();
        }

        private static bool IsDispatchSuccessful(string dispatchJson)
        {
            return dispatchJson.Contains("\"dispatched\":true");
        }

        private static void ReleaseMouseButtonBestEffort(int button)
        {
            try { InvokeBackendVoid("ReleaseMouseButton", button); }
            catch { /* best-effort cleanup */ }
        }

        private static string ExceptionToJson(Exception ex)
        {
            if (ex is ArgumentException)
                return Fail(ex.Message, "usage");
            if (ex is InvalidOperationException)
                return Fail(ex.Message, "not_supported");
            return Fail("Input operation failed: " + ex.Message, "runtime");
        }

        private static bool TryParseMouseButton(string button, out int parsedButton, out string error)
        {
            parsedButton = -1;
            if (string.IsNullOrWhiteSpace(button))
            {
                error = "Mouse button is required. Use left, right, middle, forward, back, or 0-4.";
                return false;
            }

            switch (button.Trim().ToLowerInvariant())
            {
                case "left":
                case "0":
                    parsedButton = 0;
                    break;
                case "right":
                case "1":
                    parsedButton = 1;
                    break;
                case "middle":
                case "2":
                    parsedButton = 2;
                    break;
                case "forward":
                case "3":
                    parsedButton = 3;
                    break;
                case "back":
                case "4":
                    parsedButton = 4;
                    break;
                default:
                    if (!int.TryParse(button, out parsedButton) || parsedButton < 0 || parsedButton > 4)
                    {
                        error = "Invalid mouse button: " + button;
                        return false;
                    }
                    break;
            }

            error = null;
            return true;
        }

        private static bool TryValidateFinite(string fieldName, float value, out string error)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                error = fieldName + " must be a finite number.";
                return false;
            }

            error = null;
            return true;
        }

        private static string NormalizeMouseButton(int button)
        {
            switch (button)
            {
                case 0: return "left";
                case 1: return "right";
                case 2: return "middle";
                case 3: return "forward";
                case 4: return "back";
                default: return button.ToString();
            }
        }

        private static string Success(string action, string fieldName, string fieldValue)
            => InputJson.Object()
                .String("status", "succeeded")
                .String("action", action)
                .String(fieldName, fieldValue)
                .ToString();

        private static string Fail(string error, string errorType)
            => InputJson.Object()
                .String("status", "failed")
                .String("error", error)
                .String("error_type", errorType)
                .ToString();
    }
}
