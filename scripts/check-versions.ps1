[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cargo = Get-Content (Join-Path $root 'native/Cargo.toml') -Raw
$package = Get-Content (Join-Path $root 'unity/com.pi.unity-harness/package.json') -Raw | ConvertFrom-Json
$cargoVersion = [regex]::Match($cargo, '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
if ($cargoVersion -ne $package.version) { throw "Cargo version $cargoVersion does not match Unity package version $($package.version)" }

$protocol = [regex]::Match((Get-Content (Join-Path $root 'native/src/lib.rs') -Raw), 'NATIVE_PROTOCOL_VERSION:\s*i32\s*=\s*(\d+)').Groups[1].Value
$dll = Join-Path $root 'unity/com.pi.unity-harness/Editor/Plugins/x86_64/pi_unity_harness_native.dll'
if (-not (Test-Path $dll)) { throw "Native DLL not found: $dll" }
$bytes = [IO.File]::ReadAllBytes($dll)
$text = [Text.Encoding]::ASCII.GetString($bytes)
$match = [regex]::Match($text, 'PIUH_BUILD_INFO:(\{[^\x00]+\})')
if (-not $match.Success) { throw 'PIUH_BUILD_INFO marker not found in native DLL' }
$info = $match.Groups[1].Value | ConvertFrom-Json
if ($info.crate -ne $cargoVersion) { throw "DLL crate version $($info.crate) does not match Cargo version $cargoVersion" }
if ([int]$info.protocol -ne [int]$protocol) { throw "DLL protocol $($info.protocol) does not match native protocol $protocol" }
Write-Host "versions OK: crate=$cargoVersion protocol=$protocol"
