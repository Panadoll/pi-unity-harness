$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $root 'native'
$unityPluginDir = Join-Path $root 'unity/com.pi.unity-harness/Editor/Plugins/x86_64'

Push-Location $nativeDir
try {
  cargo build --release
  if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE" }
}
finally {
  Pop-Location
}

New-Item -ItemType Directory -Force -Path $unityPluginDir | Out-Null
$dllSource = Join-Path $nativeDir 'target/release/pi_unity_harness_native.dll'
$dllDest = Join-Path $unityPluginDir 'pi_unity_harness_native.dll'
if (-not (Test-Path -LiteralPath $dllSource)) {
  throw "Native DLL not found after build: $dllSource"
}
Copy-Item -Force $dllSource $dllDest

Write-Host 'native dll copied to Unity package plugin directory'
