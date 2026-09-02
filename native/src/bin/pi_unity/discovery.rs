use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use super::client::{BridgeJson, CliError};

pub(crate) fn resolve_project_root(project_path_arg: Option<&str>) -> Result<PathBuf, CliError> {
    if let Some(explicit) = project_path_arg {
        let p = PathBuf::from(explicit);
        if p.exists() {
            return Ok(fs::canonicalize(&p).unwrap_or(p));
        }
        return Err(CliError::BridgeNotFound(format!(
            "Specified project path does not exist: {}",
            explicit
        )));
    }

    if let Ok(env_path) = std::env::var("UNITY_PROJECT_PATH") {
        if !env_path.trim().is_empty() {
            let p = PathBuf::from(env_path.trim());
            if p.join("Library/PiUnityHarness/bridge.json").exists() {
                return Ok(fs::canonicalize(&p).unwrap_or(p));
            }
        }
    }

    // Check CWD
    if let Ok(cwd) = std::env::current_dir() {
        if cwd.join("Library/PiUnityHarness/bridge.json").exists() {
            return Ok(cwd);
        }

        // Check up to 5 parent levels
        let mut curr = cwd.as_path();
        for _ in 0..5 {
            if let Some(parent) = curr.parent() {
                if parent.join("Library/PiUnityHarness/bridge.json").exists() {
                    return Ok(parent.to_path_buf());
                }
                curr = parent;
            } else {
                break;
            }
        }
    }

    // Scan running Unity.exe processes via PowerShell
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
                        if candidate.join("Library/PiUnityHarness/bridge.json").exists() {
                            return Ok(fs::canonicalize(&candidate).unwrap_or(candidate));
                        }
                    }
                }
            }
        }
    }

    Err(CliError::BridgeNotFound(
        "Could not find Unity project with active bridge.json. Ensure Unity Editor is running with com.pi.unity-harness installed, or provide --project-path."
            .to_string(),
    ))
}

pub(crate) fn load_bridge_json(project_root: &Path) -> Result<BridgeJson, CliError> {
    let bridge_path = project_root.join("Library/PiUnityHarness/bridge.json");
    if !bridge_path.exists() {
        return Err(CliError::BridgeNotFound(format!(
            "bridge.json not found at {}. Is Unity Editor running with com.pi.unity-harness?",
            bridge_path.display()
        )));
    }

    let mut content = fs::read_to_string(&bridge_path).map_err(|e| {
        CliError::BridgeNotFound(format!("Failed to read {}: {}", bridge_path.display(), e))
    })?;

    if content.starts_with('\u{feff}') {
        content = content.trim_start_matches('\u{feff}').to_string();
    }

    let bridge: BridgeJson = serde_json::from_str(&content).map_err(|e| {
        CliError::BridgeNotFound(format!("Failed to parse {}: {}", bridge_path.display(), e))
    })?;

    Ok(bridge)
}
