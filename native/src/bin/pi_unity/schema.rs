//! Presentation shaping：默认列、字段截断和 help。Pipeline payload 抽取在 domain。
//! CLI JSON 信封在 wire；TOON、Base64 与长输出落盘在 output。

use serde_json::{json, Map, Value};

pub(crate) const DEFAULT_FIELD_CHARS: usize = 800;
pub(crate) const MAX_SAFE_RESPONSE_CHARS: usize = 32_768;

#[derive(Clone, Debug, Default)]
pub(crate) struct ViewOptions {
    pub(crate) fields: Vec<String>,
    pub(crate) full: bool,
}

impl ViewOptions {
    pub(crate) fn from_parts(fields: &[String], full: bool) -> Self {
        Self {
            fields: fields.to_vec(),
            full,
        }
    }

    pub(crate) fn wants(&self, name: &str) -> bool {
        self.full || self.fields.iter().any(|f| f == name)
    }
}

pub(crate) fn truncate_chars(s: &str, limit: usize) -> (String, bool, usize) {
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

pub(crate) fn truncate_value(val: &mut Value, limit: usize) -> bool {
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

pub(crate) fn help_items(commands: &[&str]) -> Value {
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

pub(crate) fn shape_status_view(raw: &Value, opts: &ViewOptions) -> Value {
    if opts.full {
        return raw.clone();
    }
    let mut shaped = shape_status(raw);
    append_requested_fields(&mut shaped, raw, opts);
    shaped
}

pub(crate) fn shape_status(raw: &Value) -> Value {
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
    let mut shaped = json!({
        "editor": if editor.is_empty() { "unknown" } else { &editor },
        "generation": generation,
        "play": play,
        "modal": modal_text,
        "focus": pick_str(raw, &["focusState"]),
        "editorStatus": editor_status,
    });
    if let Some(warning) = build_mismatch(raw, env!("PI_UNITY_SRC_HASH")) {
        shaped["buildMismatch"] = json!(warning);
    }
    shaped
}

fn build_mismatch(raw: &Value, cli_src_hash: &str) -> Option<String> {
    let native = raw.get("native")?;
    let dll_src_hash = native.get("srcHash").and_then(Value::as_str).unwrap_or("unknown");
    (dll_src_hash != cli_src_hash).then(|| {
        format!(
            "pi-unity CLI (src {cli_src_hash}) 与 Unity 已加载的 native DLL (src {dll_src_hash}) 不是同一份源码构建；运行 scripts/build-native.ps1 后重启 Editor"
        )
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

pub(crate) fn shape_snapshot(raw: &Value, opts: &ViewOptions) -> Value {
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

pub(crate) fn shape_list_commands(raw: &Value, opts: &ViewOptions) -> Value {
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

pub(crate) fn shape_timeline(raw: &Value, opts: &ViewOptions) -> Value {
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


#[cfg(test)]
pub(crate) fn shape_run_tests(raw: &Value) -> Value {
    shape_run_tests_with(raw, false)
}

pub(crate) fn shape_run_tests_with(raw: &Value, full: bool) -> Value {
    let payload = super::domain::pipeline_payload(raw);
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
        .map(|r| {
            let mut row = Map::new();
            row.insert("name".into(), json!(pick_str_ci(r, &["name", "fullName", "testName"])));
            let message = pick_str_ci(r, &["message", "Message"]);
            let stack = pick_str_ci(r, &["stackTrace", "StackTrace"]);
            if !message.is_empty() {
                row.insert(
                    "message".into(),
                    json!(if full { message } else { truncate_chars(&message, 500).0 }),
                );
            }
            if !stack.is_empty() {
                row.insert(
                    "stackTrace".into(),
                    json!(if full { stack } else { truncate_chars(&stack, 800).0 }),
                );
            }
            Value::Object(row)
        })
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

pub(crate) fn shape_observe(raw: &Value) -> Value {
    let payload = super::domain::pipeline_payload(raw);
    let raw = &payload;
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
    let mut out = json!({
        "status": pick_str(raw, &["status"]),
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
        "timedOut": raw.get("timed_out").and_then(Value::as_bool) == Some(true),
    });
    if let Some(error_type) = raw.get("error_type").and_then(Value::as_str) {
        if !error_type.is_empty() {
            out["errorType"] = json!(error_type);
        }
    }
    out
}

/// detached job 的默认列。不含 ok：查询本身成功，执行成败由 state/error 表达。
pub(crate) fn shape_job(raw: &Value, opts: &ViewOptions) -> Value {
    let state = pick_str(raw, &["state"]);
    let mut out = Map::new();
    out.insert("jobId".into(), json!(pick_str(raw, &["jobId"])));
    out.insert("command".into(), json!(pick_str(raw, &["command"])));
    out.insert(
        "state".into(),
        json!(if state.is_empty() { "unknown" } else { &state }),
    );
    out.insert(
        "cancellationRequested".into(),
        json!(pick_bool(raw, &["cancellationRequested"])),
    );
    let error = pick_str(raw, &["error"]);
    let error_type = pick_str(raw, &["errorType"]);
    if !error.is_empty() || opts.full {
        out.insert("error".into(), json!(error));
    }
    if !error_type.is_empty() || opts.full {
        out.insert("errorType".into(), json!(error_type));
    }
    if opts.full || raw.get("result").is_some() {
        out.insert(
            "result".into(),
            raw.get("result").cloned().unwrap_or(Value::Null),
        );
    }
    if opts.full || raw.get("progress").map(|v| !v.is_null()).unwrap_or(false) {
        out.insert(
            "progress".into(),
            raw.get("progress").cloned().unwrap_or(Value::Null),
        );
    }
    if opts.full {
        for key in ["enqueuedAtMs", "startedAtMs", "completedAtMs"] {
            if let Some(v) = raw.get(key) {
                out.insert(key.to_string(), v.clone());
            }
        }
    }
    let mut shaped = Value::Object(out);
    append_requested_fields(&mut shaped, raw, opts);
    shaped
}

pub(crate) fn shape_job_progress(raw: &Value) -> Value {
    let state = {
        let value = pick_str(raw, &["state"]);
        if value.is_empty() { "unknown".to_string() } else { value }
    };
    json!({
        "jobId": pick_str(raw, &["jobId"]),
        "state": state,
        "active": pick_bool(raw, &["active"]),
        "progress": raw.get("progress").cloned().unwrap_or(Value::Null),
    })
}

pub(crate) fn shape_generic(raw: &Value, opts: &ViewOptions) -> Value {
    let mut v = raw.clone();
    if !opts.full {
        truncate_value(&mut v, DEFAULT_FIELD_CHARS);
    }
    v
}
pub(crate) fn apply_field_truncation(val: &mut Value, opts: &ViewOptions) -> bool {
    if opts.full {
        false
    } else {
        truncate_value(val, DEFAULT_FIELD_CHARS)
    }
}

pub(crate) fn with_truncation_help(mut val: Value, truncated: bool, full_hint: &str) -> Value {
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
    use crate::domain::job_execution_failed;

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
    fn status_flags_cli_and_dll_built_from_different_sources() {
        let stale = json!({"native": {"srcHash": "aaaa"}});
        let legacy = json!({"native": {"gitRev": "x"}});
        let same = json!({"native": {"srcHash": "bbbb"}});
        assert!(build_mismatch(&stale, "bbbb").unwrap().contains("src aaaa"));
        assert!(build_mismatch(&legacy, "bbbb").unwrap().contains("src unknown"));
        assert_eq!(build_mismatch(&same, "bbbb"), None);
        assert_eq!(build_mismatch(&json!({"managedState": "ready"}), "bbbb"), None);
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
    fn job_failed_keeps_state_and_error_without_claiming_success() {
        let raw = json!({
            "jobId": "job-9",
            "command": "eval_file",
            "state": "failed",
            "cancellationRequested": false,
            "error": "boom",
            "errorType": "command_error",
            "result": {"value": null, "output": ""}
        });
        let out = shape_job(&raw, &ViewOptions::default());
        assert_eq!(out["jobId"], "job-9");
        assert_eq!(out["state"], "failed");
        assert_eq!(out["error"], "boom");
        assert_eq!(out["errorType"], "command_error");
        assert_eq!(out["result"]["output"], "");
        assert!(out.get("ok").is_none());
        assert!(job_execution_failed("failed"));
        assert!(!job_execution_failed("completed"));
        assert!(!job_execution_failed("running"));
    }
    #[test]
    fn job_progress_keeps_null_progress() {
        let out = shape_job_progress(&json!({
            "jobId": "job-1",
            "state": "queued",
            "active": false,
            "progress": null
        }));
        assert_eq!(out["state"], "queued");
        assert_eq!(out["active"], false);
        assert!(out["progress"].is_null());
    }
    #[test]
    fn failed_job_query_payload_keeps_state_apart_from_query_success() {
        let raw = json!({
            "jobId": "job-9",
            "command": "eval_file",
            "state": "failed",
            "cancellationRequested": false,
            "error": "boom",
            "errorType": "timeout",
            "result": {"value": 1, "output": "partial"}
        });
        let snapshot = shape_job(&raw, &ViewOptions::default());
        let err = crate::wire::CliError::JobFinished {
            code: "timeout".into(),
            message: "boom".into(),
            snapshot,
        };
        let payload = crate::wire::error_payload(&err);
        assert_eq!(payload["ok"], false);
        assert_eq!(payload["exitCode"], 1);
        assert_eq!(payload["error_type"], "timeout");
        assert_eq!(payload["result"]["state"], "failed");
        assert_eq!(payload["result"]["jobId"], "job-9");
        assert_eq!(payload["result"]["result"]["value"], 1);
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
    fn observe_unwraps_pipeline_value_and_json_output() {
        let observed = json!({
            "status": "succeeded", "frames": ["capture.png"],
            "changed": false, "unique_count": 1, "captured_count": 1,
            "deduplicated_count": 2, "fingerprint": "0c003100",
            "latest": "capture.png"
        });
        for envelope in [
            json!({"typeName": "pipeline_command", "value": observed, "output": "ignored"}),
            json!({"typeName": "pipeline_command", "value": null, "output": observed.to_string()}),
        ] {
            let shaped = shape_observe(&envelope);
            assert_eq!(shaped["captured"], 1);
            assert_eq!(shaped["changedFrames"], 1);
            assert_eq!(shaped["dHash"], "0c003100");
            assert_eq!(shaped["frames"], json!([{"path": "capture.png"}]));
            assert_eq!(shaped["latest"], "capture.png");
            assert_eq!(shaped["embed"], false);
        }
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
    fn vision_failures_exit_nonzero_and_keep_domain_evidence() {
        let capture = json!({
            "typeName": "pipeline_command",
            "output": "ignored",
            "value": {
                "status": "failed",
                "schema": "harness.vision.capture.v1",
                "error": "GameView capture requires PlayMode",
                "error_type": "not_supported"
            }
        });
        let err = super::super::commands::vision_cli_error(&capture).expect("capture failed");
        assert_eq!(err.exit_code(), 1);
        let payload = crate::wire::error_payload(&err);
        assert_eq!(payload["ok"], false);
        assert_eq!(payload["exitCode"], 1);
        assert_eq!(payload["error_type"], "not_supported");
        assert_eq!(payload["result"]["status"], "failed");
        assert!(payload["result"].get("timed_out").is_none(), "领域证据不注入 CLI 合成字段");
        // detached job 的 JValue 会把领域对象再编码成 JSON 字符串。
        let text = capture["value"].to_string();
        for envelope in [
            json!({"value": text}),
            json!({"value": null, "output": serde_json::to_string(&text).unwrap()}),
        ] {
            let err = super::super::commands::vision_cli_error(&envelope).expect("encoded capture failed");
            assert_eq!(err.exit_code(), 1);
            let error = crate::wire::error_payload(&err);
            assert_eq!(error["error_type"], "not_supported");
            assert_eq!(error["result"]["schema"], "harness.vision.capture.v1");
            assert_eq!(error["result"]["error"], "GameView capture requires PlayMode");
        }

        let observe = json!({
            "typeName": "pipeline_command",
            "value": null,
            "output": json!({
                "status": "failed",
                "schema": "harness.vision.observe.v1",
                "error": "End-of-frame did not fire within 200ms",
                "error_type": "timeout",
                "timed_out": true,
                "unique_count": 0
            }).to_string()
        });
        let err = super::super::commands::vision_cli_error(&observe).expect("observe timeout");
        assert_eq!(err.exit_code(), 1);
        let payload = crate::wire::error_payload(&err);
        assert_eq!(payload["error_type"], "timeout");
        assert_eq!(payload["result"]["timed_out"], true);
        assert_eq!(payload["result"]["error_type"], "timeout");
        let partial = json!({
            "schema": "harness.vision.capture_analysis.v1",
            "status": "partial",
            "capture": {"schema": "harness.vision.capture.v1", "status": "succeeded", "path": "shot.png"},
            "analysis": {
                "status": "unavailable",
                "error": {"type": "provider_unavailable", "message": "No external analyzer is configured."}
            }
        });
        let err = super::super::commands::vision_cli_error(&partial).expect("analysis failed");
        assert_eq!(err.exit_code(), 1);
        assert_eq!(err.error_type(), "provider_unavailable");
        let payload = crate::wire::error_payload(&err);
        assert_eq!(payload["result"]["status"], "partial");
        assert_eq!(payload["result"]["capture"]["status"], "succeeded");
        assert_eq!(payload["result"]["analysis"]["status"], "unavailable");
        let skipped = json!({
            "schema": "harness.vision.capture_analysis.v1",
            "status": "failed",
            "capture": {"schema": "harness.vision.capture.v1", "status": "failed", "error": "No camera", "error_type": "runtime"},
            "analysis": {"schema": "harness.vision.analysis.v1", "status": "skipped"}
        });
        let err = super::super::commands::vision_cli_error(&skipped).expect("capture child failed");
        assert_eq!(err.exit_code(), 1);
        assert_eq!(err.error_type(), "runtime");

        let missing = json!({
            "typeName": "pipeline_command",
            "value": {
                "status": "failed",
                "schema": "harness.vision.provider_test.v1",
                "error": {
                    "type": "configuration",
                    "message": "Vision provider is not openai-compatible."
                }
            }
        });
        let err = super::super::commands::vision_cli_error(&missing).expect("provider missing");
        assert_eq!(err.exit_code(), 1);
        assert_eq!(err.error_type(), "configuration");
        assert!(err.message().contains("not openai-compatible"));
        let payload = crate::wire::error_payload(&err);
        assert_eq!(payload["result"]["error"]["type"], "configuration");

        let usage = json!({
            "status": "failed",
            "schema": "harness.vision.capture.v1",
            "error": "Invalid screenshot mode",
            "error_type": "usage"
        });
        assert_eq!(super::super::commands::vision_cli_error(&usage).unwrap().exit_code(), 2);

        let business = json!({"status": "failed", "error_type": "compile_error", "error": "boom"});
        assert!(super::super::commands::vision_cli_error(&business).is_none());
        let running = json!({"schema": "harness.vision.observe.v1", "status": "running"});
        assert!(super::super::commands::vision_cli_error(&running).is_none());
        let encoded_business = json!({"value": json!({"status": "failed", "schema": "other.v1"}).to_string()});
        assert!(super::super::commands::vision_cli_error(&encoded_business).is_none());
    }

    #[test]
    fn successful_observe_keeps_unique_count_and_clear_flags() {
        let shaped = shape_observe(&json!({
            "status": "succeeded",
            "frames": ["capture.png"],
            "changed": false,
            "unique_count": 1,
            "captured_count": 1,
            "fingerprint": "0c003100",
            "timed_out": false
        }));
        assert_eq!(shaped["changedFrames"], 1);
        assert_eq!(shaped["status"], "succeeded");
        assert_eq!(shaped["timedOut"], false);
        assert!(shaped.get("errorType").is_none());
    }

    #[test]
    fn run_tests_failures_exit_one_without_losing_counts() {
        let raw = json!({
            "typeName": "pipeline_command",
            "value": {
                "summary": {"passed": 474, "failed": 3, "skipped": 0},
                "results": [
                    {"FullName": "A.Pass", "Status": "Passed"},
                    {"FullName": "A.FailOne", "Status": "Failed"},
                    {"FullName": "A.FailTwo", "Status": "Failed"},
                    {"name": "A.FailThree", "status": "failed"}
                ]
            }
        });
        let shaped = shape_run_tests(&raw);
        let err = super::super::commands::run_tests_cli_error(&shaped).expect("failed tests");
        assert_eq!(err.exit_code(), 1);
        let payload = crate::wire::error_payload(&err);
        assert_eq!(payload["ok"], false);
        assert_eq!(payload["error_type"], "test_failed");
        assert_eq!(payload["result"]["passed"], 474);
        assert_eq!(payload["result"]["failed"], 3);
        assert_eq!(payload["result"]["skipped"], 0);
        assert_eq!(payload["result"]["failures"].as_array().unwrap().len(), 3);
        assert_eq!(payload["result"]["failureListComplete"], true);
        let clean = shape_run_tests(&json!({"summary": {"passed": 1, "failed": 0}}));
        assert!(super::super::commands::run_tests_cli_error(&clean).is_none());
    }

}
