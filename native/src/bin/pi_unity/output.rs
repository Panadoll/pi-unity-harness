use std::fs;
use std::path::Path;

use serde_json::{json, Value};

use super::logging::{fast_rand_id, now_ms, TraceRecorder};
use super::schema::{self, ViewOptions};
use super::toon;

pub const MAX_SAFE_RESPONSE_CHARS: usize = schema::MAX_SAFE_RESPONSE_CHARS;
const MIN_BASE64_DETECT_LENGTH: usize = 256;

pub(crate) fn is_base64_data(s: &str) -> bool {
    if s.starts_with("data:image/") && s.contains(";base64,") {
        return true;
    }
    if s.len() >= MIN_BASE64_DETECT_LENGTH {
        let prefix = &s[..128.min(s.len())];
        if prefix.bytes().all(|b| {
            b.is_ascii_alphanumeric() || b == b'+' || b == b'/' || b == b'='
        }) {
            return true;
        }
    }
    false
}

pub(crate) fn strip_large_base64_and_save(value: &mut Value, project_root: &Path, depth: usize) {
    if depth > 20 {
        return;
    }

    match value {
        Value::String(s) => {
            if is_base64_data(s) {
                let raw_b64 = if s.starts_with("data:image/") {
                    if let Some(idx) = s.find(";base64,") {
                        &s[idx + 8..]
                    } else {
                        s.as_str()
                    }
                } else {
                    s.as_str()
                };

                let decoded_bytes = base64::Engine::decode(
                    &base64::engine::general_purpose::STANDARD,
                    raw_b64.trim(),
                )
                .ok();

                let size_bytes = decoded_bytes.as_ref().map(|b| b.len()).unwrap_or((raw_b64.len() * 3) / 4);
                let size_kb = format!("{:.1}", size_bytes as f64 / 1024.0);

                if let Some(bytes) = decoded_bytes {
                    let capture_dir = project_root.join("Temp/PiUnityHarness/Captures");
                    let _ = fs::create_dir_all(&capture_dir);
                    let file_name = format!("capture_{}_{}.png", now_ms(), fast_rand_id());
                    let file_path = capture_dir.join(&file_name);
                    if fs::write(&file_path, bytes).is_ok() {
                        let rel = format!("Temp/PiUnityHarness/Captures/{}", file_name);
                        *s = format!(
                            "[Base64 Image ({} KB) stripped to prevent payload explosion. Saved to: {}]",
                            size_kb, rel
                        );
                        return;
                    }
                }

                *s = format!(
                    "[Base64 Image data ({} KB) stripped to prevent payload explosion]",
                    size_kb
                );
            }
        }
        Value::Array(arr) => {
            for item in arr.iter_mut() {
                strip_large_base64_and_save(item, project_root, depth + 1);
            }
        }
        Value::Object(map) => {
            let sibling_path = map
                .get("SavedPath")
                .or_else(|| map.get("savedPath"))
                .or_else(|| map.get("filePath"))
                .or_else(|| map.get("path"))
                .and_then(Value::as_str)
                .map(String::from);

            for (key, val) in map.iter_mut() {
                let is_b64_key = key.eq_ignore_ascii_case("base64")
                    || key.eq_ignore_ascii_case("image_base64")
                    || key.eq_ignore_ascii_case("rawimage")
                    || key.eq_ignore_ascii_case("screenshot_base64");

                if is_b64_key {
                    if let Value::String(b64_str) = val {
                        if b64_str.len() > 64 {
                            let size_kb = format!("{:.1}", (b64_str.len() * 3) as f64 / (4.0 * 1024.0));
                            if let Some(ref saved) = sibling_path {
                                *val = Value::String(format!(
                                    "[Base64 Image ({} KB) stripped. Image saved at: {}]",
                                    size_kb, saved
                                ));
                                continue;
                            }
                        }
                    }
                }
                strip_large_base64_and_save(val, project_root, depth + 1);
            }
        }
        _ => {}
    }
}

pub(crate) fn emit_value(val: &Value, json_mode: bool) -> String {
    if json_mode {
        serde_json::to_string_pretty(&json!({
            "ok": true,
            "result": val
        }))
        .unwrap_or_else(|_| "{\"ok\":true}".to_string())
    } else {
        toon::encode(val)
    }
}

pub(crate) fn format_safe_output(
    raw_val: &Value,
    project_root: &Path,
    json_mode: bool,
    recorder: Option<&TraceRecorder>,
) -> String {
    format_safe_output_with_opts(
        raw_val,
        project_root,
        json_mode,
        &ViewOptions::default(),
        Some("pi-unity <command> --full"),
        recorder,
    )
}

pub(crate) fn format_safe_output_with_opts(
    raw_val: &Value,
    project_root: &Path,
    json_mode: bool,
    opts: &ViewOptions,
    full_hint: Option<&str>,
    recorder: Option<&TraceRecorder>,
) -> String {
    let mut sanitized = raw_val.clone();
    strip_large_base64_and_save(&mut sanitized, project_root, 0);
    let truncated = schema::apply_field_truncation(&mut sanitized, opts);
    if truncated {
        if let Some(hint) = full_hint {
            if sanitized.is_object() {
                sanitized = schema::with_truncation_help(sanitized, true, hint);
            } else {
                sanitized = serde_json::json!({
                    "value": sanitized,
                    "help": [{"run": hint}]
                });
            }
        }
    }

    let rendered = emit_value(&sanitized, json_mode);
    if rendered.len() <= MAX_SAFE_RESPONSE_CHARS {
        return rendered;
    }

    if let Some(rec) = recorder {
        rec.mark_truncated();
        rec.record(
            "truncation",
            &format!("output exceeded 32KB limit ({} chars)", rendered.len()),
        );
    }

    let scratch_dir = project_root.join("Temp/PiUnityHarness/AgentScratch");
    let _ = fs::create_dir_all(&scratch_dir);
    let ext = if json_mode { "json" } else { "toon" };
    let scratch_file = format!("large_tool_output_{}.{}", now_ms(), ext);
    let scratch_path = scratch_dir.join(&scratch_file);
    let _ = fs::write(&scratch_path, &rendered);
    let rel_path = format!("Temp/PiUnityHarness/AgentScratch/{}", scratch_file);

    let preview = schema::truncate_chars(&rendered, 1200).0;
    let overflow = json!({
        "truncated": true,
        "totalChars": rendered.len(),
        "savedScratchPath": rel_path,
        "preview": preview,
        "help": [{"run": full_hint.unwrap_or("pi-unity <command> --full")}]
    });
    emit_value(&overflow, json_mode)
}
