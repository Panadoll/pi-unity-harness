//! 默认瘦 schema、字段截断、列表聚合。内部仍是 JSON。

use serde_json::{json, Map, Value};

pub const DEFAULT_FIELD_CHARS: usize = 800;
pub const MAX_SAFE_RESPONSE_CHARS: usize = 32_768;

#[derive(Clone, Debug, Default)]
pub struct ViewOptions {
    pub fields: Vec<String>,
    pub full: bool,
}

impl ViewOptions {
    pub fn from_parts(fields: &[String], full: bool) -> Self {
        Self {
            fields: fields.to_vec(),
            full,
        }
    }

    pub fn wants(&self, name: &str) -> bool {
        self.full || self.fields.iter().any(|f| f == name)
    }
}

pub fn truncate_chars(s: &str, limit: usize) -> (String, bool, usize) {
    let total = s.chars().count();
    if total <= limit {
        return (s.to_string(), false, total);
    }
    let preview: String = s.chars().take(limit).collect();
    (
        format!("{preview}... (truncated, {total} chars total)"),
        true,
        total,
    )
}

pub fn truncate_value(val: &mut Value, limit: usize) -> bool {
    let mut truncated = false;
    match val {
        Value::String(s) => {
            let (next, did, _) = truncate_chars(s, limit);
            if did {
                *s = next;
                truncated = true;
            }
        }
        Value::Array(arr) => {
            for item in arr {
                if truncate_value(item, limit) {
                    truncated = true;
                }
            }
        }
        Value::Object(map) => {
            for (_, v) in map.iter_mut() {
                if truncate_value(v, limit) {
                    truncated = true;
                }
            }
        }
        _ => {}
    }
    truncated
}

fn pick_str(obj: &Value, keys: &[&str]) -> String {
    for key in keys {
        if let Some(s) = obj.get(*key).and_then(Value::as_str) {
            if !s.is_empty() {
                return s.to_string();
            }
        }
    }
    String::new()
}

fn pick_bool(obj: &Value, keys: &[&str]) -> bool {
    for key in keys {
        if let Some(b) = obj.get(*key).and_then(Value::as_bool) {
            return b;
        }
    }
    false
}

fn pick_i64(obj: &Value, keys: &[&str], default: i64) -> i64 {
    for key in keys {
        if let Some(n) = obj.get(*key).and_then(Value::as_i64) {
            return n;
        }
    }
    default
}

pub fn help_items(commands: &[&str]) -> Value {
    let arr: Vec<Value> = commands
        .iter()
        .map(|c| json!({"run": *c}))
        .collect();
    Value::Array(arr)
}

pub fn shape_status(raw: &Value) -> Value {
    let editor = pick_str(raw, &["managedState"]);
    let generation = pick_i64(raw, &["managedGeneration"], 0);
    let editor_status = pick_str(raw, &["editorStatus"]);
    let play = editor_status == "playing" || editor_status == "paused";
    let modal = raw
        .get("modalObservation")
        .and_then(|m| m.get("present"))
        .and_then(Value::as_bool)
        .unwrap_or(false);
    let modal_text = if modal {
        raw.get("modalObservation")
            .and_then(|m| m.get("windows"))
            .and_then(Value::as_array)
            .and_then(|w| w.first())
            .and_then(|w| w.get("title"))
            .and_then(Value::as_str)
            .unwrap_or("present")
            .to_string()
    } else {
        "none".to_string()
    };
    json!({
        "editor": if editor.is_empty() { "unknown" } else { &editor },
        "generation": generation,
        "play": play,
        "modal": modal_text,
        "focus": pick_str(raw, &["focusState"]),
        "editorStatus": editor_status,
    })
}

fn flatten_hierarchy(nodes: &[Value], out: &mut Vec<Value>, opts: &ViewOptions) {
    for node in nodes {
        let mut row = Map::new();
        row.insert("path".into(), json!(pick_str(node, &["path"])));
        row.insert("name".into(), json!(pick_str(node, &["name"])));
        row.insert(
            "active".into(),
            json!(pick_bool(node, &["activeInHierarchy", "activeSelf", "active"])),
        );
        if opts.wants("childCount") {
            row.insert(
                "childCount".into(),
                json!(pick_i64(node, &["childCount"], 0)),
            );
        }
        if opts.wants("tag") {
            row.insert("tag".into(), json!(pick_str(node, &["tag"])));
        }
        if opts.wants("components") {
            row.insert(
                "components".into(),
                node.get("components").cloned().unwrap_or(json!([])),
            );
        }
        out.push(Value::Object(row));
        if let Some(children) = node.get("children").and_then(Value::as_array) {
            flatten_hierarchy(children, out, opts);
        }
    }
}

pub fn shape_snapshot(raw: &Value, opts: &ViewOptions) -> Value {
    let roots = raw
        .pointer("/hierarchy/roots")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let mut nodes = Vec::new();
    flatten_hierarchy(&roots, &mut nodes, opts);
    let shown = nodes.len();
    let total = raw
        .pointer("/hierarchy/nodeCount")
        .and_then(Value::as_i64)
        .unwrap_or(shown as i64);
    let truncated_tree = raw
        .pointer("/hierarchy/truncated")
        .and_then(Value::as_bool)
        .unwrap_or(false);
    let selected = pick_i64(
        raw.get("selection").unwrap_or(&Value::Null),
        &["count"],
        0,
    );
    let selection_name = raw
        .pointer("/selection/activeGameObject/name")
        .or_else(|| raw.pointer("/selection/activeObject/name"))
        .and_then(Value::as_str)
        .unwrap_or("");
    let log_entries = raw
        .pointer("/logs/entries")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let error_count = log_entries
        .iter()
        .filter(|e| {
            let t = pick_str(e, &["type", "level"]);
            t.eq_ignore_ascii_case("error") || t.eq_ignore_ascii_case("exception")
        })
        .count();
    let logs: Vec<Value> = log_entries
        .iter()
        .map(|e| {
            json!({
                "level": pick_str(e, &["type", "level"]),
                "message": pick_str(e, &["message"]),
            })
        })
        .collect();

    let mut out = Map::new();
    out.insert(
        "scene".into(),
        json!(pick_str(
            raw.get("scene").unwrap_or(&Value::Null),
            &["name"]
        )),
    );
    out.insert(
        "selection".into(),
        json!(if selection_name.is_empty() {
            format!("{selected}")
        } else {
            selection_name.to_string()
        }),
    );
    out.insert("selected".into(), json!(selected));
    out.insert("errors".into(), json!(error_count));
    out.insert(
        "nodes".into(),
        json!(format!("{shown} of {total} total")),
    );
    if nodes.is_empty() {
        out.insert("hierarchy".into(), json!("0 个节点 found"));
    } else {
        out.insert("hierarchy".into(), Value::Array(nodes));
    }
    if logs.is_empty() {
        out.insert("logs".into(), json!("0 条日志 found"));
    } else {
        out.insert("logs".into(), Value::Array(logs));
    }

    let mut help = Vec::new();
    if truncated_tree || (shown as i64) < total {
        help.push(json!({
            "run": format!("pi-unity snapshot --max-nodes {total} --full")
        }));
    }
    if !help.is_empty() {
        out.insert("help".into(), Value::Array(help));
    }
    Value::Object(out)
}

pub fn shape_list_commands(raw: &Value, opts: &ViewOptions) -> Value {
    let commands = raw
        .get("commands")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let total = raw
        .get("count")
        .and_then(Value::as_i64)
        .unwrap_or(commands.len() as i64);
    if commands.is_empty() {
        return json!({
            "commands": "0 个已注册的 pipeline 命令 found"
        });
    }
    let rows: Vec<Value> = commands
        .iter()
        .map(|c| {
            let mut row = json!({
                "name": pick_str(c, &["name"]),
                "summary": pick_str(c, &["description", "summary"]),
            });
            if opts.full {
                if let Some(obj) = row.as_object_mut() {
                    obj.insert(
                        "schema".into(),
                        c.get("schema").cloned().unwrap_or(Value::Null),
                    );
                    obj.insert(
                        "parameters".into(),
                        c.get("parameters").cloned().unwrap_or(json!([])),
                    );
                }
            }
            row
        })
        .collect();
    json!({
        "count": format!("{} of {} total", rows.len(), total),
        "commands": rows,
        "help": [{"run": "pi-unity pipeline <name> -p key=value"}]
    })
}

pub fn shape_timeline(raw: &Value, _opts: &ViewOptions) -> Value {
    let actions = raw
        .get("actions")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let total = raw
        .get("count")
        .and_then(Value::as_i64)
        .unwrap_or(actions.len() as i64);
    if actions.is_empty() {
        return json!({
            "actions": "0 条时间线记录 found"
        });
    }
    let rows: Vec<Value> = actions
        .iter()
        .map(|a| {
            json!({
                "id": pick_str(a, &["requestId", "id"]),
                "name": pick_str(a, &["action", "requestType", "name"]),
                "ok": a.get("success").and_then(Value::as_bool).unwrap_or(false),
            })
        })
        .collect();
    json!({
        "count": format!("{} of {} total", rows.len(), total),
        "actions": rows,
    })
}

pub fn shape_run_tests(raw: &Value) -> Value {
    let summary = raw.get("summary").cloned().unwrap_or(Value::Null);
    let passed = pick_i64(&summary, &["passed"], pick_i64(raw, &["passed"], 0));
    let failed = pick_i64(&summary, &["failed"], pick_i64(raw, &["failed"], 0));
    let skipped = pick_i64(&summary, &["skipped"], pick_i64(raw, &["skipped"], 0));
    let results = raw
        .get("results")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let failures: Vec<Value> = results
        .iter()
        .filter(|r| {
            let st = pick_str(r, &["status", "result", "state"]);
            st.eq_ignore_ascii_case("failed") || st.eq_ignore_ascii_case("failure")
        })
        .map(|r| json!({"name": pick_str(r, &["name", "fullName", "testName"])}))
        .collect();
    let mut out = json!({
        "passed": passed,
        "failed": failed,
        "skipped": skipped,
    });
    if failures.is_empty() {
        if let Some(obj) = out.as_object_mut() {
            obj.insert("failures".into(), json!("0 个失败用例 found"));
        }
    } else if let Some(obj) = out.as_object_mut() {
        obj.insert("failures".into(), Value::Array(failures));
    }
    out
}

pub fn shape_observe(raw: &Value) -> Value {
    let frames = raw
        .get("frames")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let paths: Vec<Value> = frames
        .iter()
        .filter_map(|f| f.as_str().map(|s| json!({"path": s})))
        .collect();
    let changed = if raw.get("changed").and_then(Value::as_bool) == Some(true) {
        paths.len()
    } else {
        raw.get("unique_count")
            .and_then(Value::as_u64)
            .unwrap_or(0) as usize
    };
    json!({
        "changedFrames": changed,
        "dHash": pick_str(raw, &["fingerprint", "dHash"]),
        "captured": pick_i64(raw, &["captured_count"], paths.len() as i64),
        "frames": if paths.is_empty() {
            json!("0 帧 found")
        } else {
            Value::Array(paths)
        },
        "latest": pick_str(raw, &["latest"]),
    })
}

pub fn shape_generic(raw: &Value, opts: &ViewOptions) -> Value {
    let mut v = raw.clone();
    if !opts.full {
        truncate_value(&mut v, DEFAULT_FIELD_CHARS);
    }
    v
}

pub fn apply_field_truncation(val: &mut Value, opts: &ViewOptions) -> bool {
    if opts.full {
        false
    } else {
        truncate_value(val, DEFAULT_FIELD_CHARS)
    }
}

pub fn with_truncation_help(mut val: Value, truncated: bool, full_hint: &str) -> Value {
    if !truncated {
        return val;
    }
    if let Some(obj) = val.as_object_mut() {
        let mut help = obj
            .get("help")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        help.push(json!({"run": full_hint}));
        obj.insert("help".into(), Value::Array(help));
    }
    val
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapshot_default_schema_is_slim() {
        let raw = json!({
            "scene": {"name": "SampleScene"},
            "selection": {"count": 1, "activeGameObject": {"name": "Player"}},
            "hierarchy": {
                "nodeCount": 847,
                "truncated": true,
                "roots": [{
                    "path": "/Player",
                    "name": "Player",
                    "activeInHierarchy": true,
                    "childCount": 2,
                    "components": ["Transform", "Rigidbody"],
                    "children": []
                }]
            },
            "logs": {"entries": [{"type": "Error", "message": "boom"}]}
        });
        let out = shape_snapshot(&raw, &ViewOptions::default());
        assert_eq!(out["scene"], "SampleScene");
        assert_eq!(out["errors"], 1);
        assert_eq!(out["nodes"], "1 of 847 total");
        let row = &out["hierarchy"][0];
        assert!(row.get("components").is_none());
        assert_eq!(row["path"], "/Player");
        assert_eq!(row["name"], "Player");
        assert_eq!(row["active"], true);
        assert!(out["help"][0]["run"].as_str().unwrap().contains("--full"));
    }

    #[test]
    fn empty_commands_are_explicit() {
        let out = shape_list_commands(&json!({"commands": [], "count": 0}), &ViewOptions::default());
        let text = out["commands"].as_str().unwrap();
        assert!(text.starts_with("0 "));
        assert!(text.contains("found"));
    }
}
