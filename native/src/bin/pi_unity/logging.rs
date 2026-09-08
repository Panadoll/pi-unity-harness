use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};

pub const LOG_FORMAT_VERSION: u32 = 1;
pub const DEFAULT_VERSION: &str = super::version::VERSION;
pub const MAX_EVENT_FILE_SIZE: u64 = 50 * 1024 * 1024; // 50MB
pub const SESSION_TTL_MS: i64 = 12 * 3600 * 1000; // 12 hours
pub const TRACE_RETENTION_DAYS: u64 = 7;

// ── Time & Date Helpers (Howard Hinnant Algorithm) ───────────────────────────

pub fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

pub fn utc_parts(timestamp_ms: i64) -> (i64, u32, u32, u32, u32, u32, u32) {
    let clamped_ms = timestamp_ms.max(0);
    let total_seconds = clamped_ms / 1000;
    let days = total_seconds / 86_400;
    let seconds_of_day = total_seconds % 86_400;
    let (year, month, day) = civil_from_days(days);
    (
        year,
        month,
        day,
        (seconds_of_day / 3600) as u32,
        ((seconds_of_day % 3600) / 60) as u32,
        (seconds_of_day % 60) as u32,
        (clamped_ms % 1000) as u32,
    )
}

fn civil_from_days(days_since_epoch: i64) -> (i64, u32, u32) {
    let z = days_since_epoch + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let day_of_era = z - era * 146_097;
    let year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let mut year = year_of_era + era * 400;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_prime = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_prime + 2) / 5 + 1;
    let month = month_prime + if month_prime < 10 { 3 } else { -9 };
    year += if month <= 2 { 1 } else { 0 };
    (year, month as u32, day as u32)
}

pub fn timestamp_utc(timestamp_ms: i64) -> String {
    let (year, month, day, hour, minute, second, millis) = utc_parts(timestamp_ms);
    format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{millis:03}Z")
}

pub fn date_utc(timestamp_ms: i64) -> String {
    let (year, month, day, _, _, _, _) = utc_parts(timestamp_ms);
    format!("{year:04}-{month:02}-{day:02}")
}

pub fn month_utc(timestamp_ms: i64) -> String {
    let (year, month, _, _, _, _, _) = utc_parts(timestamp_ms);
    format!("{year:04}-{month:02}")
}

pub fn compact_ts_utc(timestamp_ms: i64) -> String {
    let (year, month, day, hour, minute, second, _) = utc_parts(timestamp_ms);
    format!("{year:04}{month:02}{day:02}-{hour:02}{minute:02}{second:02}")
}

// ── Directory and Storage Paths ─────────────────────────────────────────────

pub fn log_root_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("PI_UNITY_LOG_DIR") {
        let trimmed = dir.trim();
        if !trimmed.is_empty() {
            return PathBuf::from(trimmed);
        }
    }

    if let Ok(userprofile) = std::env::var("USERPROFILE") {
        let trimmed = userprofile.trim();
        if !trimmed.is_empty() {
            return PathBuf::from(trimmed).join(".pi-unity");
        }
    }

    if let Ok(home) = std::env::var("HOME") {
        let trimmed = home.trim();
        if !trimmed.is_empty() {
            return PathBuf::from(trimmed).join(".pi-unity");
        }
    }

    PathBuf::from(".pi-unity")
}

pub fn events_log_path(log_root: &Path, timestamp_ms: i64) -> PathBuf {
    let logs_dir = log_root.join("logs");
    let month = month_utc(timestamp_ms);
    let base_name = format!("events-{}.jsonl", month);
    let base_path = logs_dir.join(&base_name);

    if !base_path.exists() {
        return base_path;
    }

    // Check base file size
    if let Ok(meta) = fs::metadata(&base_path) {
        if meta.len() < MAX_EVENT_FILE_SIZE {
            return base_path;
        }
    } else {
        return base_path;
    }

    // Rollover with numeric suffix
    let mut index = 1;
    loop {
        let candidate_name = format!("events-{}.{}.jsonl", month, index);
        let candidate_path = logs_dir.join(&candidate_name);
        if !candidate_path.exists() {
            return candidate_path;
        }
        if let Ok(meta) = fs::metadata(&candidate_path) {
            if meta.len() < MAX_EVENT_FILE_SIZE {
                return candidate_path;
            }
        }
        index += 1;
    }
}

pub fn traces_dir(log_root: &Path, timestamp_ms: i64) -> PathBuf {
    let date_str = date_utc(timestamp_ms);
    log_root.join("logs").join("traces").join(date_str)
}

pub fn current_session_path(log_root: &Path) -> PathBuf {
    log_root.join("sessions").join("current.json")
}

// ── Privacy & Redaction ─────────────────────────────────────────────────────

pub fn normalize_project_path(path: &Path) -> String {
    let s = path.to_string_lossy().replace('\\', "/");
    let mut trimmed = s.trim().to_string();
    while trimmed.ends_with('/') && trimmed.len() > 3 {
        trimmed.pop();
    }
    trimmed.to_ascii_lowercase()
}

pub fn compute_project_hash(project_root: Option<&Path>) -> Option<String> {
    let root = project_root?;
    let normalized = normalize_project_path(root);
    let mut hasher = Sha256::new();
    hasher.update(normalized.as_bytes());
    let hash = hasher.finalize();
    // SHA256 first 6 bytes in hex = 12 hex chars
    let mut out = String::with_capacity(12);
    for b in &hash[..6] {
        out.push_str(&format!("{:02x}", b));
    }
    Some(out)
}

pub fn hash_sensitive_str(val: &str) -> String {
    let mut hasher = Sha256::new();
    hasher.update(val.as_bytes());
    let hash = hasher.finalize();
    let mut out = String::with_capacity(8);
    for b in &hash[..4] {
        out.push_str(&format!("{:02x}", b));
    }
    format!("len:{}_sha:{}", val.len(), out)
}

pub fn resolve_client_name() -> String {
    if let Ok(client) = std::env::var("PI_UNITY_CLIENT") {
        let trimmed = client.trim();
        if !trimmed.is_empty() {
            return trimmed.to_string();
        }
    }
    "cli".to_string()
}

// ── Session Management ──────────────────────────────────────────────────────

#[derive(Serialize, Deserialize, Debug, Clone)]
pub struct SessionRegistryData {
    #[serde(rename = "sessionId")]
    pub session_id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub agent: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub task: Option<String>,
    #[serde(rename = "startedAtMs")]
    pub started_at_ms: i64,
}

fn nonempty_env(name: &str) -> Option<String> {
    std::env::var(name).ok().and_then(|s| {
        let trimmed = s.trim().to_string();
        if trimmed.is_empty() {
            None
        } else {
            Some(trimmed)
        }
    })
}

fn nonempty_override(value: Option<&str>) -> Option<String> {
    value.and_then(|s| {
        let trimmed = s.trim();
        if trimmed.is_empty() {
            None
        } else {
            Some(trimmed.to_string())
        }
    })
}

/// Resolve session from env then the sticky registry. Process env is read here;
/// tests should call [`resolve_session_with_env`] so they do not mutate globals.
pub fn resolve_session(log_root: &Path) -> (Option<String>, Option<String>) {
    resolve_session_with_env(
        log_root,
        nonempty_env("PI_UNITY_SESSION_ID").as_deref(),
        nonempty_env("PI_UNITY_HOST_SESSION_ID").as_deref(),
    )
}

pub fn resolve_session_with_env(
    log_root: &Path,
    session_id_env: Option<&str>,
    host_session_id_env: Option<&str>,
) -> (Option<String>, Option<String>) {
    let host_session_id = nonempty_override(host_session_id_env);

    // 1. Env override has highest priority
    if let Some(env_sess) = nonempty_override(session_id_env) {
        return (Some(env_sess), host_session_id);
    }

    // 2. Sticky registry file
    let reg_path = current_session_path(log_root);
    if reg_path.exists() {
        if let Ok(content) = fs::read_to_string(&reg_path) {
            if let Ok(data) = serde_json::from_str::<SessionRegistryData>(&content) {
                let now = now_ms();
                if now.saturating_sub(data.started_at_ms) <= SESSION_TTL_MS {
                    return (Some(data.session_id), host_session_id);
                }
            }
        }
    }

    (None, host_session_id)
}

pub(crate) fn fast_rand_id() -> String {
    let t = now_ms();
    let pid = std::process::id();
    format!("{:x}{:x}", pid, (t ^ (pid as i64)) & 0xffffff)
}

pub fn start_session(
    log_root: &Path,
    task: Option<String>,
    agent_override: Option<String>,
) -> Result<SessionRegistryData, String> {
    let now = now_ms();
    let session_id = format!("sess-{}-{}", now, fast_rand_id());
    let agent = agent_override.or_else(|| {
        std::env::var("PI_UNITY_AGENT").ok().and_then(|s| {
            let t = s.trim().to_string();
            if t.is_empty() {
                None
            } else {
                Some(t)
            }
        })
    });

    let reg_data = SessionRegistryData {
        session_id: session_id.clone(),
        agent: agent.clone(),
        task: task.clone(),
        started_at_ms: now,
    };

    let reg_path = current_session_path(log_root);
    if let Some(parent) = reg_path.parent() {
        let _ = fs::create_dir_all(parent);
    }

    let json_bytes = serde_json::to_vec_pretty(&reg_data).map_err(|e| e.to_string())?;
    fs::write(&reg_path, json_bytes).map_err(|e| {
        format!("failed to write session registry {}: {e}", reg_path.display())
    })?;

    // Emit session.start event (best-effort; registry write already succeeded)
    let event = json!({
        "v": LOG_FORMAT_VERSION,
        "kind": "session.start",
        "tsUtc": timestamp_utc(now),
        "version": DEFAULT_VERSION,
        "client": resolve_client_name(),
        "pid": std::process::id(),
        "sessionId": session_id,
        "task": task,
        "agent": agent,
    });

    append_event_line(log_root, &event);

    Ok(reg_data)
}

pub fn end_session(log_root: &Path) -> Result<Option<String>, String> {
    let reg_path = current_session_path(log_root);
    let mut ended_id = None;

    if reg_path.exists() {
        if let Ok(content) = fs::read_to_string(&reg_path) {
            if let Ok(data) = serde_json::from_str::<SessionRegistryData>(&content) {
                ended_id = Some(data.session_id);
            }
        }
        let _ = fs::remove_file(&reg_path);
    }

    if let Some(ref sid) = ended_id {
        let now = now_ms();
        let event = json!({
            "v": LOG_FORMAT_VERSION,
            "kind": "session.end",
            "tsUtc": timestamp_utc(now),
            "version": DEFAULT_VERSION,
            "client": resolve_client_name(),
            "pid": std::process::id(),
            "sessionId": sid,
        });
        append_event_line(log_root, &event);
    }

    Ok(ended_id)
}

pub fn record_mark(log_root: &Path, skill: &str, event_type: &str) {
    let now = now_ms();
    let (session_id, host_session_id) = resolve_session(log_root);
    let kind = if event_type.trim().is_empty() || event_type == "used" {
        "skill.used".to_string()
    } else {
        format!("skill.{}", event_type)
    };

    let event = json!({
        "v": LOG_FORMAT_VERSION,
        "kind": kind,
        "tsUtc": timestamp_utc(now),
        "version": DEFAULT_VERSION,
        "client": resolve_client_name(),
        "pid": std::process::id(),
        "sessionId": session_id,
        "hostSessionId": host_session_id,
        "skill": skill,
        "event": event_type,
    });

    append_event_line(log_root, &event);
}

// ── Call Event & Trace Recorder ─────────────────────────────────────────────

#[derive(Serialize, Deserialize, Debug, Clone)]
pub struct CallEventPhases {
    #[serde(rename = "discoverMs")]
    pub discover_ms: u64,
    #[serde(rename = "connectMs")]
    pub connect_ms: u64,
    #[serde(rename = "requestMs")]
    pub request_ms: u64,
}

#[derive(Serialize, Deserialize, Debug, Clone)]
pub struct CallEventFlags {
    pub json: bool,
    pub truncated: bool,
    pub reconnects: u32,
}

#[derive(Serialize, Deserialize, Debug, Clone)]
pub struct CallEvent {
    pub v: u32,
    pub kind: String,
    #[serde(rename = "tsUtc")]
    pub ts_utc: String,
    pub version: String,
    pub client: String,
    pub pid: u32,
    #[serde(rename = "sessionId")]
    pub session_id: Option<String>,
    #[serde(rename = "hostSessionId")]
    pub host_session_id: Option<String>,
    pub subcommand: String,
    #[serde(rename = "projectHash")]
    pub project_hash: Option<String>,
    #[serde(rename = "exitCode")]
    pub exit_code: i32,
    #[serde(rename = "durationMs")]
    pub duration_ms: u64,
    pub phases: CallEventPhases,
    pub flags: CallEventFlags,
    #[serde(rename = "errorType")]
    pub error_type: Option<String>,
    #[serde(rename = "requestIds")]
    pub request_ids: Vec<String>,
    #[serde(rename = "traceId")]
    pub trace_id: Option<String>,
}

#[derive(Debug, Clone)]
pub struct TraceStep {
    pub ts_ms: i64,
    pub phase: String,
    pub message: String,
    pub details: Option<Value>,
}

pub struct TraceRecorder {
    pub start_ms: i64,
    pub pid: u32,
    pub subcommand: String,
    pub steps: Mutex<Vec<TraceStep>>,
    pub request_ids: Mutex<Vec<String>>,
    pub reconnects: AtomicU32,
    pub truncated: AtomicBool,
}

impl TraceRecorder {
    pub fn new(subcommand: &str) -> Self {
        Self {
            start_ms: now_ms(),
            pid: std::process::id(),
            subcommand: subcommand.to_string(),
            steps: Mutex::new(Vec::with_capacity(64)),
            request_ids: Mutex::new(Vec::new()),
            reconnects: AtomicU32::new(0),
            truncated: AtomicBool::new(false),
        }
    }

    pub fn record(&self, phase: &str, message: &str) {
        if let Ok(mut steps) = self.steps.lock() {
            if steps.len() < 200 {
                steps.push(TraceStep {
                    ts_ms: now_ms(),
                    phase: phase.to_string(),
                    message: message.to_string(),
                    details: None,
                });
            }
        }
    }

    pub fn record_details(&self, phase: &str, message: &str, details: Value) {
        if let Ok(mut steps) = self.steps.lock() {
            if steps.len() < 200 {
                steps.push(TraceStep {
                    ts_ms: now_ms(),
                    phase: phase.to_string(),
                    message: message.to_string(),
                    details: Some(details),
                });
            }
        }
    }

    pub fn record_sensitive(&self, phase: &str, message: &str, sensitive_data: &str) {
        let hash_summary = hash_sensitive_str(sensitive_data);
        self.record_details(
            phase,
            message,
            json!({
                "summary": hash_summary,
            }),
        );
    }

    pub fn add_request_id(&self, request_id: String) {
        if let Ok(mut reqs) = self.request_ids.lock() {
            reqs.push(request_id);
        }
    }

    pub fn add_reconnect(&self) {
        self.reconnects.fetch_add(1, Ordering::SeqCst);
    }

    pub fn mark_truncated(&self) {
        self.truncated.store(true, Ordering::SeqCst);
    }

    pub fn flush_trace_if_needed(
        &self,
        log_root: &Path,
        exit_code: i32,
        force_trace: bool,
    ) -> Option<String> {
        let env_force = std::env::var("PI_UNITY_TRACE")
            .ok()
            .map(|v| {
                let lower = v.trim().to_ascii_lowercase();
                lower == "1" || lower == "true" || lower == "on"
            })
            .unwrap_or(false);

        let should_save = exit_code != 0 || force_trace || env_force;
        if !should_save {
            return None;
        }

        let trace_id = format!(
            "{}-{}-{}",
            compact_ts_utc(self.start_ms),
            self.pid,
            self.subcommand
        );

        let traces_dir = traces_dir(log_root, self.start_ms);
        let _ = fs::create_dir_all(&traces_dir);
        let trace_file = traces_dir.join(format!("{}.log", trace_id));

        let steps = self.steps.lock().unwrap_or_else(|e| e.into_inner()).clone();
        let duration_ms = (now_ms() - self.start_ms).max(0);

        let mut out = String::new();
        out.push_str(&format!("=== TRACE: {} ===\n", trace_id));
        out.push_str(&format!("Subcommand: {}\n", self.subcommand));
        out.push_str(&format!("PID: {}\n", self.pid));
        out.push_str(&format!("StartedAtUtc: {}\n", timestamp_utc(self.start_ms)));
        out.push_str(&format!("DurationMs: {}\n", duration_ms));
        out.push_str(&format!("ExitCode: {}\n", exit_code));
        out.push_str(&format!(
            "RequestIds: {:?}\n",
            self.request_ids
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .clone()
        ));
        out.push_str("--- STEPS ---\n");

        for step in steps {
            let offset_ms = step.ts_ms - self.start_ms;
            if let Some(details) = step.details {
                out.push_str(&format!(
                    "+{:>6}ms [{}] {} | {}\n",
                    offset_ms, step.phase, step.message, details
                ));
            } else {
                out.push_str(&format!(
                    "+{:>6}ms [{}] {}\n",
                    offset_ms, step.phase, step.message
                ));
            }
        }

        let _ = fs::write(&trace_file, out.as_bytes());

        Some(trace_id)
    }
}

// ── Atomic Event Line Appending ─────────────────────────────────────────────

pub fn append_event_line(log_root: &Path, event: &Value) -> bool {
    let now = now_ms();
    let path = events_log_path(log_root, now);

    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }

    let line_str = match serde_json::to_string(event) {
        Ok(s) => s,
        Err(_) => return false,
    };

    let mut line_bytes = line_str.into_bytes();
    line_bytes.push(b'\n');

    let open_res = OpenOptions::new().create(true).append(true).open(&path);

    if let Ok(mut file) = open_res {
        file.write_all(&line_bytes).is_ok()
    } else {
        false
    }
}

// ── Lazy Cleanup ────────────────────────────────────────────────────────────

pub fn lazy_cleanup_old_traces(log_root: &Path) {
    let traces_base = log_root.join("logs").join("traces");
    if !traces_base.exists() {
        return;
    }

    let entries = match fs::read_dir(&traces_base) {
        Ok(e) => e,
        Err(_) => return,
    };

    let now = now_ms();
    let cutoff_days = (now / 86_400_000).saturating_sub(TRACE_RETENTION_DAYS as i64);

    for entry in entries.flatten() {
        if let Ok(file_type) = entry.file_type() {
            if file_type.is_dir() {
                let name = entry.file_name().to_string_lossy().into_owned();
                // Expect YYYY-MM-DD format
                if name.len() == 10 && name.as_bytes()[4] == b'-' && name.as_bytes()[7] == b'-' {
                    if let (Ok(y), Ok(m), Ok(d)) = (
                        name[0..4].parse::<i64>(),
                        name[5..7].parse::<u32>(),
                        name[8..10].parse::<u32>(),
                    ) {
                        let dir_days = days_from_civil(y, m, d);
                        if dir_days < cutoff_days {
                            let _ = fs::remove_dir_all(entry.path());
                        }
                    }
                }
            }
        }
    }
}

fn days_from_civil(y: i64, m: u32, d: u32) -> i64 {
    let mut y = y;
    let m = m as i64;
    let d = d as i64;
    y -= if m <= 2 { 1 } else { 0 };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = y - era * 400;
    let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

// ── Unit Tests ──────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicU64;

    static TEST_COUNTER: AtomicU64 = AtomicU64::new(1);

    fn test_dir(name: &str) -> PathBuf {
        let idx = TEST_COUNTER.fetch_add(1, Ordering::SeqCst);
        let path = std::env::temp_dir()
            .join("pi_unity_harness_log_tests")
            .join(format!("{}_{}_{}", name, now_ms(), idx));
        let _ = fs::create_dir_all(&path);
        path
    }

    #[test]
    fn test_utc_formatting_and_dates() {
        assert_eq!(timestamp_utc(0), "1970-01-01T00:00:00.000Z");
        assert_eq!(date_utc(0), "1970-01-01");
        assert_eq!(month_utc(0), "1970-01");
        assert_eq!(compact_ts_utc(0), "19700101-000000");

        // 2026-09-02T12:00:00.000Z -> ms: 1788350400000
        let ms = 1788350400000;
        let ts = timestamp_utc(ms);
        assert!(ts.starts_with("2026-09-02"));
        assert_eq!(month_utc(ms), "2026-09");
        assert_eq!(date_utc(ms), "2026-09-02");
    }

    #[test]
    fn test_project_hash_sha256_redaction() {
        let path1 = Path::new("F:/UnityProjects/GP1");
        let hash1 = compute_project_hash(Some(path1)).expect("hash should exist");
        assert_eq!(hash1.len(), 12); // 6 bytes hex = 12 hex chars

        // Backslash vs forward slash vs trailing slash normalisation
        let path2 = Path::new(r"F:\UnityProjects\GP1\");
        let hash2 = compute_project_hash(Some(path2)).expect("hash should exist");
        assert_eq!(hash1, hash2);

        // Case insensitivity
        let path3 = Path::new("f:/unityprojects/gp1");
        let hash3 = compute_project_hash(Some(path3)).expect("hash should exist");
        assert_eq!(hash1, hash3);

        // None returns None
        assert!(compute_project_hash(None).is_none());
    }

    #[test]
    fn test_session_lifecycle_and_priority() {
        let root = test_dir("sess_priority");

        // Initially no session
        let (sess1, _) = resolve_session(&root);
        assert!(sess1.is_none());

        // Start session
        let reg = start_session(&root, Some("test_task".to_string()), None).unwrap();
        assert!(reg.session_id.starts_with("sess-"));

        // Resolved from sticky registry
        let (sess2, _) = resolve_session(&root);
        assert_eq!(sess2.as_deref(), Some(reg.session_id.as_str()));

        // Env override takes precedence without mutating process env
        let (sess3, _) = resolve_session_with_env(&root, Some("env-session-123"), None);
        assert_eq!(sess3.as_deref(), Some("env-session-123"));

        // End session
        let ended = end_session(&root).unwrap();
        assert_eq!(ended.as_deref(), Some(reg.session_id.as_str()));

        // Resolved is now None
        let (sess4, _) = resolve_session(&root);
        assert!(sess4.is_none());
    }

    #[test]
    fn test_session_ttl_expiry() {
        let root = test_dir("sess_ttl");
        let old_ms = now_ms() - SESSION_TTL_MS - 1000;
        let reg_data = SessionRegistryData {
            session_id: "expired-sess".to_string(),
            agent: None,
            task: None,
            started_at_ms: old_ms,
        };
        let reg_path = current_session_path(&root);
        fs::create_dir_all(reg_path.parent().unwrap()).unwrap();
        fs::write(&reg_path, serde_json::to_string(&reg_data).unwrap()).unwrap();

        let (sess, _) = resolve_session(&root);
        assert!(sess.is_none(), "Expired session must resolve to None");
    }

    #[test]
    fn test_start_session_fails_when_registry_cannot_be_written() {
        let root = test_dir("sess_write_fail");
        let sessions_path = root.join("sessions");
        fs::write(&sessions_path, b"not-a-directory").unwrap();
        let err = start_session(&root, None, None).unwrap_err();
        assert!(
            err.contains("failed to write session registry"),
            "unexpected error: {err}"
        );
    }

    #[test]
    fn test_events_log_path_and_50mb_rollover() {
        let root = test_dir("events_rollover");
        let now = now_ms();
        let p1 = events_log_path(&root, now);
        let month = month_utc(now);
        assert_eq!(
            p1.file_name().unwrap().to_str().unwrap(),
            format!("events-{}.jsonl", month)
        );

        // Simulate 50MB file
        fs::create_dir_all(p1.parent().unwrap()).unwrap();
        let dummy = fs::File::create(&p1).unwrap();
        dummy.set_len(MAX_EVENT_FILE_SIZE + 10).unwrap();

        let p2 = events_log_path(&root, now);
        assert_eq!(
            p2.file_name().unwrap().to_str().unwrap(),
            format!("events-{}.1.jsonl", month)
        );
    }

    #[test]
    fn test_trace_recorder_and_redaction() {
        let root = test_dir("trace_recorder");
        let recorder = TraceRecorder::new("eval");
        recorder.record("init", "starting command");
        recorder.record_sensitive("eval_code", "executing repl snippet", "private_var = 123;");
        recorder.add_request_id("cli-req-1".to_string());

        // On success (exit 0) without force, no trace written
        let trace_id_none = recorder.flush_trace_if_needed(&root, 0, false);
        assert!(trace_id_none.is_none());

        // On error (exit 1), trace is written
        let trace_id = recorder.flush_trace_if_needed(&root, 1, false);
        assert!(trace_id.is_some());
        let tid = trace_id.unwrap();
        assert!(tid.contains("-eval"));

        let trace_file = traces_dir(&root, recorder.start_ms).join(format!("{}.log", tid));
        assert!(trace_file.exists());
        let trace_content = fs::read_to_string(&trace_file).unwrap();
        assert!(!trace_content.contains("private_var"));
        assert!(trace_content.contains("len:18_sha:"));
    }

    #[test]
    fn test_call_event_json_serialization() {
        let root = test_dir("call_event");
        let call_event = json!({
            "v": 1,
            "kind": "call",
            "tsUtc": "2026-09-02T03:00:00.000Z",
            "version": "0.1.0",
            "client": "cli",
            "pid": 1234,
            "sessionId": null,
            "hostSessionId": null,
            "subcommand": "compile",
            "projectHash": "3f9a12345678",
            "exitCode": 0,
            "durationMs": 81234,
            "phases": {
                "discoverMs": 80,
                "connectMs": 12,
                "requestMs": 81000
            },
            "flags": {
                "json": false,
                "truncated": false,
                "reconnects": 0
            },
            "errorType": null,
            "requestIds": ["cli-123-1-1788325000"],
            "traceId": null
        });

        assert!(append_event_line(&root, &call_event));

        let now = now_ms();
        let log_file = events_log_path(&root, now);
        assert!(log_file.exists());
        let content = fs::read_to_string(&log_file).unwrap();
        let parsed: Value = serde_json::from_str(content.trim()).unwrap();
        assert_eq!(parsed["kind"], "call");
        assert_eq!(parsed["subcommand"], "compile");
        assert_eq!(parsed["exitCode"], 0);
    }
}
