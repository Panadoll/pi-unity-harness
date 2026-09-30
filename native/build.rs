use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

fn command_output(program: &str, args: &[&str]) -> Option<String> {
    let output = Command::new(program).args(args).output().ok()?;
    if !output.status.success() {
        return None;
    }
    let value = String::from_utf8(output.stdout).ok()?.trim().to_string();
    (!value.is_empty()).then_some(value)
}

/// CLI 与 cdylib 共用这个值：status 据此发现 bin/pi-unity.exe 和 Unity 已加载的 DLL 来自不同源码。
fn source_hash() -> String {
    fn collect(dir: &Path, out: &mut Vec<PathBuf>) {
        let Ok(entries) = fs::read_dir(dir) else { return };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                collect(&path, out);
            } else {
                out.push(path);
            }
        }
    }
    let mut files = vec![PathBuf::from("Cargo.toml")];
    collect(Path::new("src"), &mut files);
    files.sort();
    let mut hash: u64 = 0xcbf29ce484222325;
    for file in files {
        let name = file.to_string_lossy().replace('\\', "/");
        let body = fs::read(&file).unwrap_or_default();
        for byte in name.as_bytes().iter().chain(&[0u8]).chain(&body) {
            hash ^= u64::from(*byte);
            hash = hash.wrapping_mul(0x100000001b3);
        }
    }
    format!("{hash:016x}")
}

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed=src");
    println!("cargo:rerun-if-changed=Cargo.toml");
    println!("cargo:rustc-env=PI_UNITY_SRC_HASH={}", source_hash());
    let git_rev = command_output("git", &["rev-parse", "--short", "HEAD"])
        .unwrap_or_else(|| "unknown".to_string());
    let git_dirty = command_output("git", &["status", "--porcelain"])
        .map(|value| !value.is_empty())
        .unwrap_or(false);
    let build_utc = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs().to_string())
        .unwrap_or_else(|_| "0".to_string());

    println!("cargo:rustc-env=PI_UNITY_GIT_REV={git_rev}");
    println!("cargo:rustc-env=PI_UNITY_GIT_DIRTY={git_dirty}");
    println!("cargo:rustc-env=PI_UNITY_BUILD_UTC={build_utc}");
    println!("cargo:rustc-env=PI_UNITY_PROTOCOL_VERSION=1");
}
