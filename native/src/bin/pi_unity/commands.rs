use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use serde_json::{json, Value};

use super::args::*;
use super::client::{CliError, HarnessClient};
use super::home;
use super::logging::TraceRecorder;
use super::output::format_safe_output_with_opts;
use super::schema::{self, ViewOptions};
use super::usage;

pub(crate) fn parse_param_pairs(
    pairs: &[String],
    explicit_json: Option<&str>,
) -> Result<Value, CliError> {
    let mut map = serde_json::Map::new();

    if let Some(json_str) = explicit_json {
        let val: Value = serde_json::from_str(json_str).map_err(|e| {
            usage::usage_error(
                format!("--params-json 无效: {e}"),
                &["pi-unity pipeline <name> --params-json '{\"key\":\"value\"}'"],
            )
        })?;
        if let Value::Object(obj) = val {
            for (k, v) in obj {
                map.insert(k, v);
            }
        } else {
            return Err(usage::usage_error(
                "--params-json 必须是 JSON 对象",
                &["pi-unity pipeline <name> --params-json '{\"key\":\"value\"}'"],
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

const LEGACY_CONSUMER_SKILLS: &[&str] = &[
    "pi-unity-eval",
    "pi-unity-compile",
    "pi-unity-status",
    "pi-unity-snapshot",
    "pi-unity-pipeline",
    "pi-unity-run-tests",
    "pi-unity-observe",
    "pi-unity-capture",
    "pi-unity-timeline",
];

pub(crate) fn expected_skill_markdown() -> String {
    home::static_skill_markdown()
}

pub(crate) fn skill_is_current(committed: &str) -> bool {
    normalize_skill_text(committed) == normalize_skill_text(&expected_skill_markdown())
}

fn normalize_skill_text(s: &str) -> String {
    s.replace("\r\n", "\n").trim().to_string()
}

fn check_skill_drift(project_root: &Path) -> Result<(), CliError> {
    let repo_root = find_skills_source_root(project_root);
    let skill_path = repo_root.join("skills/pi-unity/SKILL.md");
    let committed = fs::read_to_string(&skill_path)
        .map_err(|e| CliError::Other(format!("无法读取 {}: {}", skill_path.display(), e)))?;
    if skill_is_current(&committed) {
        Ok(())
    } else {
        Err(CliError::Other(
            "skills/pi-unity/SKILL.md 与 CLI home 文案漂移，请同步后再提交".to_string(),
        ))
    }
}

pub(crate) fn handle_skills_command(
    args: SkillsArgs,
    project_root: &Path,
    json_mode: bool,
) -> Result<String, CliError> {
    if args.action == "check" {
        check_skill_drift(project_root)?;
        let result = json!({"check": "ok"});
        return Ok(super::output::emit_value(&result, json_mode));
    }
    if args.action != "install" {
        return Err(usage::usage_error(
            format!("未知 skills 动作 {}", args.action),
            &["pi-unity skills install", "pi-unity skills check"],
        ));
    }

    let repo_root = find_skills_source_root(project_root);
    let skills_source = repo_root.join("skills");
    if !skills_source.exists() {
        return Err(CliError::Other(format!(
            "找不到 skills 源目录 {}",
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

    for target_dir in &target_dirs {
        let _ = fs::create_dir_all(target_dir);
        remove_safe_legacy_skills(target_dir);
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
                    installed_count += sync_skill_dir(&path, &dest_skill_dir);
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

    Ok(super::output::emit_value(&result, json_mode))
}

fn should_skip_skill_entry(name: &str) -> bool {
    name.starts_with('.') || name.eq_ignore_ascii_case("Thumbs.db")
}

const SKILL_MANIFEST_FILENAME: &str = ".pi-unity-manifest.json";

#[derive(serde::Serialize, serde::Deserialize, Default, Debug, PartialEq, Eq)]
struct SkillManifest {
    version: u32,
    files: Vec<String>,
}

fn normalize_rel_path(path: &Path) -> String {
    path.to_string_lossy().replace('\\', "/")
}

fn collect_managed_files_recursive(base: &Path, current: &Path, files: &mut Vec<String>) {
    let Ok(entries) = fs::read_dir(current) else {
        return;
    };
    for entry in entries.flatten() {
        let name = entry.file_name();
        let name_str = name.to_string_lossy();
        if should_skip_skill_entry(&name_str) {
            continue;
        }
        let path = entry.path();
        if path.is_dir() {
            collect_managed_files_recursive(base, &path, files);
        } else if path.is_file() {
            if let Ok(rel) = path.strip_prefix(base) {
                files.push(normalize_rel_path(rel));
            }
        }
    }
}

fn read_skill_manifest(dest: &Path) -> Option<SkillManifest> {
    let manifest_path = dest.join(SKILL_MANIFEST_FILENAME);
    let content = fs::read_to_string(manifest_path).ok()?;
    serde_json::from_str(&content).ok()
}

fn write_skill_manifest(dest: &Path, files: &[String]) {
    let manifest_path = dest.join(SKILL_MANIFEST_FILENAME);
    let manifest = SkillManifest {
        version: 1,
        files: files.to_vec(),
    };
    if let Ok(json_str) = serde_json::to_string_pretty(&manifest) {
        let _ = fs::write(manifest_path, json_str);
    }
}

fn remove_empty_dirs_recursive(dir: &Path) {
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            remove_empty_dirs_recursive(&path);
            let _ = fs::remove_dir(&path);
        }
    }
}

fn sync_skill_dir(src: &Path, dest: &Path) -> usize {
    if fs::create_dir_all(dest).is_err() {
        return 0;
    }

    let mut src_files = Vec::new();
    collect_managed_files_recursive(src, src, &mut src_files);
    src_files.sort();
    let src_file_set: std::collections::HashSet<&str> =
        src_files.iter().map(|s| s.as_str()).collect();

    let mut current_dest_files = Vec::new();
    collect_managed_files_recursive(dest, dest, &mut current_dest_files);
    current_dest_files.sort();
    let current_dest_set: std::collections::HashSet<&str> =
        current_dest_files.iter().map(|s| s.as_str()).collect();

    let old_manifest = read_skill_manifest(dest);

    if let Some(old) = old_manifest {
        let old_manifest_set: std::collections::HashSet<&str> =
            old.files.iter().map(|s| s.as_str()).collect();
        for file in current_dest_set {
            if old_manifest_set.contains(file) && !src_file_set.contains(file) {
                let to_remove = dest.join(file);
                let _ = fs::remove_file(to_remove);
            }
        }
    }

    let mut copied_count = 0;
    for rel in &src_files {
        let from = src.join(rel);
        let to = dest.join(rel);
        if let Some(parent) = to.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if fs::copy(&from, &to).is_ok() {
            copied_count += 1;
        }
    }

    remove_empty_dirs_recursive(dest);
    write_skill_manifest(dest, &src_files);

    copied_count
}

fn is_tool_managed_legacy_skill(dir: &Path, expected_name: &str) -> bool {
    let skill_file = dir.join("SKILL.md");
    if !skill_file.is_file() {
        return false;
    }
    let Ok(entries) = fs::read_dir(dir) else {
        return false;
    };
    let mut saw_skill = false;
    for entry in entries.flatten() {
        let name = entry.file_name();
        let name_str = name.to_string_lossy();
        if should_skip_skill_entry(&name_str) {
            continue;
        }
        if name_str == "SKILL.md" && entry.path().is_file() {
            saw_skill = true;
            continue;
        }
        return false;
    }
    if !saw_skill {
        return false;
    }
    let Ok(content) = fs::read_to_string(skill_file) else {
        return false;
    };
    content.contains(&format!("name: {expected_name}"))
}

fn remove_safe_legacy_skills(target_dir: &Path) {
    for name in LEGACY_CONSUMER_SKILLS {
        let dir = target_dir.join(name);
        if dir.is_dir() && is_tool_managed_legacy_skill(&dir, name) {
            let _ = fs::remove_dir_all(&dir);
        }
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

struct FormatCtx<'a> {
    project_root: &'a Path,
    json_mode: bool,
    opts: &'a ViewOptions,
    recorder: &'a TraceRecorder,
}

fn emit_shaped(ctx: &FormatCtx<'_>, shaped: Value, full_hint: &str) -> String {
    format_safe_output_with_opts(
        &shaped,
        ctx.project_root,
        ctx.json_mode,
        ctx.opts,
        Some(full_hint),
        Some(ctx.recorder),
    )
}

async fn send_and_format(
    client: &mut HarnessClient,
    req_type: &str,
    payload: Value,
    timeout_ms: u64,
    ctx: &FormatCtx<'_>,
    full_hint: &str,
    shape: impl FnOnce(&Value) -> Value,
) -> Result<String, CliError> {
    let res = client.send_request(req_type, payload, timeout_ms).await?;
    Ok(emit_shaped(ctx, shape(&res), full_hint))
}

async fn send_pipeline_command(
    client: &mut HarnessClient,
    name: &str,
    params: Value,
    timeout_ms: u64,
    ctx: &FormatCtx<'_>,
) -> Result<String, CliError> {
    let params_json_str = serde_json::to_string(&params).unwrap_or_else(|_| "{}".to_string());
    let hint = format!("pi-unity pipeline {name} --full");
    send_and_format(
        client,
        "command",
        json!({
            "name": name,
            "parametersJson": params_json_str,
        }),
        timeout_ms,
        ctx,
        &hint,
        |raw| match name {
            "run_tests" => schema::shape_run_tests(raw),
            "vision_observe" => schema::shape_observe(raw),
            _ => schema::shape_generic(raw, ctx.opts),
        },
    )
    .await
}

pub(crate) async fn execute_harness_command(
    command: Commands,
    client: &mut HarnessClient,
    project_root: &Path,
    json_mode: bool,
    opts: &ViewOptions,
    recorder: &TraceRecorder,
) -> Result<String, CliError> {
    let ctx = FormatCtx {
        project_root,
        json_mode,
        opts,
        recorder,
    };
    match command {
        Commands::Ping(args) => {
            send_and_format(
                client,
                "ping",
                json!({}),
                args.timeout,
                &ctx,
                "pi-unity ping --json",
                |raw| raw.clone(),
            )
            .await
        }

        Commands::Status(args) => {
            send_and_format(
                client,
                "status",
                json!({}),
                args.timeout,
                &ctx,
                "pi-unity status --full",
                |raw| schema::shape_status_view(raw, opts),
            )
            .await
        }

        Commands::Eval(args) => {
            let (req_type, payload) = if let Some(code_str) = args.code {
                recorder.record_sensitive("eval", "Evaluating inline C# code", &code_str);
                ("validate_execute_code", json!({ "code": code_str }))
            } else if let Some(file_str) = args.file {
                let path_buf = PathBuf::from(&file_str);
                let full_path = if path_buf.is_absolute() {
                    path_buf
                } else {
                    project_root.join(path_buf)
                };

                if !full_path.exists() {
                    return Err(CliError::ExecutionFailed(format!(
                        "找不到 eval 文件: {}",
                        full_path.display()
                    )));
                }

                let forward_rel = match full_path.strip_prefix(project_root) {
                    Ok(rel) => rel.to_string_lossy().replace('\\', "/"),
                    Err(_) => full_path.to_string_lossy().replace('\\', "/"),
                };

                recorder.record("eval", &format!("Evaluating script file: {}", forward_rel));

                ("validate_execute_file", json!({ "filePath": forward_rel }))
            } else {
                return Err(usage::usage_error(
                    "eval 需要 CODE 或 --file",
                    &[
                        "pi-unity eval \"<code>\"",
                        "pi-unity eval -f Temp/PiUnityHarness/AgentScratch/probe.repl",
                    ],
                ));
            };

            send_and_format(
                client,
                req_type,
                payload,
                args.timeout,
                &ctx,
                "pi-unity eval --full",
                |raw| schema::shape_generic(raw, opts),
            )
            .await
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
                        let is_ready_now =
                            status_val.get("managedState").and_then(Value::as_str) == Some("ready");
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
                    Err(_) => {}
                }
                tokio::time::sleep(Duration::from_millis(500)).await;
            }

            if !is_ready {
                return Err(CliError::Timeout(
                    "编译和域重载等待 managedState=ready 超时".to_string(),
                ));
            }

            let result = json!({
                "compiled": true,
                "managedState": "ready",
                "generation": last_status.get("managedGeneration").cloned().unwrap_or(Value::Null),
                "editorStatus": last_status.get("editorStatus").cloned().unwrap_or(Value::Null),
            });

            Ok(emit_shaped(&ctx, result, "pi-unity compile --full"))
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
                "includeComponents": opts.full || opts.wants("components"),
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

            Ok(emit_shaped(
                &ctx,
                schema::shape_snapshot(&final_content, opts),
                "pi-unity snapshot --full",
            ))
        }

        Commands::ListCommands(args) => {
            send_and_format(
                client,
                "list_commands",
                json!({}),
                args.timeout,
                &ctx,
                "pi-unity list-commands --full",
                |raw| schema::shape_list_commands(raw, opts),
            )
            .await
        }

        Commands::Pipeline(args) => {
            let param_obj = parse_param_pairs(&args.params, args.params_json.as_deref())?;
            recorder.record(
                "pipeline",
                &format!("Executing pipeline command {}", args.name),
            );
            send_pipeline_command(client, &args.name, param_obj, args.timeout, &ctx).await
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

            send_pipeline_command(
                client,
                "run_tests",
                Value::Object(param_map),
                args.timeout,
                &ctx,
            )
            .await
        }

        Commands::Observe(args) => {
            let overlay_str = match args.overlay {
                OverlayMode::Grid => "grid",
                OverlayMode::Annotations => "annotations",
                OverlayMode::Both => "both",
                OverlayMode::None => "none",
            };

            send_pipeline_command(
                client,
                "vision_observe",
                json!({
                    "mode": "game",
                    "frames": args.frames,
                    "intervalMs": args.interval,
                    "overlay": overlay_str,
                }),
                args.timeout,
                &ctx,
            )
            .await
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

            send_pipeline_command(
                client,
                "vision_capture",
                Value::Object(param_map),
                args.timeout,
                &ctx,
            )
            .await
        }

        Commands::Timeline(args) => {
            let success_filter = match args.success {
                TimelineSuccessFilter::All => "all",
                TimelineSuccessFilter::Success => "success",
                TimelineSuccessFilter::Failure => "failure",
            };

            send_and_format(
                client,
                "timeline",
                json!({
                    "limit": args.limit,
                    "success": success_filter,
                }),
                args.timeout,
                &ctx,
                "pi-unity timeline --full",
                |raw| schema::shape_timeline(raw, opts),
            )
            .await
        }

        Commands::Skills(_)
        | Commands::Session(_)
        | Commands::Mark(_)
        | Commands::Setup(_)
        | Commands::Mux => {
            unreachable!()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn write_file(path: &Path, contents: &str) {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).unwrap();
        }
        fs::write(path, contents).unwrap();
    }

    #[test]
    fn sync_skill_dir_installs_and_creates_manifest() {
        let root = std::env::temp_dir().join(format!("pi-unity-skill-sync-{}", std::process::id()));
        let src = root.join("src");
        let dest = root.join("dest");
        write_file(&src.join("SKILL.md"), "---\nname: foo\n---\n");
        write_file(&src.join("references/bar.md"), "# bar\n");
        write_file(&src.join(".hidden"), "nope");
        let copied = sync_skill_dir(&src, &dest);
        assert_eq!(copied, 2);
        assert!(dest.join("SKILL.md").is_file());
        assert!(dest.join("references/bar.md").is_file());
        assert!(!dest.join(".hidden").exists());

        let manifest = read_skill_manifest(&dest).expect("Manifest must be written");
        assert_eq!(manifest.files, vec!["SKILL.md", "references/bar.md"]);

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn sync_skill_dir_removes_stale_files_on_reinstall() {
        let root =
            std::env::temp_dir().join(format!("pi-unity-skill-stale-{}", std::process::id()));
        let src = root.join("src");
        let dest = root.join("dest");

        // First install with bar.md and stale.md
        write_file(&src.join("SKILL.md"), "---\nname: foo\n---\n");
        write_file(&src.join("references/bar.md"), "# bar\n");
        write_file(&src.join("references/stale.md"), "# stale\n");
        let copied = sync_skill_dir(&src, &dest);
        assert_eq!(copied, 3);
        assert!(dest.join("references/stale.md").is_file());

        // Remove stale.md from source and add new.md
        let _ = fs::remove_file(src.join("references/stale.md"));
        write_file(&src.join("references/new.md"), "# new\n");

        let copied2 = sync_skill_dir(&src, &dest);
        assert_eq!(copied2, 3);
        assert!(dest.join("SKILL.md").is_file());
        assert!(dest.join("references/bar.md").is_file());
        assert!(dest.join("references/new.md").is_file());
        assert!(
            !dest.join("references/stale.md").exists(),
            "Stale file must be removed"
        );

        let manifest = read_skill_manifest(&dest).expect("Manifest must be updated");
        assert_eq!(
            manifest.files,
            vec!["SKILL.md", "references/bar.md", "references/new.md"]
        );

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn sync_skill_dir_preserves_user_files_and_custom_dirs() {
        let root = std::env::temp_dir().join(format!("pi-unity-skill-user-{}", std::process::id()));
        let src = root.join("src");
        let dest = root.join("dest");

        write_file(&src.join("SKILL.md"), "---\nname: foo\n---\n");
        write_file(&src.join("references/bar.md"), "# bar\n");
        write_file(&src.join("references/stale.md"), "# stale\n");
        sync_skill_dir(&src, &dest);

        // User adds custom files into dest
        write_file(&dest.join("user_notes.md"), "# my notes\n");
        write_file(&dest.join("custom/guide.md"), "# my guide\n");

        // Source updates (stale.md removed, bar.md updated)
        let _ = fs::remove_file(src.join("references/stale.md"));
        write_file(&src.join("references/bar.md"), "# bar updated\n");

        sync_skill_dir(&src, &dest);

        // Stale tool file removed
        assert!(!dest.join("references/stale.md").exists());
        // Updated tool file updated
        assert_eq!(
            fs::read_to_string(dest.join("references/bar.md")).unwrap(),
            "# bar updated\n"
        );
        // User files preserved
        assert_eq!(
            fs::read_to_string(dest.join("user_notes.md")).unwrap(),
            "# my notes\n"
        );
        assert_eq!(
            fs::read_to_string(dest.join("custom/guide.md")).unwrap(),
            "# my guide\n"
        );

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn legacy_skill_with_only_matching_skill_md_is_removed() {
        let root =
            std::env::temp_dir().join(format!("pi-unity-skill-legacy-{}", std::process::id()));
        let target = root.join("skills");
        write_file(
            &target.join("pi-unity-eval/SKILL.md"),
            "---\nname: pi-unity-eval\n---\n# eval\n",
        );
        write_file(
            &target.join("pi-unity-eval-keep/SKILL.md"),
            "---\nname: pi-unity-eval\n---\n# keep\n",
        );
        write_file(&target.join("pi-unity-eval-keep/notes.md"), "user file\n");
        write_file(
            &target.join("other-skill/SKILL.md"),
            "---\nname: other-skill\n---\n",
        );
        remove_safe_legacy_skills(&target);
        assert!(!target.join("pi-unity-eval").exists());
        assert!(target.join("pi-unity-eval-keep/notes.md").is_file());
        assert!(target.join("other-skill/SKILL.md").is_file());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn committed_skill_matches_home_copy() {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../skills/pi-unity/SKILL.md");
        let committed = fs::read_to_string(&path).expect("SKILL.md");
        assert!(
            skill_is_current(&committed),
            "skills/pi-unity/SKILL.md 与 CLI home 文案漂移"
        );
    }
}
