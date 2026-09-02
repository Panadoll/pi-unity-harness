use clap::{Args, Parser, Subcommand, ValueEnum};

#[derive(Parser, Debug)]
#[command(
    name = "pi-unity",
    about = "CLI for Unity Editor AI Agent bridge (pi-unity-harness)",
    version = "0.1.0"
)]
pub(crate) struct Cli {
    /// Path to the Unity project root (defaults to auto-discovery)
    #[arg(long, global = true)]
    pub(crate) project_path: Option<String>,

    /// Output responses in structured JSON format on stdout
    #[arg(long, global = true)]
    pub(crate) json: bool,

    /// Force full trace logging to traces/ directory even on success
    #[arg(long, global = true)]
    pub(crate) trace: bool,

    #[command(subcommand)]
    pub(crate) command: Commands,
}

#[derive(Subcommand, Debug)]
pub(crate) enum Commands {
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
    pub(crate) fn subcommand_name(&self) -> &'static str {
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
pub(crate) struct SessionArgs {
    #[command(subcommand)]
    pub(crate) action: SessionSubcommands,
}

#[derive(Subcommand, Debug)]
pub(crate) enum SessionSubcommands {
    /// Start a new session and save to sticky registry
    Start(SessionStartArgs),

    /// End the current active session
    End,
}

#[derive(Args, Debug)]
pub(crate) struct SessionStartArgs {
    /// Optional task description or tag
    #[arg(long)]
    pub(crate) task: Option<String>,

    /// Optional agent identifier
    #[arg(long)]
    pub(crate) agent: Option<String>,
}

#[derive(Args, Debug)]
pub(crate) struct MarkArgs {
    /// Name of the skill (e.g. pi-unity)
    #[arg(long)]
    pub(crate) skill: String,

    /// Event name (default: used)
    #[arg(long, default_value = "used")]
    pub(crate) event: String,
}


#[derive(Args, Debug)]
pub(crate) struct PingArgs {
    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 5000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct StatusArgs {
    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 5000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct EvalArgs {
    /// Inline C# code to execute
    #[arg(value_name = "CODE")]
    pub(crate) code: Option<String>,

    /// Path to a .cs or .repl script file
    #[arg(short = 'f', long = "file", value_name = "PATH")]
    pub(crate) file: Option<String>,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 30000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct CompileArgs {
    /// Timeout in milliseconds to wait for compilation and domain reload
    #[arg(long, default_value_t = 120000)]
    pub(crate) timeout: u64,
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq)]
pub(crate) enum LogLevel {
    Error,
    Warning,
    All,
}

#[derive(Args, Debug)]
pub(crate) struct SnapshotArgs {
    /// Max hierarchy depth to traverse
    #[arg(long, default_value_t = 3)]
    pub(crate) depth: u32,

    /// Max GameObjects to include in hierarchy
    #[arg(long, default_value_t = 500)]
    pub(crate) max_nodes: u32,

    /// Max recent logs to include
    #[arg(long, default_value_t = 50)]
    pub(crate) log_limit: u32,

    /// Minimum log level
    #[arg(long, value_enum, default_value_t = LogLevel::Error)]
    pub(crate) log_level: LogLevel,

    /// Omit component details from hierarchy dump
    #[arg(long)]
    pub(crate) no_components: bool,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 20000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct ListCommandsArgs {
    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 15000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct PipelineArgs {
    /// Name of the pipeline command to execute
    pub(crate) name: String,

    /// Key=value parameter pairs (can be repeated)
    #[arg(short = 'p', long = "param", value_name = "KEY=VAL")]
    pub(crate) params: Vec<String>,

    /// Raw JSON parameters object string
    #[arg(long, value_name = "JSON")]
    pub(crate) params_json: Option<String>,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 30000)]
    pub(crate) timeout: u64,
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq)]
pub(crate) enum TestMode {
    #[value(name = "edit", alias = "editor")]
    Edit,
    #[value(name = "play", alias = "playmode")]
    Play,
    #[value(name = "all")]
    All,
}

#[derive(Args, Debug)]
pub(crate) struct RunTestsArgs {
    /// Test mode (edit or play)
    #[arg(long, value_enum, default_value_t = TestMode::Edit)]
    pub(crate) mode: TestMode,

    /// Test name filter pattern
    #[arg(long)]
    pub(crate) filter: Option<String>,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 330000)]
    pub(crate) timeout: u64,
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq)]
pub(crate) enum OverlayMode {
    Grid,
    Annotations,
    Both,
    None,
}

#[derive(Args, Debug)]
pub(crate) struct ObserveArgs {
    /// Number of frames to capture
    #[arg(long, default_value_t = 3)]
    pub(crate) frames: u32,

    /// Interval between frames in milliseconds
    #[arg(long, default_value_t = 160)]
    pub(crate) interval: u64,

    /// Overlay mode
    #[arg(long, value_enum, default_value_t = OverlayMode::Both)]
    pub(crate) overlay: OverlayMode,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 30000)]
    pub(crate) timeout: u64,
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq)]
pub(crate) enum CaptureMode {
    Game,
    Scene,
}

#[derive(Args, Debug)]
pub(crate) struct CaptureArgs {
    /// Viewport mode
    #[arg(long, value_enum, default_value_t = CaptureMode::Game)]
    pub(crate) mode: CaptureMode,

    /// Output file path (optional)
    #[arg(long)]
    pub(crate) out: Option<String>,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 30000)]
    pub(crate) timeout: u64,
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq)]
pub(crate) enum TimelineSuccessFilter {
    All,
    Success,
    Failure,
}

#[derive(Args, Debug)]
pub(crate) struct TimelineArgs {
    /// Number of timeline events to return (1-200)
    #[arg(long, default_value_t = 20)]
    pub(crate) limit: u32,

    /// Filter by success status
    #[arg(long, value_enum, default_value_t = TimelineSuccessFilter::All)]
    pub(crate) success: TimelineSuccessFilter,

    /// Request timeout in milliseconds
    #[arg(long, default_value_t = 15000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct SkillsArgs {
    /// Subaction (default: install)
    #[arg(default_value = "install")]
    pub(crate) action: String,

    /// Install skills to .agents/skills/
    #[arg(long)]
    pub(crate) agents: bool,

    /// Install skills to .claude/skills/
    #[arg(long)]
    pub(crate) claude: bool,

    /// Custom target directory for installed skills
    #[arg(long)]
    pub(crate) target: Option<String>,
}

