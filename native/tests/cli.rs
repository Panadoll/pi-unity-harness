use std::fs;
use std::path::PathBuf;
use std::process::Command;

fn bin_exe() -> PathBuf {
    env!("CARGO_BIN_EXE_pi-unity").into()
}

fn run_cli(args: &[&str]) -> (i32, String, String) {
    let output = Command::new(bin_exe())
        .args(args)
        .output()
        .expect("run pi-unity");
    let code = output.status.code().unwrap_or(255);
    (
        code,
        String::from_utf8_lossy(&output.stdout).into_owned(),
        String::from_utf8_lossy(&output.stderr).into_owned(),
    )
}

#[test]
fn version_fast_path_prints_bare_version() {
    for flag in ["--version", "-v", "-V"] {
        let (code, stdout, stderr) = run_cli(&[flag]);
        assert_eq!(code, 0, "{flag}");
        assert_eq!(stdout.trim(), env!("CARGO_PKG_VERSION"));
        assert!(stderr.is_empty(), "{flag} stderr={stderr}");
        assert!(!stdout.contains("pi-unity"));
    }
}

#[test]
fn no_args_home_is_not_usage() {
    let (code, stdout, _stderr) = run_cli(&[]);
    assert_eq!(code, 0);
    assert!(stdout.contains("bin:"));
    assert!(stdout.contains("description:"));
    assert!(stdout.contains("snapshot"));
    assert!(!stdout.to_lowercase().contains("usage:"));
}

#[test]
fn usage_unknown_flag_is_exit_2_on_stdout() {
    let (code, stdout, stderr) = run_cli(&["snapshot", "--stat"]);
    assert_eq!(code, 2);
    assert!(stdout.contains("error:"));
    assert!(stdout.contains("--stat") || stdout.contains("未知"));
    assert!(!stderr.contains("[pi-unity error]"));
}

#[test]
fn eval_missing_code_is_usage() {
    let (code, stdout, _stderr) = run_cli(&["eval"]);
    assert_eq!(code, 2);
    assert!(stdout.contains("eval 需要 CODE") || stdout.contains("error:"));
    assert!(stdout.contains("pi-unity eval"));
}

#[test]
fn session_end_without_session_is_noop() {
    let dir = std::env::temp_dir().join(format!("pi-unity-session-{}", std::process::id()));
    let _ = fs::create_dir_all(&dir);
    let output = Command::new(bin_exe())
        .args(["session", "end"])
        .env("PI_UNITY_LOG_DIR", &dir)
        .output()
        .unwrap();
    assert_eq!(output.status.code(), Some(0));
    let stdout = String::from_utf8_lossy(&output.stdout);
    assert!(stdout.contains("already") && stdout.contains("no-op"));
    let _ = fs::remove_dir_all(dir);
}

#[test]
fn json_mode_still_available() {
    let (code, stdout, _stderr) = run_cli(&["--json"]);
    assert_eq!(code, 0);
    let parsed: serde_json::Value = serde_json::from_str(&stdout).expect("json");
    assert_eq!(parsed["ok"], serde_json::json!(true));
}

#[test]
fn setup_does_not_run_from_status() {
    let (code, stdout, _stderr) = run_cli(&["status", "--help"]);
    assert_eq!(code, 0);
    assert!(!stdout.contains("SessionStart"));
    assert!(stdout.contains("--timeout"));
}

#[test]
fn snapshot_help_is_concise() {
    let (code, stdout, _stderr) = run_cli(&["snapshot", "--help"]);
    assert_eq!(code, 0);
    assert!(stdout.contains("--depth"));
    assert!(
        stdout.contains("示例")
            || stdout.contains("Examples")
            || stdout.contains("pi-unity snapshot")
    );
    assert!(!stdout.contains("run-tests"));
    assert!(!stdout.contains("--no-components"));
}

#[test]
fn ping_does_not_register_hooks() {
    let home = std::env::temp_dir().join(format!("pi-unity-nohook-{}", std::process::id()));
    let _ = fs::create_dir_all(&home);
    let output = Command::new(bin_exe())
        .args(["ping"])
        .env("HOME", &home)
        .env("USERPROFILE", &home)
        .output()
        .unwrap();
    assert!(!home.join(".claude/settings.json").exists());
    assert!(!home.join(".codex/hooks.json").exists());
    let _ = output;
    let _ = fs::remove_dir_all(home);
}
