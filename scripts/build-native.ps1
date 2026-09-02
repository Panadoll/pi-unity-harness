[CmdletBinding()]
param(
  [switch]$Strict
)

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

try {
  Copy-Item -Force $dllSource $dllDest
  Write-Host 'native dll copied to Unity package plugin directory'
} catch {
  if ($Strict -or -not (Test-Path -LiteralPath $dllDest)) {
    throw "Failed to copy DLL to Unity plugin directory: $_"
  } else {
    Write-Warning "Could not update DLL in Unity plugin directory (currently locked by running Unity Editor). Close Unity Editor and rerun build to refresh plugin DLL."
  }
}

$binDir = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

$cliSource = Join-Path $nativeDir 'target/release/pi-unity.exe'
if (Test-Path -LiteralPath $cliSource) {
  Copy-Item -Force $cliSource (Join-Path $binDir 'pi-unity.exe')
  Write-Host 'pi-unity CLI binary copied to bin/'
}
