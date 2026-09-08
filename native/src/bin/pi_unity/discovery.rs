use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use super::client::{BridgeJson, CliError};

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

    Ok(bridge)
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
}
