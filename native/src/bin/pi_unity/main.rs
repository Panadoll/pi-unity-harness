use std::path::{Path, PathBuf};
use std::process::ExitCode;
use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde_json::{json, Value};

mod args;
mod client;
mod commands;
mod discovery;
mod home;
mod logging;
mod output;
mod schema;
mod setup;
mod toon;
mod usage;
mod version;

#[cfg(all(test, windows))]
mod mock_pipe;

use args::{Cli, Commands, SessionSubcommands};
use clap::Parser;
use client::{CliError, HarnessClient};
use commands::{execute_harness_command, handle_skills_command};
use discovery::{resolve_project_root, resolve_project_root_fast};
use logging::{
    append_event_line, compute_project_hash, end_session_scoped, lazy_cleanup_old_traces,
    log_root_dir, now_ms, record_mark, resolve_session_scoped, resolve_client_name,
    current_agent_id, start_session_scoped, timestamp_utc,
    CallEvent, CallEventFlags, CallEventPhases, TraceRecorder, DEFAULT_VERSION, LOG_FORMAT_VERSION,
};
use output::{emit_value, MAX_SAFE_RESPONSE_CHARS};
use schema::ViewOptions;
use std::fs;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};

pub const EXPECTED_PROTOCOL_VERSION: i32 = 1;

#[tokio::main]
async fn main() -> ExitCode {
    let argv: Vec<String> = std::env::args().collect();
    if usage::try_version_fast_path(&argv[1..]) {
        if argv.iter().any(|arg| arg == "--json") {
            println!("{}", usage::format_version_json());
        } else {
            usage::print_version();
        }
        return ExitCode::SUCCESS;
    }

    let start_instant = Instant::now();
    let log_root = log_root_dir();
    lazy_cleanup_old_traces(&log_root);

    let json_mode = argv.iter().any(|a| a == "--json");
    let cli = match usage::parse_cli() {
        Ok(cli) => cli,
        Err(err) => {
            let (out, code) = usage::from_clap_error(err, json_mode);
            print!("{out}");
            if !out.ends_with('\n') {
                println!();
            }
            return ExitCode::from(code as u8);
        }
    };
    let json_mode = cli.json;
    let trace_flag = cli.trace;
    let view_opts = ViewOptions::from_parts(&cli.fields, cli.full);
    let subcommand_name = cli
        .command
        .as_ref()
        .map(Commands::subcommand_name)
        .unwrap_or("home");
    let recorder = Arc::new(TraceRecorder::new(subcommand_name));

    recorder.record(
        "init",
        &format!("pi-unity CLI started (subcommand: {})", subcommand_name),
    );

    let initial_project = resolve_project_root_fast(cli.project_path.as_deref()).ok();
    let (session_id, host_session_id) = resolve_session_scoped(&log_root, initial_project.as_deref());

    let call_base = CallContext {
        json_mode,
        trace_flag,
        log_root: &log_root,
        recorder: recorder.as_ref(),
        subcommand: subcommand_name.to_string(),
        project_root: None,
        session_id,
        host_session_id,
        discover_ms: 0,
        connect_ms: 0,
        request_ms: 0,
        start_instant,
    };

    if let Some(Commands::Eval(ref args)) = cli.command {
        if args.code.is_none() && args.file.is_none() {
            return handle_exit(
                Err(usage::usage_error(
                    "eval 需要 CODE 或 --file",
                    &[
                        "pi-unity eval \"<code>\"",
                        "pi-unity eval -f Temp/PiUnityHarness/AgentScratch/probe.repl",
                    ],
                )),
                call_base.clone(),
            );
        }
    }

    let mut discover_ms = 0u64;
    let mut connect_ms = 0u64;
    let mut request_ms = 0u64;
    let mut project_root_opt: Option<PathBuf> = None;

    let result: Result<String, CliError> = match cli.command {
        None => run_home(cli.project_path.as_deref(), json_mode, recorder.clone()).await,
        Some(Commands::Session(session_args)) => match session_args.action {
            SessionSubcommands::Start(start_args) => {
                recorder.record("session", "Starting sticky session");
                let project = resolve_project_root_fast(cli.project_path.as_deref()).ok();
                match start_session_scoped(&log_root, project.as_deref(), start_args.task, start_args.agent) {
                    Ok(data) => Ok(emit_value(
                        &json!({
                            "session": data.session_id,
                            "started": true
                        }),
                        json_mode,
                    )),
                    Err(e) => Err(CliError::Other(format!("无法开始会话: {}", e))),
                }
            }
            SessionSubcommands::End => {
                recorder.record("session", "Ending sticky session");
                let project = resolve_project_root_fast(cli.project_path.as_deref()).ok();
                match end_session_scoped(&log_root, project.as_deref()) {
                    Ok(Some(id)) => Ok(emit_value(
                        &json!({
                            "session": id,
                            "ended": true
                        }),
                        json_mode,
                    )),
                    Ok(None) => Ok(emit_value(
                        &json!({
                            "session": "already ended (no-op)"
                        }),
                        json_mode,
                    )),
                    Err(e) => Err(CliError::Other(format!("无法结束会话: {}", e))),
                }
            }
        },
        Some(Commands::Mark(mark_args)) => {
            recorder.record("mark", &format!("Marking skill {}", mark_args.skill));
            record_mark(&log_root, &mark_args.skill, &mark_args.event);
            Ok(emit_value(
                &json!({
                    "skill": mark_args.skill,
                    "event": mark_args.event
                }),
                json_mode,
            ))
        }
        Some(Commands::Setup(setup_args)) => {
            let disc_start = Instant::now();
            let project_root = if setup_args.project {
                match resolve_project_root(cli.project_path.as_deref()) {
                    Ok(root) => {
                        project_root_opt = Some(root.clone());
                        Some(root)
                    }
                    Err(_) => std::env::current_dir().ok(),
                }
            } else {
                None
            };
            discover_ms = disc_start.elapsed().as_millis() as u64;
            let exe = std::env::current_exe().unwrap_or_else(|_| PathBuf::from("pi-unity"));
            let (claude, codex, plugin) = setup::default_hook_paths(project_root.as_deref());
            match setup::install_hooks(&exe, &claude, &codex, &plugin) {
                Ok(report) => Ok(setup::format_report(&report, json_mode)),
                Err(e) => Err(e),
            }
        }
        Some(Commands::Mux) => {
            return run_mux(cli.project_path.as_deref()).await;
        }
        Some(Commands::Skills(skills_args)) => {
            let disc_start = Instant::now();
            let p_root = match resolve_project_root(cli.project_path.as_deref()) {
                Ok(root) => {
                    project_root_opt = Some(root.clone());
                    root
                }
                Err(_) => std::env::current_dir().unwrap_or_else(|_| PathBuf::from(".")),
            };
            discover_ms = disc_start.elapsed().as_millis() as u64;
            handle_skills_command(skills_args, &p_root, json_mode)
        }
        Some(other_cmd) => {
            let disc_start = Instant::now();
            let p_root = match resolve_project_root(cli.project_path.as_deref()) {
                Ok(root) => {
                    discover_ms = disc_start.elapsed().as_millis() as u64;
                    project_root_opt = Some(root.clone());
                    recorder.record("discover", &format!("Project root: {}", root.display()));
                    root
                }
                Err(err) => {
                    discover_ms = disc_start.elapsed().as_millis() as u64;
                    recorder.record("discover", "Project discovery failed");
                    return handle_exit(
                        Err(err),
                        CallContext {
                            discover_ms,
                            ..call_base.clone()
                        },
                    );
                }
            };

            let conn_start = Instant::now();
            let mut client = match HarnessClient::new(p_root.clone(), recorder.clone()) {
                Ok(c) => c,
                Err(e) => {
                    connect_ms = conn_start.elapsed().as_millis() as u64;
                    recorder.record(
                        "connect",
                        &format!("Failed to initialize client: {}", e.message()),
                    );
                    return handle_exit(
                        Err(e),
                        CallContext {
                            project_root: project_root_opt.as_deref(),
                            discover_ms,
                            connect_ms,
                            ..call_base.clone()
                        },
                    );
                }
            };

            if let Err(e) = client.handshake().await {
                connect_ms = conn_start.elapsed().as_millis() as u64;
                recorder.record("handshake", &format!("Handshake failed: {}", e.message()));
                return handle_exit(
                    Err(e),
                    CallContext {
                        project_root: project_root_opt.as_deref(),
                        discover_ms,
                        connect_ms,
                        ..call_base.clone()
                    },
                );
            }
            connect_ms = conn_start.elapsed().as_millis() as u64;

            let req_start = Instant::now();
            let exec_res = execute_harness_command(
                other_cmd,
                &mut client,
                &p_root,
                json_mode,
                &view_opts,
                &recorder,
            )
            .await;
            request_ms = req_start.elapsed().as_millis() as u64;

            exec_res
        }
    };

    handle_exit(
        result,
        CallContext {
            project_root: project_root_opt.as_deref(),
            discover_ms,
            connect_ms,
            request_ms,
            ..call_base.clone()
        },
    )
}

const HOME_PROBE_MS: u64 = 200;
const MUX_PING_MS: u64 = 5000;

#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct MuxLine {
    id: Option<String>,
    argv: Option<Vec<String>>,
    quit: Option<bool>,
}

async fn run_mux(project_path: Option<&str>) -> ExitCode {
    let mut project_root: Option<PathBuf> = None;
    let mut client: Option<HarnessClient> = None;

    let stdin = tokio::io::stdin();
    let mut lines = BufReader::new(stdin).lines();
    let mut stdout = tokio::io::stdout();
    let mut ping = tokio::time::interval(Duration::from_millis(MUX_PING_MS));
    ping.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);

    loop {
        tokio::select! {
            _ = ping.tick() => {
                if let Some(ref mut c) = client {
                    if c.is_connected() {
                        let connected_before = c.is_connected();
                        if c.reload_bridge().is_err() {
                            c.disconnect();
                        } else if !c.is_connected() {
                            // token/generation 变了，等下条业务再连
                        } else if connected_before
                            && c.send_request("ping", json!({}), 2000).await.is_err()
                        {
                            c.disconnect();
                        }
                    }
                }
            }
            read = lines.next_line() => {
                match read {
                    Ok(Some(line)) => {
                        if line.trim().is_empty() {
                            continue;
                        }
                        let reply = handle_mux_line(&line, &mut client, &mut project_root, project_path).await;
                        if write_mux_stdout(&mut stdout, &reply.0).await.is_err() {
                            break;
                        }
                        if reply.1 {
                            break;
                        }
                    }
                    Ok(None) => break,
                    Err(_) => break,
                }
            }
        }
    }
    ExitCode::SUCCESS
}

async fn write_mux_stdout(
    stdout: &mut tokio::io::Stdout,
    value: &Value,
) -> Result<(), std::io::Error> {
    let mut bytes = serde_json::to_vec(value).unwrap_or_else(|_| br#"{"ok":false}"#.to_vec());
    bytes.push(b'\n');
    stdout.write_all(&bytes).await?;
    stdout.flush().await
}

fn mux_error_value(id: &str, err: &CliError) -> Value {
    // 基于 error_payload 构建：保留其附加诊断字段（如编译失败的 compiled:false），
    // 再注入 mux 信封的 id / text。
    let mut value = usage::error_payload(err);
    if let Some(obj) = value.as_object_mut() {
        obj.insert("id".to_string(), json!(id));
        obj.insert("text".to_string(), json!(usage::format_error(err, false)));
    }
    value
}

fn mux_ok_value(id: &str, output: &str) -> Value {
    let result = match serde_json::from_str::<Value>(output) {
        Ok(Value::Object(map)) if map.get("ok").and_then(Value::as_bool) == Some(true) => {
            map.get("result").cloned().unwrap_or(Value::Null)
        }
        Ok(other) => other,
        Err(_) => Value::String(output.to_string()),
    };
    json!({
        "id": id,
        "ok": true,
        "exitCode": 0,
        "result": result,
        "text": emit_value(&result, false),
    })
}

#[derive(Debug)]
enum MuxParsed {
    Command(Commands, ViewOptions),
    Help(String),
}

fn parse_mux_command(argv: &[String]) -> Result<MuxParsed, CliError> {
    let mut args = Vec::with_capacity(argv.len() + 1);
    args.push("pi-unity".to_string());
    args.extend(argv.iter().cloned());
    match Cli::try_parse_from(args) {
        Ok(parsed) => {
            if parsed.project_path.is_some() {
                return Err(usage::usage_error(
                    "mux 请求不能带 --project-path",
                    &["工程在 mux 启动时固定"],
                ));
            }
            if parsed.trace {
                return Err(usage::usage_error(
                    "mux 不支持 --trace",
                    &["pi-unity status"],
                ));
            }
            match parsed.command {
                Some(Commands::Mux) => {
                    Err(usage::usage_error("mux 不能嵌套", &["pi-unity status"]))
                }
                Some(Commands::Skills(_))
                | Some(Commands::Session(_))
                | Some(Commands::Mark(_))
                | Some(Commands::Setup(_)) => Err(usage::usage_error(
                    "mux 不支持该子命令",
                    &["pi-unity status", "pi-unity ping"],
                )),
                Some(Commands::Eval(eval_args))
                    if eval_args.code.is_none() && eval_args.file.is_none() =>
                {
                    Err(usage::usage_error(
                        "eval 需要 CODE 或 --file",
                        &[
                            "pi-unity eval \"<code>\"",
                            "pi-unity eval -f Temp/PiUnityHarness/AgentScratch/probe.repl",
                        ],
                    ))
                }
                Some(cmd) => Ok(MuxParsed::Command(
                    cmd,
                    ViewOptions::from_parts(&parsed.fields, parsed.full),
                )),
                None => Err(usage::usage_error(
                    "mux 请求需要子命令",
                    &["pi-unity status"],
                )),
            }
        }
        Err(err) => match usage::clap_to_cli_error(err) {
            Ok(help_text) => Ok(MuxParsed::Help(help_text)),
            Err(cli_err) => Err(cli_err),
        },
    }
}

async fn handle_mux_line(
    line: &str,
    client: &mut Option<HarnessClient>,
    project_root: &mut Option<PathBuf>,
    project_path: Option<&str>,
) -> (Value, bool) {
    let parsed: MuxLine = match serde_json::from_str(line) {
        Ok(v) => v,
        Err(e) => {
            return (
                mux_error_value(
                    "",
                    &usage::usage_error(
                        format!("mux JSON 无效: {e}"),
                        &["{\"id\":\"1\",\"argv\":[\"status\"]}"],
                    ),
                ),
                false,
            );
        }
    };
    let id = parsed.id.unwrap_or_default();
    let quit = parsed.quit.unwrap_or(false)
        || parsed
            .argv
            .as_ref()
            .and_then(|a| a.first())
            .map(|s| s == "quit")
            .unwrap_or(false);
    if quit {
        return (
            json!({
                "id": id,
                "ok": true,
                "exitCode": 0,
                "result": {"quit": true},
            }),
            true,
        );
    }
    let Some(argv) = parsed.argv else {
        return (
            mux_error_value(
                &id,
                &usage::usage_error(
                    "mux 请求需要 id 和 argv",
                    &["{\"id\":\"1\",\"argv\":[\"status\"]}"],
                ),
            ),
            false,
        );
    };
    if id.is_empty() {
        return (
            mux_error_value(
                "",
                &usage::usage_error(
                    "mux 请求需要 id 和 argv",
                    &["{\"id\":\"1\",\"argv\":[\"status\"]}"],
                ),
            ),
            false,
        );
    }

    let parsed_cmd = match parse_mux_command(&argv) {
        Ok(v) => v,
        Err(err) => return (mux_error_value(&id, &err), false),
    };
    let (command, view_opts) = match parsed_cmd {
        MuxParsed::Help(help_text) => {
            return (mux_ok_value(&id, &json!(help_text).to_string()), false);
        }
        MuxParsed::Command(command, view_opts) => (command, view_opts),
    };

    if project_root.is_none() {
        match resolve_project_root(project_path) {
            Ok(root) => *project_root = Some(root),
            Err(err) => return (mux_error_value(&id, &err), false),
        }
    }
    let Some(root) = project_root.clone() else {
        return (
            mux_error_value(&id, &CliError::BridgeNotFound("找不到 Unity 工程".into())),
            false,
        );
    };

    // 每个请求在独立的 tokio 任务里执行：await 之后的 panic 只影响当前请求，
    // 不拖垮整个 mux 循环；panic 时丢弃可疑客户端状态，下一条请求重建。
    if client.is_none() {
        let recorder = Arc::new(TraceRecorder::new("mux"));
        match HarnessClient::new_persistent(root.clone(), recorder) {
            Ok(c) => *client = Some(c),
            Err(err) => return (mux_error_value(&id, &err), false),
        }
    }
    let taken = client.take();
    let exec = run_mux_exec(id.clone(), command, view_opts, taken, root);
    match spawn_isolated(exec).await {
        Ok((reply, quit, restored)) => {
            *client = restored;
            (reply, quit)
        }
        Err(()) => {
            // 不暴露原始 panic 载荷，只返回结构化内部错误（客户端状态已被丢弃）。
            (mux_internal_error(&id), false)
        }
    }
}

/// 在独立 tokio 任务里执行请求，捕获 panic：Err(()) 表示任务 panic。
async fn spawn_isolated<T, F>(fut: F) -> Result<T, ()>
where
    F: std::future::Future<Output = T> + Send + 'static,
    T: Send + 'static,
{
    match tokio::task::spawn(fut).await {
        Ok(value) => Ok(value),
        Err(_) => Err(()),
    }
}

fn mux_internal_error(id: &str) -> Value {
    // panic 隔离后请求结果未知：绝不盲目建议重试（编译/动作类请求可能已产生副作用）。
    let text = "mux 请求内部错误：已隔离该请求并丢弃其客户端状态，该请求的结果未知，先检查 status/state 确认是否已生效，再决定是否重试";
    json!({
        "id": id,
        "ok": false,
        "exitCode": 1,
        "error": text,
        "error_type": "internal_error",
        "help": ["pi-unity status 检查当前 Editor 状态", "确认结果未知后再决定是否重试"],
        "text": format!("[internal_error] {}", text),
    })
}

/// 在隔离任务内执行一次 mux 请求；返回回复、是否退出、以及（无 panic 时）恢复的客户端。
async fn run_mux_exec(
    id: String,
    command: Commands,
    view_opts: ViewOptions,
    client: Option<HarnessClient>,
    root: PathBuf,
) -> (Value, bool, Option<HarnessClient>) {
    let Some(mut c) = client else {
        return (
            mux_error_value(&id, &CliError::Other("mux client 不可用".into())),
            false,
            None,
        );
    };
    let recorder = Arc::new(TraceRecorder::new(command.subcommand_name()));
    c.set_recorder(recorder.clone());
    match execute_harness_command(command, &mut c, &root, true, &view_opts, &recorder).await {
        Ok(output) => (mux_ok_value(&id, &output), false, Some(c)),
        Err(err) => (mux_error_value(&id, &err), false, Some(c)),
    }
}

async fn run_home(
    project_path: Option<&str>,
    json_mode: bool,
    recorder: Arc<TraceRecorder>,
) -> Result<String, CliError> {
    let bin = std::env::current_exe().unwrap_or_else(|_| PathBuf::from("pi-unity"));
    let project = resolve_project_root_fast(project_path).ok();
    let mut status = None;
    let mut reason = None;
    if let Some(root) = project.as_ref() {
        match HarnessClient::new(root.clone(), recorder.clone()) {
            Ok(mut client) => {
                status = client
                    .send_request("status", json!({}), HOME_PROBE_MS)
                    .await
                    .ok();
                if status.is_none() {
                    reason = Some("broker 未在 200ms 内响应".to_string());
                }
            }
            Err(e) => reason = Some(e.message().to_string()),
        }
    } else {
        reason = Some("找不到正在运行的 Unity Editor 或未加载 com.pi.unity-harness".to_string());
    }

    if json_mode {
        let body = json!({
            "bin": home::collapse_home(&bin),
            "description": home::DESCRIPTION,
            "connected": status.is_some(),
            "status": status,
            "reason": reason,
        });
        return Ok(emit_value(&body, true));
    }

    Ok(home::render_home(
        &bin,
        status.as_ref(),
        None,
        project_path,
        reason.as_deref(),
    ))
}

#[derive(Clone)]
struct CallContext<'a> {
    json_mode: bool,
    trace_flag: bool,
    log_root: &'a Path,
    recorder: &'a TraceRecorder,
    subcommand: String,
    project_root: Option<&'a Path>,
    session_id: Option<String>,
    host_session_id: Option<String>,
    discover_ms: u64,
    connect_ms: u64,
    request_ms: u64,
    start_instant: Instant,
}

fn handle_exit(result: Result<String, CliError>, ctx: CallContext<'_>) -> ExitCode {
    let duration_ms = ctx.start_instant.elapsed().as_millis() as u64;
    let (exit_code, error_type) = match &result {
        Ok(_) => (0, None),
        Err(e) => (e.exit_code(), Some(e.error_type())),
    };

    let trace_id = ctx.recorder.flush_trace_if_needed(ctx.log_root, exit_code, ctx.trace_flag);
    let request_ids = ctx
        .recorder
        .request_ids
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .clone();
    let reconnects = ctx.recorder.reconnects.load(Ordering::SeqCst);
    let is_truncated = ctx.recorder.truncated.load(Ordering::SeqCst);

    let call_event = CallEvent {
        v: LOG_FORMAT_VERSION,
        kind: "call".to_string(),
        ts_utc: timestamp_utc(ctx.recorder.start_ms),
        version: DEFAULT_VERSION.to_string(),
        client: resolve_client_name(),
        pid: std::process::id(),
        session_id: ctx.session_id,
        host_session_id: ctx.host_session_id,
        subcommand: ctx.subcommand,
        project_hash: compute_project_hash(ctx.project_root),
        agent_id: current_agent_id(),
        exit_code,
        duration_ms,
        phases: CallEventPhases {
            discover_ms: ctx.discover_ms,
            connect_ms: ctx.connect_ms,
            request_ms: ctx.request_ms,
        },
        flags: CallEventFlags {
            json: ctx.json_mode,
            truncated: is_truncated,
            reconnects,
        },
        error_type,
        request_ids,
        trace_id,
    };

    if let Ok(value) = serde_json::to_value(&call_event) {
        let _ = append_event_line(ctx.log_root, &value);
    }

    match result {
        Ok(output) => {
            println!("{}", output);
            ExitCode::SUCCESS
        }
        Err(err) => {
            handle_error(&err, ctx.json_mode, ctx.project_root);
            ExitCode::from(exit_code as u8)
        }
    }
}

/// 截断到 UTF-8 字符边界，绝不 panic（String::truncate / 直接切片可能 panic）。
fn truncate_chars_safe(s: &str, limit: usize) -> &str {
    if s.len() <= limit {
        return s;
    }
    let mut end = limit;
    while end > 0 && !s.is_char_boundary(end) {
        end -= 1;
    }
    &s[..end]
}

fn handle_error(err: &CliError, json_mode: bool, project_root: Option<&Path>) {
    let mut msg = err.message().to_string();

    if msg.len() > MAX_SAFE_RESPONSE_CHARS {
        if let Some(root) = project_root {
            let scratch_dir = root.join("Temp/PiUnityHarness/AgentScratch");
            let _ = fs::create_dir_all(&scratch_dir);
            let scratch_file = format!("large_error_{}.txt", now_ms());
            let scratch_path = scratch_dir.join(&scratch_file);
            let _ = fs::write(&scratch_path, &msg);
            let rel_path = format!("Temp/PiUnityHarness/AgentScratch/{}", scratch_file);
            let preview = truncate_chars_safe(&msg, MAX_SAFE_RESPONSE_CHARS);
            msg = format!(
                "[Error truncated from {} to {} chars. Full message saved to: {}]\n{}",
                msg.len(),
                MAX_SAFE_RESPONSE_CHARS,
                rel_path,
                preview
            );
        } else {
            msg = truncate_chars_safe(&msg, MAX_SAFE_RESPONSE_CHARS).to_string();
        }
    }

    let out = if msg == err.message() {
        usage::format_error(err, json_mode)
    } else {
        usage::format_error(
            &match err {
                CliError::Usage { help, .. } => CliError::Usage {
                    error: msg,
                    help: help.clone(),
                },
                CliError::ExecutionFailed(_) => CliError::ExecutionFailed(msg),
                CliError::BridgeNotFound(_) => CliError::BridgeNotFound(msg),
                CliError::Timeout(_) => CliError::Timeout(msg),
                CliError::Other(_) => CliError::Other(msg),
                CliError::ProtocolMismatch { expected, actual } => {
                    CliError::ProtocolMismatch { expected: *expected, actual: *actual }
                }
                CliError::Broker { code, message } => CliError::Broker {
                    code: code.clone(),
                    message: message.clone(),
                },
                CliError::Busy(_) => CliError::Busy(msg),
            },
            json_mode,
        )
    };
    println!("{}", out);
}

#[cfg(test)]
mod tests {
    use super::client::normalize_pipe_name;
    use super::commands::parse_param_pairs;
    use super::output::{format_safe_output, is_base64_data, strip_large_base64_and_save};
    use super::*;

    #[test]
    fn test_exit_codes() {
        assert_eq!(CliError::ExecutionFailed("err".into()).exit_code(), 1);
        assert_eq!(CliError::BridgeNotFound("err".into()).exit_code(), 1);
        assert_eq!(CliError::Timeout("err".into()).exit_code(), 1);
        assert_eq!(
            CliError::Usage {
                error: "x".into(),
                help: vec![]
            }
            .exit_code(),
            2
        );
        assert_eq!(CliError::Other("err".into()).exit_code(), 1);
    }

    #[test]
    fn test_normalize_pipe_name() {
        assert_eq!(
            normalize_pipe_name("pi_unity_1234"),
            r"\\.\pipe\pi_unity_1234"
        );
        assert_eq!(
            normalize_pipe_name(r"\\.\pipe\pi_unity_5678"),
            r"\\.\pipe\pi_unity_5678"
        );
        assert_eq!(
            normalize_pipe_name(r"\pipe\pi_unity_abcd"),
            r"\\.\pipe\pipe\pi_unity_abcd"
        );
    }

    #[test]
    fn test_is_base64_data() {
        assert!(is_base64_data("data:image/png;base64,iVBORw0KGgoAAAANSU"));
        assert!(!is_base64_data("hello world"));
        let long_b64 = "A".repeat(300);
        assert!(is_base64_data(&long_b64));
    }

    #[test]
    fn test_parse_param_pairs() {
        let pairs = vec![
            "flag=true".to_string(),
            "count=42".to_string(),
            "ratio=2.5".to_string(),
            "name=test".to_string(),
            "nested={\"a\":1}".to_string(),
            "list=[1,2,3]".to_string(),
        ];
        let val = parse_param_pairs(&pairs, None).unwrap();
        assert_eq!(val["flag"], json!(true));
        assert_eq!(val["count"], json!(42));
        assert_eq!(val["ratio"], json!(2.5));
        assert_eq!(val["name"], json!("test"));
        assert_eq!(val["nested"], json!({"a": 1}));
        assert_eq!(val["list"], json!([1, 2, 3]));
    }

    #[test]
    fn test_parse_param_pairs_with_explicit_json() {
        let pairs = vec!["override_key=new_val".to_string()];
        let explicit = Some("{\"base_key\": 100, \"override_key\": \"old_val\"}");
        let val = parse_param_pairs(&pairs, explicit).unwrap();
        assert_eq!(val["base_key"], json!(100));
        assert_eq!(val["override_key"], json!("new_val"));
    }

    #[test]
    fn test_format_safe_output_truncation_json() {
        let temp_dir = std::env::temp_dir();
        let mut large_map = serde_json::Map::new();
        for i in 0..4000 {
            large_map.insert(
                format!("key_{}", i),
                json!("A long value to exceed buffer size and force 32KB truncation"),
            );
        }
        let raw = Value::Object(large_map);
        let out = format_safe_output(&raw, &temp_dir, true, None);
        assert!(out.len() <= MAX_SAFE_RESPONSE_CHARS + 2048);
        let parsed: Value = serde_json::from_str(&out).unwrap();
        assert_eq!(parsed["ok"], json!(true));
        let result = parsed.get("result").unwrap();
        assert_eq!(result["truncated"], json!(true));
        assert!(result["savedScratchPath"].as_str().is_some());
    }

    #[test]
    fn test_format_safe_output_truncation_toon_has_full_hint() {
        let temp_dir = std::env::temp_dir();
        let long_str = Value::String("Line of test output. ".repeat(3000));
        let out = format_safe_output(&long_str, &temp_dir, false, None);
        assert!(out.contains("truncated") || out.contains("(truncated"));
        assert!(out.contains("--full") || out.contains("savedScratchPath") || out.contains("help"));
    }

    #[test]
    fn test_strip_large_base64_in_object() {
        let temp_dir = std::env::temp_dir();
        let mut obj = json!({
            "name": "screenshot",
            "SavedPath": "Captures/shot.png",
            "base64": "A".repeat(500),
            "regular": "normal text"
        });
        strip_large_base64_and_save(&mut obj, &temp_dir, 0);
        assert_eq!(obj["regular"], json!("normal text"));
        let b64_val = obj["base64"].as_str().unwrap();
        assert!(b64_val.contains("Base64 Image"));
        assert!(b64_val.contains("Captures/shot.png"));
    }

    #[test]
    fn test_error_type_mapping() {
        assert_eq!(
            CliError::BridgeNotFound("err".into()).error_type(),
            "bridge_not_found"
        );
        assert_eq!(CliError::Timeout("err".into()).error_type(), "timeout");
        assert_eq!(
            CliError::ExecutionFailed("err".into()).error_type(),
            "execution_failed"
        );
        assert_eq!(CliError::Other("err".into()).error_type(), "other");
    }

    #[test]
    fn mux_eval_missing_code_is_usage_without_unity() {
        let err = parse_mux_command(&["eval".into()]).unwrap_err();
        assert_eq!(err.exit_code(), 2);
        assert!(err.message().contains("eval 需要 CODE"));
    }

    #[test]
    fn mux_rejects_project_path_and_trace() {
        let err = parse_mux_command(&["--project-path".into(), "D:/other".into(), "status".into()])
            .unwrap_err();
        assert_eq!(err.exit_code(), 2);
        assert!(err.message().contains("--project-path"));
        let err = parse_mux_command(&["--trace".into(), "status".into()]).unwrap_err();
        assert_eq!(err.exit_code(), 2);
        assert!(err.message().contains("--trace"));
    }

    #[test]
    fn is_base64_data_no_panic_when_128_splits_multibyte_char() {
        // 126 个 ASCII 字节后紧跟 3 字节汉字，byte 128 落在汉字中间：旧实现 s[..128] 会 panic。
        let mut s = "A".repeat(126);
        s.push('中');
        s.push_str(&"B".repeat(300));
        assert!(s.len() >= 256);
        assert!(!is_base64_data(&s));
    }

    #[test]
    fn is_base64_data_long_chinese_emoji_mixed_no_panic() {
        let mut s = String::new();
        for _ in 0..40 {
            s.push_str("你好世界🌍🚀混合输出测试");
        }
        s.push_str(&"A".repeat(64));
        assert!(!is_base64_data(&s));
    }

    #[test]
    fn is_base64_data_ascii_prefix_with_multibyte_tail_is_false() {
        // 前 128 字节内出现非 base64 多字节字符 → false，且不 panic。
        let s = format!("{}中{}", "A".repeat(40), "B".repeat(300));
        assert!(!is_base64_data(&s));
        assert!(s.len() >= 256);
    }

    #[test]
    fn format_safe_output_keeps_unicode_intact() {
        let temp_dir = std::env::temp_dir();
        let raw = json!({"message": "中文输出\nemoji 🌍🚀\nmixed: äöü 中文 emoji 🎉"});
        let out = format_safe_output(&raw, &temp_dir, true, None);
        let parsed: Value = serde_json::from_str(&out).expect("json");
        assert_eq!(parsed["result"]["message"], raw["message"]);
    }

    #[test]
    fn format_safe_output_long_unicode_truncation_no_panic() {
        let temp_dir = std::env::temp_dir();
        let long = "中🌍🚀".repeat(10000);
        let out = format_safe_output(&Value::String(long), &temp_dir, true, None);
        assert!(out.len() <= MAX_SAFE_RESPONSE_CHARS + 2048);
    }

    #[test]
    fn truncate_error_message_never_splits_utf8() {
        let long = "中".repeat(20000); // 60000 字节，远超 32KB
        let truncated = truncate_chars_safe(&long, MAX_SAFE_RESPONSE_CHARS);
        assert!(truncated.len() <= MAX_SAFE_RESPONSE_CHARS);
        assert!(truncated.is_char_boundary(truncated.len()));
    }

    #[tokio::test]
    async fn mux_request_panic_after_await_is_isolated_and_next_succeeds() {
        let first: Result<usize, ()> = spawn_isolated(async {
            tokio::time::sleep(Duration::from_millis(2)).await;
            panic!("模拟 await 之后 panic：boom-payload-XYZ");
        })
        .await;
        assert!(first.is_err(), "panic 请求必须被隔离为 Err");
        let second: Result<usize, ()> = spawn_isolated(async { 42 }).await;
        assert_eq!(second, Ok(42), "紧随其后的请求必须成功");
    }

    #[test]
    fn mux_internal_error_keeps_id_and_is_structured() {
        let v = mux_internal_error("req-9");
        assert_eq!(v["id"], "req-9");
        assert_eq!(v["ok"], json!(false));
        assert_eq!(v["error_type"], "internal_error");
        assert_eq!(v["exitCode"], json!(1));
        assert!(v["help"].is_array());
        let text = serde_json::to_string(&v).unwrap();
        assert!(!text.contains("boom"), "不得暴露原始 panic 载荷");
    }

    #[cfg(windows)]
    #[tokio::test(flavor = "multi_thread")]
    async fn mux_status_requests_reuse_client_across_isolation() {
        use crate::mock_pipe::{caps_reply, ready_status, MockBroker};
        use std::sync::atomic::Ordering as Ao;

        let broker = MockBroker::spawn_default(|req| match req
            .get("type")
            .and_then(Value::as_str)
            .unwrap_or("")
        {
            "bridge_capabilities" => caps_reply(),
            "status" => json!({"ok": true, "result": ready_status()}),
            _ => json!({"ok": false, "error": "unsupported"}),
        });
        let _bridge = broker.write_bridge(Some(std::process::id()));
        let root = Some(broker.bridge_dir.clone());
        let mut project_root: Option<PathBuf> = root;
        let mut client: Option<HarnessClient> = None;
        let (reply1, quit1) = handle_mux_line(
            r#"{"id":"m1","argv":["status"]}"#,
            &mut client,
            &mut project_root,
            None,
        )
        .await;
        let (reply2, quit2) = handle_mux_line(
            r#"{"id":"m2","argv":["status"]}"#,
            &mut client,
            &mut project_root,
            None,
        )
        .await;
        assert!(!quit1 && !quit2);
        assert_eq!(reply1["ok"], json!(true));
        assert_eq!(reply1["id"], "m1");
        // status 非 full 的 shape 不保留 managedState：状态放在 editor 字段。
        assert_eq!(reply1["result"]["editor"], "ready");
        assert_eq!(reply1["result"]["generation"], json!(7));
        assert_eq!(reply2["id"], "m2");
        assert!(client.is_some(), "隔离包装必须恢复客户端状态");
        assert_eq!(
            broker.accepted.load(Ao::SeqCst),
            1,
            "两个请求应复用同一条连接"
        );
    }
}
