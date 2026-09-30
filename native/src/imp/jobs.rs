use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;

use serde_json::{json, Map, Value};

use super::{ManagedRequest, AUDIT_VALUE_MAX_CHARS};

pub(super) const JOB_QUEUE_LIMIT: usize = 100;
pub(super) const JOB_TERMINAL_LIMIT: usize = 100;
pub(super) const JOB_RETENTION_MS: i64 = 60 * 60 * 1000;
pub(super) const JOB_RESULT_LIMIT: usize = 8 * 1024 * 1024;
pub(super) const JOB_PROGRESS_LIMIT: usize = 64 * 1024;
pub(super) const DEFAULT_EXECUTION_TIMEOUT_MS: i64 = 300_000;
pub(super) const MIN_EXECUTION_TIMEOUT_MS: i64 = 1;
pub(super) const MAX_EXECUTION_TIMEOUT_MS: i64 = 86_400_000;

const STATE_QUEUED: &str = "queued";
const STATE_RUNNING: &str = "running";
const STATE_COMPLETED: &str = "completed";
const STATE_FAILED: &str = "failed";
const STATE_CANCELED: &str = "canceled";
const STATE_INTERRUPTED: &str = "interrupted";

#[derive(Clone, Copy, PartialEq, Eq)]
enum Phase {
    Queued,
    Buffered,
    Running,
    Completed,
    Failed,
    Canceled,
    Interrupted,
}

impl Phase {
    fn as_str(self) -> &'static str {
        match self {
            Phase::Queued | Phase::Buffered => STATE_QUEUED,
            Phase::Running => STATE_RUNNING,
            Phase::Completed => STATE_COMPLETED,
            Phase::Failed => STATE_FAILED,
            Phase::Canceled => STATE_CANCELED,
            Phase::Interrupted => STATE_INTERRUPTED,
        }
    }

    fn is_terminal(self) -> bool {
        matches!(
            self,
            Phase::Completed | Phase::Failed | Phase::Canceled | Phase::Interrupted
        )
    }
}

struct JobRecord {
    id: String,
    command: String,
    phase: Phase,
    cancellation_requested: bool,
    timeout_requested: bool,
    enqueued_at_ms: i64,
    started_at_ms: i64,
    completed_at_ms: i64,
    execution_timeout_ms: i64,
    result: Option<Value>,
    error: Option<String>,
    error_type: Option<String>,
    progress: Option<Value>,
    audit_action_id: u64,
    request_type: String,
    action: String,
    managed_line: Vec<u8>,
}

pub(super) struct JobStore {
    next_id: AtomicU64,
    records: Mutex<HashMap<String, JobRecord>>,
    admission: Mutex<VecDeque<String>>,
    terminal: Mutex<VecDeque<String>>,
}

#[derive(Debug)]
pub(super) enum SubmitError {
    InvalidTimeout,
    MissingCommand,
    QueueFull,
}

pub(super) struct PreparedJob {
    pub audit: ManagedRequest,
    pub snapshot: Value,
}

impl JobStore {
    pub(super) fn new() -> Self {
        Self {
            next_id: AtomicU64::new(1),
            records: Mutex::new(HashMap::new()),
            admission: Mutex::new(VecDeque::new()),
            terminal: Mutex::new(VecDeque::new()),
        }
    }

    pub(super) fn submit(
        &self,
        client_id: &str,
        value: &Value,
        now: i64,
        audit_action_id: u64,
    ) -> Result<PreparedJob, SubmitError> {
        let payload = value.get("payload").unwrap_or(&Value::Null);
        let command = payload
            .get("name")
            .and_then(Value::as_str)
            .unwrap_or("")
            .trim();
        if command.is_empty() {
            return Err(SubmitError::MissingCommand);
        }
        let execution_timeout_ms = match execution_timeout_ms(payload) {
            Some(timeout) => timeout,
            None => return Err(SubmitError::InvalidTimeout),
        };
        let parameters_json = payload
            .get("parametersJson")
            .and_then(Value::as_str)
            .unwrap_or("{}");
        self.reap(now);

        let mut records = match self.records.lock() {
            Ok(records) => records,
            Err(_) => return Err(SubmitError::QueueFull),
        };
        let mut admission = match self.admission.lock() {
            Ok(admission) => admission,
            Err(_) => return Err(SubmitError::QueueFull),
        };
        if active_count(&records) >= JOB_QUEUE_LIMIT {
            return Err(SubmitError::QueueFull);
        }

        let id = self.allocate_id(now);
        let managed_line = managed_job_line(&id, command, parameters_json, execution_timeout_ms);
        let record = JobRecord {
            id: id.clone(),
            command: command.to_string(),
            phase: Phase::Queued,
            cancellation_requested: false,
            timeout_requested: false,
            enqueued_at_ms: now,
            started_at_ms: 0,
            completed_at_ms: 0,
            execution_timeout_ms,
            result: None,
            error: None,
            error_type: None,
            progress: None,
            audit_action_id,
            request_type: "command_job".to_string(),
            action: command.to_string(),
            managed_line,
        };
        let snapshot = snapshot_of(&record);
        let audit = audit_request(&record, client_id);
        records.insert(id.clone(), record);
        admission.push_back(id);
        Ok(PreparedJob { audit, snapshot })
    }

    pub(super) fn take_dispatchable(&self, now: i64) -> Option<ManagedRequest> {
        self.reap(now);
        let mut records = self.records.lock().ok()?;
        let occupied = records
            .values()
            .any(|record| record.phase == Phase::Buffered || record.phase == Phase::Running);
        let mut admission = self.admission.lock().ok()?;
        let mut canceled = Vec::new();
        let mut kept = VecDeque::new();
        let mut dispatched = None;
        while let Some(id) = admission.pop_front() {
            let Some(record) = records.get_mut(&id) else {
                continue;
            };
            if record.phase != Phase::Queued {
                continue;
            }
            if record.cancellation_requested {
                finish_terminal(
                    record,
                    Phase::Canceled,
                    now,
                    None,
                    Some("canceled".to_string()),
                    Some("canceled".to_string()),
                );
                canceled.push(id);
                continue;
            }
            if occupied || dispatched.is_some() {
                kept.push_back(id);
                continue;
            }
            record.phase = Phase::Buffered;
            record.result = None;
            record.progress = None;
            record.error = None;
            record.error_type = None;
            dispatched = Some(managed_request(record));
        }
        admission.extend(kept);
        drop(admission);
        drop(records);
        for id in canceled {
            remember_terminal(&self.terminal, &id);
        }
        dispatched
    }

    pub(super) fn requeue_undelivered(&self, id: &str) {
        let Ok(mut records) = self.records.lock() else {
            return;
        };
        let Some(record) = records.get_mut(id) else {
            return;
        };
        if record.phase != Phase::Buffered {
            return;
        }
        record.phase = Phase::Queued;
        if let Ok(mut admission) = self.admission.lock() {
            admission.push_front(id.to_string());
        }
    }

    pub(super) fn note_delivered(&self, id: &str) {
        let Ok(mut records) = self.records.lock() else {
            return;
        };
        let Some(record) = records.get_mut(id) else {
            return;
        };
        // 交给 C# 缓冲不等于已经执行。running 只由 try_start 授予。
        if record.phase == Phase::Queued {
            record.phase = Phase::Buffered;
        }
    }

    pub(super) fn try_start(&self, id: &str, now: i64) -> bool {
        let Ok(mut records) = self.records.lock() else {
            return false;
        };
        if records.values().any(|record| record.phase == Phase::Running) {
            return false;
        }
        let Some(record) = records.get_mut(id) else {
            return false;
        };
        if record.phase != Phase::Buffered || record.cancellation_requested || record.timeout_requested {
            return false;
        }
        record.phase = Phase::Running;
        record.started_at_ms = now;
        true
    }

    pub(super) fn cancellation_requested(&self, id: &str, now: i64) -> bool {
        self.reap(now);
        let Ok(records) = self.records.lock() else {
            return true;
        };
        match records.get(id) {
            Some(record) => {
                record.phase != Phase::Running
                    || record.cancellation_requested
                    || record.timeout_requested
            }
            None => true,
        }
    }

    pub(super) fn report_progress(&self, id: &str, progress_json: &str, now: i64) -> bool {
        if progress_json.len() > JOB_PROGRESS_LIMIT {
            return false;
        }
        let Value::Object(object) = serde_json::from_str::<Value>(progress_json).unwrap_or(Value::Null) else {
            return false;
        };
        let Some(progress) = normalize_progress(&object) else {
            return false;
        };
        let Ok(mut records) = self.records.lock() else {
            return false;
        };
        let Some(record) = records.get_mut(id) else {
            return false;
        };
        if record.phase != Phase::Running || execution_expired(record, now) {
            return false;
        }
        record.progress = Some(progress);
        true
    }

    pub(super) fn status(&self, id: &str, now: i64) -> Option<Value> {
        self.reap(now);
        let records = self.records.lock().ok()?;
        records.get(id).map(snapshot_of)
    }

    pub(super) fn progress(&self, id: &str, now: i64) -> Option<Value> {
        self.reap(now);
        let records = self.records.lock().ok()?;
        let record = records.get(id)?;
        Some(json!({
            "jobId": record.id,
            "state": record.phase.as_str(),
            "active": record.phase == Phase::Running,
            "progress": record.progress.clone().unwrap_or(Value::Null),
        }))
    }

    pub(super) fn cancel(&self, id: &str, now: i64) -> Option<Value> {
        self.reap(now);
        let mut records = self.records.lock().ok()?;
        let record = records.get_mut(id)?;
        if record.phase.is_terminal() {
            return Some(snapshot_of(record));
        }
        record.cancellation_requested = true;
        let queued_or_buffered = record.phase == Phase::Queued || record.phase == Phase::Buffered;
        if queued_or_buffered {
            finish_terminal(
                record,
                Phase::Canceled,
                now,
                None,
                Some("canceled".to_string()),
                Some("canceled".to_string()),
            );
        }
        let snapshot = snapshot_of(record);
        drop(records);
        if queued_or_buffered {
            remember_terminal(&self.terminal, id);
        }
        Some(snapshot)
    }

    /// 拦截 job 完成。返回 true 表示这是 job，调用方不得再向客户端回第二份响应。
    pub(super) fn complete(&self, id: &str, response: &[u8], now: i64) -> bool {
        let Ok(mut records) = self.records.lock() else {
            return false;
        };
        let Some(record) = records.get_mut(id) else {
            return false;
        };
        if record.phase.is_terminal() {
            return true;
        }
        if response.len() > JOB_RESULT_LIMIT {
            finish_terminal(
                record,
                Phase::Failed,
                now,
                None,
                Some("result exceeds 8MiB".to_string()),
                Some("result_too_large".to_string()),
            );
        } else {
            apply_completion(record, response, now);
        }
        let finished = record.phase.is_terminal();
        drop(records);
        if finished {
            remember_terminal(&self.terminal, id);
        }
        true
    }

    pub(super) fn reap(&self, now: i64) {
        let Ok(mut records) = self.records.lock() else {
            return;
        };
        let mut finished = Vec::new();
        for record in records.values_mut() {
            if record.phase.is_terminal() || !execution_expired(record, now) {
                continue;
            }
            // Buffered 尚未 TryStart，没有在跑的代码可协作，必须终态以免永久占着串行槽。
            // Running 只请求协作取消，实际返回后才终态，避免超时提前放开串行锁。
            if record.phase == Phase::Running {
                record.timeout_requested = true;
                continue;
            }
            let before_dispatch = record.phase == Phase::Queued;
            finish_terminal(
                record,
                Phase::Failed,
                now,
                None,
                Some(
                    if before_dispatch {
                        "execution timeout before dispatch".to_string()
                    } else {
                        "execution timeout before start".to_string()
                    },
                ),
                Some("timeout".to_string()),
            );
            finished.push(record.id.clone());
        }
        drop(records);
        if !finished.is_empty() { for id in finished { remember_terminal(&self.terminal, &id); } return; }
        self.evict(now);
    }

    pub(super) fn interrupt_incomplete(&self, now: i64) {
        let Ok(mut records) = self.records.lock() else {
            return;
        };
        let mut finished = Vec::new();
        for record in records.values_mut() {
            if record.phase.is_terminal() {
                continue;
            }
            finish_terminal(
                record,
                Phase::Interrupted,
                now,
                None,
                Some("domain reload or editor exit interrupted the job".to_string()),
                Some("interrupted".to_string()),
            );
            finished.push(record.id.clone());
        }
        drop(records);
        if let Ok(mut admission) = self.admission.lock() {
            admission.clear();
        }
        for id in finished {
            remember_terminal(&self.terminal, &id);
        }
    }

    pub(super) fn active_len(&self) -> usize {
        self.records
            .lock()
            .map(|records| active_count(&records))
            .unwrap_or(0)
    }

    fn allocate_id(&self, now: i64) -> String {
        let seq = self.next_id.fetch_add(1, Ordering::Relaxed);
        format!("job-{now}-{seq}")
    }

    fn evict(&self, now: i64) {
        let Ok(mut records) = self.records.lock() else {
            return;
        };
        let Ok(mut terminal) = self.terminal.lock() else {
            return;
        };
        while let Some(id) = terminal.front().cloned() {
            let expired = records.get(&id).is_some_and(|record| {
                record.phase.is_terminal()
                    && now.saturating_sub(record.completed_at_ms) > JOB_RETENTION_MS
            });
            let over_limit = terminal.len() > JOB_TERMINAL_LIMIT;
            if !expired && !over_limit {
                break;
            }
            terminal.pop_front();
            if records
                .get(&id)
                .is_some_and(|record| record.phase.is_terminal())
            {
                records.remove(&id);
            }
        }
    }
}

fn active_count(records: &HashMap<String, JobRecord>) -> usize {
    records
        .values()
        .filter(|record| !record.phase.is_terminal())
        .count()
}

fn execution_timeout_ms(payload: &Value) -> Option<i64> {
    match payload.get("executionTimeoutMs").and_then(Value::as_i64) {
        Some(timeout)
            if (MIN_EXECUTION_TIMEOUT_MS..=MAX_EXECUTION_TIMEOUT_MS).contains(&timeout) =>
        {
            Some(timeout)
        }
        Some(_) => None,
        None => Some(DEFAULT_EXECUTION_TIMEOUT_MS),
    }
}

fn execution_expired(record: &JobRecord, now: i64) -> bool {
    now.saturating_sub(record.enqueued_at_ms) > record.execution_timeout_ms
}

fn managed_job_line(
    id: &str,
    command: &str,
    parameters_json: &str,
    execution_timeout_ms: i64,
) -> Vec<u8> {
    serde_json::to_vec(&json!({
        "id": id,
        "type": "command_job",
        "timeoutMs": execution_timeout_ms,
        "payload": {
            "name": command,
            "parametersJson": parameters_json,
            "jobId": id,
        }
    }))
    .unwrap_or_else(|_| format!(r#"{{"id":"{id}","type":"command_job"}}"#).into_bytes())
}

fn managed_request(record: &JobRecord) -> ManagedRequest {
    ManagedRequest {
        id: record.id.clone(),
        line: record.managed_line.clone(),
        enqueued_at_ms: record.enqueued_at_ms,
        delivered_at_ms: 0,
        timeout_ms: record.execution_timeout_ms,
        audit_action_id: record.audit_action_id,
        request_type: record.request_type.clone(),
        action: record.action.clone(),
        detached_job: true,
    }
}

fn audit_request(record: &JobRecord, request_id: &str) -> ManagedRequest {
    ManagedRequest {
        id: request_id.to_string(),
        line: Vec::new(),
        enqueued_at_ms: record.enqueued_at_ms,
        delivered_at_ms: record.started_at_ms,
        timeout_ms: record.execution_timeout_ms,
        audit_action_id: record.audit_action_id,
        request_type: "command_submit".to_string(),
        action: record.action.clone(),
        detached_job: true,
    }
}

fn finish_terminal(
    record: &mut JobRecord,
    phase: Phase,
    now: i64,
    result: Option<Value>,
    error: Option<String>,
    error_type: Option<String>,
) {
    if record.phase.is_terminal() {
        return;
    }
    record.phase = phase;
    record.completed_at_ms = now;
    record.result = result;
    record.error = error;
    record.error_type = error_type;
    if phase != Phase::Completed {
        record.progress = None;
    }
}

fn remember_terminal(terminal: &Mutex<VecDeque<String>>, id: &str) {
    if let Ok(mut terminal) = terminal.lock() {
        if !terminal.iter().any(|existing| existing == id) {
            terminal.push_back(id.to_string());
        }
    }
}

fn apply_completion(record: &mut JobRecord, response: &[u8], now: i64) {
    let parsed = serde_json::from_slice::<Value>(trim_response(response)).ok();
    let timeout_pending = record.timeout_requested || execution_expired(record, now);
    if timeout_pending {
        finish_terminal(
            record,
            Phase::Failed,
            now,
            None,
            Some("execution timeout".to_string()),
            Some("timeout".to_string()),
        );
        return;
    }
    let success = parsed
        .as_ref()
        .and_then(|value| value.get("ok"))
        .and_then(Value::as_bool)
        .unwrap_or(false);
    if record.cancellation_requested && !success {
        let (error, error_type) = completion_error(parsed.as_ref());
        finish_terminal(
            record,
            Phase::Canceled,
            now,
            None,
            Some(error),
            Some(error_type),
        );
        return;
    }
    if success {
        let result = parsed
            .as_ref()
            .and_then(|value| value.get("result"))
            .cloned()
            .unwrap_or(Value::Null);
        finish_terminal(record, Phase::Completed, now, Some(result), None, None);
        return;
    }
    let (error, error_type) = completion_error(parsed.as_ref());
    finish_terminal(
        record,
        Phase::Failed,
        now,
        None,
        Some(error),
        Some(error_type),
    );
}

fn completion_error(parsed: Option<&Value>) -> (String, String) {
    let error = parsed
        .and_then(|value| value.get("error"))
        .and_then(Value::as_str)
        .unwrap_or("unity request failed")
        .to_string();
    let error_type = parsed
        .and_then(|value| value.get("error_type").or_else(|| value.get("errorType")))
        .and_then(Value::as_str)
        .unwrap_or("command_error")
        .to_string();
    (error, error_type)
}

fn trim_response(response: &[u8]) -> &[u8] {
    let mut start = 0usize;
    let mut end = response.len();
    while start < end && response[start].is_ascii_whitespace() {
        start += 1;
    }
    while end > start && response[end - 1].is_ascii_whitespace() {
        end -= 1;
    }
    &response[start..end]
}

fn snapshot_of(record: &JobRecord) -> Value {
    let mut object = Map::new();
    object.insert("jobId".to_string(), json!(record.id));
    object.insert("command".to_string(), json!(record.command));
    object.insert("state".to_string(), json!(record.phase.as_str()));
    object.insert(
        "cancellationRequested".to_string(),
        json!(record.cancellation_requested || record.timeout_requested),
    );
    object.insert("enqueuedAtMs".to_string(), json!(record.enqueued_at_ms));
    object.insert("startedAtMs".to_string(), json!(record.started_at_ms));
    object.insert("completedAtMs".to_string(), json!(record.completed_at_ms));
    object.insert(
        "result".to_string(),
        record.result.clone().unwrap_or(Value::Null),
    );
    if let Some(error) = &record.error {
        object.insert("error".to_string(), json!(error));
    }
    if let Some(error_type) = &record.error_type {
        object.insert("errorType".to_string(), json!(error_type));
    }
    object.insert(
        "progress".to_string(),
        record.progress.clone().unwrap_or(Value::Null),
    );
    Value::Object(object)
}

fn normalize_progress(object: &Map<String, Value>) -> Option<Value> {
    let title = bounded_progress_text(object.get("title").and_then(Value::as_str).unwrap_or(""));
    let info = bounded_progress_text(object.get("info").and_then(Value::as_str).unwrap_or(""));
    let current = object.get("current").and_then(Value::as_f64);
    let total = object.get("total").and_then(Value::as_f64);
    let mut progress = object.get("progress").and_then(Value::as_f64);
    if let (Some(current), Some(total)) = (current, total) {
        if total > 0.0 && progress.is_none() {
            progress = Some((current / total).clamp(0.0, 1.0));
        }
    }
    if let Some(value) = progress {
        if !(0.0..=1.0).contains(&value) {
            return None;
        }
    }
    let mut normalized = Map::new();
    normalized.insert("title".to_string(), json!(title));
    normalized.insert("info".to_string(), json!(info));
    if let Some(current) = current {
        normalized.insert("current".to_string(), json!(current));
    }
    if let Some(total) = total {
        normalized.insert("total".to_string(), json!(total));
    }
    if let Some(progress) = progress {
        normalized.insert("progress".to_string(), json!(progress));
    }
    Some(Value::Object(normalized))
}

fn bounded_progress_text(value: &str) -> String {
    value.chars().take(AUDIT_VALUE_MAX_CHARS).collect()
}

#[cfg(test)]
pub(super) fn test_store() -> JobStore {
    JobStore::new()
}
