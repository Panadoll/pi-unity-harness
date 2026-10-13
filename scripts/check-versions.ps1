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

$rebuildHint = 'close every Unity Editor using com.pi.unity-harness, run scripts/build-native.ps1, then commit the plugin DLL'
$abi = [regex]::Match((Get-Content (Join-Path $root 'native/src/lib.rs') -Raw), 'NATIVE_ABI_VERSION:\s*i32\s*=\s*(\d+)').Groups[1].Value
$managedAbi = [regex]::Match((Get-Content (Join-Path $root 'unity/com.pi.unity-harness/Editor/PiUnityBridge.cs') -Raw), 'NativeAbiVersion\s*=\s*(\d+)').Groups[1].Value
if ($abi -ne $managedAbi) { throw "PiUnityBridge.NativeAbiVersion $managedAbi does not match NATIVE_ABI_VERSION $abi" }
if ($null -eq $info.abi -or $null -eq $info.srcHash) { throw "Plugin DLL predates abi/srcHash build info (stale build); $rebuildHint" }
if ([int]$info.abi -ne [int]$abi) { throw "DLL abi $($info.abi) does not match NATIVE_ABI_VERSION $abi; $rebuildHint" }

# 与 native/build.rs 的 source_hash() 一致：FNV-1a 64，Cargo.toml + src/** 按路径逐段排序，逐个喂入 "路径\0内容"。
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
public static class PiUnitySourceHash {
    static int ComparePaths(string a, string b) {
        string[] x = a.Split('/'), y = b.Split('/');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++) {
            int c = string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }
    public static string Compute(string nativeDir) {
        var files = new List<string> { "Cargo.toml" };
        foreach (var f in Directory.GetFiles(Path.Combine(nativeDir, "src"), "*", SearchOption.AllDirectories))
            files.Add(f.Substring(nativeDir.Length).TrimStart('\\', '/').Replace('\\', '/'));
        files.Sort(ComparePaths);
        ulong hash = 0xcbf29ce484222325UL;
        foreach (var name in files) {
            var body = File.ReadAllBytes(Path.Combine(nativeDir, name));
            var bytes = new List<byte>(System.Text.Encoding.UTF8.GetBytes(name)) { 0 };
            bytes.AddRange(body);
            foreach (var b in bytes) { hash ^= b; hash = unchecked(hash * 0x100000001b3UL); }
        }
        return hash.ToString("x16");
    }
}
'@
$srcHash = [PiUnitySourceHash]::Compute((Resolve-Path (Join-Path $root 'native')).Path)
if ($info.srcHash -ne $srcHash) { throw "Plugin DLL was built from native src $($info.srcHash), current src is $srcHash; $rebuildHint" }
Write-Host "versions OK: crate=$cargoVersion protocol=$protocol abi=$abi srcHash=$srcHash"
