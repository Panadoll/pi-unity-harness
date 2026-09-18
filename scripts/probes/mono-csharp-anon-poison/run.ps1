[CmdletBinding()]
param(
  [string]$UnityEditorData = 'F:\Program Files\Unity 2022.3.14f1\Editor\Data',
  [string]$MonoCSharpAssembly
)

# 复现 Mono.CSharp Evaluator 的匿名类型污染，并验证“清掉 module.anonymous_types 缓存条目”
# 可以在不重建实例、不丢持久变量的前提下恢复。
$ErrorActionPreference = 'Stop'
$probeDir = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $probeDir)
$mono = Join-Path $UnityEditorData 'MonoBleedingEdge/bin/mono.exe'
$csc = Join-Path $UnityEditorData 'MonoBleedingEdge/lib/mono/4.5/csc.exe'
if (-not $MonoCSharpAssembly) {
  $MonoCSharpAssembly = Join-Path $UnityEditorData 'MonoBleedingEdge/lib/mono/4.5/Mono.CSharp.dll'
}
foreach ($required in @($mono, $csc, $MonoCSharpAssembly)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing file: $required" }
}
$UnityEditorData = (Resolve-Path -LiteralPath $UnityEditorData).Path
$MonoCSharpAssembly = (Resolve-Path -LiteralPath $MonoCSharpAssembly).Path

$runDirectory = Join-Path ([IO.Path]::GetTempPath()) ('pi-anon-poison-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$outPath = Join-Path $runDirectory 'PoisonMatrixProbe.exe'

Write-Host "Isolated artifacts: $runDirectory"
& $mono $csc -nologo -target:exe "-out:$outPath" "-reference:$MonoCSharpAssembly" (Join-Path $probeDir 'PoisonMatrixProbe.cs')
if ($LASTEXITCODE -ne 0) { throw "Probe compilation failed: $LASTEXITCODE" }

& $mono $outPath $MonoCSharpAssembly
$exit = $LASTEXITCODE
Write-Host "Probe exit code: $exit"
# 探针只做观察，不作为通过/失败门槛；污染与修复结果需要人工比对输出。
exit 0
