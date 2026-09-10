using io.github.hatayama.UnityCliLoop.FirstPartyTools;
using io.github.hatayama.UnityCliLoop.ToolContracts;
using UnityEditor;

namespace Pi.UnityHarness.Editor
{
    [InitializeOnLoad]
    internal static class PiUloopVendorBootstrap
    {
        static PiUloopVendorBootstrap()
        {
            MainThreadSwitcher.RegisterService(new EditorUpdateMainThreadDispatcher());
            MainThreadSwitcher.InitializeForEditorStartup();
            HotReloadEditorStartup.Initialize();
            PausePointEditorStartup.Initialize();
            RecordVideoEditorStartup.Initialize();
            EditorFrameWaiter.InitializeForEditorStartup();
        }
    }
}
