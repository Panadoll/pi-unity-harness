use clap::{Args, Parser, Subcommand, ValueEnum};

use super::version::VERSION;

#[derive(Parser, Debug)]
#[command(
    name = "pi-unity",
    about = "从 shell 驱动正在运行的 Unity Editor",
    version = VERSION,
    disable_version_flag = true,
    subcommand_required = false,
    arg_required_else_help = false,
    after_help = "示例:\n  pi-unity\n  pi-unity snapshot\n  pi-unity eval \"UnityEngine.Application.unityVersion\""
)]
pub(crate) struct Cli {
    /// Unity 项目根目录（默认自动发现）
    #[arg(long, global = true)]
    pub(crate) project_path: Option<String>,

    /// stdout 输出 JSON（默认 TOON）
    #[arg(long, global = true)]
    pub(crate) json: bool,

    /// 成功时也写完整 trace
    #[arg(long, global = true)]
    pub(crate) trace: bool,

    /// 在默认列之外追加字段，逗号分隔
    #[arg(long, global = true, value_delimiter = ',')]
    pub(crate) fields: Vec<String>,

    /// 输出长字段 / 全量 hierarchy / 完整 schema
    #[arg(long, global = true)]
    pub(crate) full: bool,

    #[command(subcommand)]
    pub(crate) command: Option<Commands>,
}

#[derive(Subcommand, Debug)]
pub(crate) enum Commands {
    /// 探测 broker
    #[command(after_help = "示例:\n  pi-unity ping\n  pi-unity ping --timeout 3000")]
    Ping(PingArgs),

    /// Editor / 域重载 / 模态
    #[command(after_help = "示例:\n  pi-unity status\n  pi-unity status --json")]
    Status(StatusArgs),

    /// 主线程执行 C#
    #[command(after_help = "示例:\n  pi-unity eval \"UnityEngine.Application.unityVersion\"\n  pi-unity eval -f Temp/PiUnityHarness/AgentScratch/probe.repl")]
    Eval(EvalArgs),

    /// 编译并等到 ready
    #[command(after_help = "示例:\n  pi-unity compile\n  pi-unity compile --timeout 180000")]
    Compile(CompileArgs),

    /// 场景树、选中、日志
    #[command(after_help = "示例:\n  pi-unity snapshot\n  pi-unity snapshot --fields childCount,tag\n  pi-unity snapshot --full")]
    Snapshot(SnapshotArgs),

    /// 列出 [CliCommand]
    #[command(after_help = "示例:\n  pi-unity list-commands\n  pi-unity list-commands --full")]
    ListCommands(ListCommandsArgs),

    /// 跑一条 pipeline
    #[command(after_help = "示例:\n  pi-unity pipeline gameobject_find -p name=\"Main Camera\"\n  pi-unity pipeline uitree_find -p query=\"Start Game\"")]
    Pipeline(PipelineArgs),

    /// EditMode / PlayMode 测试
    #[command(after_help = "示例:\n  pi-unity run-tests --mode edit\n  pi-unity run-tests --mode play --filter FooTests")]
    RunTests(RunTestsArgs),

    /// 多帧画面
    #[command(after_help = "示例:\n  pi-unity observe\n  pi-unity observe --frames 4 --overlay none")]
    Observe(ObserveArgs),

    /// 单张截图
    #[command(after_help = "示例:\n  pi-unity capture\n  pi-unity capture --mode scene --out Temp/shot.png")]
    Capture(CaptureArgs),

    /// 最近操作
    #[command(after_help = "示例:\n  pi-unity timeline\n  pi-unity timeline --success failure --limit 50")]
    Timeline(TimelineArgs),

    /// 安装 Agent Skill
    #[command(after_help = "示例:\n  pi-unity skills install --agents\n  pi-unity skills check")]
    Skills(SkillsArgs),

    /// 粘性会话
    #[command(after_help = "示例:\n  pi-unity session start --task \"重构\"\n  pi-unity session end")]
    Session(SessionArgs),

    /// 记一条 skill 事件
    #[command(after_help = "示例:\n  pi-unity mark --skill pi-unity --event used")]
    Mark(MarkArgs),

    /// 安装 SessionStart hook
    #[command(after_help = "示例:\n  pi-unity setup\n  pi-unity setup --project")]
    Setup(SetupArgs),
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
            Commands::Setup(_) => "setup",
        }
    }
}

#[derive(Args, Debug)]
pub(crate) struct SetupArgs {
    /// 写到当前项目的 .claude / .codex / .opencode，而不是用户目录
    #[arg(long)]
    pub(crate) project: bool,
}

#[derive(Args, Debug)]
pub(crate) struct SessionArgs {
    #[command(subcommand)]
    pub(crate) action: SessionSubcommands,
}

#[derive(Subcommand, Debug)]
pub(crate) enum SessionSubcommands {
    /// 开始会话
    Start(SessionStartArgs),
    /// 结束会话（无会话时 no-op）
    End,
}

#[derive(Args, Debug)]
pub(crate) struct SessionStartArgs {
    #[arg(long)]
    pub(crate) task: Option<String>,
    #[arg(long)]
    pub(crate) agent: Option<String>,
}

#[derive(Args, Debug)]
pub(crate) struct MarkArgs {
    #[arg(long)]
    pub(crate) skill: String,
    #[arg(long, default_value = "used")]
    pub(crate) event: String,
}

#[derive(Args, Debug)]
pub(crate) struct PingArgs {
    /// 超时毫秒
    #[arg(long, default_value_t = 5000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct StatusArgs {
    /// 超时毫秒
    #[arg(long, default_value_t = 5000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct EvalArgs {
    #[arg(value_name = "CODE")]
    pub(crate) code: Option<String>,
    #[arg(short = 'f', long = "file", value_name = "PATH")]
    pub(crate) file: Option<String>,
    /// 超时毫秒
    #[arg(long, default_value_t = 30000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct CompileArgs {
    /// 等待编译和域重载的超时毫秒
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
    /// 层级深度，默认 3
    #[arg(long, default_value_t = 3)]
    pub(crate) depth: u32,
    /// 最大节点数，默认 500
    #[arg(long, default_value_t = 500)]
    pub(crate) max_nodes: u32,
    /// 日志条数，默认 50
    #[arg(long, default_value_t = 50)]
    pub(crate) log_limit: u32,
    /// 最低日志等级，默认 error
    #[arg(long, value_enum, default_value_t = LogLevel::Error)]
    pub(crate) log_level: LogLevel,
    /// 超时毫秒
    #[arg(long, default_value_t = 20000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct ListCommandsArgs {
    /// 超时毫秒
    #[arg(long, default_value_t = 15000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct PipelineArgs {
    pub(crate) name: String,
    #[arg(short = 'p', long = "param", value_name = "KEY=VAL")]
    pub(crate) params: Vec<String>,
    #[arg(long, value_name = "JSON")]
    pub(crate) params_json: Option<String>,
    /// 超时毫秒
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
    /// 默认 edit
    #[arg(long, value_enum, default_value_t = TestMode::Edit)]
    pub(crate) mode: TestMode,
    #[arg(long)]
    pub(crate) filter: Option<String>,
    /// 超时毫秒，默认 330000
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
    /// 默认 3
    #[arg(long, default_value_t = 3)]
    pub(crate) frames: u32,
    /// 默认 160
    #[arg(long, default_value_t = 160)]
    pub(crate) interval: u64,
    /// 默认 both
    #[arg(long, value_enum, default_value_t = OverlayMode::Both)]
    pub(crate) overlay: OverlayMode,
    /// 超时毫秒
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
    /// 默认 game
    #[arg(long, value_enum, default_value_t = CaptureMode::Game)]
    pub(crate) mode: CaptureMode,
    #[arg(long)]
    pub(crate) out: Option<String>,
    /// 超时毫秒
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
    /// 默认 20
    #[arg(long, default_value_t = 20)]
    pub(crate) limit: u32,
    /// 默认 all
    #[arg(long, value_enum, default_value_t = TimelineSuccessFilter::All)]
    pub(crate) success: TimelineSuccessFilter,
    /// 超时毫秒
    #[arg(long, default_value_t = 15000)]
    pub(crate) timeout: u64,
}

#[derive(Args, Debug)]
pub(crate) struct SkillsArgs {
    /// install 或 check，默认 install
    #[arg(default_value = "install")]
    pub(crate) action: String,
    #[arg(long)]
    pub(crate) agents: bool,
    #[arg(long)]
    pub(crate) claude: bool,
    #[arg(long)]
    pub(crate) target: Option<String>,
}
