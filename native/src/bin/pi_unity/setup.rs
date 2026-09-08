//! `pi-unity setup`：显式安装 SessionStart hook。不在 ping/status 里注册。

use std::fs;
use std::path::{Path, PathBuf};

use serde_json::{json, Map, Value};

use super::client::CliError;
use super::home;
use super::toon;

const HOOK_COMMENT: &str = "pi-unity-session-start";

pub fn hook_command(current_exe: &Path) -> String {
    let name = "pi-unity";
    if let Ok(resolved) = which_in_path(name) {
        if same_exe(&resolved, current_exe) {
            return name.to_string();
        }
    }
    current_exe.display().to_string().replace('\\', "/")
}

fn same_exe(a: &Path, b: &Path) -> bool {
    let ca = fs::canonicalize(a).unwrap_or_else(|_| a.to_path_buf());
    let cb = fs::canonicalize(b).unwrap_or_else(|_| b.to_path_buf());
    ca == cb
}

fn which_in_path(name: &str) -> Result<PathBuf, ()> {
    let path_var = std::env::var_os("PATH").ok_or(())?;
    for dir in std::env::split_paths(&path_var) {
        let candidate = dir.join(name);
        if candidate.is_file() {
            return Ok(candidate);
        }
        let exe = dir.join(format!("{name}.exe"));
        if exe.is_file() {
            return Ok(exe);
        }
    }
    Err(())
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SetupReport {
    pub command: String,
    pub changed: Vec<String>,
    pub unchanged: Vec<String>,
}

pub fn install_hooks(
    current_exe: &Path,
    claude_settings: &Path,
    codex_hooks: &Path,
    opencode_plugin: &Path,
) -> Result<SetupReport, CliError> {
    let command = hook_command(current_exe);
    let mut changed = Vec::new();
    let mut unchanged = Vec::new();

    if upsert_claude(claude_settings, &command)? {
        changed.push(claude_settings.display().to_string());
    } else {
        unchanged.push(claude_settings.display().to_string());
    }
    if upsert_codex(codex_hooks, &command)? {
        changed.push(codex_hooks.display().to_string());
    } else {
        unchanged.push(codex_hooks.display().to_string());
    }
    if upsert_opencode(opencode_plugin, &command)? {
        changed.push(opencode_plugin.display().to_string());
    } else {
        unchanged.push(opencode_plugin.display().to_string());
    }

    Ok(SetupReport {
        command,
        changed,
        unchanged,
    })
}

fn read_json_object(path: &Path) -> Result<Map<String, Value>, CliError> {
    if !path.exists() {
        return Ok(Map::new());
    }
    let text = fs::read_to_string(path).map_err(|e| {
        CliError::ExecutionFailed(format!("无法读取 {}: {}", path.display(), e))
    })?;
    if text.trim().is_empty() {
        return Ok(Map::new());
    }
    let val: Value = serde_json::from_str(&text).map_err(|e| {
        CliError::ExecutionFailed(format!("无法解析 {}: {}", path.display(), e))
    })?;
    match val {
        Value::Object(map) => Ok(map),
        _ => Err(CliError::ExecutionFailed(format!(
            "{} 不是 JSON 对象",
            path.display()
        ))),
    }
}

fn write_json_object(path: &Path, map: &Map<String, Value>) -> Result<(), CliError> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| {
            CliError::ExecutionFailed(format!("无法创建 {}: {}", parent.display(), e))
        })?;
    }
    let text = serde_json::to_string_pretty(&Value::Object(map.clone())).map_err(|e| {
        CliError::ExecutionFailed(format!("无法序列化 {}: {}", path.display(), e))
    })?;
    fs::write(path, text + "\n").map_err(|e| {
        CliError::ExecutionFailed(format!("无法写入 {}: {}", path.display(), e))
    })
}

fn upsert_claude(path: &Path, command: &str) -> Result<bool, CliError> {
    let mut root = read_json_object(path)?;
    let hooks = root.entry("hooks".to_string()).or_insert_with(|| json!({}));
    let hooks_obj = hooks.as_object_mut().ok_or_else(|| {
        CliError::ExecutionFailed("Claude settings.hooks 不是对象".into())
    })?;
    let session = hooks_obj
        .entry("SessionStart".to_string())
        .or_insert_with(|| json!([]));
    let arr = session.as_array_mut().ok_or_else(|| {
        CliError::ExecutionFailed("SessionStart 不是数组".into())
    })?;

    let desired = json!({
        "hooks": [{
            "type": "command",
            "command": command,
            "comment": HOOK_COMMENT
        }]
    });

    for item in arr.iter_mut() {
        if item
            .pointer("/hooks/0/comment")
            .and_then(Value::as_str)
            == Some(HOOK_COMMENT)
        {
            if item.pointer("/hooks/0/command").and_then(Value::as_str) == Some(command) {
                return Ok(false);
            }
            *item = desired;
            write_json_object(path, &root)?;
            return Ok(true);
        }
    }
    arr.push(desired);
    write_json_object(path, &root)?;
    Ok(true)
}

fn upsert_codex(path: &Path, command: &str) -> Result<bool, CliError> {
    let mut root = read_json_object(path)?;
    let hooks = root.entry("hooks".to_string()).or_insert_with(|| json!({}));
    let hooks_obj = hooks.as_object_mut().ok_or_else(|| {
        CliError::ExecutionFailed("Codex hooks.json 的 hooks 不是对象".into())
    })?;
    let session = hooks_obj
        .entry("SessionStart".to_string())
        .or_insert_with(|| json!([]));
    let arr = session.as_array_mut().ok_or_else(|| {
        CliError::ExecutionFailed("Codex SessionStart 不是数组".into())
    })?;
    let desired = json!({
        "command": command,
        "comment": HOOK_COMMENT
    });
    for item in arr.iter_mut() {
        if item.get("comment").and_then(Value::as_str) == Some(HOOK_COMMENT) {
            if item.get("command").and_then(Value::as_str) == Some(command) {
                return Ok(false);
            }
            *item = desired;
            write_json_object(path, &root)?;
            return Ok(true);
        }
    }
    arr.push(desired);
    write_json_object(path, &root)?;
    Ok(true)
}

fn upsert_opencode(path: &Path, command: &str) -> Result<bool, CliError> {
    if path.exists() {
        if let Ok(existing) = fs::read_to_string(path) {
            if existing.contains(HOOK_COMMENT) && existing.contains(command) {
                return Ok(false);
            }
        }
    }
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| {
            CliError::ExecutionFailed(format!("无法创建 {}: {}", parent.display(), e))
        })?;
    }
    let js = format!(
        r#"// {HOOK_COMMENT}
export const plugin = {{
  name: "pi-unity",
  async system() {{
    const {{ execSync }} = await import("node:child_process");
    try {{
      return execSync({command:?}, {{ encoding: "utf8" }}).trim();
    }} catch {{
      return "pi-unity: 未连接";
    }}
  }}
}};
"#
    );
    fs::write(path, js).map_err(|e| {
        CliError::ExecutionFailed(format!("无法写入 {}: {}", path.display(), e))
    })?;
    Ok(true)
}

pub fn default_hook_paths(project_root: Option<&Path>) -> (PathBuf, PathBuf, PathBuf) {
    let home = home::home_dir().unwrap_or_else(|| PathBuf::from("."));
    let claude = project_root
        .map(|p| p.join(".claude/settings.json"))
        .unwrap_or_else(|| home.join(".claude/settings.json"));
    let codex = project_root
        .map(|p| p.join(".codex/hooks.json"))
        .unwrap_or_else(|| home.join(".codex/hooks.json"));
    let opencode = project_root
        .map(|p| p.join(".opencode/plugins/pi-unity.js"))
        .unwrap_or_else(|| home.join(".config/opencode/plugins/pi-unity.js"));
    (claude, codex, opencode)
}

pub fn format_report(report: &SetupReport, json_mode: bool) -> String {
    let status = if report.changed.is_empty() {
        "already installed (no-op)"
    } else {
        "installed"
    };
    if json_mode {
        return serde_json::to_string_pretty(&json!({
            "ok": true,
            "result": {
                "status": status,
                "command": report.command,
                "changed": report.changed,
                "unchanged": report.unchanged
            }
        }))
        .unwrap();
    }
    let mut map = serde_json::Map::new();
    map.insert("setup".into(), json!(status));
    map.insert("command".into(), json!(report.command));
    if report.changed.is_empty() {
        map.insert("changed".into(), json!("0 个 hook 文件 found"));
    } else {
        map.insert(
            "changed".into(),
            json!(report
                .changed
                .iter()
                .map(|p| json!({"path": p}))
                .collect::<Vec<_>>()),
        );
    }
    toon::encode(&Value::Object(map))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn setup_is_idempotent() {
        let root = std::env::temp_dir().join(format!("pi-unity-setup-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let claude = root.join("claude/settings.json");
        let codex = root.join("codex/hooks.json");
        let plugin = root.join("opencode/pi-unity.js");
        let exe = PathBuf::from("/tmp/pi-unity");
        let first = install_hooks(&exe, &claude, &codex, &plugin).unwrap();
        assert!(!first.changed.is_empty());
        let second = install_hooks(&exe, &claude, &codex, &plugin).unwrap();
        assert!(second.changed.is_empty());
        assert_eq!(second.unchanged.len(), 3);
        let claude_json: Value =
            serde_json::from_str(&fs::read_to_string(&claude).unwrap()).unwrap();
        assert_eq!(
            claude_json.pointer("/hooks/SessionStart/0/hooks/0/command"),
            Some(&json!(hook_command(&exe)))
        );
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn setup_repairs_moved_binary() {
        let root = std::env::temp_dir().join(format!("pi-unity-setup-move-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let claude = root.join("claude/settings.json");
        let codex = root.join("codex/hooks.json");
        let plugin = root.join("opencode/pi-unity.js");
        let old = PathBuf::from("/old/pi-unity");
        install_hooks(&old, &claude, &codex, &plugin).unwrap();
        let new = PathBuf::from("/new/pi-unity");
        let report = install_hooks(&new, &claude, &codex, &plugin).unwrap();
        assert!(!report.changed.is_empty());
        let claude_json: Value =
            serde_json::from_str(&fs::read_to_string(&claude).unwrap()).unwrap();
        let cmd = claude_json
            .pointer("/hooks/SessionStart/0/hooks/0/command")
            .and_then(Value::as_str)
            .unwrap();
        assert!(cmd.contains("new") || cmd == "pi-unity" || cmd.contains("pi-unity"));
        let _ = fs::remove_dir_all(&root);
    }
}
