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

/// 大小写不敏感的字段取值：Pipeline 包的对象用 PascalCase（Status/FullName），
/// harness 自己的对象用 camelCase，两边都要能读。
fn pick_str_ci(obj: &Value, keys: &[&str]) -> String {
    let Some(map) = obj.as_object() else {
        return String::new();
    };
    for key in keys {
        for (name, value) in map {
            if name.eq_ignore_ascii_case(key) {
                if let Some(s) = value.as_str() {
                    if !s.is_empty() {
                        return s.to_string();
                    }
                }
            }
        }
    }
    String::new()
}

fn pick_i64_ci(obj: &Value, keys: &[&str], default: i64) -> i64 {
    let Some(map) = obj.as_object() else {
        return default;
    };
    for key in keys {
        for (name, value) in map {
            if name.eq_ignore_ascii_case(key) {
                if let Some(n) = value.as_i64() {
                    return n;
                }
            }
        }
    }
    default
}

/// Pipeline 命令的桥接信封是 {output, typeName, command, valueTypeName, value}，
/// 真正的命令结果在 value（或 output 里的 JSON 文本）上。
fn pipeline_payload(raw: &Value) -> Value {
    if let Some(value) = raw.get("value") {
        if !value.is_null() {
            return value.clone();
        }
    }
    if let Some(text) = raw.get("output").and_then(Value::as_str) {
        if let Ok(parsed) = serde_json::from_str::<Value>(text) {
            return parsed;
        }
    }
    raw.clone()
}

pub fn help_items(commands: &[&str]) -> Value {
    let arr: Vec<Value> = commands.iter().map(|c| json!({"run": *c})).collect();
    Value::Array(arr)
}

fn append_requested_fields(target: &mut Value, raw: &Value, opts: &ViewOptions) {
    if opts.fields.is_empty() {
        return;
    }
    let Some(obj) = target.as_object_mut() else {
        return;
    };
    for field in &opts.fields {
        if let Some(v) = raw.get(field) {
            obj.insert(field.clone(), v.clone());
        }
    }
}

pub fn shape_status_view(raw: &Value, opts: &ViewOptions) -> Value {
    if opts.full {
        return raw.clone();
    }
    let mut shaped = shape_status(raw);
    append_requested_fields(&mut shaped, raw, opts);
    shaped
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
            json!(pick_bool(
                node,
                &["activeInHierarchy", "activeSelf", "active"]
            )),
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
    let selected = pick_i64(raw.get("selection").unwrap_or(&Value::Null), &["count"], 0);
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
    out.insert("nodes".into(), json!(format!("{shown} of {total} total")));
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
                "mutability": c.get("policy").and_then(|p| p.get("mutability")).and_then(Value::as_str).unwrap_or("write"),
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

pub fn shape_timeline(raw: &Value, opts: &ViewOptions) -> Value {
    if opts.full {
        return raw.clone();
    }
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
            let mut row = json!({
                "id": pick_str(a, &["requestId", "id"]),
                "name": pick_str(a, &["action", "requestType", "name"]),
                "ok": a.get("success").and_then(Value::as_bool).unwrap_or(false),
            });
            append_requested_fields(&mut row, a, opts);
            row
        })
        .collect();
    json!({
        "count": format!("{} of {} total", rows.len(), total),
        "actions": rows,
    })
}

pub fn shape_run_tests(raw: &Value) -> Value {
    let payload = pipeline_payload(raw);
    let summary = payload.get("summary").cloned().unwrap_or(Value::Null);
    let passed = pick_i64_ci(&summary, &["passed"], pick_i64_ci(&payload, &["passed"], 0));
    let failed = pick_i64_ci(&summary, &["failed"], pick_i64_ci(&payload, &["failed"], 0));
    let skipped = pick_i64_ci(&summary, &["skipped"], pick_i64_ci(&payload, &["skipped"], 0));
    let results = payload
        .get("results")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let failures: Vec<Value> = results
        .iter()
        .filter(|r| {
            let st = pick_str_ci(r, &["status", "result", "state"]);
            st.eq_ignore_ascii_case("failed") || st.eq_ignore_ascii_case("failure")
        })
        .map(|r| json!({"name": pick_str_ci(r, &["name", "fullName", "testName"])}))
        .collect();
    let listed_failures = failures.len() as i64;
    json!({
        "passed": passed,
        "failed": failed,
        "skipped": skipped,
        "failures": failures,
        // A summary can survive even when the bridge omits individual results.
        // Keep that mismatch explicit instead of reporting a false empty list.
        "failureListComplete": listed_failures == failed,
    })
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
        raw.get("unique_count").and_then(Value::as_u64).unwrap_or(0) as usize
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
        "embed": false,
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
        let out = shape_list_commands(
            &json!({"commands": [], "count": 0}),
            &ViewOptions::default(),
        );
        let text = out["commands"].as_str().unwrap();
        assert!(text.starts_with("0 "));
        assert!(text.contains("found"));
    }

    #[test]
    fn status_full_returns_raw() {
        let raw = json!({
            "managedState": "ready",
            "pipe": r"\\.\pipe\x",
            "extra": 1
        });
        let out = shape_status_view(
            &raw,
            &ViewOptions {
                fields: vec![],
                full: true,
            },
        );
        assert_eq!(out, raw);
    }

    #[test]
    fn status_fields_appends_raw_keys() {
        let raw = json!({
            "managedState": "ready",
            "pipe": r"\\.\pipe\x",
            "token": "abc"
        });
        let out = shape_status_view(
            &raw,
            &ViewOptions {
                fields: vec!["pipe".into()],
                full: false,
            },
        );
        assert_eq!(out["editor"], "ready");
        assert_eq!(out["pipe"], r"\\.\pipe\x");
        assert!(out.get("token").is_none());
        assert!(out.get("state").is_none());
    }

    #[test]
    fn run_tests_reads_bridge_envelope_and_pascal_case_results() {
        let raw = json!({
            "output": "{\"status\":\"completed\"}",
            "typeName": "pipeline_command",
            "command": "run_tests",
            "valueTypeName": "Unity.Pipeline.TestExecutionResponse",
            "value": {
                "status": "completed",
                "summary": {"total": 3, "passed": 1, "failed": 2, "skipped": 0, "inconclusive": 0},
                "results": [
                    {"FullName": "A.B.PassOne", "Status": "Passed"},
                    {"FullName": "A.B.FailOne", "Status": "Failed"},
                    {"FullName": "A.B.FailTwo", "Status": "Failed"}
                ]
            }
        });
        let out = shape_run_tests(&raw);
        assert_eq!(out["passed"], 1);
        assert_eq!(out["failed"], 2);
        assert_eq!(out["skipped"], 0);
        let failures = out["failures"].as_array().unwrap();
        assert_eq!(failures.len(), 2);
        assert_eq!(failures[0]["name"], "A.B.FailOne");
        assert_eq!(failures[1]["name"], "A.B.FailTwo");
    }

    #[test]
    fn run_tests_reads_json_output_when_value_is_absent() {
        let raw = json!({
            "output": "{\"summary\":{\"passed\":2,\"failed\":1},\"results\":[{\"status\":\"failed\",\"name\":\"CaseOne\"}]}",
            "typeName": "pipeline_command"
        });
        let out = shape_run_tests(&raw);
        assert_eq!(out["passed"], 2);
        assert_eq!(out["failed"], 1);
        assert_eq!(out["failures"][0]["name"], "CaseOne");
    }

    #[test]
    fn run_tests_accepts_flat_payload() {
        let out = shape_run_tests(&json!({
            "summary": {"passed": 4, "failed": 0, "skipped": 1},
            "results": [{"fullName": "A.B.Pass", "status": "Passed"}]
        }));
        assert_eq!(out["passed"], 4);
        assert_eq!(out["skipped"], 1);
        assert!(out["failures"].as_array().unwrap().is_empty());
        assert_eq!(out["failureListComplete"], true);
    }

    #[test]
    fn run_tests_marks_missing_failure_rows_as_incomplete() {
        let out = shape_run_tests(&json!({
            "summary": {"passed": 2, "failed": 1, "skipped": 0}
        }));
        assert_eq!(out["failed"], 1);
        assert!(out["failures"].as_array().unwrap().is_empty());
        assert_eq!(out["failureListComplete"], false);
    }

    #[test]
    fn observe_always_embeds_false_and_preserves_data() {
        let raw = json!({
            "frames": ["frame_a.png", "frame_b.png", "frame_c.png"],
            "changed": false,
            "fingerprint": "abc123",
            "captured_count": 3,
            "unique_count": 2,
            "latest": "frame_c.png"
        });
        assert!(raw.get("embed").is_none());
        let out = shape_observe(&raw);
        assert_eq!(out["embed"], false);
        assert_eq!(out["changedFrames"], 2);
        assert_eq!(out["dHash"], "abc123");
        assert_eq!(out["captured"], 3);
        assert_eq!(out["latest"], "frame_c.png");
        let frames = out["frames"].as_array().unwrap();
        assert_eq!(frames.len(), 3);
        assert_eq!(frames[0]["path"], "frame_a.png");
        assert_eq!(frames[2]["path"], "frame_c.png");
        assert!(out["frames"][0].get("data").is_none());
        assert!(out.get("image").is_none());
    }

    #[test]
    fn observe_empty_payload_embeds_false_and_keeps_counts() {
        let out = shape_observe(&json!({}));
        assert_eq!(out["embed"], false);
        assert_eq!(out["changedFrames"], 0);
        assert_eq!(out["dHash"], "");
        assert_eq!(out["captured"], 0);
        assert_eq!(out["latest"], "");
        let text = out["frames"].as_str().unwrap();
        assert!(text.starts_with("0 "));
        assert!(text.contains("found"));
    }

    #[test]
    fn timeline_full_and_fields() {
        let raw = json!({
            "count": 1,
            "actions": [{
                "requestId": "1",
                "action": "status",
                "success": true,
                "durationMs": 12
            }]
        });
        let full = shape_timeline(
            &raw,
            &ViewOptions {
                fields: vec![],
                full: true,
            },
        );
        assert_eq!(full, raw);
        let slim = shape_timeline(
            &raw,
            &ViewOptions {
                fields: vec!["durationMs".into()],
                full: false,
            },
        );
        assert_eq!(slim["actions"][0]["id"], "1");
        assert_eq!(slim["actions"][0]["durationMs"], 12);
    }
}
