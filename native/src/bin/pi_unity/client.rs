use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde::Deserialize;
use serde_json::{json, Value};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};

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
    Usage { error: String, help: Vec<String> },
    Other(String),
}

impl CliError {
    pub(crate) fn exit_code(&self) -> i32 {
        match self {
            CliError::Usage { .. } => 2,
            CliError::ExecutionFailed(_) => 1,
            CliError::BridgeNotFound(_) => 1,
            CliError::Timeout(_) => 1,
            CliError::Other(_) => 1,
        }
    }

    pub(crate) fn error_type(&self) -> &'static str {
        match self {
            CliError::ExecutionFailed(_) => "execution_failed",
            CliError::BridgeNotFound(_) => "bridge_not_found",
            CliError::Timeout(_) => "timeout",
            CliError::Usage { .. } => "usage",
            CliError::Other(_) => "other",
        }
    }

    pub(crate) fn message(&self) -> &str {
        match self {
            CliError::ExecutionFailed(msg) => msg,
            CliError::BridgeNotFound(msg) => msg,
            CliError::Timeout(msg) => msg,
            CliError::Usage { error, .. } => error,
            CliError::Other(msg) => msg,
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
            CliError::ExecutionFailed(_) | CliError::Other(_) => Vec::new(),
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

fn remaining_ms(deadline: Instant) -> u64 {
    deadline
        .saturating_duration_since(Instant::now())
        .as_millis() as u64
}

pub(crate) struct HarnessClient {
    project_root: PathBuf,
    bridge: BridgeJson,
    req_counter: u64,
    recorder: Arc<TraceRecorder>,
    persistent: bool,
    connection: Option<BufReader<NamedPipeClient>>,
    handshake_done: bool,
    #[cfg(test)]
    connect_count: u64,
    #[cfg(test)]
    handshake_count: u64,
}

impl HarnessClient {
    pub(crate) fn new(
        project_root: PathBuf,
        recorder: Arc<TraceRecorder>,
    ) -> Result<Self, CliError> {
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
            persistent: false,
            connection: None,
            handshake_done: false,
            #[cfg(test)]
            connect_count: 0,
            #[cfg(test)]
            handshake_count: 0,
        })
    }

    pub(crate) fn new_persistent(
        project_root: PathBuf,
        recorder: Arc<TraceRecorder>,
    ) -> Result<Self, CliError> {
        let mut client = Self::new(project_root, recorder)?;
        client.persistent = true;
        Ok(client)
    }

    pub(crate) fn set_recorder(&mut self, recorder: Arc<TraceRecorder>) {
        self.recorder = recorder;
    }

    pub(crate) fn disconnect(&mut self) {
        self.connection = None;
        self.handshake_done = false;
    }

    pub(crate) fn is_connected(&self) -> bool {
        self.connection.is_some()
    }

    #[cfg(test)]
    pub(crate) fn connect_count(&self) -> u64 {
        self.connect_count
    }

    #[cfg(test)]
    pub(crate) fn handshake_count(&self) -> u64 {
        self.handshake_count
    }

    pub(crate) fn reload_bridge(&mut self) -> Result<(), CliError> {
        let next = super::discovery::load_bridge_json(&self.project_root)?;
        if next.pipe != self.bridge.pipe
            || next.token != self.bridge.token
            || next.generation != self.bridge.generation
        {
            self.connection = None;
            self.handshake_done = false;
        }
        self.bridge = next;
        Ok(())
    }

    pub(crate) async fn handshake(&mut self) -> Result<(), CliError> {
        self.handshake_with_timeout(5000).await
    }

    pub(crate) async fn handshake_with_timeout(&mut self, timeout_ms: u64) -> Result<(), CliError> {
        if self.persistent && self.handshake_done && self.connection.is_some() {
            return Ok(());
        }
        self.recorder.record(
            "handshake",
            "Probing bridge_capabilities protocol handshake",
        );
        #[cfg(test)]
        {
            self.handshake_count += 1;
        }
        let caps = match self
            .exchange("bridge_capabilities", json!({}), timeout_ms)
            .await
        {
            Ok(caps) => caps,
            Err(err) => {
                self.disconnect();
                return Err(err);
            }
        };
        let version = caps
            .get("protocolVersion")
            .and_then(Value::as_i64)
            .unwrap_or(0) as i32;
        if version != EXPECTED_PROTOCOL_VERSION {
            self.disconnect();
            return Err(CliError::Other(format!(
                "Protocol version mismatch: Broker reported version {}, but CLI expected version {}.",
                version, EXPECTED_PROTOCOL_VERSION
            )));
        }
        if self.persistent {
            self.handshake_done = true;
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
        if self.persistent
            && req_type != "bridge_capabilities"
            && (!self.handshake_done || self.connection.is_none())
        {
            self.handshake_with_timeout(timeout_ms).await?;
        }
        self.exchange(req_type, payload, timeout_ms).await
    }

    async fn exchange(
        &mut self,
        req_type: &str,
        payload: Value,
        timeout_ms: u64,
    ) -> Result<Value, CliError> {
        let deadline = Instant::now() + Duration::from_millis(timeout_ms.max(50));
        self.req_counter += 1;
        let id = format!(
            "cli-{}-{}-{}",
            std::process::id(),
            self.req_counter,
            now_ms()
        );
        self.recorder.add_request_id(id.clone());

        let mut pipe = match self.connection.take() {
            Some(existing) => existing,
            None => match self.open_pipe(remaining_ms(deadline).max(50)).await {
                Ok(opened) => opened,
                Err(err) => {
                    self.disconnect();
                    return Err(err);
                }
            },
        };

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

        let mut line_bytes = match serde_json::to_vec(&frame) {
            Ok(bytes) => bytes,
            Err(e) => {
                self.disconnect();
                return Err(CliError::Other(format!(
                    "Failed to serialize request frame: {}",
                    e
                )));
            }
        };
        line_bytes.push(b'\n');

        let io_fut = async {
            pipe.get_mut().write_all(&line_bytes).await?;
            pipe.get_mut().flush().await?;
            let mut line = String::new();
            while pipe.read_line(&mut line).await? > 0 {
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

        let left = remaining_ms(deadline).max(1);
        let resp = match tokio::time::timeout(Duration::from_millis(left), io_fut).await {
            Ok(Ok(resp)) => resp,
            Ok(Err(e)) => {
                drop(pipe);
                self.disconnect();
                return Err(CliError::ExecutionFailed(format!(
                    "I/O error reading from Named Pipe: {}",
                    e
                )));
            }
            Err(_) => {
                drop(pipe);
                self.disconnect();
                return Err(CliError::Timeout(format!(
                    "Request '{}' (id: {}) timed out after {}ms",
                    req_type, id, timeout_ms
                )));
            }
        };

        if self.persistent {
            self.connection = Some(pipe);
        }

        let ok = resp.get("ok").and_then(Value::as_bool).unwrap_or(false);
        if ok {
            Ok(resp.get("result").cloned().unwrap_or(Value::Null))
        } else {
            let err_msg = resp
                .get("error")
                .and_then(Value::as_str)
                .unwrap_or("Unknown broker error")
                .to_string();
            Err(CliError::ExecutionFailed(err_msg))
        }
    }

    async fn open_pipe(&mut self, timeout_ms: u64) -> Result<BufReader<NamedPipeClient>, CliError> {
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
                            && start.elapsed() < Duration::from_millis(timeout_ms.min(3000)) =>
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

        let pipe_client =
            tokio::time::timeout(Duration::from_millis(timeout_ms.max(50)), connect_fut)
                .await
                .map_err(|_| {
                    CliError::BridgeNotFound(format!(
                        "连接 Named Pipe {} 超时（{}ms）",
                        pipe_name, timeout_ms
                    ))
                })??;

        #[cfg(test)]
        {
            self.connect_count += 1;
        }
        Ok(BufReader::new(pipe_client))
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;
    use std::fs;
    use std::sync::atomic::{AtomicU32, Ordering};
    use std::sync::Arc as StdArc;
    use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
    use tokio::net::windows::named_pipe::{PipeMode, ServerOptions};

    #[tokio::test(flavor = "multi_thread")]
    async fn persistent_client_reuses_one_pipe_for_handshake_and_two_status() {
        let pid = std::process::id();
        let pipe_leaf = format!("pi_unity_mux_test_{pid}");
        let pipe_name = format!(r"\\.\pipe\{pipe_leaf}");
        let root = std::env::temp_dir().join(format!("pi-unity-mux-client-{pid}"));
        let bridge_dir = root.join("Library/PiUnityHarness");
        fs::create_dir_all(&bridge_dir).unwrap();
        fs::write(
            bridge_dir.join("bridge.json"),
            format!(r#"{{"pipe":"{pipe_name}","token":"test-token","generation":1}}"#),
        )
        .unwrap();

        let accept_count = StdArc::new(AtomicU32::new(0));
        let caps_count = StdArc::new(AtomicU32::new(0));
        let status_count = StdArc::new(AtomicU32::new(0));
        let server_accept = accept_count.clone();
        let server_caps = caps_count.clone();
        let server_status = status_count.clone();
        let server_pipe = pipe_name.clone();

        let server = tokio::spawn(async move {
            let server = ServerOptions::new()
                .access_inbound(true)
                .access_outbound(true)
                .pipe_mode(PipeMode::Byte)
                .create(&server_pipe)
                .unwrap();
            server.connect().await.unwrap();
            server_accept.fetch_add(1, Ordering::SeqCst);
            let (reader, mut writer) = tokio::io::split(server);
            let mut lines = BufReader::new(reader).lines();
            while let Ok(Some(line)) = lines.next_line().await {
                let req: Value = serde_json::from_str(&line).unwrap();
                let id = req.get("id").and_then(Value::as_str).unwrap_or("");
                let req_type = req.get("type").and_then(Value::as_str).unwrap_or("");
                let result = match req_type {
                    "bridge_capabilities" => {
                        server_caps.fetch_add(1, Ordering::SeqCst);
                        json!({"protocolVersion": 1})
                    }
                    "status" => {
                        server_status.fetch_add(1, Ordering::SeqCst);
                        json!({"managedState": "ready"})
                    }
                    other => panic!("unexpected request {other}"),
                };
                let reply = json!({"reply_to": id, "ok": true, "result": result});
                let mut bytes = serde_json::to_vec(&reply).unwrap();
                bytes.push(b'\n');
                writer.write_all(&bytes).await.unwrap();
                writer.flush().await.unwrap();
                if server_status.load(Ordering::SeqCst) >= 2 {
                    break;
                }
            }
        });

        let recorder = Arc::new(TraceRecorder::new("mux-test"));
        let mut client = HarnessClient::new_persistent(root.clone(), recorder).unwrap();
        let run = async {
            client.handshake_with_timeout(3000).await.unwrap();
            let a = client
                .send_request("status", json!({}), 3000)
                .await
                .unwrap();
            let b = client
                .send_request("status", json!({}), 3000)
                .await
                .unwrap();
            assert_eq!(a["managedState"], "ready");
            assert_eq!(b["managedState"], "ready");
            client.disconnect();
        };
        tokio::time::timeout(Duration::from_secs(8), run)
            .await
            .expect("client timed out");
        tokio::time::timeout(Duration::from_secs(8), server)
            .await
            .expect("server timed out")
            .unwrap();
        assert_eq!(accept_count.load(Ordering::SeqCst), 1);
        assert_eq!(caps_count.load(Ordering::SeqCst), 1);
        assert_eq!(status_count.load(Ordering::SeqCst), 2);
        let _ = fs::remove_dir_all(root);
    }
}
