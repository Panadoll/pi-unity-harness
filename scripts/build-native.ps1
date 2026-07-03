$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $root 'native'
$unityPluginDir = Join-Path $root 'unity/com.pi.unity-harness/Editor/Plugins/x86_64'

Push-Location $nativeDir
cargo build --release
Pop-Location

New-Item -ItemType Directory -Force -Path $unityPluginDir | Out-Null
Copy-Item -Force \
  (Join-Path $nativeDir 'target/release/pi_unity_harness_native.dll') \
  (Join-Path $unityPluginDir 'pi_unity_harness_native.dll')

Write-Host 'native dll copied to Unity package plugin directory'
