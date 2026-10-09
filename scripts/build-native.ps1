[CmdletBinding()]
param(
  [switch]$Strict
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

$root = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $root 'native'
$unityPluginDir = Join-Path $root 'unity/com.pi.unity-harness/Editor/Plugins/x86_64'

# 把本机路径（用户目录、仓库根）映射成通用占位，避免 panic 源路径等把用户名写进 DLL/EXE。
$pathRemaps = @(
  "--remap-path-prefix=$env:USERPROFILE=~",
  "--remap-path-prefix=$root=."
) -join ' '
$previousRustflags = $env:RUSTFLAGS
$env:RUSTFLAGS = (@($previousRustflags, $pathRemaps) | Where-Object { $_ }) -join ' '

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
$cliDest = Join-Path $binDir 'pi-unity.exe'
if (Test-Path -LiteralPath $cliSource) {
  Get-ChildItem -LiteralPath $binDir -Filter 'pi-unity.exe.old-*' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
  try {
    Copy-Item -Force $cliSource $cliDest
  } catch {
    # 常驻的 pi-unity mux 会锁住 exe；Windows 允许改名正在运行的 exe，已在跑的进程继续用旧映像。
    Rename-Item -LiteralPath $cliDest -NewName ("pi-unity.exe.old-{0}" -f [DateTime]::UtcNow.Ticks)
    Copy-Item -Force $cliSource $cliDest
    Write-Warning 'bin/pi-unity.exe was in use; old binary renamed. Running mux processes keep the old build until restarted.'
  }
  Write-Host 'pi-unity CLI binary copied to bin/'
}
