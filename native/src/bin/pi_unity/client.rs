use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde::Deserialize;
use serde_json::{json, Value};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::windows::named_pipe::ClientOptions;

use super::logging::{now_ms, TraceRecorder};
use super::EXPECTED_PROTOCOL_VERSION;

#[derive(Debug, Deserialize)]
pub(crate) struct BridgeJson {
    project: Option<String>,
    pid: Option<u32>,
    pipe: String,
    token: String,
    generation: Option<i64>,
    #[serde(rename = "statePlaneName")]
    state_plane_name: Option<String>,
}

#[derive(Debug, PartialEq, Eq)]
pub(crate) enum CliError {
    ExecutionFailed(String),
    BridgeNotFound(String),
    Timeout(String),
    Other(String),
}

impl CliError {
    pub(crate) fn exit_code(&self) -> i32 {
        match self {
            CliError::ExecutionFailed(_) => 1,
            CliError::BridgeNotFound(_) => 2,
            CliError::Timeout(_) => 3,
            CliError::Other(_) => 1,
        }
    }

    pub(crate) fn error_type(&self) -> &'static str {
        match self {
            CliError::ExecutionFailed(_) => "execution_failed",
            CliError::BridgeNotFound(_) => "bridge_not_found",
            CliError::Timeout(_) => "timeout",
            CliError::Other(_) => "other",
        }
    }

    pub(crate) fn message(&self) -> &str {
        match self {
            CliError::ExecutionFailed(msg) => msg,
            CliError::BridgeNotFound(msg) => msg,
            CliError::Timeout(msg) => msg,
            CliError::Other(msg) => msg,
        }
    }
}


pub(crate) fn normalize_pipe_name(pipe_name: &str) -> String {
    let value = pipe_name.trim();
    if value.starts_with(r"\\.\pipe\") {
        value.to_string()
    } else {
        format!(r"\\.\pipe\{}", value.trim_start_matches('\\'))
    }
}

pub(crate) struct HarnessClient {
    project_root: PathBuf,
    bridge: BridgeJson,
    req_counter: u64,
    recorder: Arc<TraceRecorder>,
}

impl HarnessClient {
    pub(crate) fn new(project_root: PathBuf, recorder: Arc<TraceRecorder>) -> Result<Self, CliError> {
        let bridge = super::discovery::load_bridge_json(&project_root)?;
        recorder.record_details(
            "bridge",
            "bridge.json loaded",
            json!({
                "pipe": bridge.pipe,
                "pid": bridge.pid,
                "generation": bridge.generation,
            }),
        );
        Ok(Self {
            project_root,
            bridge,
            req_counter: 0,
            recorder,
        })
    }

    pub(crate) fn reload_bridge(&mut self) -> Result<(), CliError> {
        self.bridge = super::discovery::load_bridge_json(&self.project_root)?;
        Ok(())
    }

    pub(crate) async fn handshake(&mut self) -> Result<(), CliError> {
        self.recorder
            .record("handshake", "Probing bridge_capabilities protocol handshake");
        let caps = self
            .send_request("bridge_capabilities", json!({}), 5000)
            .await?;
        let version = caps
            .get("protocolVersion")
            .and_then(Value::as_i64)
            .unwrap_or(0) as i32;
        if version != EXPECTED_PROTOCOL_VERSION {
            return Err(CliError::Other(format!(
                "Protocol version mismatch: Broker reported version {}, but CLI expected version {}.",
                version, EXPECTED_PROTOCOL_VERSION
            )));
        }
        self.recorder
            .record("handshake", "Protocol handshake verified (v1)");
        Ok(())
    }

    pub(crate) async fn send_request(
        &mut self,
        req_type: &str,
        payload: Value,
        timeout_ms: u64,
    ) -> Result<Value, CliError> {
        self.req_counter += 1;
        let id = format!("cli-{}-{}-{}", std::process::id(), self.req_counter, now_ms());
        self.recorder.add_request_id(id.clone());
        let pipe_name = normalize_pipe_name(&self.bridge.pipe);

        let recorder = self.recorder.clone();
        let pipe_name_clone = pipe_name.clone();

        let connect_fut = async {
            let start = Instant::now();
            loop {
                match ClientOptions::new().open(&pipe_name_clone) {
                    Ok(client) => return Ok::<_, CliError>(client),
                    Err(e)
                        if e.raw_os_error() == Some(231)
                            && start.elapsed() < Duration::from_millis(3000) =>
                    {
                        recorder.add_reconnect();
                        recorder.record("pipe", "Pipe busy (231), backing off 50ms...");
                        tokio::time::sleep(Duration::from_millis(50)).await;
                        continue;
                    }
                    Err(e) => {
                        return Err(CliError::BridgeNotFound(format!(
                            "Failed to connect to Named Pipe {}: {}",
                            pipe_name_clone, e
                        )));
                    }
                }
            }
        };

        let pipe_client = tokio::time::timeout(Duration::from_millis(5000), connect_fut)
            .await
            .map_err(|_| {
                CliError::BridgeNotFound(format!(
                    "Connection to Named Pipe {} timed out after 5000ms",
                    pipe_name
                ))
            })??;

        let (reader, mut writer) = tokio::io::split(pipe_client);
        let mut buf_reader = BufReader::new(reader);

        let frame = json!({
            "id": id,
            "type": req_type,
            "token": self.bridge.token,
            "timeoutMs": timeout_ms,
            "payload": payload,
        });

        self.recorder.record_details(
            "request_send",
            &format!("Sending request {}", req_type),
            json!({
                "id": id,
                "timeoutMs": timeout_ms,
            }),
        );

        let mut line_bytes = serde_json::to_vec(&frame).map_err(|e| {
            CliError::Other(format!("Failed to serialize request frame: {}", e))
        })?;
        line_bytes.push(b'\n');

        writer.write_all(&line_bytes).await.map_err(|e| {
            CliError::BridgeNotFound(format!("Failed to write to Named Pipe: {}", e))
        })?;
        writer.flush().await.map_err(|e| {
            CliError::BridgeNotFound(format!("Failed to flush Named Pipe: {}", e))
        })?;

        let read_fut = async {
            let mut line = String::new();
            while buf_reader.read_line(&mut line).await? > 0 {
                let trimmed = line.trim();
                if trimmed.is_empty() {
                    line.clear();
                    continue;
                }
                let resp_val: Value = match serde_json::from_str(trimmed) {
                    Ok(v) => v,
                    Err(_) => {
                        line.clear();
                        continue;
                    }
                };
                if let Some(reply_to) = resp_val.get("reply_to").and_then(Value::as_str) {
                    if reply_to == id {
                        return Ok(resp_val);
                    }
                }
                line.clear();
            }
            Err(std::io::Error::new(
                std::io::ErrorKind::UnexpectedEof,
                "Pipe closed before response received",
            ))
        };

        let timeout_duration = Duration::from_millis(timeout_ms.max(1000));
        let resp = tokio::time::timeout(timeout_duration, read_fut)
            .await
            .map_err(|_| {
                CliError::Timeout(format!(
                    "Request '{}' (id: {}) timed out after {}ms",
                    req_type, id, timeout_ms
                ))
            })?
            .map_err(|e| {
                CliError::ExecutionFailed(format!("I/O error reading from Named Pipe: {}", e))
            })?;

        let ok = resp.get("ok").and_then(Value::as_bool).unwrap_or(false);
        if ok {
            let result = resp.get("result").cloned().unwrap_or(Value::Null);
            Ok(result)
        } else {
            let err_msg = resp
                .get("error")
                .and_then(Value::as_str)
                .unwrap_or("Unknown broker error")
                .to_string();
            Err(CliError::ExecutionFailed(err_msg))
        }
    }
}
