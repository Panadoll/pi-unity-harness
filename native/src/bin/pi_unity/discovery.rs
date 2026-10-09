use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use super::client::BridgeJson;
use super::wire::CliError;

fn has_bridge(path: &Path) -> bool {
    path.join("Library/PiUnityHarness/bridge.json").exists()
}

/// 只看显式路径、环境变量和 cwd/父目录。不扫进程，供无参 home 使用。
pub(crate) fn resolve_project_root_fast(
    project_path_arg: Option<&str>,
) -> Result<PathBuf, CliError> {
    if let Some(explicit) = project_path_arg {
        let p = PathBuf::from(explicit);
        if p.exists() {
            return Ok(fs::canonicalize(&p).unwrap_or(p));
        }
        return Err(CliError::BridgeNotFound(format!(
            "指定的项目路径不存在: {}",
            explicit
        )));
    }

    if let Ok(env_path) = std::env::var("UNITY_PROJECT_PATH") {
        if !env_path.trim().is_empty() {
            let p = PathBuf::from(env_path.trim());
            if has_bridge(&p) {
                return Ok(fs::canonicalize(&p).unwrap_or(p));
            }
        }
    }

    if let Ok(cwd) = std::env::current_dir() {
        if has_bridge(&cwd) {
            return Ok(cwd);
        }
        let mut curr = cwd.as_path();
        for _ in 0..5 {
            if let Some(parent) = curr.parent() {
                if has_bridge(parent) {
                    return Ok(parent.to_path_buf());
                }
                curr = parent;
            } else {
                break;
            }
        }
    }

    Err(CliError::BridgeNotFound(
        "找不到正在运行的 Unity Editor 或未加载 com.pi.unity-harness".to_string(),
    ))
}

pub(crate) fn resolve_project_root(project_path_arg: Option<&str>) -> Result<PathBuf, CliError> {
    match resolve_project_root_fast(project_path_arg) {
        Ok(root) => return Ok(root),
        Err(err) if project_path_arg.is_some() => return Err(err),
        Err(_) => {}
    }

    // 子命令才扫进程。无参 home 不走这里。
    if cfg!(windows) {
        if let Ok(output) = Command::new("powershell.exe")
            .args([
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "Get-CimInstance Win32_Process -Filter \"name='Unity.exe'\" | ForEach-Object { if ($_.CommandLine) { $_.CommandLine } }",
            ])
            .output()
        {
            if output.status.success() {
                let stdout = String::from_utf8_lossy(&output.stdout);
                let re_project = regex::Regex::new(r#"(?i)-(?:projectPath|createproject)\s+(?:"([^"]+)"|'([^']+)'|([^\s\r\n]+))"#)
                    .unwrap();
                for cap in re_project.captures_iter(&stdout) {
                    let matched_str = cap.get(1).or_else(|| cap.get(2)).or_else(|| cap.get(3));
                    if let Some(m) = matched_str {
                        let candidate = PathBuf::from(m.as_str().trim());
                        if has_bridge(&candidate) {
                            return Ok(fs::canonicalize(&candidate).unwrap_or(candidate));
                        }
                    }
                }
            }
        }
    }

    Err(CliError::BridgeNotFound(
        "找不到带 bridge.json 的 Unity 项目。请打开已加载 com.pi.unity-harness 的 Editor，或提供 --project-path。"
            .to_string(),
    ))
}

/// broker PID 是否存活（Windows；权限不足/其他平台一律视为不存活）。
/// 用于 ERROR_FILE_NOT_FOUND(2) 时判断是否值得短暂重试，避免对已退出的 Editor 干等。
pub(crate) fn process_alive(pid: u32) -> bool {
    #[cfg(windows)]
    {
        use std::ffi::c_void;
        type Handle = *mut c_void;
        extern "system" {
            fn OpenProcess(
                dwDesiredAccess: u32,
                bInheritHandle: i32,
                dwProcessId: u32,
            ) -> Handle;
            fn GetExitCodeProcess(hProcess: Handle, lpExitCode: *mut u32) -> i32;
            fn CloseHandle(hObject: Handle) -> i32;
        }
        const PROCESS_QUERY_LIMITED_INFORMATION: u32 = 0x1000;
        const STILL_ACTIVE: u32 = 259;
        unsafe {
            let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
            if handle.is_null() {
                return false;
            }
            let mut exit_code: u32 = 0;
            let ok = GetExitCodeProcess(handle, &mut exit_code);
            let _ = CloseHandle(handle);
            ok != 0 && exit_code == STILL_ACTIVE
        }
    }
    #[cfg(not(windows))]
    {
        let _ = pid;
        false
    }
}

pub(crate) fn load_bridge_json(project_root: &Path) -> Result<BridgeJson, CliError> {
    let bridge_path = project_root.join("Library/PiUnityHarness/bridge.json");
    if !bridge_path.exists() {
        return Err(CliError::BridgeNotFound(format!(
            "找不到 bridge.json: {}。Unity Editor 是否已加载 com.pi.unity-harness？",
            bridge_path.display()
        )));
    }

    let mut content = fs::read_to_string(&bridge_path).map_err(|e| {
        CliError::BridgeNotFound(format!("无法读取 {}: {}", bridge_path.display(), e))
    })?;

    if content.starts_with('\u{feff}') {
        content = content.trim_start_matches('\u{feff}').to_string();
    }

    let bridge: BridgeJson = serde_json::from_str(&content).map_err(|e| {
        CliError::BridgeNotFound(format!("无法解析 {}: {}", bridge_path.display(), e))
    })?;

    if let Some(project) = bridge.project.as_deref() {
        if !same_project(Path::new(project), project_root) {
            let shown = |p: &Path| p.display().to_string().trim_start_matches(r"\\?\").to_string();
            return Err(CliError::ProjectMismatch(format!(
                "{} 属于工程 {}，不是当前工程 {}",
                shown(&bridge_path),
                project,
                shown(project_root)
            )));
        }
    }

    Ok(bridge)
}

fn same_project(a: &Path, b: &Path) -> bool {
    fn key(p: &Path) -> String {
        let resolved = fs::canonicalize(p).unwrap_or_else(|_| p.to_path_buf());
        let s = resolved.to_string_lossy().replace('\\', "/");
        let s = s.strip_prefix("//?/").unwrap_or(&s);
        s.trim_end_matches('/').to_lowercase()
    }
    key(a) == key(b)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fast_path_missing_explicit_path_fails_without_powershell() {
        let err = resolve_project_root_fast(Some(
            "Z:/definitely-not-a-unity-project-pi-unity-bench",
        ))
        .unwrap_err();
        assert!(err.message().contains("不存在"));
    }

    #[test]
    fn same_project_ignores_separator_case_and_verbatim_prefix() {
        assert!(same_project(
            Path::new("F:/SampleProjects/Test"),
            Path::new(r"\\?\F:\sampleprojects\Test\")
        ));
        assert!(!same_project(
            Path::new("F:/SampleProjects/Test"),
            Path::new(r"F:\SampleProjects\UniGameKit")
        ));
    }

    #[test]
    fn bridge_json_from_another_project_is_rejected() {
        let root = std::env::temp_dir().join(format!("pi_unity_mismatch_{}", std::process::id()));
        let dir = root.join("Library/PiUnityHarness");
        fs::create_dir_all(&dir).unwrap();
        fs::write(
            dir.join("bridge.json"),
            r#"{"project":"Z:/elsewhere/Test","pid":1,"pipe":"p","token":"t","generation":1}"#,
        )
        .unwrap();
        let err = load_bridge_json(&root).unwrap_err();
        let _ = fs::remove_dir_all(&root);
        assert_eq!(err.error_type(), "project_mismatch");
    }

    #[test]
    #[cfg(windows)]
    fn process_alive_sees_current_process() {
        assert!(process_alive(std::process::id()));
        assert!(!process_alive(u32::MAX - 5));
    }
}
