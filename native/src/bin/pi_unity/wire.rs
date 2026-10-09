//! Wire envelope：pipe 方向分类与 CLI 错误 JSON。
//! 只处理 ok / error_type / result。mux 的 id 包装在 output，不在这里。
//! 不解析 Unity 对象，不把 pipe 的 reply_to 和 mux 的 id 当成同一个字段。

use serde_json::{json, Value};

#[derive(Debug, PartialEq, Eq)]
pub(crate) enum CliError {
    ExecutionFailed(String),
    BridgeNotFound(String),
    Timeout(String),
    Usage {
        error: String,
        help: Vec<String>,
    },
    Other(String),
    ProtocolMismatch {
        expected: i32,
        actual: i32,
    },
    /// 结构化 broker/Unity 侧错误：code 保存错误码（managed_reloading、command_error、compilation_failed...）。
    Broker {
        code: String,
        message: String,
    },
    /// 管道忙（ERROR_PIPE_BUSY / 231）：单客户端架构下已有客户端占用。
    Busy(String),
    /// bridge.json 记录的 Unity 进程已退出（Editor 重启中或已关闭）。
    StaleBridge(String),
    /// bridge.json 的 project 字段不是当前解析出的工程（复制 Library 或旧文件残留）。
    ProjectMismatch(String),
    /// 查询成功，但 job 已终态失败。snapshot 是已整形的 job 视图。
    JobFinished {
        code: String,
        message: String,
        snapshot: Value,
    },
    /// 管道 ok，但视觉领域已终态失败。snapshot 保留原始领域证据。
    DomainFinished {
        code: String,
        message: String,
        snapshot: Value,
        usage: bool,
    },
}

impl CliError {
    pub(crate) fn exit_code(&self) -> i32 {
        match self {
            CliError::Usage { .. } | CliError::DomainFinished { usage: true, .. } => 2,
            CliError::ExecutionFailed(_)
            | CliError::BridgeNotFound(_)
            | CliError::Timeout(_)
            | CliError::Other(_)
            | CliError::ProtocolMismatch { .. }
            | CliError::Broker { .. }
            | CliError::Busy(_)
            | CliError::StaleBridge(_)
            | CliError::ProjectMismatch(_)
            | CliError::JobFinished { .. }
            | CliError::DomainFinished { .. } => 1,
        }
    }

    pub(crate) fn error_type(&self) -> String {
        match self {
            CliError::ExecutionFailed(_) => "execution_failed".to_string(),
            CliError::BridgeNotFound(_) => "bridge_not_found".to_string(),
            CliError::Timeout(_) => "timeout".to_string(),
            CliError::Usage { .. } => "usage".to_string(),
            CliError::Other(_) => "other".to_string(),
            CliError::ProtocolMismatch { .. } => "protocol_mismatch".to_string(),
            CliError::Broker { code, .. } => code.clone(),
            CliError::Busy(_) => "busy".to_string(),
            CliError::StaleBridge(_) => "stale_bridge".to_string(),
            CliError::ProjectMismatch(_) => "project_mismatch".to_string(),
            CliError::JobFinished { code, .. } | CliError::DomainFinished { code, .. } => {
                code.clone()
            }
        }
    }

    pub(crate) fn message(&self) -> &str {
        match self {
            CliError::ExecutionFailed(msg) => msg,
            CliError::BridgeNotFound(msg) => msg,
            CliError::Timeout(msg) => msg,
            CliError::Usage { error, .. } => error,
            CliError::Other(msg) => msg,
            CliError::ProtocolMismatch { .. } => "Protocol version mismatch",
            CliError::Broker { message, .. } => message,
            CliError::Busy(msg) => msg,
            CliError::StaleBridge(msg) => msg,
            CliError::ProjectMismatch(msg) => msg,
            CliError::JobFinished { message, .. } | CliError::DomainFinished { message, .. } => {
                message
            }
        }
    }

    pub(crate) fn help(&self) -> Vec<String> {
        match self {
            CliError::Usage { help, .. } => help.clone(),
            CliError::BridgeNotFound(_) => vec![
                "打开 Unity Editor 并加载 com.pi.unity-harness".to_string(),
                "pi-unity --project-path <path> status".to_string(),
            ],
            CliError::Timeout(_) => vec!["pi-unity status".to_string()],
            CliError::Busy(_) => vec![
                "占用者可能是另一个正在执行的 pi-unity 请求，稍后重试 pi-unity status".to_string(),
                "持续 busy 时检查旧版 pi-unity.exe mux 常驻进程（新版 mux 空闲 3 秒即释放管道）"
                    .to_string(),
            ],
            CliError::StaleBridge(_) => vec![
                "Unity Editor 重启中：等待加载完成，bridge.json 会自动刷新".to_string(),
                "pi-unity --project-path <path> status".to_string(),
            ],
            CliError::ProjectMismatch(_) => vec![
                "删除这个残留的 bridge.json，或用 --project-path 指向正在运行的 Unity 工程"
                    .to_string(),
            ],
            CliError::ExecutionFailed(_)
            | CliError::Other(_)
            | CliError::ProtocolMismatch { .. }
            | CliError::Broker { .. }
            | CliError::JobFinished { .. }
            | CliError::DomainFinished { .. } => Vec::new(),
        }
    }
}

/// broker 原生裸错误码白名单：native broker 只发 `error` 字段（无 error_type）且只发这些码。
/// 白名单外的一律按 execution_failed 处理，绝不把任意原始错误文本当 error_type 透出。
const BROKER_NATIVE_CODES: &[&str] = &[
    "managed_not_ready",
    "managed_reloading",
    "managed_quitting",
    "request_timeout_in_flight",
    "managed_heartbeat_timeout",
];

/// pipe 响应的消费者分类。缺 `ok` 与 `ok:false` 走同一错误优先级：
/// 显式 `error_type` 优先于 broker 裸码白名单，其余原始文本是 `execution_failed`。
/// 成功且缺 `result` 归一化为 null。`reply_to` 匹配不在这里做。
pub(crate) fn classify_pipe_response(resp: &Value) -> Result<Value, CliError> {
    let ok = resp.get("ok").and_then(Value::as_bool).unwrap_or(false);
    if ok {
        return Ok(resp.get("result").cloned().unwrap_or(Value::Null));
    }
    let typed = resp
        .get("error_type")
        .and_then(Value::as_str)
        .map(str::to_string);
    let msg = resp
        .get("error")
        .and_then(Value::as_str)
        .unwrap_or("Unknown broker error")
        .to_string();
    match typed {
        Some(code) => Err(CliError::Broker { code, message: msg }),
        None if BROKER_NATIVE_CODES.contains(&msg.as_str()) => Err(CliError::Broker {
            code: msg.clone(),
            message: msg,
        }),
        None => Err(CliError::ExecutionFailed(msg)),
    }
}

/// CLI 错误信封。字段与历史 `error_payload` 一致，不在这里注入 mux `id`。
pub(crate) fn error_payload(err: &CliError) -> Value {
    let mut payload = json!({
        "ok": false,
        "error": err.message(),
        "error_type": err.error_type(),
        "exitCode": err.exit_code(),
        "help": err.help(),
    });
    if let CliError::ProtocolMismatch { expected, actual } = err {
        if let Some(obj) = payload.as_object_mut() {
            obj.insert(
                "result".into(),
                json!({ "expected": expected, "actual": actual }),
            );
        }
    }
    if let CliError::JobFinished { snapshot, .. } | CliError::DomainFinished { snapshot, .. } = err
    {
        if let Some(obj) = payload.as_object_mut() {
            obj.insert("result".into(), snapshot.clone());
        }
    }
    // 编译失败诊断：standalone 与 mux 共用此载荷。匹配编译失败码时附加
    // compiled:false 与错误摘要，调用方直接读 compiled 即可，无需再解析文本。
    if let CliError::Broker { code, message } = err {
        if code == "compile_error" || code == "compilation_failed" {
            if let Some(obj) = payload.as_object_mut() {
                obj.insert("compiled".into(), json!(false));
                obj.insert("compiledErrorType".into(), json!(code));
                obj.insert("compiledError".into(), json!(message));
            }
        }
    }
    payload
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn fixture_root() -> std::path::PathBuf {
        std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../protocol/fixtures")
    }

    fn load_fixture(relative: &str) -> Value {
        let path = fixture_root().join(relative);
        serde_json::from_str(&std::fs::read_to_string(&path).unwrap())
            .unwrap_or_else(|error| panic!("{}: {error}", path.display()))
    }

    #[test]
    fn response_fixtures_use_real_pipe_error_precedence() {
        let mismatch = load_fixture("response/err_protocol_mismatch.json");
        let err = classify_pipe_response(&mismatch["wire"]).unwrap_err();
        assert_eq!(err.error_type(), "protocol_mismatch");
        assert_eq!(err.message(), mismatch["wire"]["error"].as_str().unwrap());
        assert_eq!(err.exit_code(), 1);
        let payload = crate::wire::error_payload(&CliError::ProtocolMismatch {
            expected: 1,
            actual: 2,
        });
        assert_eq!(
            payload["result"]["expected"],
            mismatch["wire"]["result"]["expected"]
        );
        assert_eq!(
            payload["result"]["actual"],
            mismatch["wire"]["result"]["actual"]
        );

        let bare = load_fixture("response/err_managed_not_ready.json");
        assert_eq!(
            classify_pipe_response(&bare["wire"])
                .unwrap_err()
                .error_type(),
            "managed_not_ready"
        );

        let typed_wins = json!({
            "reply_to": "r",
            "ok": false,
            "error": "managed_not_ready",
            "error_type": "compile_error"
        });
        let typed = classify_pipe_response(&typed_wins).unwrap_err();
        assert_eq!(typed.error_type(), "compile_error");
        assert_eq!(typed.message(), "managed_not_ready");

        let raw = json!({"reply_to": "r", "ok": false, "error": "unauthorized"});
        assert_eq!(
            classify_pipe_response(&raw).unwrap_err().error_type(),
            "execution_failed"
        );

        let missing_ok = load_fixture("response/missing_ok.json");
        let malformed = classify_pipe_response(&missing_ok["wire"]).unwrap_err();
        assert_eq!(malformed.error_type(), "execution_failed");
        assert_eq!(
            malformed.message(),
            missing_ok["wire"]["error"].as_str().unwrap()
        );

        let null_result = load_fixture("response/ok_result_null.json");
        assert_eq!(
            classify_pipe_response(&null_result["wire"]).unwrap(),
            Value::Null
        );
        let extra = load_fixture("response/unknown_extra_field.json");
        assert_eq!(classify_pipe_response(&extra["wire"]).unwrap()["value"], 1);
    }

    #[test]
    fn capabilities_fixture_version_uses_handshake_rule() {
        let supported = load_fixture("status/capabilities_v1.json");
        let unsupported = load_fixture("status/capabilities_v2_mismatch.json");
        let supported_version = supported["wire"]["protocolVersion"].as_i64().unwrap() as i32;
        let unsupported_version = unsupported["wire"]["protocolVersion"].as_i64().unwrap() as i32;
        assert_eq!(supported_version, crate::EXPECTED_PROTOCOL_VERSION);
        assert_ne!(unsupported_version, crate::EXPECTED_PROTOCOL_VERSION);
        let err = CliError::ProtocolMismatch {
            expected: crate::EXPECTED_PROTOCOL_VERSION,
            actual: unsupported_version,
        };
        assert_eq!(
            err.error_type(),
            unsupported["expect"]["errorType"].as_str().unwrap()
        );
        assert_eq!(
            err.exit_code(),
            unsupported["expect"]["exitCode"].as_i64().unwrap() as i32
        );
    }
}
