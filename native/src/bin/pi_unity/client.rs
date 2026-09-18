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
    #[allow(dead_code)]
    project: Option<String>,
    pid: Option<u32>,
    pipe: String,
    token: String,
    generation: Option<i64>,
    #[serde(rename = "statePlaneName")]
    #[allow(dead_code)]
    state_plane_name: Option<String>,
}

#[derive(Debug, PartialEq, Eq)]
pub(crate) enum CliError {
    ExecutionFailed(String),
    BridgeNotFound(String),
    Timeout(String),
    Usage { error: String, help: Vec<String> },
    Other(String),
    /// 结构化 broker/Unity 侧错误：code 保存错误码（managed_reloading、command_error、compilation_failed...）。
    Broker { code: String, message: String },
    /// 管道忙（ERROR_PIPE_BUSY / 231）：单客户端架构下已有客户端占用。
    Busy(String),
}

impl CliError {
    pub(crate) fn exit_code(&self) -> i32 {
        match self {
            CliError::Usage { .. } => 2,
            CliError::ExecutionFailed(_)
            | CliError::BridgeNotFound(_)
            | CliError::Timeout(_)
            | CliError::Other(_)
            | CliError::Broker { .. }
            | CliError::Busy(_) => 1,
        }
    }

    pub(crate) fn error_type(&self) -> String {
        match self {
            CliError::ExecutionFailed(_) => "execution_failed".to_string(),
            CliError::BridgeNotFound(_) => "bridge_not_found".to_string(),
            CliError::Timeout(_) => "timeout".to_string(),
            CliError::Usage { .. } => "usage".to_string(),
            CliError::Other(_) => "other".to_string(),
            CliError::Broker { code, .. } => code.clone(),
            CliError::Busy(_) => "busy".to_string(),
        }
    }

    pub(crate) fn message(&self) -> &str {
        match self {
            CliError::ExecutionFailed(msg) => msg,
            CliError::BridgeNotFound(msg) => msg,
            CliError::Timeout(msg) => msg,
            CliError::Usage { error, .. } => error,
            CliError::Other(msg) => msg,
            CliError::Broker { message, .. } => message,
            CliError::Busy(msg) => msg,
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
                "同一时间只允许一个客户端连接 Unity 管道（单客户端架构）".to_string(),
                "等待当前请求完成或稍后重试，不要并发多个 pi-unity 客户端".to_string(),
            ],
            CliError::ExecutionFailed(_) | CliError::Other(_) | CliError::Broker { .. } => {
                Vec::new()
            }
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

/// 预分发错误（managed_reloading / managed_not_ready）的重试间隔。
const RETRY_POLL_MS: u64 = 300;

/// broker 原生裸错误码白名单：native broker 只发 `error` 字段（无 error_type）且只发这些码。
/// 白名单外的一律按 execution_failed 处理，绝不把任意原始错误文本当 error_type 透出。
const BROKER_NATIVE_CODES: &[&str] = &[
    "managed_not_ready",
    "managed_reloading",
    "managed_quitting",
    "request_timeout_in_flight",
    "managed_heartbeat_timeout",
];

pub(crate) fn remaining_ms(deadline: Instant) -> u64 {
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
        let deadline = Instant::now() + Duration::from_millis(timeout_ms);
        self.handshake_with_deadline(deadline).await
    }

    async fn handshake_with_deadline(&mut self, deadline: Instant) -> Result<(), CliError> {
        if self.persistent && self.handshake_done && self.connection.is_some() {
            return Ok(());
        }
        self.recorder.record(
            "handshake",
            "Probing bridge_capabilities protocol handshake",
        );
        let caps = match self
            .exchange("bridge_capabilities", json!({}), deadline)
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
        // 整个请求（连接 + 握手 + 重试）共享同一个 deadline，绝不按重试轮次重置。
        let deadline = Instant::now() + Duration::from_millis(timeout_ms);
        loop {
            if self.persistent
                && req_type != "bridge_capabilities"
                && (!self.handshake_done || self.connection.is_none())
            {
                self.reload_bridge()?;
                if let Err(e) = self.handshake_with_deadline(deadline).await {
                    return Err(e);
                }
            }
            match self.exchange(req_type, payload.clone(), deadline).await {
                Ok(v) => return Ok(v),
                // 只重试 broker 明确拒绝且未派发的预分发错误（broker 保证
                // managed_reloading / managed_not_ready 不会入队）。
                Err(CliError::Broker { code, message })
                    if code == "managed_reloading" || code == "managed_not_ready" =>
                {
                    let remaining = remaining_ms(deadline);
                    if remaining < RETRY_POLL_MS + 100 {
                        return Err(CliError::Broker {
                            code,
                            message: format!("{}（重试预算耗尽）", message),
                        });
                    }
                    self.recorder.record(
                        "retry",
                        &format!("{} 收到预分发 {}，等待后重试", req_type, code),
                    );
                    tokio::time::sleep(Duration::from_millis(RETRY_POLL_MS)).await;
                }
                // 不确定的 I/O 超时/断连、已派发业务请求等绝不重放。
                Err(e) => return Err(e),
            }
        }
    }

    async fn exchange(
        &mut self,
        req_type: &str,
        payload: Value,
        deadline: Instant,
    ) -> Result<Value, CliError> {
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
            None => match self.open_pipe(deadline).await {
                Ok(opened) => opened,
                Err(err) => {
                    self.disconnect();
                    return Err(err);
                }
            },
        };

        // 连接/握手可能已耗尽预算：预算耗尽绝不写业务帧（否则 broker 会把它当成可派发请求）。
        let remaining = remaining_ms(deadline);
        if remaining == 0 {
            drop(pipe);
            self.disconnect();
            return Err(CliError::Timeout(format!(
                "请求 '{}' (id: {}) 预算在连接/握手阶段耗尽，未派发",
                req_type, id
            )));
        }
        // 帧内保留剩余预算（不按重试轮次重置），broker 据此决定入队/在途超时。
        let frame_timeout_ms = remaining;
        let frame = json!({
            "id": id,
            "type": req_type,
            "token": self.bridge.token,
            "timeoutMs": frame_timeout_ms,
            "payload": payload,
        });

        self.recorder.record_details(
            "request_send",
            &format!("Sending request {}", req_type),
            json!({
                "id": id,
                "timeoutMs": frame_timeout_ms,
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

        let left = remaining_ms(deadline);
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
                    req_type, id, frame_timeout_ms
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
            // 错误码只信两类来源：
            //  - error_type：Unity/C# 侧显式类型（其值才是码，error 只是消息文本）；
            //  - error 字段命中 broker 原生裸码白名单（native broker 只发这些码）；
            // 其他任意原始错误文本一律 execution_failed，绝不当 error_type 透出。
            let typed = resp
                .get("error_type")
                .and_then(Value::as_str)
                .map(|s| s.to_string());
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
    }

    async fn open_pipe(&mut self, deadline: Instant) -> Result<BufReader<NamedPipeClient>, CliError> {
        let pipe_name = normalize_pipe_name(&self.bridge.pipe);
        let recorder = self.recorder.clone();
        let pipe_name_clone = pipe_name.clone();
        let project_root = self.project_root.clone();
        let broker_pid = self.bridge.pid;

        let connect_fut = async {
            let start = Instant::now();
            loop {
                match ClientOptions::new().open(&pipe_name_clone) {
                    Ok(client) => return Ok::<_, CliError>(client),
                    // 231 = ERROR_PIPE_BUSY：消耗调用方剩余连接预算重试，预算耗尽归类为 busy。
                    Err(e) if e.raw_os_error() == Some(231) => {
                        let remaining = remaining_ms(deadline);
                        if remaining < 100 {
                            return Err(CliError::Busy(format!(
                                "Named Pipe {} 忙（错误 231）：同一时间只允许一个客户端连接 Unity 管道，等待当前请求完成或稍后重试",
                                pipe_name_clone
                            )));
                        }
                        recorder.add_reconnect();
                        recorder.record("pipe", "Pipe busy (231), backing off 50ms...");
                        tokio::time::sleep(Duration::from_millis(50.min(remaining))).await;
                        continue;
                    }
                    // 2 = ERROR_FILE_NOT_FOUND：仅当 bridge 元数据存在且 broker PID 存活时
                    // 短暂重试（≤2s 且不超过剩余预算），否则立即失败。
                    Err(e) if e.raw_os_error() == Some(2) => {
                        let metadata_ok = super::discovery::load_bridge_json(&project_root).is_ok();
                        let pid_alive = broker_pid
                            .map(super::discovery::process_alive)
                            .unwrap_or(false);
                        let remaining = remaining_ms(deadline);
                        let bounded = start.elapsed() < Duration::from_secs(2) && remaining >= 100;
                        if metadata_ok && pid_alive && bounded {
                            recorder.add_reconnect();
                            recorder.record(
                                "pipe",
                                "Pipe not found (2), broker metadata alive, backing off 100ms...",
                            );
                            tokio::time::sleep(Duration::from_millis(100.min(remaining))).await;
                            continue;
                        }
                        return Err(CliError::BridgeNotFound(format!(
                            "无法连接 Named Pipe {}: {}",
                            pipe_name_clone, e
                        )));
                    }
                    Err(e) => {
                        return Err(CliError::BridgeNotFound(format!(
                            "无法连接 Named Pipe {}: {}",
                            pipe_name_clone, e
                        )));
                    }
                }
            }
        };

        let pipe_client = tokio::time::timeout(
            Duration::from_millis(remaining_ms(deadline)),
            connect_fut,
        )
        .await
        .map_err(|_| {
            CliError::BridgeNotFound(format!(
                "连接 Named Pipe {} 超时（剩余预算耗尽）",
                pipe_name
            ))
        })??;

        Ok(BufReader::new(pipe_client))
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;
    use crate::mock_pipe::{caps_reply, ready_status, MockBroker};
    use std::fs;
    use std::sync::atomic::{AtomicU32, Ordering};
    use std::sync::Arc as StdArc;
    use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
    use tokio::net::windows::named_pipe::{PipeMode, ServerOptions};

    fn write_bridge_at(root: &std::path::Path, pipe_name: &str, token: &str, pid: Option<u32>) {
        let bridge_dir = root.join("Library/PiUnityHarness");
        fs::create_dir_all(&bridge_dir).unwrap();
        fs::write(
            bridge_dir.join("bridge.json"),
            serde_json::to_vec(&json!({
                "pipe": pipe_name,
                "token": token,
                "generation": 1,
                "pid": pid,
            }))
            .unwrap(),
        )
        .unwrap();
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn persistent_client_reuses_one_pipe_for_handshake_and_two_status() {
        let pid = std::process::id();
        let stamp = now_ms();
        let pipe_leaf = format!("pi_unity_mux_test_{pid}_{stamp}");
        let pipe_name = format!(r"\\.\pipe\{pipe_leaf}");
        let root = std::env::temp_dir().join(format!("pi-unity-mux-client-{pid}-{stamp}"));
        let bridge_dir = root.join("Library/PiUnityHarness");
        fs::create_dir_all(&bridge_dir).unwrap();
        fs::write(
            bridge_dir.join("bridge.json"),
            serde_json::to_vec(&json!({
                "pipe": pipe_name,
                "token": "test-token",
                "generation": 1
            }))
            .unwrap(),
        )
        .unwrap();

        let accept_count = StdArc::new(AtomicU32::new(0));
        let caps_count = StdArc::new(AtomicU32::new(0));
        let status_count = StdArc::new(AtomicU32::new(0));
        let server_accept = accept_count.clone();
        let server_caps = caps_count.clone();
        let server_status = status_count.clone();

        let listener = ServerOptions::new()
            .access_inbound(true)
            .access_outbound(true)
            .pipe_mode(PipeMode::Byte)
            .create(&pipe_name)
            .unwrap();

        let server = tokio::spawn(async move {
            listener.connect().await.unwrap();
            server_accept.fetch_add(1, Ordering::SeqCst);
            let (reader, mut writer) = tokio::io::split(listener);
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

    #[tokio::test(flavor = "multi_thread")]
    async fn handshake_budget_exhaustion_never_dispatches_business_frame() {
        let pid = std::process::id();
        let stamp = now_ms();
        let leaf = format!("pi_unity_handshake_budget_{pid}_{stamp}");
        let pipe_name = format!(r"\\.\pipe\{leaf}");
        let root = std::env::temp_dir().join(&leaf);
        write_bridge_at(&root, &pipe_name, "budget-secret-token", Some(pid));

        let caps_count = StdArc::new(AtomicU32::new(0));
        let status_count = StdArc::new(AtomicU32::new(0));
        let server_caps = caps_count.clone();
        let server_status = status_count.clone();
        let listener = ServerOptions::new()
            .access_inbound(true)
            .access_outbound(true)
            .pipe_mode(PipeMode::Byte)
            .create(&pipe_name)
            .unwrap();
        let server = tokio::spawn(async move {
            listener.connect().await.unwrap();
            let (reader, mut writer) = tokio::io::split(listener);
            let mut lines = BufReader::new(reader).lines();
            let first = lines.next_line().await.unwrap().unwrap();
            let req: Value = serde_json::from_str(&first).unwrap();
            assert_eq!(req["type"], "bridge_capabilities");
            server_caps.fetch_add(1, Ordering::SeqCst);
            tokio::time::sleep(Duration::from_millis(250)).await;
            let id = req["id"].as_str().unwrap();
            let mut bytes = serde_json::to_vec(&json!({
                "reply_to": id,
                "ok": true,
                "result": {"protocolVersion": 1}
            }))
            .unwrap();
            bytes.push(b'\n');
            if writer.write_all(&bytes).await.is_ok() && writer.flush().await.is_ok() {
                if let Ok(Ok(Some(line))) = tokio::time::timeout(
                    Duration::from_millis(200),
                    lines.next_line(),
                )
                .await
                {
                    let req: Value = serde_json::from_str(&line).unwrap();
                    if req["type"] == "status" {
                        server_status.fetch_add(1, Ordering::SeqCst);
                    }
                }
            }
        });

        let recorder = Arc::new(TraceRecorder::new("handshake-budget-test"));
        let mut client = HarnessClient::new_persistent(root.clone(), recorder).unwrap();
        let err = client
            .send_request("status", json!({}), 100)
            .await
            .unwrap_err();
        assert!(matches!(err, CliError::Timeout(_)), "实际 {err:?}");
        tokio::time::timeout(Duration::from_secs(2), server)
            .await
            .expect("server timed out")
            .unwrap();
        assert_eq!(caps_count.load(Ordering::SeqCst), 1);
        assert_eq!(status_count.load(Ordering::SeqCst), 0, "握手预算耗尽后不得派发 status");
        assert!(!err.message().contains("budget-secret-token"));
        let _ = fs::remove_dir_all(root);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn send_request_retries_pre_dispatch_managed_reloading_within_deadline() {
        let attempts = StdArc::new(AtomicU32::new(0));
        let attempts_clone = attempts.clone();
        let broker = MockBroker::spawn_default(move |req| {
            match req.get("type").and_then(Value::as_str).unwrap_or("") {
                "bridge_capabilities" => caps_reply(),
                "status" => {
                    if attempts_clone.fetch_add(1, Ordering::SeqCst) == 0 {
                        json!({"ok": false, "error": "managed_reloading"})
                    } else {
                        json!({"ok": true, "result": ready_status()})
                    }
                }
                _ => json!({"ok": false, "error": "unsupported"}),
            }
        });
        let _bridge = broker.write_bridge(Some(std::process::id()));
        let recorder = Arc::new(TraceRecorder::new("retry-test"));
        let mut client = HarnessClient::new_persistent(broker.bridge_dir.clone(), recorder).unwrap();
        let started = Instant::now();
        let result = client
            .send_request("status", json!({}), 3000)
            .await
            .expect("预分发 managed_reloading 应在原 deadline 内重试成功");
        assert_eq!(result["managedState"], "ready");
        assert_eq!(attempts.load(Ordering::SeqCst), 2);
        assert!(started.elapsed() < Duration::from_secs(3));
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn send_request_never_replays_dispatch_timeout_or_business_error() {
        let broker = MockBroker::spawn_default(|req| {
            match req.get("type").and_then(Value::as_str).unwrap_or("") {
                "bridge_capabilities" => caps_reply(),
                "status" => json!({"ok": false, "error": "request_timeout_in_flight"}),
                _ => json!({"ok": false, "error": "unsupported"}),
            }
        });
        let _bridge = broker.write_bridge(Some(std::process::id()));
        let recorder = Arc::new(TraceRecorder::new("no-replay-test"));
        let mut client = HarnessClient::new_persistent(broker.bridge_dir.clone(), recorder).unwrap();
        let err = client
            .send_request("status", json!({}), 2000)
            .await
            .unwrap_err();
        match &err {
            CliError::Broker { code, .. } => assert_eq!(code, "request_timeout_in_flight"),
            other => panic!("期望 Broker 错误，实际 {other:?}"),
        }
        assert_eq!(broker.count("status"), 1, "派发超时绝不重放");

        let broker2 = MockBroker::spawn_default(|req| {
            match req.get("type").and_then(Value::as_str).unwrap_or("") {
                "bridge_capabilities" => caps_reply(),
                "command" => {
                    json!({"ok": false, "error_type": "command_error", "error": "boom"})
                }
                _ => json!({"ok": false, "error": "unsupported"}),
            }
        });
        let _bridge2 = broker2.write_bridge(Some(std::process::id()));
        let recorder2 = Arc::new(TraceRecorder::new("no-replay-business"));
        let mut client2 =
            HarnessClient::new_persistent(broker2.bridge_dir.clone(), recorder2).unwrap();
        let err2 = client2
            .send_request("command", json!({"name": "x"}), 2000)
            .await
            .unwrap_err();
        match &err2 {
            CliError::Broker { code, message } => {
                assert_eq!(code, "command_error");
                assert_eq!(message, "boom");
            }
            other => panic!("期望 Broker 错误，实际 {other:?}"),
        }
        assert_eq!(broker2.count("command"), 1, "已派发业务请求绝不重放");
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn send_request_never_replays_io_error_when_server_drops_pipe() {
        let broker = MockBroker::spawn_default(|req| {
            match req.get("type").and_then(Value::as_str).unwrap_or("") {
                "bridge_capabilities" => caps_reply(),
                "status" => json!({"__mock_close_connection": true}),
                _ => json!({"ok": false, "error": "unsupported"}),
            }
        });
        let _bridge = broker.write_bridge(Some(std::process::id()));
        let recorder = Arc::new(TraceRecorder::new("io-error-test"));
        let mut client = HarnessClient::new_persistent(broker.bridge_dir.clone(), recorder).unwrap();
        let err = client
            .send_request("status", json!({}), 2000)
            .await
            .unwrap_err();
        assert!(
            matches!(err, CliError::ExecutionFailed(_)),
            "不确定的 I/O 断连必须透传，不得重放"
        );
        assert_eq!(broker.count("status"), 1);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn send_request_raw_error_text_is_execution_failed_not_broker_code() {
        // 未带 error_type 的任意原始错误文本（如编译器输出）绝不当作 error_type 透出。
        let broker = MockBroker::spawn_default(|req| {
            match req.get("type").and_then(Value::as_str).unwrap_or("") {
                "bridge_capabilities" => caps_reply(),
                "status" => json!({
                    "ok": false,
                    "error": "Assets/Broken.cs(12,5): error CS1234: boom"
                }),
                _ => json!({"ok": false, "error": "unsupported"}),
            }
        });
        let _bridge = broker.write_bridge(Some(std::process::id()));
        let recorder = Arc::new(TraceRecorder::new("raw-text-test"));
        let mut client = HarnessClient::new_persistent(broker.bridge_dir.clone(), recorder).unwrap();
        let err = client
            .send_request("status", json!({}), 2000)
            .await
            .unwrap_err();
        assert!(
            matches!(err, CliError::ExecutionFailed(_)),
            "任意原始错误文本不得当 error_type 透出，实际 {err:?}"
        );
        assert_eq!(err.error_type(), "execution_failed");
        assert!(
            err.message().contains("CS1234"),
            "原始文本应保留在消息里: {}",
            err.message()
        );

        // 同场景下 broker 原生裸码（白名单内）仍应保持结构化 Broker。
        let broker2 = MockBroker::spawn_default(|req| {
            match req.get("type").and_then(Value::as_str).unwrap_or("") {
                "bridge_capabilities" => caps_reply(),
                "status" => json!({"ok": false, "error": "managed_quitting"}),
                _ => json!({"ok": false, "error": "unsupported"}),
            }
        });
        let _bridge2 = broker2.write_bridge(Some(std::process::id()));
        let recorder2 = Arc::new(TraceRecorder::new("naked-code-test"));
        let mut client2 =
            HarnessClient::new_persistent(broker2.bridge_dir.clone(), recorder2).unwrap();
        let err2 = client2
            .send_request("status", json!({}), 2000)
            .await
            .unwrap_err();
        match &err2 {
            CliError::Broker { code, .. } => assert_eq!(code, "managed_quitting"),
            other => panic!("白名单内裸码应保持 Broker，实际 {other:?}"),
        }
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn connect_231_busy_consumes_budget_and_classifies_busy() {
        let pid = std::process::id();
        let stamp = now_ms();
        let leaf = format!("pi_unity_busy_{pid}_{stamp}_{}", crate::logging::fast_rand_id());
        let pipe_name = format!(r"\\.\pipe\{leaf}");
        let server = ServerOptions::new()
            .access_inbound(true)
            .access_outbound(true)
            .pipe_mode(PipeMode::Byte)
            .create(&pipe_name)
            .unwrap();
        let hold = tokio::spawn(async move {
            let _ = server.connect().await;
            tokio::time::sleep(Duration::from_millis(2500)).await;
        });
        let pipe_name_hold = pipe_name.clone();
        let hold_client = tokio::spawn(async move {
            let c1 = ClientOptions::new().open(&pipe_name_hold).unwrap();
            tokio::time::sleep(Duration::from_millis(2500)).await;
            drop(c1);
        });
        tokio::time::sleep(Duration::from_millis(150)).await;

        let root = std::env::temp_dir().join(format!("pi-unity-busy-{pid}-{stamp}"));
        write_bridge_at(&root, &pipe_name, "busy-secret-token", Some(pid));
        let recorder = Arc::new(TraceRecorder::new("busy-test"));
        let mut client = HarnessClient::new(root.clone(), recorder).unwrap();
        let started = Instant::now();
        let err = client
            .send_request("status", json!({}), 500)
            .await
            .unwrap_err();
        let elapsed = started.elapsed();
        assert!(
            matches!(err, CliError::Busy(_)),
            "231 预算耗尽应归类 busy，实际 {err:?}"
        );
        let msg = err.message();
        assert!(
            msg.contains("单客户端")
                || msg.contains("等待当前请求完成")
                || msg.contains("稍后重试"),
            "应含可操作的单客户端帮助: {msg}"
        );
        assert!(!msg.contains("busy-secret-token"), "不得打印 token: {msg}");
        assert!(elapsed >= Duration::from_millis(300), "应消耗调用方预算 {elapsed:?}");
        assert!(elapsed < Duration::from_millis(2000), "预算不应超限 {elapsed:?}");
        let _ = fs::remove_dir_all(root);
        drop(hold_client);
        drop(hold);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn connect_error2_retries_bounded_when_metadata_and_pid_alive() {
        let pid = std::process::id();
        let stamp = now_ms();
        let root = std::env::temp_dir().join(format!("pi-unity-err2-{pid}-{stamp}"));
        write_bridge_at(
            &root,
            r"\.\pipe\pi_unity_nonexistent_err2",
            "err2-secret-token",
            Some(pid),
        );
        let recorder = Arc::new(TraceRecorder::new("err2-test"));
        let mut client = HarnessClient::new(root.clone(), recorder).unwrap();
        let started = Instant::now();
        let err = client
            .send_request("status", json!({}), 1000)
            .await
            .unwrap_err();
        let elapsed = started.elapsed();
        assert!(
            matches!(err, CliError::BridgeNotFound(_)),
            "元数据存在 + PID 存活时有限重试后应报 BridgeNotFound，实际 {err:?}"
        );
        assert!(elapsed >= Duration::from_millis(700), "应有限重试 {elapsed:?}");
        assert!(elapsed < Duration::from_millis(2500), "不得突破 2s 上限 {elapsed:?}");
        assert!(
            !err.message().contains("err2-secret-token"),
            "不得打印 token: {}",
            err.message()
        );
        let _ = fs::remove_dir_all(root);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn connect_error2_fails_fast_when_pid_dead_or_missing() {
        let pid = std::process::id();
        let stamp = now_ms();

        let root = std::env::temp_dir().join(format!("pi-unity-err2-dead-{pid}-{stamp}"));
        write_bridge_at(&root, r"\.\pipe\pi_unity_nonexistent_err2", "t", Some(u32::MAX - 5));
        let recorder = Arc::new(TraceRecorder::new("err2-dead-test"));
        let mut client = HarnessClient::new(root.clone(), recorder).unwrap();
        let started = Instant::now();
        let err = client
            .send_request("status", json!({}), 2000)
            .await
            .unwrap_err();
        assert!(matches!(err, CliError::BridgeNotFound(_)));
        assert!(
            started.elapsed() < Duration::from_millis(800),
            "PID 已死不重试，应快速失败"
        );
        let _ = fs::remove_dir_all(root);

        let root2 = std::env::temp_dir().join(format!("pi-unity-err2-nopid-{pid}-{stamp}"));
        write_bridge_at(&root2, r"\.\pipe\pi_unity_nonexistent_err2", "t", None);
        let recorder2 = Arc::new(TraceRecorder::new("err2-nopid-test"));
        let mut client2 = HarnessClient::new(root2.clone(), recorder2).unwrap();
        let started2 = Instant::now();
        let err2 = client2
            .send_request("status", json!({}), 2000)
            .await
            .unwrap_err();
        assert!(matches!(err2, CliError::BridgeNotFound(_)));
        assert!(started2.elapsed() < Duration::from_millis(800));
        let _ = fs::remove_dir_all(root2);
    }
}
