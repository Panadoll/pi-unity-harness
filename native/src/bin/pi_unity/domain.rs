//! Domain result：只抽取 Pipeline 桥接 payload，并判断视觉领域终态与 job 终态失败。
//! 默认列、截断、TOON、Base64 和 CLI 信封不在这里。
//! 只认 harness.vision.* 的执行终态，不把任意业务 status 当成失败。

use serde_json::Value;

const VISION_SCHEMA_PREFIX: &str = "harness.vision.";

/// Pipeline 命令的桥接信封是 {output, typeName, command, valueTypeName, value}，
/// 真正的命令结果在 value（或 output 里的 JSON 文本）上。
pub(crate) fn pipeline_payload(raw: &Value) -> Value {
    if let Some(value) = raw.get("value") {
        if !value.is_null() {
            if let Some(parsed) = value.as_str().and_then(parse_json_payload) {
                return parsed;
            }
            return value.clone();
        }
    }
    if let Some(text) = raw.get("output").and_then(Value::as_str) {
        if let Some(parsed) = parse_json_payload(text) {
            return parsed;
        }
    }
    raw.clone()
}

/// 与 pipeline_payload 同一解包顺序，但只在结果是 harness.vision.* 时才克隆或解析。
/// 每个 pipeline 命令都会经过这里，非视觉的大结果不能被白白复制。
pub(crate) fn vision_payload(raw: &Value) -> Option<Value> {
    let candidate = match raw.get("value") {
        Some(value) if !value.is_null() => value,
        _ => match raw.get("output") {
            Some(output @ Value::String(_)) => output,
            _ => raw,
        },
    };
    match candidate {
        Value::String(text) if text.contains(VISION_SCHEMA_PREFIX) => {
            parse_json_payload(text).filter(is_vision)
        }
        Value::Object(_) if is_vision(candidate) => Some(candidate.clone()),
        _ => None,
    }
}

fn is_vision(payload: &Value) -> bool {
    payload
        .get("schema")
        .and_then(Value::as_str)
        .is_some_and(|schema| schema.starts_with(VISION_SCHEMA_PREFIX))
}

fn parse_json_payload(text: &str) -> Option<Value> {
    let parsed = serde_json::from_str::<Value>(text).ok()?;
    if let Some(inner) = parsed.as_str() {
        if let Ok(object) = serde_json::from_str::<Value>(inner) {
            if object.is_object() {
                return Some(object);
            }
        }
    }
    Some(parsed)
}

pub(crate) fn job_execution_failed(state: &str) -> bool {
    matches!(state, "failed" | "canceled" | "cancelled" | "interrupted")
}

/// 视觉领域已结束且未成功。缺 schema、进行中、succeeded、skipped 都不是这里的失败。
/// capture_analysis 的 partial 只在 capture/analysis 子结果实际失败时算失败。
pub(crate) fn vision_terminal_failure(payload: &Value) -> Option<VisionFailure> {
    if !is_vision(payload) {
        return None;
    }
    if payload.get("schema").and_then(Value::as_str) == Some("harness.vision.capture_analysis.v1") {
        // 子对象没有独立 schema。capture 失败优先于 analysis。
        return payload
            .get("capture")
            .and_then(vision_failure_fields)
            .or_else(|| payload.get("analysis").and_then(vision_failure_fields));
    }
    vision_failure_fields(payload)
}

/// capture/observe 写 error 字符串 + error_type；analysis/provider 写 error:{type,message}。
fn vision_failure_fields(payload: &Value) -> Option<VisionFailure> {
    let status = payload.get("status").and_then(Value::as_str).unwrap_or("");
    if !matches!(status, "failed" | "unavailable") {
        return None;
    }
    let non_empty = |pointer: &str| {
        payload
            .pointer(pointer)
            .and_then(Value::as_str)
            .filter(|value| !value.is_empty())
    };
    let error_type = non_empty("/error_type")
        .or_else(|| non_empty("/error/type"))
        .unwrap_or(status)
        .to_string();
    let error = non_empty("/error")
        .or_else(|| non_empty("/error/message"))
        .map(str::to_string)
        .unwrap_or_else(|| format!("vision {status}"));
    Some(VisionFailure {
        usage: error_type == "usage",
        error_type,
        error,
    })
}

pub(crate) struct VisionFailure {
    pub(crate) error_type: String,
    pub(crate) error: String,
    pub(crate) usage: bool,
}
