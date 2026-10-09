using System;
using System.IO;

namespace Pi.UnityHarness.Editor.Capabilities.Vision
{
    /// <summary>
    /// 输出路径 profile。有 profile 时不与其他默认目录合并。
    /// CLI Base64 的 Temp/PiUnityHarness/Captures 只在 native presentation，不属于本服务。
    /// </summary>
    internal enum CapturePathProfile : byte
    {
        Harness = 0,
        PlaytestObserve = 1,
        PlaytestAfter = 2,
        LegacyCamera = 3
    }

    /// <summary>
    /// 单一路径解析。显式 projectRoot，不读 Application.dataPath。
    /// </summary>
    internal static class CapturePathService
    {
        public static string Resolve(CapturePathProfile profile, string requested, string projectRoot, DateTime utcNow, bool createDirectory)
        {
            string resolved = ResolveWithoutCreate(profile, requested, projectRoot, utcNow);
            if (createDirectory)
            {
                string dir = Path.GetDirectoryName(resolved);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }
            return resolved;
        }

        public static string ResolveWithoutCreate(CapturePathProfile profile, string requested, string projectRoot, DateTime utcNow)
        {
            if (profile == CapturePathProfile.LegacyCamera)
                return ResolveLegacyCamera(requested, projectRoot, utcNow);

            if (!string.IsNullOrWhiteSpace(requested))
            {
                string path = requested;
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(projectRoot, path);
                return Path.GetFullPath(path);
            }

            if (profile == CapturePathProfile.Harness)
            {
                return Path.Combine(
                    projectRoot, "Temp", "Harness", "vision",
                    utcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png");
            }

            string fallback = profile == CapturePathProfile.PlaytestAfter
                ? "Library/PiUnityHarness/playtest/after"
                : "Library/PiUnityHarness/playtest/observe";
            return Path.Combine(projectRoot, fallback);
        }

        private static string ResolveLegacyCamera(string requested, string projectRoot, DateTime utcNow)
        {
            string path = requested;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(
                    projectRoot, "Temp", "PiUnityHarness", "Screenshots",
                    "capture-" + utcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png");
            }

            // 非空相对 path 保持 Path.GetFullPath，基准是进程 cwd，不是项目根。
            return Path.GetFullPath(path);
        }
    }
}
