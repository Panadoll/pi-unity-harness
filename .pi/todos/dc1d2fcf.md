{
  "id": "dc1d2fcf",
  "title": "提取重复的 JsonString/ErrorJson/EscapeJson/SuccessJson 辅助函数",
  "tags": [
    "refactor",
    "maintainability"
  ],
  "status": "complete",
  "created_at": "2026-07-04T06:35:36.045Z"
}

JsonString、ErrorJson、SuccessJson、EscapeJson 在 PiUnityPipelineCommandExecutor.cs、PiUnityBridge.cs、PiUnityTestCoordinator.cs 三个文件中重复定义，同属 Pi.UnityHarness.Editor asmdef。提取为共享 internal static 辅助类。
