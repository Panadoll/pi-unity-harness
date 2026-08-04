// 2022 compat stubs: real HotReload/Roslyn stack disabled on this embedded fork.
using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Pipeline.Threading;

namespace Unity.Pipeline.HotReload
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HotReloadAttribute : Attribute
    {
        public bool RequireMainThread;
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HotReloadWithOverridesAttribute : Attribute
    {
        public bool RequireMainThread;
    }

    public static class HotReloadRegistry
    {
        public static Dispatcher Dispatcher { get; set; }
        public static IList<string> AllowedReloadRoots { get; set; }

        public static void RegisterReloadableMethod(MethodInfo method, HotReloadWithOverridesAttribute attr)
        {
        }
    }
}
