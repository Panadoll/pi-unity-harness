//! 无参 home view 与 skill 静态文案（同源）。

use std::path::{Path, PathBuf};

use serde_json::{json, Value};

use super::schema::{help_items, shape_status};
use super::toon;
use super::version::VERSION;

pub const DESCRIPTION: &str = "从 shell 驱动正在运行的 Unity Editor";

pub fn collapse_home(path: &Path) -> String {
    let displayed = path.display().to_string();
    if let Some(home) = home_dir() {
        let home_s = home.display().to_string();
        if displayed.starts_with(&home_s) {
            return format!("~{}", &displayed[home_s.len()..]).replace('\\', "/");
        }
    }
    displayed.replace('\\', "/")
}

pub fn home_dir() -> Option<PathBuf> {
    std::env::var_os("HOME")
        .or_else(|| std::env::var_os("USERPROFILE"))
        .map(PathBuf::from)
}

pub fn next_steps(project_path: Option<&str>) -> Vec<String> {
    let prefix = match project_path {
        Some(p) if !p.is_empty() => format!("pi-unity --project-path {p} "),
        _ => "pi-unity ".to_string(),
    };
    vec![
        format!("{prefix}snapshot"),
        format!("{prefix}eval \"<code>\""),
        format!("{prefix}compile"),
    ]
}

pub fn render_home(
    bin: &Path,
    connected: Option<&Value>,
    snapshot: Option<&Value>,
    project_path: Option<&str>,
    reason: Option<&str>,
) -> String {
    let mut map = serde_json::Map::new();
    map.insert("bin".into(), json!(collapse_home(bin)));
    map.insert("description".into(), json!(DESCRIPTION));
    if let Some(status) = connected {
        let shaped = shape_status(status);
        map.insert(
            "editor".into(),
            shaped.get("editor").cloned().unwrap_or(json!("unknown")),
        );
        map.insert(
            "generation".into(),
            shaped.get("generation").cloned().unwrap_or(json!(0)),
        );
        map.insert(
            "play".into(),
            shaped.get("play").cloned().unwrap_or(json!(false)),
        );
        map.insert(
            "modal".into(),
            shaped.get("modal").cloned().unwrap_or(json!("none")),
        );
        if let Some(snap) = snapshot {
            map.insert(
                "scene".into(),
                json!(snap
                    .pointer("/scene/name")
                    .and_then(Value::as_str)
                    .unwrap_or("")),
            );
            let sel = snap
                .pointer("/selection/activeGameObject/name")
                .or_else(|| snap.pointer("/selection/activeObject/name"))
                .and_then(Value::as_str)
                .unwrap_or("");
            map.insert("selection".into(), json!(sel));
            let errors = snap
                .pointer("/logs/entries")
                .and_then(Value::as_array)
                .map(|a| {
                    a.iter()
                        .filter(|e| {
                            e.get("type")
                                .and_then(Value::as_str)
                                .map(|t| t.eq_ignore_ascii_case("error") || t.eq_ignore_ascii_case("exception"))
                                .unwrap_or(false)
                        })
                        .count()
                })
                .unwrap_or(0);
            map.insert("errors".into(), json!(errors));
        }
    } else {
        map.insert("editor".into(), json!("未连接"));
        map.insert(
            "reason".into(),
            json!(reason.unwrap_or("找不到正在运行的 Unity Editor 或未加载 com.pi.unity-harness")),
        );
    }
    let steps = next_steps(project_path);
    map.insert(
        "help".into(),
        help_items(&steps.iter().map(|s| s.as_str()).collect::<Vec<_>>()),
    );
    toon::encode(&Value::Object(map))
}

pub fn static_skill_markdown() -> String {
    format!(
        r#"---
name: pi-unity
description: >
  通过 pi-unity CLI 驱动已打开的 Unity Editor（status、eval、compile、
  snapshot、pipeline、run-tests、observe、capture、timeline）。Unity 已打开且
  需要检查场景、跑 C#、编译、测试或截 PlayMode 时使用。不要用本 skill 改
  pi-unity-harness 源码树。
---

# pi-unity

{DESCRIPTION}。默认 stdout 是 TOON；`--json` 才输出 JSON。无参 `pi-unity` 打印 live dashboard，不要先跑 `--help`。

需要某条子命令的字段表时，读同目录 `references/<命令>.md`。

## 命令速查

| 命令 | 用途 | 细节 |
| :--- | :--- | :--- |
| `pi-unity ping` | 探测 broker | [status.md](references/status.md) |
| `pi-unity status` | Editor / 域重载 / 模态 | [status.md](references/status.md) |
| `pi-unity snapshot` | 场景树、选中、日志 | [snapshot.md](references/snapshot.md) |
| `pi-unity eval "<code>"` | 主线程执行 C# | [eval.md](references/eval.md) |
| `pi-unity compile` | 编译并等到 `ready` | [compile.md](references/compile.md) |
| `pi-unity list-commands` | 列出 `[CliCommand]` | [pipeline.md](references/pipeline.md) |
| `pi-unity pipeline <name>` | 跑一条 pipeline | [pipeline.md](references/pipeline.md) |
| `pi-unity run-tests` | EditMode / PlayMode | [run-tests.md](references/run-tests.md) |
| `pi-unity observe` | 多帧画面 | [observe.md](references/observe.md) |
| `pi-unity capture` | 单张截图 | [capture.md](references/capture.md) |
| `pi-unity timeline` | 最近操作 | [timeline.md](references/timeline.md) |
| `pi-unity setup` | 安装 SessionStart hook | 见下 |
| `pi-unity skills install` | 安装本 skill | 见下 |

```bash
pi-unity pipeline gameobject_find -p name="Main Camera"
pi-unity pipeline gameobject_create -p name="Player" -p parent_path="WorldRoot"
pi-unity pipeline uitree_find -p query="Start Game"
pi-unity pipeline uitree_snapshot -p interactive_only=true
pi-unity pipeline input_click -p x=640 -p y=360
```

## 速度模式和 GUI 模式

日常走速度模式：`snapshot`、`eval`、`uitree_*`。不看大图。

只有画面、动画、坐标、自绘 UI 对不上的时候才用 `observe` / `capture`。图写到 `Temp/PiUnityHarness/Captures/`，stdout 只给路径。不要把 Base64 倒进终端。

## Verify loop

改脚本、场景或资产时按这个走。无关的只读查询不必五步都做。

```text
1. snapshot    改前基线
2. 改代码 / 资产 / 场景
3. compile     等到 ready，退出码必须是 0
4. run-tests 或 eval
5. snapshot    对一下改后状态
```

## 退出码

- `0` 成功，含幂等 no-op（文案含 already … (no-op)）
- `1` 执行失败，含连不上 Unity、超时
- `2` 用法错误（缺参、未知 flag、未知子命令）

`--json` 时 stdout 是 JSON。别把 stderr 拼进 JSON。默认 TOON。`--help` 永远合法。

## eval 红线

1. 多行或临时变量写到 `Temp/PiUnityHarness/AgentScratch/*.repl`，用 `pi-unity eval -f`。
2. eval 里不要调 `AssetDatabase.Refresh()`，也不要自己触发编译。编译用 `pi-unity compile`。
3. 代码跑在 Editor 主线程。不要 `Thread.Sleep`，不要同步死锁。

## 发现

优先 `pi-unity setup` 装 SessionStart hook（Claude Code / Codex / OpenCode），每个会话注入 live dashboard。本 skill 是第二条路径，不含 live Editor 状态。CLI 版本 {VERSION}。
"#
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn disconnected_home_is_explicit_and_has_next_steps() {
        let bin = PathBuf::from("/usr/bin/pi-unity");
        let out = render_home(&bin, None, None, Some("/proj"), Some("找不到 bridge.json"));
        assert!(out.contains("bin:"));
        assert!(out.contains("description: 从 shell 驱动正在运行的 Unity Editor"));
        assert!(out.contains("editor: 未连接"));
        assert!(out.contains("找不到 bridge.json"));
        assert!(out.contains("pi-unity --project-path /proj snapshot"));
        assert!(!out.to_lowercase().contains("usage:"));
    }

    #[test]
    fn connected_home_uses_status_fields() {
        let bin = PathBuf::from("/usr/bin/pi-unity");
        let status = json!({
            "managedState": "ready",
            "managedGeneration": 12,
            "editorStatus": "editing",
            "modalObservation": {"present": false}
        });
        let snap = json!({
            "scene": {"name": "SampleScene"},
            "selection": {"activeGameObject": {"name": "Player"}},
            "logs": {"entries": []}
        });
        let out = render_home(&bin, Some(&status), Some(&snap), None, None);
        assert!(out.contains("editor: ready"));
        assert!(out.contains("generation: 12"));
        assert!(out.contains("play: false"));
        assert!(out.contains("modal: none"));
        assert!(out.contains("scene: SampleScene"));
        assert!(out.contains("selection: Player"));
        assert!(out.contains("errors: 0"));
    }
}
