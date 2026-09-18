//! 真实 CLI 端到端（mock named pipe broker）：
//! compile 编译失败必须 exit 1、stdout JSON ok:false、compiled:false，
//! 并且失败后绝不轮询 status。这里跑的是真正的 pi-unity 二进制（非单元 helper）。

#![cfg(windows)]

use std::fs;
use std::path::PathBuf;
use std::process::Command;
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Duration;

use serde_json::{json, Value};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::windows::named_pipe::{PipeMode, ServerOptions};

static PIPE_SEQ: AtomicU64 = AtomicU64::new(0);

fn bin_exe() -> PathBuf {
    env!("CARGO_BIN_EXE_pi-unity").into()
}

fn unique_leaf(tag: &str) -> String {
    let pid = std::process::id();
    let seq = PIPE_SEQ.fetch_add(1, Ordering::SeqCst);
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    format!("pi_unity_{tag}_{pid}_{nanos}_{seq}")
}

/// 脚本化 broker：bridge_capabilities 握手成功；recompile 返回真实形状的编译失败；
/// PiUnityCompileCoordinator 内部结果码 compilation_failed 会由
/// PiUnityBridge.CompleteCompileResult 映射为 wire-level compile_error；
/// status 计数（正常流程不应出现）。
fn spawn_mock_broker() -> (
    String,
    PathBuf,
    Arc<AtomicU32>,
    Arc<AtomicU32>,
    tokio::task::JoinHandle<()>,
) {
    let leaf = unique_leaf("cli_mock");
    let pipe_name = format!(r"\\.\pipe\{leaf}");
    let bridge_dir = std::env::temp_dir().join(&leaf);

    let status_count = Arc::new(AtomicU32::new(0));
    let recompile_count = Arc::new(AtomicU32::new(0));
    let status_clone = status_count.clone();
    let recompile_clone = recompile_count.clone();
    let pipe_name_clone = pipe_name.clone();

    let handle = tokio::spawn(async move {
        loop {
            // 客户端每次请求都可能重开连接（standalone 非持久连接）。
            let server = match ServerOptions::new()
                .access_inbound(true)
                .access_outbound(true)
                .pipe_mode(PipeMode::Byte)
                .create(&pipe_name_clone)
            {
                Ok(s) => s,
                Err(_) => {
                    tokio::time::sleep(Duration::from_millis(50)).await;
                    continue;
                }
            };
            match server.connect().await {
                Ok(()) => {
                    let (reader, mut writer) = tokio::io::split(server);
                    let mut lines = BufReader::new(reader).lines();
                    while let Ok(Some(line)) = lines.next_line().await {
                        let req: Value = match serde_json::from_str(&line) {
                            Ok(v) => v,
                            Err(_) => continue,
                        };
                        let id = req.get("id").and_then(Value::as_str).unwrap_or("");
                        let req_type = req.get("type").and_then(Value::as_str).unwrap_or("");
                        let mut reply = match req_type {
                            "bridge_capabilities" => {
                                json!({"ok": true, "result": {"protocolVersion": 1}})
                            }
                            "recompile" => {
                                recompile_clone.fetch_add(1, Ordering::SeqCst);
                                json!({
                                    "ok": false,
                                    "error_type": "compile_error",
                                    "error": "Assets/Broken.cs(12,5): error CS1234: boom"
                                })
                            }
                            "status" => {
                                status_clone.fetch_add(1, Ordering::SeqCst);
                                json!({"ok": true, "result": {"managedState": "ready"}})
                            }
                            _ => json!({"ok": false, "error": "unsupported"}),
                        };
                        if let Some(obj) = reply.as_object_mut() {
                            if !obj.contains_key("reply_to") {
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
                    }
                }
                Err(_) => {}
            }
        }
    });

    (pipe_name, bridge_dir, status_count, recompile_count, handle)
}

fn write_bridge(bridge_dir: &std::path::Path, pipe_name: &str) {
    fs::create_dir_all(bridge_dir.join("Library/PiUnityHarness")).unwrap();
    fs::write(
        bridge_dir.join("Library/PiUnityHarness/bridge.json"),
        serde_json::to_vec(&json!({
            "pipe": pipe_name,
            "token": "mock-secret-token",
            "generation": 1,
            "pid": Some(std::process::id()),
        }))
        .unwrap(),
    )
    .unwrap();
}

#[tokio::test(flavor = "multi_thread")]
async fn compile_failure_exits_1_compiled_false_and_never_polls_status() {
    let (pipe_name, bridge_dir, status_count, recompile_count, _server) = spawn_mock_broker();
    write_bridge(&bridge_dir, &pipe_name);

    let project = bridge_dir.clone();
    let output = tokio::task::spawn_blocking(move || {
        Command::new(bin_exe())
            .args([
                "--project-path",
                project.to_str().unwrap(),
                "compile",
                "--json",
                "--timeout",
                "5000",
            ])
            .output()
            .expect("run pi-unity compile")
    })
    .await
    .expect("join pi-unity compile");
    let stdout = String::from_utf8_lossy(&output.stdout).into_owned();
    let stderr = String::from_utf8_lossy(&output.stderr).into_owned();

    // 编译失败必须 exit 1（不能是 exit0 ok:true 的成功信封）。
    assert_eq!(
        output.status.code(),
        Some(1),
        "编译失败必须 exit 1；stdout={stdout} stderr={stderr}"
    );
    let parsed: Value = serde_json::from_str(stdout.trim())
        .unwrap_or_else(|e| panic!("stdout 应为 JSON（--json）：{e}\n{stdout}"));
    assert_eq!(parsed["ok"], json!(false));
    assert_eq!(parsed["exitCode"], json!(1));
    assert_eq!(parsed["error_type"], "compile_error");
    assert_eq!(parsed["compiled"], json!(false), "必须携带 compiled:false 诊断");
    assert_eq!(parsed["compiledErrorType"], "compile_error");
    assert!(
        parsed["compiledError"].as_str().unwrap().contains("CS1234"),
        "诊断应保留编译错误摘要: {}",
        parsed["compiledError"]
    );

    // 失败后绝不轮询 status、recompile 只触发一次。
    assert_eq!(status_count.load(Ordering::SeqCst), 0, "编译失败后 CLI 绝不轮询 status");
    assert_eq!(recompile_count.load(Ordering::SeqCst), 1);

    let _ = fs::remove_dir_all(&bridge_dir);
}

/// mux 信封同样必须 ok:false + exitCode 1 + compiled:false（真实 CLI 进程内 mux）。
#[tokio::test(flavor = "multi_thread")]
async fn mux_compile_failure_keeps_compiled_false_in_error_envelope() {
    let (pipe_name, bridge_dir, status_count, _recompile_count, _server) = spawn_mock_broker();
    write_bridge(&bridge_dir, &pipe_name);

    let project = bridge_dir.clone();
    let out = tokio::task::spawn_blocking(move || {
        let mut child = Command::new(bin_exe())
            .args(["--project-path", project.to_str().unwrap(), "mux"])
            .stdin(std::process::Stdio::piped())
            .stdout(std::process::Stdio::piped())
            .spawn()
            .expect("spawn pi-unity mux");
        use std::io::Write;
        child
            .stdin
            .take()
            .unwrap()
            .write_all(
                br#"{"id":"c1","argv":["compile","--timeout","5000"]}
{"id":"q","quit":true}
"#,
            )
            .unwrap();
        child.wait_with_output().expect("wait mux")
    })
    .await
    .expect("join pi-unity mux");
    let stdout = String::from_utf8_lossy(&out.stdout).into_owned();
    let first = stdout.lines().next().unwrap_or("");
    let parsed: Value = serde_json::from_str(first).unwrap_or_else(|e| {
        panic!("mux 首行应为 JSON：{e}\n{stdout}")
    });
    assert_eq!(parsed["id"], "c1");
    assert_eq!(parsed["ok"], json!(false));
    assert_eq!(parsed["exitCode"], json!(1));
    assert_eq!(parsed["error_type"], "compile_error");
    assert_eq!(parsed["compiled"], json!(false), "mux 信封必须保留 compiled:false");
    assert_eq!(status_count.load(Ordering::SeqCst), 0);
    let _ = fs::remove_dir_all(&bridge_dir);
}