#if PI_UNITY_PIPELINE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    /// <summary>
    /// 脏场景处理策略：scene_open / scene_create / scene_unload 与 run_tests 共用的
    /// dirtyAction 参数语义（参考 UniCli DirtyScenePolicy）。
    /// 脚本化 EditorSceneManager 操作会静默丢弃未保存修改（2022.3/6000.x 已验证），
    /// 因此 discard 直接放行，不触发模态对话框。
    /// </summary>
    public enum DirtySceneAction
    {
        /// <summary>有脏场景即报错（默认）</summary>
        Abort,
        /// <summary>先保存脏场景再继续；untitled 场景无法保存，按 abort 报错</summary>
        Save,
        /// <summary>丢弃未保存修改后继续</summary>
        Discard,
    }

    /// <summary>
    /// 共享的脏场景策略判断。命令在修改场景状态前调用 <see cref="Apply"/>，
    /// 返回非 null 即表示策略拒绝继续（错误信息），命令应直接返回错误。
    /// </summary>
    public static class DirtyScenePolicy
    {
        /// <summary>
        /// 解析 dirtyAction 字符串参数。null/空/非法值回退到 Abort 并给出错误信息。
        /// </summary>
        public static DirtySceneAction Parse(string value, string commandName, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(value))
                return DirtySceneAction.Abort;
            if (string.Equals(value, "abort", StringComparison.OrdinalIgnoreCase))
                return DirtySceneAction.Abort;
            if (string.Equals(value, "save", StringComparison.OrdinalIgnoreCase))
                return DirtySceneAction.Save;
            if (string.Equals(value, "discard", StringComparison.OrdinalIgnoreCase))
                return DirtySceneAction.Discard;

            error = $"dirtyAction \"{value}\" 无效（{commandName}）。可选值：save / discard / abort（默认 abort）。";
            return DirtySceneAction.Abort;
        }

        /// <summary>
        /// 对受影响场景应用策略。返回 null 表示可继续；返回非 null 为错误信息（命令应中止）。
        /// </summary>
        public static string Apply(DirtySceneAction action, IReadOnlyList<Scene> affectedScenes, string commandName)
        {
            var dirtyScenes = affectedScenes
                .Where(s => s.IsValid() && s.isDirty)
                .ToList();
            if (dirtyScenes.Count == 0)
                return null;

            switch (action)
            {
                case DirtySceneAction.Abort:
                    return BuildAbortError(dirtyScenes, commandName);

                case DirtySceneAction.Save:
                    return SaveDirtyScenes(dirtyScenes, commandName);

                case DirtySceneAction.Discard:
                    // 脚本化操作会静默丢弃未保存修改，直接放行，不弹模态对话框。
                    return null;

                default:
                    return $"未知 dirtyAction（{commandName}）。";
            }
        }

        private static string BuildAbortError(List<Scene> dirtyScenes, string commandName)
        {
            return $"{DescribeScenes(dirtyScenes)} 有未保存修改，abort 策略拒绝执行 {commandName}。" +
                   "请先保存场景，或指定 dirtyAction=save / discard。";
        }

        private static string SaveDirtyScenes(List<Scene> dirtyScenes, string commandName)
        {
            // untitled（从未保存）场景没有保存路径，直接保存会弹模态文件对话框（挂起 headless）；
            // 按 abort 处理并报错。
            var untitled = dirtyScenes
                .Where(s => string.IsNullOrEmpty(s.path))
                .ToList();
            if (untitled.Count > 0)
            {
                return $"无法以 save 策略处理 {commandName}：{DescribeScenes(untitled)} 从未保存（无路径）。" +
                       "请先用 scene_save 指定路径保存，或改用 discard。";
            }

            foreach (var scene in dirtyScenes)
            {
                if (!EditorSceneManager.SaveScene(scene))
                    return $"保存场景失败（{commandName}）：{scene.name} ({scene.path})。";
            }
            return null;
        }

        private static string DescribeScenes(List<Scene> scenes)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < scenes.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                var scene = scenes[i];
                sb.Append("场景 '").Append(scene.name).Append('\'');
                if (string.IsNullOrEmpty(scene.path))
                    sb.Append("（untitled）");
            }
            return sb.ToString();
        }
    }
}
#endif
