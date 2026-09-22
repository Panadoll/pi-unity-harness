use std::cmp::Reverse;
use std::collections::HashMap;
use std::fs::{self, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::PathBuf;
use serde_json::{json, Value};

#[allow(dead_code)]
const SCHEMA_VERSION: u64 = 1;
#[allow(dead_code)]
const FILE_MAX_BYTES: u64 = 5 * 1024 * 1024;
#[allow(dead_code)]
const VALUE_MAX_CHARS: usize = 4096;
#[allow(dead_code)]
const QUERY_MAX_EVENTS: usize = 20_000;
#[allow(dead_code)]
const QUERY_DEFAULT_LIMIT: usize = 50;
#[allow(dead_code)]
const QUERY_MAX_LIMIT: usize = 200;

#[derive(Clone)]
#[allow(dead_code)]
pub(super) struct AuditRequest { pub id: String, pub action_id: u64, pub enqueued_at_ms: i64, pub request_type: String, pub action: String }

pub(super) fn input(value: &Value) -> Value {
    let request_type = value.get("type").and_then(Value::as_str).unwrap_or("unknown"); let payload = value.get("payload").unwrap_or(&Value::Null);
    match request_type { "execute_code" | "validate_execute_code" | "validate_code" => json!({"codeLength": payload.get("code").and_then(Value::as_str).unwrap_or("").chars().count(), "codeRecorded": false}), "execute_file" | "validate_execute_file" | "validate_file" => json!({"filePath": payload.get("filePath").cloned().unwrap_or(Value::Null)}), "command" => json!({"command": payload.get("name").cloned().unwrap_or(Value::Null), "parameters": payload.get("parametersJson").and_then(Value::as_str).unwrap_or("{}")}), "context_snapshot" => payload.clone(), _ => json!({}) }
}

#[allow(dead_code)]
pub(super) fn append_started(path: &PathBuf, request: &AuditRequest, value: &Value) { append(path, &json!({"schemaVersion": SCHEMA_VERSION, "event":"started", "actionId":request.action_id, "requestId":request.id, "requestType":request.request_type, "action":request.action, "timestampMs":request.enqueued_at_ms, "input":input(value)})); }
#[allow(dead_code)]
pub(super) fn append_completed(path: &PathBuf, request: &AuditRequest, response: &[u8], completed_at_ms: i64) { let parsed: Value = serde_json::from_slice(response).unwrap_or_else(|_| json!({"ok":false,"error":"invalid_managed_response"})); append(path, &json!({"schemaVersion": SCHEMA_VERSION, "event":"completed", "actionId":request.action_id, "requestId":request.id, "requestType":request.request_type, "action":request.action, "timestampMs":completed_at_ms, "durationMs":completed_at_ms.saturating_sub(request.enqueued_at_ms), "success":parsed.get("ok").and_then(Value::as_bool).unwrap_or(false), "result":parsed.get("result"), "errorType":parsed.get("error_type"), "error":parsed.get("error")})); }
#[allow(dead_code)]
fn append(path: &PathBuf, value: &Value) { if let Some(parent) = path.parent() { let _ = fs::create_dir_all(parent); }
    if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(path) { if let Ok(mut bytes) = serde_json::to_vec(value) { bytes.push(b'\n'); let _ = file.write_all(&bytes); } } }

pub(super) fn bounded(value: Value) -> Value { value }

#[allow(dead_code)]
pub(super) fn query(directory: PathBuf, value: &Value) -> Result<Value, String> {
    let payload = value.get("payload").unwrap_or(&Value::Null); let limit = payload.get("limit").and_then(Value::as_u64).unwrap_or(QUERY_DEFAULT_LIMIT as u64).clamp(1, QUERY_MAX_LIMIT as u64) as usize;
    let mut files: Vec<PathBuf> = fs::read_dir(&directory).map(|entries| entries.filter_map(Result::ok).map(|entry| entry.path()).filter(|path| path.extension().and_then(|ext| ext.to_str()) == Some("jsonl")).collect()).unwrap_or_default(); files.sort_by_key(|path| Reverse(fs::metadata(path).and_then(|m| m.modified()).ok()));
    let mut events = Vec::new(); for path in files { if events.len() >= QUERY_MAX_EVENTS { break; } read_events(&path, QUERY_MAX_EVENTS - events.len(), &mut events); }
    let mut actions: HashMap<String, Value> = HashMap::new(); for event in events { merge(&mut actions, event); }
    let mut result: Vec<Value> = actions.into_values().collect(); result.sort_by_key(|item| Reverse(item.get("completedAtMs").and_then(Value::as_i64).or_else(|| item.get("startedAtMs").and_then(Value::as_i64)).unwrap_or(0))); result.truncate(limit);
    Ok(json!({"schemaVersion": SCHEMA_VERSION, "capturedAtMs": super::now_ms(), "directory": directory.to_string_lossy().replace('\\', "/"), "count": result.len(), "actions": result}))
}
#[allow(dead_code)]
fn merge(actions: &mut HashMap<String, Value>, event: Value) { let request = event.get("requestId").and_then(Value::as_str).unwrap_or(""); if request.is_empty() { return; } let key = format!("{}:{}", event.get("actionId").and_then(Value::as_u64).unwrap_or(0), request); let item = actions.entry(key).or_insert_with(|| json!({"requestId":request,"status":"pending"})); if let Some(obj) = item.as_object_mut() { if event.get("event") == Some(&json!("started")) { obj.insert("startedAtMs".into(), event.get("timestampMs").cloned().unwrap_or(Value::Null)); } else if event.get("event") == Some(&json!("completed")) { obj.insert("status".into(), json!("completed")); obj.insert("completedAtMs".into(), event.get("timestampMs").cloned().unwrap_or(Value::Null)); obj.insert("success".into(), event.get("success").cloned().unwrap_or(Value::Bool(false))); } } }
#[allow(dead_code)]
fn read_events(path: &PathBuf, limit: usize, output: &mut Vec<Value>) { let Ok(file) = fs::File::open(path) else { return }; let Ok(length) = file.metadata().map(|m|m.len()) else { return }; let mut reader = std::io::BufReader::new(file); let mut position = length; let mut carry = Vec::new(); while position > 0 && output.len() < limit { let size = usize::try_from(position.min(64 * 1024)).unwrap_or(64 * 1024); position -= size as u64; if reader.seek(SeekFrom::Start(position)).is_err() { return; } let mut chunk = vec![0u8; size]; if reader.read_exact(&mut chunk).is_err() { return; } chunk.extend_from_slice(&carry); let mut end = chunk.len(); for index in (0..chunk.len()).rev() { if chunk[index] == b'\n' { if index + 1 < end { if let Ok(value) = serde_json::from_slice(&chunk[index + 1..end]) { output.push(value); } } end = index; } } carry = chunk[..end].to_vec(); } }
