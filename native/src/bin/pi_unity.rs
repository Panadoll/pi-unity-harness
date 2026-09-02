use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, ExitCode};
use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::time::{Duration, Instant};


use clap::{Args, Parser, Subcommand, ValueEnum};
use serde::Deserialize;
use serde_json::{json, Value};

use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::windows::named_pipe::ClientOptions;

mod logging;

use logging::{
    append_event_line, compute_project_hash, end_session, lazy_cleanup_old_traces, log_root_dir,
    now_ms, record_mark, resolve_client_name, resolve_session, start_session, timestamp_utc,
    CallEvent, CallEventFlags, CallEventPhases, TraceRecorder, DEFAULT_VERSION, LOG_FORMAT_VERSION,
};

pub const EXPECTED_PROTOCOL_VERSION: i32 = 1;
const MAX_SAFE_RESPONSE_CHARS: usize = 32_768;
const MIN_BASE64_DETECT_LENGTH: usize = 256;

#[derive(Parser, Debug)]
#[command(
    name = "pi-unity",
    about = "CLI for Unity Editor AI Agent bridge (pi-unity-harness)",
    version = "0.1.0"
)]
struct Cli {
    /// Path to the Unity project root (defaults to auto-discovery)
    #[arg(long, global = true)]
    project_path: Option<String>,

    /// Output responses in structured JSON format on stdout
    #[arg(long, global = true)]
    json: bool,

    /// Force full trace logging to traces/ directory even on success
    #[arg(long, global = true)]
    trace: bool,

    #[command(subcommand)]
    command: Commands,
}

#[derive(Subcommand, Debug)]
enum Commands {
    /// Probe Unity Broker responsiveness
    Ping(PingArgs),

    /// Get Unity Editor and Broker status
    Status(StatusArgs),

    /// Execute C# code or script in Unity main thread
    Eval(EvalArgs),

    /// Trigger Unity script compilation and wait for domain reload to complete
    Compile(CompileArgs),

    /// Get context snapshot of active scene hierarchy, selection, and logs
    Snapshot(SnapshotArgs),

    /// List all registered Unity Pipeline [CliCommand] commands
    ListCommands(ListCommandsArgs),

    /// Execute a Unity Pipeline [CliCommand]
    Pipeline(PipelineArgs),

    /// Run Unity test suite (shortcut for run_tests pipeline command)
    RunTests(RunTestsArgs),

    /// Multi-frame visual observation (shortcut for vision_observe pipeline command)
    Observe(ObserveArgs),

    /// Viewport screenshot capture (shortcut for vision_capture pipeline command)
    Capture(CaptureArgs),

    /// Query recent operations timeline and audit history
    Timeline(TimelineArgs),

    /// Install/sync agent skills to .agents/skills/ or .claude/skills/
    Skills(SkillsArgs),

    /// Manage sticky agent sessions (start / end)
    Session(SessionArgs),

    /// Mark a skill usage event in the observability log
    Mark(MarkArgs),
}

impl Commands {
    fn subcommand_name(&self) -> &'static str {
        match self {
            Commands::Ping(_) => "ping",
            Commands::Status(_) => "status",
            Commands::Eval(_) => "eval",
            Commands::Compile(_) => "compile",
            Commands::Snapshot(_) => "snapshot",
            Commands::ListCommands(_) => "list-commands",
            Commands::Pipeline(_) => "pipeline",
            Commands::RunTests(_) => "run-tests",
            Commands::Observe(_) => "observe",
            Commands::Capture(_) => "capture",
            Commands::Timeline(_) => "timeline",
            Commands::Skills(_) => "skills",
            Commands::Session(_) => "session",
            Commands::Mark(_) => "mark",
        }
    }
}

#[derive(Args, Debug)]
struct SessionArgs {
    #[command(subcommand)]
    action: SessionSubcommands,
}

#[derive(Subcommand, Debug)]
enum SessionSubcommands {
    /// Start a new session and save to sticky registry
    Start(SessionStartArgs),

    /// End the current active session
    End,
}

#[derive(Args, Debug)]
struct SessionStartArgs {
    /// Optional task description or tag
    #[arg(long)]
    task: Option<String>,

    /// Optional agent identifier
    #[arg(long)]
    agent: Option<String>,
}

#[derive(Args, Debug)]
struct MarkArgs {
    /// Name of the skill (e.g. pi-unity-compile)
    #[arg(long)]
    skill: String,

    /// Event name (default: used)
    #[arg(long, default_value = "used")]
    event: String,
}


#[derive(Args, Debug)]
struct PingArgs {
    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 5000)]
    timeout: u64,
}

#[derive(Args, Debug)]
struct StatusArgs {
    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 5000)]
    timeout: u64,
}

#[derive(Args, Debug)]
struct EvalArgs {
    /// Inline C# code to execute
    #[arg(value_name = "CODE")]
    code: Option<String>,

    /// Path to a .cs or .repl script file
    #[arg(short = 'f', long = "file", value_name = "PATH")]
    file: Option<String>,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 30000)]
    timeout: u64,
}

#[derive(Args, Debug)]
struct CompileArgs {
    /// Timeout in milliseconds to wait for compilation and domain reload
    #[arg(long, default_value_t = 120000)]
    timeout: u64,
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq)]
enum LogLevel {
    Error,
    Warning,
    All,
}

#[derive(Args, Debug)]
struct SnapshotArgs {
    /// Max hierarchy depth to traverse
    #[arg(long, default_value_t = 3)]
    depth: u32,

    /// Max GameObjects to include in hierarchy
    #[arg(long, default_value_t = 500)]
    max_nodes: u32,

    /// Max recent logs to include
    #[arg(long, default_value_t = 50)]
    log_limit: u32,

    /// Minimum log level
    #[arg(long, value_enum, default_value_t = LogLevel::Error)]
    log_level: LogLevel,

    /// Omit component details from hierarchy dump
    #[arg(long)]
    no_components: bool,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 20000)]
    timeout: u64,
}

#[derive(Args, Debug)]
struct ListCommandsArgs {
    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 15000)]
    timeout: u64,
}

#[derive(Args, Debug)]
struct PipelineArgs {
    /// Name of the pipeline command to execute
    name: String,

    /// Key=value parameter pairs (can be repeated)
    #[arg(short = 'p', long = "param", value_name = "KEY=VAL")]
    params: Vec<String>,

    /// Raw JSON parameters object string
    #[arg(long, value_name = "JSON")]
    params_json: Option<String>,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 30000)]
    timeout: u64,
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq)]
enum TestMode {
    #[value(name = "edit", alias = "editor")]
    Edit,
    #[value(name = "play", alias = "playmode")]
    Play,
    #[value(name = "all")]
    All,
}

#[derive(Args, Debug)]
struct RunTestsArgs {
    /// Test mode (edit or play)
    #[arg(long, value_enum, default_value_t = TestMode::Edit)]
    mode: TestMode,

    /// Test name filter pattern
    #[arg(long)]
    filter: Option<String>,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 330000)]
    timeout: u64,
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq)]
enum OverlayMode {
    Grid,
    Annotations,
    Both,
    None,
}

#[derive(Args, Debug)]
struct ObserveArgs {
    /// Number of frames to capture
    #[arg(long, default_value_t = 3)]
    frames: u32,

    /// Interval between frames in milliseconds
    #[arg(long, default_value_t = 160)]
    interval: u64,

    /// Overlay mode
    #[arg(long, value_enum, default_value_t = OverlayMode::Both)]
    overlay: OverlayMode,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 30000)]
    timeout: u64,
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq)]
enum CaptureMode {
    Game,
    Scene,
}

#[derive(Args, Debug)]
struct CaptureArgs {
    /// Viewport mode
    #[arg(long, value_enum, default_value_t = CaptureMode::Game)]
    mode: CaptureMode,

    /// Output file path (optional)
    #[arg(long)]
    out: Option<String>,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 30000)]
    timeout: u64,
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq)]
enum TimelineSuccessFilter {
    All,
    Success,
    Failure,
}

#[derive(Args, Debug)]
struct TimelineArgs {
    /// Number of timeline events to return (1-200)
    #[arg(long, default_value_t = 20)]
    limit: u32,

    /// Filter by success status
    #[arg(long, value_enum, default_value_t = TimelineSuccessFilter::All)]
    success: TimelineSuccessFilter,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 15000)]
    timeout: u64,
}

#[derive(Args, Debug)]
struct SkillsArgs {
    /// Subaction (default: install)
    #[arg(default_value = "install")]
    action: String,

    /// Install skills to .agents/skills/
    #[arg(long)]
    agents: bool,

    /// Install skills to .claude/skills/
    #[arg(long)]
    claude: bool,

    /// Custom target directory for installed skills
    #[arg(long)]
    target: Option<String>,
}

#[allow(dead_code)]
#[derive(Debug, Deserialize)]
struct BridgeJson {
    project: Option<String>,
    pid: Option<u32>,
    pipe: String,
    token: String,
    generation: Option<i64>,
    #[serde(rename = "statePlaneName")]
    state_plane_name: Option<String>,
}

#[derive(Debug, PartialEq, Eq)]
enum CliError {
    ExecutionFailed(String),
    BridgeNotFound(String),
    Timeout(String),
    Other(String),
}

impl CliError {
    fn exit_code(&self) -> i32 {
        match self {
            CliError::ExecutionFailed(_) => 1,
            CliError::BridgeNotFound(_) => 2,
            CliError::Timeout(_) => 3,
            CliError::Other(_) => 1,
        }
    }

    fn error_type(&self) -> &'static str {
        match self {
            CliError::ExecutionFailed(_) => "execution_failed",
            CliError::BridgeNotFound(_) => "bridge_not_found",
            CliError::Timeout(_) => "timeout",
            CliError::Other(_) => "other",
        }
    }

    fn message(&self) -> &str {
        match self {
            CliError::ExecutionFailed(msg) => msg,
            CliError::BridgeNotFound(msg) => msg,
            CliError::Timeout(msg) => msg,
            CliError::Other(msg) => msg,
        }
    }
}


fn normalize_pipe_name(pipe_name: &str) -> String {
    let value = pipe_name.trim();
    if value.starts_with(r"\\.\pipe\") {
        value.to_string()
    } else {
        format!(r"\\.\pipe\{}", value.trim_start_matches('\\'))
    }
}

fn resolve_project_root(project_path_arg: Option<&str>) -> Result<PathBuf, CliError> {
    if let Some(explicit) = project_path_arg {
        let p = PathBuf::from(explicit);
        if p.exists() {
            return Ok(fs::canonicalize(&p).unwrap_or(p));
        }
        return Err(CliError::BridgeNotFound(format!(
            "Specified project path does not exist: {}",
            explicit
        )));
    }

    if let Ok(env_path) = std::env::var("UNITY_PROJECT_PATH") {
        if !env_path.trim().is_empty() {
            let p = PathBuf::from(env_path.trim());
            if p.join("Library/PiUnityHarness/bridge.json").exists() {
                return Ok(fs::canonicalize(&p).unwrap_or(p));
            }
        }
    }

    // Check CWD
    if let Ok(cwd) = std::env::current_dir() {
        if cwd.join("Library/PiUnityHarness/bridge.json").exists() {
            return Ok(cwd);
        }

        // Check up to 5 parent levels
        let mut curr = cwd.as_path();
        for _ in 0..5 {
            if let Some(parent) = curr.parent() {
                if parent.join("Library/PiUnityHarness/bridge.json").exists() {
                    return Ok(parent.to_path_buf());
                }
                curr = parent;
            } else {
                break;
            }
        }
    }

    // Scan running Unity.exe processes via PowerShell
    if cfg!(windows) {
        if let Ok(output) = Command::new("powershell.exe")
            .args([
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "Get-CimInstance Win32_Process -Filter \"name='Unity.exe'\" | ForEach-Object { if ($_.CommandLine) { $_.CommandLine } }",
            ])
            .output()
        {
            if output.status.success() {
                let stdout = String::from_utf8_lossy(&output.stdout);
                let re_project = regex::Regex::new(r#"(?i)-(?:projectPath|createproject)\s+(?:"([^"]+)"|'([^']+)'|([^\s\r\n]+))"#)
                    .unwrap();
                for cap in re_project.captures_iter(&stdout) {
                    let matched_str = cap.get(1).or_else(|| cap.get(2)).or_else(|| cap.get(3));
                    if let Some(m) = matched_str {
                        let candidate = PathBuf::from(m.as_str().trim());
                        if candidate.join("Library/PiUnityHarness/bridge.json").exists() {
                            return Ok(fs::canonicalize(&candidate).unwrap_or(candidate));
                        }
                    }
                }
            }
        }
    }

    Err(CliError::BridgeNotFound(
        "Could not find Unity project with active bridge.json. Ensure Unity Editor is running with com.pi.unity-harness installed, or provide --project-path."
            .to_string(),
    ))
}

fn load_bridge_json(project_root: &Path) -> Result<BridgeJson, CliError> {
    let bridge_path = project_root.join("Library/PiUnityHarness/bridge.json");
    if !bridge_path.exists() {
        return Err(CliError::BridgeNotFound(format!(
            "bridge.json not found at {}. Is Unity Editor running with com.pi.unity-harness?",
            bridge_path.display()
        )));
    }

    let mut content = fs::read_to_string(&bridge_path).map_err(|e| {
        CliError::BridgeNotFound(format!("Failed to read {}: {}", bridge_path.display(), e))
    })?;

    if content.starts_with('\u{feff}') {
        content = content.trim_start_matches('\u{feff}').to_string();
    }

    let bridge: BridgeJson = serde_json::from_str(&content).map_err(|e| {
        CliError::BridgeNotFound(format!("Failed to parse {}: {}", bridge_path.display(), e))
    })?;

    Ok(bridge)
}

struct HarnessClient {
    project_root: PathBuf,
    bridge: BridgeJson,
    req_counter: u64,
    recorder: Arc<TraceRecorder>,
}

impl HarnessClient {
    fn new(project_root: PathBuf, recorder: Arc<TraceRecorder>) -> Result<Self, CliError> {
        let bridge = load_bridge_json(&project_root)?;
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

    fn reload_bridge(&mut self) -> Result<(), CliError> {
        self.bridge = load_bridge_json(&self.project_root)?;
        Ok(())
    }

    async fn handshake(&mut self) -> Result<(), CliError> {
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

    async fn send_request(
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

fn is_base64_data(s: &str) -> bool {
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

fn strip_large_base64_and_save(value: &mut Value, project_root: &Path, depth: usize) {
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

fn fast_rand_id() -> String {
    let t = now_ms();
    let pid = std::process::id();
    format!("{:x}{:x}", pid, t % 0xffff)
}

fn create_bounded_summary(val: &Value) -> Value {
    if let Value::Object(map) = val {
        let mut summary_map = serde_json::Map::new();
        let total_keys = map.len();
        let limit = 15;
        let mut count = 0;
        for (k, v) in map {
            if count >= limit {
                break;
            }
            count += 1;
            if let Value::Array(arr) = v {
                let arr_count = arr.len();
                if arr_count > 5 {
                    let sample: Vec<Value> = arr.iter().take(3).cloned().collect();
                    summary_map.insert(
                        k.clone(),
                        json!({
                            "totalCount": arr_count,
                            "omittedCount": arr_count - 3,
                            "sample": sample,
                        }),
                    );
                    continue;
                }
            }
            summary_map.insert(k.clone(), v.clone());
        }
        if total_keys > limit {
            summary_map.insert(
                "_omittedKeysCount".to_string(),
                json!(total_keys - limit),
            );
        }
        Value::Object(summary_map)
    } else if let Value::Array(arr) = val {
        let count = arr.len();
        if count > 10 {
            let sample: Vec<Value> = arr.iter().take(5).cloned().collect();
            json!({
                "totalCount": count,
                "omittedCount": count - 5,
                "sample": sample,
            })
        } else {
            val.clone()
        }
    } else {
        val.clone()
    }
}


fn format_safe_output(
    raw_val: &Value,
    project_root: &Path,
    json_mode: bool,
    recorder: Option<&TraceRecorder>,
) -> String {
    let mut sanitized = raw_val.clone();
    strip_large_base64_and_save(&mut sanitized, project_root, 0);

    if json_mode {
        let full_json = serde_json::to_string_pretty(&json!({
            "ok": true,
            "result": sanitized
        }))
        .unwrap_or_else(|_| "{\"ok\":true}".to_string());

        if full_json.len() <= MAX_SAFE_RESPONSE_CHARS {
            return full_json;
        }

        if let Some(rec) = recorder {
            rec.mark_truncated();
            rec.record(
                "truncation",
                &format!("JSON output exceeded 32KB limit ({} chars)", full_json.len()),
            );
        }

        let scratch_dir = project_root.join("Temp/PiUnityHarness/AgentScratch");
        let _ = fs::create_dir_all(&scratch_dir);
        let scratch_file = format!("large_tool_output_{}.json", now_ms());
        let scratch_path = scratch_dir.join(&scratch_file);
        let _ = fs::write(&scratch_path, &full_json);
        let rel_path = format!("Temp/PiUnityHarness/AgentScratch/{}", scratch_file);

        let truncated_obj = json!({
            "ok": true,
            "truncated": true,
            "savedScratchPath": rel_path,
            "totalChars": full_json.len(),
            "warning": format!(
                "Tool JSON output was truncated from {} characters to prevent Error 413 (Payload Too Large). Full output saved to: {}",
                full_json.len(), rel_path
            ),
            "result": create_bounded_summary(&sanitized)
        });

        return serde_json::to_string_pretty(&truncated_obj).unwrap_or(full_json);
    }

    let text = if let Value::String(s) = &sanitized {
        s.clone()
    } else if let Some(output_field) = sanitized.get("output").and_then(Value::as_str) {
        output_field.to_string()
    } else {
        serde_json::to_string_pretty(&sanitized).unwrap_or_else(|_| format!("{:?}", sanitized))
    };

    if text.len() <= MAX_SAFE_RESPONSE_CHARS {
        return text;
    }

    if let Some(rec) = recorder {
        rec.mark_truncated();
        rec.record(
            "truncation",
            &format!("Text output exceeded 32KB limit ({} chars)", text.len()),
        );
    }

    let scratch_dir = project_root.join("Temp/PiUnityHarness/AgentScratch");
    let _ = fs::create_dir_all(&scratch_dir);
    let scratch_file = format!("large_output_{}.txt", now_ms());
    let scratch_path = scratch_dir.join(&scratch_file);
    let _ = fs::write(&scratch_path, &text);
    let rel_path = format!("Temp/PiUnityHarness/AgentScratch/{}", scratch_file);

    let head_size = (MAX_SAFE_RESPONSE_CHARS * 3) / 4;
    let tail_size = MAX_SAFE_RESPONSE_CHARS / 5;
    let head = &text[..head_size.min(text.len())];
    let tail = if text.len() > tail_size {
        &text[text.len() - tail_size..]
    } else {
        ""
    };
    let omitted = text.len().saturating_sub(head_size + tail_size);

    format!(
        "[WARNING: Output truncated from {} to {} characters. Full output saved to: {}]\n\n--- Output (first {} chars) ---\n{}\n\n... [{} characters omitted] ...\n\n--- Output (last {} chars) ---\n{}",
        text.len(),
        MAX_SAFE_RESPONSE_CHARS,
        rel_path,
        head_size,
        head,
        omitted,
        tail_size,
        tail
    )
}


fn parse_param_pairs(pairs: &[String], explicit_json: Option<&str>) -> Result<Value, CliError> {
    let mut map = serde_json::Map::new();

    if let Some(json_str) = explicit_json {
        let val: Value = serde_json::from_str(json_str).map_err(|e| {
            CliError::ExecutionFailed(format!("Invalid --params-json string: {}", e))
        })?;
        if let Value::Object(obj) = val {
            for (k, v) in obj {
                map.insert(k, v);
            }
        } else {
            return Err(CliError::ExecutionFailed(
                "--params-json must be a JSON object".to_string(),
            ));
        }
    }

    for pair in pairs {
        let mut parts = pair.splitn(2, '=');
        let key = parts.next().unwrap_or("").trim();
        let raw_val = parts.next().unwrap_or("").trim();
        if key.is_empty() {
            continue;
        }

        let val = if raw_val.eq_ignore_ascii_case("true") {
            Value::Bool(true)
        } else if raw_val.eq_ignore_ascii_case("false") {
            Value::Bool(false)
        } else if let Ok(n) = raw_val.parse::<i64>() {
            Value::Number(n.into())
        } else if let Ok(f) = raw_val.parse::<f64>() {
            serde_json::Number::from_f64(f)
                .map(Value::Number)
                .unwrap_or_else(|| Value::String(raw_val.to_string()))
        } else if (raw_val.starts_with('{') && raw_val.ends_with('}'))
            || (raw_val.starts_with('[') && raw_val.ends_with(']'))
        {
            serde_json::from_str(raw_val).unwrap_or_else(|_| Value::String(raw_val.to_string()))
        } else {
            Value::String(raw_val.to_string())
        };

        map.insert(key.to_string(), val);
    }

    Ok(Value::Object(map))
}

#[tokio::main]
async fn main() -> ExitCode {
    let start_instant = Instant::now();
    let log_root = log_root_dir();
    lazy_cleanup_old_traces(&log_root);

    let cli = Cli::parse();
    let json_mode = cli.json;
    let trace_flag = cli.trace;
    let subcommand_name = cli.command.subcommand_name();
    let recorder = Arc::new(TraceRecorder::new(subcommand_name));

    recorder.record(
        "init",
        &format!("pi-unity CLI started (subcommand: {})", subcommand_name),
    );

    let (session_id, host_session_id) = resolve_session(&log_root);

    let mut discover_ms = 0u64;
    let mut connect_ms = 0u64;
    let mut request_ms = 0u64;
    let mut project_root_opt: Option<PathBuf> = None;

    let result: Result<String, CliError> = match cli.command {
        Commands::Session(session_args) => match session_args.action {
            SessionSubcommands::Start(start_args) => {
                recorder.record("session", "Starting sticky session");
                match start_session(&log_root, start_args.task, start_args.agent) {
                    Ok(data) => {
                        if json_mode {
                            Ok(serde_json::to_string_pretty(&json!({
                                "ok": true,
                                "result": data
                            }))
                            .unwrap())
                        } else {
                            Ok(format!("Session started: {}", data.session_id))
                        }
                    }
                    Err(e) => Err(CliError::Other(format!("Failed to start session: {}", e))),
                }
            }
            SessionSubcommands::End => {
                recorder.record("session", "Ending sticky session");
                match end_session(&log_root) {
                    Ok(Some(id)) => {
                        if json_mode {
                            Ok(serde_json::to_string_pretty(&json!({
                                "ok": true,
                                "result": { "sessionId": id, "ended": true }
                            }))
                            .unwrap())
                        } else {
                            Ok(format!("Session ended: {}", id))
                        }
                    }
                    Ok(None) => {
                        if json_mode {
                            Ok(serde_json::to_string_pretty(&json!({
                                "ok": true,
                                "result": { "ended": false, "message": "No active session" }
                            }))
                            .unwrap())
                        } else {
                            Ok("No active session".to_string())
                        }
                    }
                    Err(e) => Err(CliError::Other(format!("Failed to end session: {}", e))),
                }
            }
        },
        Commands::Mark(mark_args) => {
            recorder.record("mark", &format!("Marking skill {}", mark_args.skill));
            record_mark(&log_root, &mark_args.skill, &mark_args.event);
            if json_mode {
                Ok(serde_json::to_string_pretty(&json!({
                    "ok": true,
                    "result": {
                        "skill": mark_args.skill,
                        "event": mark_args.event
                    }
                }))
                .unwrap())
            } else {
                Ok(format!(
                    "Marked skill: {} (event: {})",
                    mark_args.skill, mark_args.event
                ))
            }
        }
        Commands::Skills(skills_args) => {
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
        other_cmd => {
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

    if json_mode {
        let out = json!({
            "ok": false,
            "error": msg,
            "exitCode": err.exit_code()
        });
        println!("{}", serde_json::to_string_pretty(&out).unwrap());
    } else {
        eprintln!("[pi-unity error] {}", msg);
    }
}

fn handle_skills_command(
    args: SkillsArgs,
    project_root: &Path,
    json_mode: bool,
) -> Result<String, CliError> {
    let repo_root = find_skills_source_root(project_root);
    let skills_source = repo_root.join("skills");
    if !skills_source.exists() {
        return Err(CliError::Other(format!(
            "Skills source directory not found at {}",
            skills_source.display()
        )));
    }

    let mut target_dirs: Vec<PathBuf> = Vec::new();

    if let Some(custom_target) = args.target {
        target_dirs.push(PathBuf::from(custom_target));
    } else {
        if args.agents || (!args.agents && !args.claude) {
            target_dirs.push(project_root.join(".agents/skills"));
        }
        if args.claude {
            target_dirs.push(project_root.join(".claude/skills"));
        }
    }

    let mut installed_count = 0;
    let mut installed_skills: Vec<String> = Vec::new();

    let entries = fs::read_dir(&skills_source).map_err(|e| {
        CliError::Other(format!("Failed to read {}: {}", skills_source.display(), e))
    })?;

    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            let skill_name = entry.file_name().to_string_lossy().into_owned();
            let skill_file = path.join("SKILL.md");
            if skill_file.exists() {
                for target_dir in &target_dirs {
                    let dest_skill_dir = target_dir.join(&skill_name);
                    let _ = fs::create_dir_all(&dest_skill_dir);
                    if let Ok(content) = fs::read_to_string(&skill_file) {
                        let dest_file = dest_skill_dir.join("SKILL.md");
                        if fs::write(&dest_file, content).is_ok() {
                            installed_count += 1;
                        }
                    }
                }
                installed_skills.push(skill_name);
            }
        }
    }

    let result = json!({
        "installed": true,
        "skillsCount": installed_skills.len(),
        "skills": installed_skills,
        "targets": target_dirs.iter().map(|d| d.display().to_string()).collect::<Vec<_>>(),
        "totalFilesCopied": installed_count
    });

    if json_mode {
        Ok(serde_json::to_string_pretty(&json!({ "ok": true, "result": result })).unwrap())
    } else {
        let msg = format!(
            "[pi-unity] Installed {} skills ({}) to {}",
            result["skillsCount"],
            result["skills"].as_array().map(|a| a.iter().filter_map(|v| v.as_str()).collect::<Vec<_>>().join(", ")).unwrap_or_default(),
            result["targets"].as_array().map(|a| a.iter().filter_map(|v| v.as_str()).collect::<Vec<_>>().join("; ")).unwrap_or_default()
        );
        Ok(msg)
    }
}

fn find_skills_source_root(project_root: &Path) -> PathBuf {
    if project_root.join("skills").exists() {
        return project_root.to_path_buf();
    }
    let mut curr = project_root;
    while let Some(parent) = curr.parent() {
        if parent.join("skills").exists() {
            return parent.to_path_buf();
        }
        curr = parent;
    }
    if let Ok(cwd) = std::env::current_dir() {
        if cwd.join("skills").exists() {
            return cwd;
        }
        let mut curr = cwd.as_path();
        while let Some(parent) = curr.parent() {
            if parent.join("skills").exists() {
                return parent.to_path_buf();
            }
            curr = parent;
        }
    }
    if let Ok(exe) = std::env::current_exe() {
        let mut curr = exe.as_path();
        while let Some(parent) = curr.parent() {
            if parent.join("skills").exists() {
                return parent.to_path_buf();
            }
            curr = parent;
        }
    }
    project_root.to_path_buf()
}

async fn execute_harness_command(
    command: Commands,
    client: &mut HarnessClient,
    project_root: &Path,
    json_mode: bool,
    recorder: &TraceRecorder,
) -> Result<String, CliError> {
    match command {
        Commands::Ping(args) => {
            let res = client.send_request("ping", json!({}), args.timeout).await?;
            Ok(format_safe_output(
                &res,
                project_root,
                json_mode,
                Some(recorder),
            ))
        }

        Commands::Status(args) => {
            let res = client
                .send_request("status", json!({}), args.timeout)
                .await?;
            Ok(format_safe_output(
                &res,
                project_root,
                json_mode,
                Some(recorder),
            ))
        }

        Commands::Eval(args) => {
            let (req_type, payload) = if let Some(code_str) = args.code {
                recorder.record_sensitive("eval", "Evaluating inline C# code", &code_str);
                (
                    "validate_execute_code",
                    json!({ "code": code_str }),
                )
            } else if let Some(file_str) = args.file {
                let path_buf = PathBuf::from(&file_str);
                let full_path = if path_buf.is_absolute() {
                    path_buf
                } else {
                    project_root.join(path_buf)
                };

                if !full_path.exists() {
                    return Err(CliError::ExecutionFailed(format!(
                        "Eval file does not exist: {}",
                        full_path.display()
                    )));
                }

                let forward_rel = match full_path.strip_prefix(project_root) {
                    Ok(rel) => rel.to_string_lossy().replace('\\', "/"),
                    Err(_) => full_path.to_string_lossy().replace('\\', "/"),
                };

                recorder.record("eval", &format!("Evaluating script file: {}", forward_rel));

                (
                    "validate_execute_file",
                    json!({ "filePath": forward_rel }),
                )
            } else {
                return Err(CliError::ExecutionFailed(
                    "Either inline code or -f/--file must be provided for eval".to_string(),
                ));
            };

            let res = client
                .send_request(req_type, payload, args.timeout)
                .await?;
            Ok(format_safe_output(
                &res,
                project_root,
                json_mode,
                Some(recorder),
            ))
        }

        Commands::Compile(args) => {
            recorder.record("compile", "Triggering recompile on Unity broker");
            let initial_res = client
                .send_request("recompile", json!({}), args.timeout)
                .await;

            if let Err(ref e) = initial_res {
                if let CliError::BridgeNotFound(msg) = e {
                    return Err(CliError::BridgeNotFound(msg.clone()));
                }
            }

            let deadline = Instant::now() + Duration::from_millis(args.timeout);
            tokio::time::sleep(Duration::from_millis(600)).await;

            let mut last_status = Value::Null;
            let mut is_ready = false;

            while Instant::now() < deadline {
                let _ = client.reload_bridge();
                recorder.record("compile_poll", "Polling status for ready state");
                match client.send_request("status", json!({}), 3000).await {
                    Ok(status_val) => {
                        let is_ready_now = status_val
                            .get("managedState")
                            .and_then(Value::as_str)
                            == Some("ready");
                        last_status = status_val;
                        if is_ready_now {
                            is_ready = true;
                            recorder.record("compile_poll", "Managed state recovered to ready");
                            break;
                        }
                    }
                    Err(CliError::BridgeNotFound(msg)) => {
                        return Err(CliError::BridgeNotFound(msg));
                    }
                    Err(_) => {
                        // Connection dropped or pipe not ready yet during reload; expected.
                    }
                }
                tokio::time::sleep(Duration::from_millis(500)).await;
            }

            if !is_ready {
                return Err(CliError::Timeout(
                    "Compilation and domain reload timed out waiting for managed state 'ready'"
                        .to_string(),
                ));
            }

            let result = json!({
                "compiled": true,
                "managedState": "ready",
                "generation": last_status.get("managedGeneration").cloned().unwrap_or(Value::Null),
                "editorStatus": last_status.get("editorStatus").cloned().unwrap_or(Value::Null),
            });

            Ok(format_safe_output(
                &result,
                project_root,
                json_mode,
                Some(recorder),
            ))
        }

        Commands::Snapshot(args) => {
            let log_level_str = match args.log_level {
                LogLevel::Error => "error",
                LogLevel::Warning => "warning",
                LogLevel::All => "all",
            };

            let payload = json!({
                "maxDepth": args.depth,
                "maxNodes": args.max_nodes,
                "logLimit": args.log_limit,
                "logLevel": log_level_str,
                "includeComponents": !args.no_components,
            });

            let res = client
                .send_request("context_snapshot", payload, args.timeout)
                .await?;

            let mut final_content = res.clone();
            if let Some(channel) = res.get("channel").and_then(Value::as_str) {
                if channel == "file" {
                    if let Some(file_rel) = res.get("filePath").and_then(Value::as_str) {
                        let full_file = if Path::new(file_rel).is_absolute() {
                            PathBuf::from(file_rel)
                        } else {
                            project_root.join(file_rel)
                        };

                        if full_file.exists() {
                            if let Ok(file_text) = fs::read_to_string(&full_file) {
                                if let Ok(parsed_json) = serde_json::from_str::<Value>(&file_text) {
                                    final_content = parsed_json;
                                } else {
                                    final_content = Value::String(file_text);
                                }
                            }
                        }
                    }
                }
            }

            Ok(format_safe_output(
                &final_content,
                project_root,
                json_mode,
                Some(recorder),
            ))
        }

        Commands::ListCommands(args) => {
            let res = client
                .send_request("list_commands", json!({}), args.timeout)
                .await?;
            Ok(format_safe_output(
                &res,
                project_root,
                json_mode,
                Some(recorder),
            ))
        }

        Commands::Pipeline(args) => {
            let param_obj = parse_param_pairs(&args.params, args.params_json.as_deref())?;
            let params_json_str =
                serde_json::to_string(&param_obj).unwrap_or_else(|_| "{}".to_string());

            recorder.record("pipeline", &format!("Executing pipeline command {}", args.name));

            let payload = json!({
                "name": args.name,
                "parametersJson": params_json_str,
            });

            let res = client
                .send_request("command", payload, args.timeout)
                .await?;
            Ok(format_safe_output(
                &res,
                project_root,
                json_mode,
                Some(recorder),
            ))
        }

        Commands::RunTests(args) => {
            let mode_str = match args.mode {
                TestMode::Edit => "editor",
                TestMode::Play => "playmode",
                TestMode::All => "all",
            };

            let mut param_map = serde_json::Map::new();
            param_map.insert("mode".to_string(), Value::String(mode_str.to_string()));
            if let Some(f) = args.filter {
                param_map.insert("filter".to_string(), Value::String(f));
            }

            let params_json_str = serde_json::to_string(&Value::Object(param_map)).unwrap();

            let payload = json!({
                "name": "run_tests",
                "parametersJson": params_json_str,
            });

            let res = client
                .send_request("command", payload, args.timeout)
                .await?;
            Ok(format_safe_output(
                &res,
                project_root,
                json_mode,
                Some(recorder),
            ))
        }

        Commands::Observe(args) => {
            let overlay_str = match args.overlay {
                OverlayMode::Grid => "grid",
                OverlayMode::Annotations => "annotations",
                OverlayMode::Both => "both",
                OverlayMode::None => "none",
            };

            let mut param_map = serde_json::Map::new();
            param_map.insert("mode".to_string(), Value::String("game".to_string()));
            param_map.insert("frames".to_string(), json!(args.frames));
            param_map.insert("intervalMs".to_string(), json!(args.interval));
            param_map.insert("overlay".to_string(), Value::String(overlay_str.to_string()));

            let params_json_str = serde_json::to_string(&Value::Object(param_map)).unwrap();

            let payload = json!({
                "name": "vision_observe",
                "parametersJson": params_json_str,
            });

            let res = client
                .send_request("command", payload, args.timeout)
                .await?;
            Ok(format_safe_output(
                &res,
                project_root,
                json_mode,
                Some(recorder),
            ))
        }

        Commands::Capture(args) => {
            let mode_str = match args.mode {
                CaptureMode::Game => "game",
                CaptureMode::Scene => "scene",
            };

            let mut param_map = serde_json::Map::new();
            param_map.insert("mode".to_string(), Value::String(mode_str.to_string()));
            if let Some(out_p) = args.out {
                param_map.insert("outPath".to_string(), Value::String(out_p));
            }

            let params_json_str = serde_json::to_string(&Value::Object(param_map)).unwrap();

            let payload = json!({
                "name": "vision_capture",
                "parametersJson": params_json_str,
            });

            let res = client
                .send_request("command", payload, args.timeout)
                .await?;
            Ok(format_safe_output(
                &res,
                project_root,
                json_mode,
                Some(recorder),
            ))
        }

        Commands::Timeline(args) => {
            let success_filter = match args.success {
                TimelineSuccessFilter::All => "all",
                TimelineSuccessFilter::Success => "success",
                TimelineSuccessFilter::Failure => "failure",
            };

            let payload = json!({
                "limit": args.limit,
                "success": success_filter,
            });

            let res = client
                .send_request("timeline", payload, args.timeout)
                .await?;
            Ok(format_safe_output(
                &res,
                project_root,
                json_mode,
                Some(recorder),
            ))
        }

        Commands::Skills(_) | Commands::Session(_) | Commands::Mark(_) => unreachable!(),
    }
}


#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_exit_codes() {
        assert_eq!(CliError::ExecutionFailed("err".into()).exit_code(), 1);
        assert_eq!(CliError::BridgeNotFound("err".into()).exit_code(), 2);
        assert_eq!(CliError::Timeout("err".into()).exit_code(), 3);
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
        for i in 0..1000 {
            large_map.insert(format!("key_{}", i), json!("A long value to exceed buffer size"));
        }
        let raw = Value::Object(large_map);
        let out = format_safe_output(&raw, &temp_dir, true, None);
        assert!(out.len() <= MAX_SAFE_RESPONSE_CHARS);
        let parsed: Value = serde_json::from_str(&out).unwrap();
        assert_eq!(parsed["ok"], json!(true));
        assert_eq!(parsed["truncated"], json!(true));
        assert!(parsed["savedScratchPath"].as_str().is_some());
    }

    #[test]
    fn test_format_safe_output_truncation_text() {
        let temp_dir = std::env::temp_dir();
        let long_str = Value::String("Line of test output. ".repeat(3000));
        let out = format_safe_output(&long_str, &temp_dir, false, None);
        assert!(out.contains("WARNING: Output truncated"));
        assert!(out.contains("Full output saved to:"));
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
