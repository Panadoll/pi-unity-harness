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

use args::{Cli, Commands, SessionSubcommands};
use clap::Parser;
use client::{CliError, HarnessClient};
use commands::{execute_harness_command, handle_skills_command};
use discovery::{resolve_project_root, resolve_project_root_fast};
use logging::{
    append_event_line, compute_project_hash, end_session, lazy_cleanup_old_traces, log_root_dir,
    now_ms, record_mark, resolve_client_name, resolve_session, start_session, timestamp_utc,
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
        usage::print_version();
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

    let (session_id, host_session_id) = resolve_session(&log_root);

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
                json_mode,
                trace_flag,
                &log_root,
                &recorder,
                subcommand_name,
                None,
                session_id,
                host_session_id,
                0,
                0,
                0,
                start_instant,
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
                match start_session(&log_root, start_args.task, start_args.agent) {
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
                match end_session(&log_root) {
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
                        json_mode,
                        trace_flag,
                        &log_root,
                        &recorder,
                        subcommand_name,
                        None,
                        session_id,
                        host_session_id,
                        discover_ms,
                        0,
                        0,
                        start_instant,
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
                        json_mode,
                        trace_flag,
                        &log_root,
                        &recorder,
                        subcommand_name,
                        project_root_opt.as_deref(),
                        session_id,
                        host_session_id,
                        discover_ms,
                        connect_ms,
                        0,
                        start_instant,
                    );
                }
            };

            if let Err(e) = client.handshake().await {
                connect_ms = conn_start.elapsed().as_millis() as u64;
                recorder.record("handshake", &format!("Handshake failed: {}", e.message()));
                return handle_exit(
                    Err(e),
                    json_mode,
                    trace_flag,
                    &log_root,
                    &recorder,
                    subcommand_name,
                    project_root_opt.as_deref(),
                    session_id,
                    host_session_id,
                    discover_ms,
                    connect_ms,
                    0,
                    start_instant,
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
        json_mode,
        trace_flag,
        &log_root,
        &recorder,
        subcommand_name,
        project_root_opt.as_deref(),
        session_id,
        host_session_id,
        discover_ms,
        connect_ms,
        request_ms,
        start_instant,
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
    let mut project_root = match resolve_project_root(project_path) {
        Ok(root) => Some(root),
        Err(_) => None,
    };
    let mut client = match project_root.as_ref() {
        Some(root) => {
            let recorder = Arc::new(TraceRecorder::new("mux"));
            HarnessClient::new_persistent(root.clone(), recorder).ok()
        }
        None => None,
    };

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
                        if c.send_request("ping", json!({}), 2000).await.is_err() {
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
    let payload = json!({
        "error": err.message(),
        "error_type": err.error_type(),
        "exitCode": err.exit_code(),
        "help": err.help(),
    });
    json!({
        "id": id,
        "ok": false,
        "exitCode": err.exit_code(),
        "error": err.message(),
        "error_type": err.error_type(),
        "help": err.help(),
        "text": emit_value(&payload, false),
    })
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

enum MuxParsed {
    Command(Commands, ViewOptions),
    Help(String),
}

fn parse_mux_command(argv: &[String]) -> Result<MuxParsed, CliError> {
    let mut args = Vec::with_capacity(argv.len() + 1);
    args.push("pi-unity".to_string());
    args.extend(argv.iter().cloned());
    match Cli::try_parse_from(args) {
        Ok(parsed) => match parsed.command {
            Some(Commands::Mux) => Err(usage::usage_error("mux 不能嵌套", &["pi-unity status"])),
            Some(Commands::Skills(_))
            | Some(Commands::Session(_))
            | Some(Commands::Mark(_))
            | Some(Commands::Setup(_)) => Err(usage::usage_error(
                "mux 不支持该子命令",
                &["pi-unity status", "pi-unity ping"],
            )),
            Some(cmd) => Ok(MuxParsed::Command(
                cmd,
                ViewOptions::from_parts(&parsed.fields, parsed.full),
            )),
            None => Err(usage::usage_error(
                "mux 请求需要子命令",
                &["pi-unity status"],
            )),
        },
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
        *project_root = resolve_project_root(project_path).ok();
    }
    let Some(root) = project_root.clone() else {
        let err = resolve_project_root(project_path).unwrap_err();
        return (mux_error_value(&id, &err), false);
    };

    if client.is_none() {
        let recorder = Arc::new(TraceRecorder::new("mux"));
        match HarnessClient::new_persistent(root.clone(), recorder) {
            Ok(c) => *client = Some(c),
            Err(err) => return (mux_error_value(&id, &err), false),
        }
    }

    let Some(c) = client.as_mut() else {
        return (
            mux_error_value(&id, &CliError::Other("mux client 不可用".into())),
            false,
        );
    };
    let recorder = Arc::new(TraceRecorder::new(command.subcommand_name()));
    c.set_recorder(recorder.clone());
    match execute_harness_command(command, c, &root, true, &view_opts, &recorder).await {
        Ok(output) => (mux_ok_value(&id, &output), false),
        Err(err) => (mux_error_value(&id, &err), false),
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

fn handle_exit(
    result: Result<String, CliError>,
    json_mode: bool,
    trace_flag: bool,
    log_root: &Path,
    recorder: &TraceRecorder,
    subcommand_name: &str,
    project_root: Option<&Path>,
    session_id: Option<String>,
    host_session_id: Option<String>,
    discover_ms: u64,
    connect_ms: u64,
    request_ms: u64,
    start_instant: Instant,
) -> ExitCode {
    let duration_ms = start_instant.elapsed().as_millis() as u64;
    let (exit_code, error_type) = match &result {
        Ok(_) => (0, None),
        Err(e) => (e.exit_code(), Some(e.error_type().to_string())),
    };

    let trace_id = recorder.flush_trace_if_needed(log_root, exit_code, trace_flag);
    let request_ids = recorder
        .request_ids
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .clone();
    let reconnects = recorder.reconnects.load(Ordering::SeqCst);
    let is_truncated = recorder.truncated.load(Ordering::SeqCst);

    let call_event = CallEvent {
        v: LOG_FORMAT_VERSION,
        kind: "call".to_string(),
        ts_utc: timestamp_utc(recorder.start_ms),
        version: DEFAULT_VERSION.to_string(),
        client: resolve_client_name(),
        pid: std::process::id(),
        session_id,
        host_session_id,
        subcommand: subcommand_name.to_string(),
        project_hash: compute_project_hash(project_root),
        exit_code,
        duration_ms,
        phases: CallEventPhases {
            discover_ms,
            connect_ms,
            request_ms,
        },
        flags: CallEventFlags {
            json: json_mode,
            truncated: is_truncated,
            reconnects,
        },
        error_type,
        request_ids,
        trace_id,
    };

    if let Ok(value) = serde_json::to_value(&call_event) {
        let _ = append_event_line(log_root, &value);
    }

    match result {
        Ok(output) => {
            println!("{}", output);
            ExitCode::SUCCESS
        }
        Err(err) => {
            handle_error(&err, json_mode, project_root);
            ExitCode::from(exit_code as u8)
        }
    }
}

fn handle_error(err: &CliError, json_mode: bool, project_root: Option<&Path>) {
    let mut msg = err.message().to_string();
    let help = err.help();

    if msg.len() > MAX_SAFE_RESPONSE_CHARS {
        if let Some(root) = project_root {
            let scratch_dir = root.join("Temp/PiUnityHarness/AgentScratch");
            let _ = fs::create_dir_all(&scratch_dir);
            let scratch_file = format!("large_error_{}.txt", now_ms());
            let scratch_path = scratch_dir.join(&scratch_file);
            let _ = fs::write(&scratch_path, &msg);
            let rel_path = format!("Temp/PiUnityHarness/AgentScratch/{}", scratch_file);
            msg = format!(
                "[Error truncated from {} to {} chars. Full message saved to: {}]\n{}",
                msg.len(),
                MAX_SAFE_RESPONSE_CHARS,
                rel_path,
                &msg[..MAX_SAFE_RESPONSE_CHARS]
            );
        } else {
            msg.truncate(MAX_SAFE_RESPONSE_CHARS);
        }
    }

    let out = usage::format_usage(&msg, &help, json_mode);
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
            "ratio=3.14".to_string(),
            "name=test".to_string(),
            "nested={\"a\":1}".to_string(),
            "list=[1,2,3]".to_string(),
        ];
        let val = parse_param_pairs(&pairs, None).unwrap();
        assert_eq!(val["flag"], json!(true));
        assert_eq!(val["count"], json!(42));
        assert_eq!(val["ratio"], json!(3.14));
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
}
