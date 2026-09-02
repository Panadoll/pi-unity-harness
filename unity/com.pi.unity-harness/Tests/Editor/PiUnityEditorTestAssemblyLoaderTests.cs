using System;
using System.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Tests
{
    public sealed class PiUnityEditorTestAssemblyLoaderTests
    {
        [Test]
        public void TryLoadEditorTestAssemblies_LoadsThisEditorTestAssembly()
        {
            var names = PiUnityEditorTestAssemblyLoader.TryLoadEditorTestAssemblies();
            Assert.That(names, Does.Contain("Pi.UnityHarness.Editor.Tests"));
            Assert.That(
                AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name),
                Does.Contain("Pi.UnityHarness.Editor.Tests"));
        }
    }
}
