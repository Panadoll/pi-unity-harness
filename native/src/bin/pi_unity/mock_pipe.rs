//! 测试用脚本化 named pipe broker（仅 Windows 测试）。
//! 用法：MockBroker::spawn(script, stop_after)；script 接收请求 Value 返回回复 Value，
//! 返回 {"__mock_close_connection": true} 表示不回复并断开连接（模拟 I/O 错误）。

#![cfg(all(test, windows))]

use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use serde_json::{json, Value};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::windows::named_pipe::{PipeMode, ServerOptions};

pub(crate) const CLOSE_MARKER: &str = "__mock_close_connection";

/// 进程内递增序号：保证并行测试在同一毫秒创建多个 mock broker 时管道名互不串台。
static MOCK_PIPE_SEQ: AtomicU32 = AtomicU32::new(0);

pub(crate) struct MockBroker {
    pub pipe_name: String,
    pub bridge_dir: PathBuf,
    pub counters: Arc<Mutex<HashMap<String, u32>>>,
    pub accepted: Arc<AtomicU32>,
    pub handle: tokio::task::JoinHandle<()>,
}

impl MockBroker {
    /// 脚本化 broker：script(&req) -> reply（若返回 {"__mock_close_connection": true} 则不回复并断开连接）。
    /// stop_after_requests: Some(n) 时处理完 n 个请求后整个 broker 退出。
    pub fn spawn(
        script: impl Fn(&Value) -> Value + Send + 'static,
        stop_after_requests: Option<usize>,
    ) -> Self {
        let pid = std::process::id();
        let stamp = crate::logging::now_ms();
        // fast_rand_id 是 pid/time 的确定性哈希，同毫秒会重复；用进程内递增序号保证唯一。
        let seq = MOCK_PIPE_SEQ.fetch_add(1, Ordering::SeqCst);
        let leaf = format!("pi_unity_mock_{pid}_{stamp}_{seq}");
        let pipe_name = format!(r"\\.\pipe\{leaf}");
        let bridge_dir = std::env::temp_dir().join(format!("pi-unity-mock-{pid}-{stamp}-{seq}"));

        let counters = Arc::new(Mutex::new(HashMap::new()));
        let accepted = Arc::new(AtomicU32::new(0));
        let counters_clone = counters.clone();
        let accepted_clone = accepted.clone();
        let pipe_name_clone = pipe_name.clone();

        let handle = tokio::spawn(async move {
            let mut processed: usize = 0;
            loop {
                let server = match ServerOptions::new()
                    .access_inbound(true)
                    .access_outbound(true)
                    .pipe_mode(PipeMode::Byte)
                    .create(&pipe_name_clone)
                {
                    Ok(server) => server,
                    Err(_) => {
                        tokio::time::sleep(Duration::from_millis(50)).await;
                        continue;
                    }
                };
                match server.connect().await {
                    Ok(()) => {
                        accepted_clone.fetch_add(1, Ordering::SeqCst);
                        let (reader, mut writer) = tokio::io::split(server);
                        let mut lines = BufReader::new(reader).lines();
                        while let Ok(Some(line)) = lines.next_line().await {
                            let req: Value = match serde_json::from_str(&line) {
                                Ok(v) => v,
                                Err(_) => continue,
                            };
                            let req_type = req.get("type").and_then(Value::as_str).unwrap_or("");
                            if !req_type.is_empty() {
                                if let Ok(mut map) = counters_clone.lock() {
                                    *map.entry(req_type.to_string()).or_insert(0) += 1;
                                }
                                processed += 1;
                            }
                            let mut reply = script(&req);
                            if reply.get(CLOSE_MARKER).and_then(Value::as_bool) == Some(true) {
                                break; // 不回复，断开连接
                            }
                            if let Some(obj) = reply.as_object_mut() {
                                if !obj.contains_key("reply_to") {
                                    let id = req.get("id").and_then(Value::as_str).unwrap_or("");
                                    obj.insert("reply_to".to_string(), Value::String(id.to_string()));
                                }
                            }
                            let mut bytes = match serde_json::to_vec(&reply) {
                                Ok(b) => b,
                                Err(_) => continue,
                            };
                            bytes.push(b'\n');
                            if writer.write_all(&bytes).await.is_err() {
                                break;
                            }
                            if writer.flush().await.is_err() {
                                break;
                            }
                            if let Some(stop) = stop_after_requests {
                                if processed >= stop {
                                    return;
                                }
                            }
                        }
                        if let Some(stop) = stop_after_requests {
                            if processed >= stop {
                                return;
                            }
                        }
                    }
                    Err(_) => {
                        tokio::time::sleep(Duration::from_millis(50)).await;
                    }
                }
            }
        });

        Self {
            pipe_name,
            bridge_dir,
            counters,
            accepted,
            handle,
        }
    }

    pub fn spawn_default(script: impl Fn(&Value) -> Value + Send + 'static) -> Self {
        Self::spawn(script, None)
    }

    /// 写入 bridge.json 并返回内容（token 固定为 mock-secret-token，绝不回显在错误里）。
    pub fn write_bridge(&self, pid: Option<u32>) -> Value {
        fs::create_dir_all(self.bridge_dir.join("Library/PiUnityHarness")).unwrap();
        let bridge = json!({
            "pipe": self.pipe_name,
            "token": "mock-secret-token",
            "generation": 1,
            "pid": pid,
        });
        fs::write(
            self.bridge_dir.join("Library/PiUnityHarness/bridge.json"),
            serde_json::to_vec(&bridge).unwrap(),
        )
        .unwrap();
        bridge
    }

    pub fn count(&self, req_type: &str) -> u32 {
        self.counters
            .lock()
            .map(|m| m.get(req_type).copied().unwrap_or(0))
            .unwrap_or(0)
    }
}

impl Drop for MockBroker {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.bridge_dir);
        self.handle.abort();
    }
}

pub(crate) fn ready_status() -> Value {
    json!({
        "managedState": "ready",
        "managedGeneration": 7,
        "editorStatus": "editing",
    })
}

pub(crate) fn reloading_status() -> Value {
    json!({
        "managedState": "reloading",
        "managedGeneration": 6,
        "editorStatus": "editing",
    })
}

pub(crate) fn caps_reply() -> Value {
    json!({"ok": true, "result": {"protocolVersion": 1}})
}