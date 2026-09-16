using System.Runtime.CompilerServices;

// Lets the runtime test assembly's PipelineClient read a server's internal Token to authenticate.
[assembly: InternalsVisibleTo("Unity.Pipeline.Tests.Runtime")]
// Lets the EditMode test assembly unit-test internal runtime utilities (e.g. MaxLengthStream).
[assembly: InternalsVisibleTo("Unity.Pipeline.Tests.Editor")]
// Lets the Editor assembly feed CliProgress's internal ambient mirror (EditorProgressMirror)
// and consume internal interpreter glue (e.g. IlInterpreterHostBindings, which the hot-reload
// link.xml generator derives its preservation set from).
[assembly: InternalsVisibleTo("Unity.Pipeline.Editor")]

// Lets the pi-unity harness bridge (shipped as a separate package) drive the pipeline test runner
// and read internal response models such as TestExecutionResponse. The harness ships its pipeline
// integration behind #if PI_UNITY_PIPELINE, so this is only exercised when the package is present.
[assembly: InternalsVisibleTo("Pi.UnityHarness.Editor")]
[assembly: InternalsVisibleTo("Pi.UnityHarness.Editor.Tests")]
