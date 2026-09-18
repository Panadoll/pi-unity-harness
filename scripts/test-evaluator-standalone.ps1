[CmdletBinding()]
param(
  [string]$UnityEditorData = 'F:\Program Files\Unity 2022.3.14f1\Editor\Data',
  [string]$MonoCSharpAssembly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$mono = Join-Path $UnityEditorData 'MonoBleedingEdge/bin/mono.exe'
$csc = Join-Path $UnityEditorData 'MonoBleedingEdge/lib/mono/4.5/csc.exe'
if (-not $MonoCSharpAssembly) {
  foreach ($relative in @(
    'MonoBleedingEdge/lib/mono/4.5/Mono.CSharp.dll',
    'Resources/Scripting/MonoBleedingEdge/lib/mono/4.5/Mono.CSharp.dll'
  )) {
    $candidate = Join-Path $UnityEditorData $relative
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
      $MonoCSharpAssembly = $candidate
      break
    }
  }
}
if (-not $MonoCSharpAssembly) { throw 'Mono.CSharp runtime assembly not found; supply -MonoCSharpAssembly.' }
$sources = @(
  'unity/com.pi.unity-harness/Editor/PiUnityEvaluator.cs',
  'unity/com.pi.unity-harness/Editor/PiUnityEvaluator.CodeScanner.cs',
  'unity/com.pi.unity-harness/Editor/PiUnityEvaluator.Lint.cs',
  'unity/com.pi.unity-harness/Tests/Editor/PiUnityEvaluatorExecutionTests.cs'
) | ForEach-Object { Join-Path $repoRoot $_ }
foreach ($required in @($mono, $csc, $MonoCSharpAssembly) + $sources) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing file: $required" }
}
$UnityEditorData = (Resolve-Path -LiteralPath $UnityEditorData).Path
$MonoCSharpAssembly = (Resolve-Path -LiteralPath $MonoCSharpAssembly).Path

# Unique OS temp directory only. Retain all build artifacts for inspection; no cleanup/deletion.
$runDirectory = Join-Path ([IO.Path]::GetTempPath()) ('pi-evaluator-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$stubPath = Join-Path $runDirectory 'UnityHostStubs.cs'
$outputPath = Join-Path $runDirectory 'PiEvaluatorRegressions.exe'
@'
namespace UnityEditor
{
    public static class EditorApplication
    {
        public static string applicationContentsPath;
    }
}
namespace UnityEngine
{
    public class Object { }

    public static class Debug
    {
        public static void Log(object message) { System.Console.WriteLine(message); }
    }
}
'@ | Set-Content -LiteralPath $stubPath -Encoding UTF8

Write-Host "Isolated artifacts: $runDirectory"
Write-Host "Compiling actual evaluator with $mono and $csc"
& $mono $csc /nologo /target:exe /langversion:latest /define:PI_EVALUATOR_STANDALONE `
  "/out:$outputPath" "/reference:$MonoCSharpAssembly" @sources $stubPath
$compileExit = $LASTEXITCODE
Write-Host "Compile exit code: $compileExit"
if ($compileExit -ne 0) { throw "Evaluator compilation failed: $compileExit" }

& $mono $outputPath $UnityEditorData $MonoCSharpAssembly
$testExit = $LASTEXITCODE
Write-Host "Regression exit code: $testExit"
if ($testExit -ne 0) { throw "Evaluator regressions failed: $testExit" }