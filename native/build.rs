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

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
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
