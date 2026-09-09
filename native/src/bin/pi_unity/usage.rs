//! 用法错误：stdout 结构化，exit 2。

use clap::error::ErrorKind;
use clap::Parser;
use serde_json::json;

use super::args::Cli;
use super::client::CliError;
use super::toon;
use super::version::VERSION;

pub fn try_version_fast_path(args: &[String]) -> bool {
    matches!(args, [flag] if flag == "-v" || flag == "-V" || flag == "--version")
}

pub fn print_version() {
    println!("{VERSION}");
}

pub fn usage_error(error: impl Into<String>, help: &[&str]) -> CliError {
    CliError::Usage {
        error: error.into(),
        help: help.iter().map(|s| (*s).to_string()).collect(),
    }
}

pub fn error_payload(err: &CliError) -> serde_json::Value {
    json!({
        "ok": false,
        "error": err.message(),
        "error_type": err.error_type(),
        "exitCode": err.exit_code(),
        "help": err.help(),
    })
}

pub fn format_error(err: &CliError, json_mode: bool) -> String {
    let payload = error_payload(err);
    if json_mode {
        return serde_json::to_string_pretty(&payload).unwrap();
    }
    toon::encode(&payload)
}

pub fn format_usage(error: &str, help: &[String], json_mode: bool) -> String {
    format_error(
        &CliError::Usage {
            error: error.to_string(),
            help: help.to_vec(),
        },
        json_mode,
    )
}

pub fn valid_flags(subcommand: &str) -> &'static str {
    match subcommand {
        "ping" | "status" => "--timeout (--help 永远合法)",
        "eval" => "CODE, -f/--file, --timeout (--help 永远合法)",
        "compile" => "--timeout (--help 永远合法)",
        "snapshot" => "--depth, --max-nodes, --log-limit, --log-level, --timeout, --fields, --full (--help 永远合法)",
        "list-commands" => "--timeout, --fields, --full (--help 永远合法)",
        "pipeline" => "<name>, -p/--param, --params-json, --timeout (--help 永远合法)",
        "run-tests" => "--mode, --filter, --timeout (--help 永远合法)",
        "observe" => "--frames, --interval, --overlay, --timeout (--help 永远合法)",
        "capture" => "--mode, --out, --timeout (--help 永远合法)",
        "timeline" => "--limit, --success, --timeout, --fields, --full (--help 永远合法)",
        "skills" => "install|check, --agents, --claude, --target (--help 永远合法)",
        "session" => "start|end, --task, --agent (--help 永远合法)",
        "mark" => "--skill, --event (--help 永远合法)",
        "setup" => "--project (--help 永远合法)",
        _ => "--project-path, --json, --trace, --fields, --full, --help, --version",
    }
}

const RENAMES: &[(&str, &str)] = &[
    (
        "--no-components",
        "--no-components 已删除；默认不含 components，全量用 --full 或 --fields components",
    ),
    (
        "--status",
        "--status 已改名；请用 --success（timeline）或 pi-unity status",
    ),
];

pub fn clap_to_cli_error(err: clap::Error) -> Result<String, CliError> {
    match err.kind() {
        ErrorKind::DisplayHelp | ErrorKind::DisplayHelpOnMissingArgumentOrSubcommand => {
            Ok(err.render().to_string())
        }
        ErrorKind::DisplayVersion => Ok(format!("{VERSION}\n")),
        _ => {
            let rendered = err.render().to_string();
            let (msg, help) = translate_clap(&rendered);
            Err(CliError::Usage { error: msg, help })
        }
    }
}

pub fn from_clap_error(err: clap::Error, json_mode: bool) -> (String, i32) {
    match clap_to_cli_error(err) {
        Ok(out) => (out, 0),
        Err(cli_err) => (
            format_usage(cli_err.message(), &cli_err.help(), json_mode),
            2,
        ),
    }
}

fn translate_clap(rendered: &str) -> (String, Vec<String>) {
    let compact = rendered.replace('\r', "");
    if let Some(flag) = extract_unknown_flag(&compact) {
        for (old, hint) in RENAMES {
            if flag == *old {
                return ((*hint).to_string(), vec!["pi-unity --help".to_string()]);
            }
        }
        let cmd = infer_subcommand(&compact);
        let msg = format!("未知 flag {flag}（{cmd}）");
        let help = vec![format!("{cmd} 合法 flags: {}", valid_flags(cmd))];
        return (msg, help);
    }
    if compact.contains("unrecognized subcommand") || compact.contains("unrecognized command") {
        let cmd = extract_quoted(&compact).unwrap_or("unknown");
        return (
            format!("未知子命令 {cmd}"),
            vec!["pi-unity --help".to_string()],
        );
    }
    let first = compact
        .lines()
        .find(|l| !l.trim().is_empty())
        .unwrap_or("用法错误")
        .trim()
        .trim_start_matches("error: ")
        .to_string();
    (first, vec!["pi-unity --help".to_string()])
}

fn extract_unknown_flag(text: &str) -> Option<&str> {
    for line in text.lines() {
        let line = line.trim();
        if let Some(rest) = line.strip_prefix("unexpected argument") {
            return extract_quoted(rest);
        }
        if line.contains("unexpected argument") {
            return extract_quoted(line);
        }
    }
    None
}

fn extract_quoted(text: &str) -> Option<&str> {
    let start = text.find('\'')?;
    let rest = &text[start + 1..];
    let end = rest.find('\'')?;
    Some(&rest[..end])
}

fn infer_subcommand(text: &str) -> &'static str {
    const NAMES: &[&str] = &[
        "list-commands",
        "run-tests",
        "snapshot",
        "pipeline",
        "compile",
        "observe",
        "capture",
        "timeline",
        "status",
        "skills",
        "session",
        "setup",
        "eval",
        "ping",
        "mark",
    ];
    for name in NAMES {
        if text.contains(name) {
            return name;
        }
    }
    "pi-unity"
}

pub fn parse_cli() -> Result<Cli, clap::Error> {
    Cli::try_parse()
}
