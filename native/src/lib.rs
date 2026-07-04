use std::os::raw::c_int;

pub const MANAGED_STATE_INITIALIZING: i32 = 0;
pub const MANAGED_STATE_READY: i32 = 1;
pub const MANAGED_STATE_RELOADING: i32 = 2;
pub const MANAGED_STATE_QUITTING: i32 = 3;
pub const NATIVE_PROTOCOL_VERSION: i32 = 1;

unsafe fn slice_from_raw<'a>(ptr: *const u8, len: i32) -> &'a [u8] {
    if ptr.is_null() || len <= 0 {
        &[]
    } else {
        std::slice::from_raw_parts(ptr, len as usize)
    }
}

unsafe fn string_from_raw(ptr: *const u8, len: i32) -> String {
    String::from_utf8_lossy(slice_from_raw(ptr, len)).into_owned()
}

#[cfg(windows)]
fn normalize_pipe_name(pipe_name: String) -> String {
    let value = pipe_name.trim().to_string();
    if value.starts_with(r"\\.\pipe\") {
        value
    } else {
        format!(r"\\.\pipe\{}", value.trim_start_matches('\\'))
    }
}

#[cfg(windows)]
mod imp {
    use std::collections::{HashMap, VecDeque};
    use std::ffi::{c_void, OsStr};
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::null_mut;
    use std::sync::atomic::{AtomicBool, AtomicI32, AtomicI64, Ordering};
    use std::sync::{Arc, Mutex, OnceLock};
    use std::time::{Duration, SystemTime, UNIX_EPOCH};

    use serde_json::{json, Value};
    use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
    use tokio::net::windows::named_pipe::{NamedPipeServer, PipeMode, ServerOptions};
    use tokio::sync::{mpsc, Notify};

    use super::{
        normalize_pipe_name, MANAGED_STATE_INITIALIZING, MANAGED_STATE_QUITTING,
        MANAGED_STATE_READY, MANAGED_STATE_RELOADING, NATIVE_PROTOCOL_VERSION,
    };

    const WRITER_CHANNEL_LIMIT: usize = 256;
    const REQUEST_BUFFER_LIMIT: usize = 1024 * 1024;
    const STATE_PLANE_MAGIC: u32 = 0x4855_4950;
    const STATE_PLANE_VERSION: u16 = 1;
    const STATE_PLANE_HEADER_SIZE: usize = 64;
    const STATE_PLANE_SLOT_COUNT: usize = 2;
    const STATE_PLANE_SLOT_SIZE: usize = 64 * 1024;
    const STATE_PLANE_SLOT_PAYLOAD_OFFSET: usize = 24;
    const STATE_PLANE_MAX_PAYLOAD: usize =
        STATE_PLANE_SLOT_SIZE - STATE_PLANE_SLOT_PAYLOAD_OFFSET;
    const HEARTBEAT_TIMEOUT_MS: i64 = 5_000;
    const REQUEST_TIMEOUT_MS: i64 = 60_000;
    const CLIENT_HEARTBEAT_TIMEOUT_MS: i64 = 15_000;
    const CAPABILITIES: [&str; 9] = [
        "native-broker",
        "direct-status",
        "reload-stable-pipe",
        "state-plane-v1",
        "background-runner",
        "focus-state",
        "heartbeat-timeout",
        "request-timeout",
        "client-heartbeat-timeout",
    ];

    type Bool = i32;
    type Dword = u32;
    type Handle = *mut c_void;

    const INVALID_HANDLE_VALUE: Handle = -1isize as Handle;
    const PAGE_READWRITE: Dword = 0x04;
    const FILE_MAP_WRITE: Dword = 0x0002;

    unsafe extern "system" {
        fn CreateFileMappingW(
            hFile: Handle,
            lpFileMappingAttributes: *mut c_void,
            flProtect: Dword,
            dwMaximumSizeHigh: Dword,
            dwMaximumSizeLow: Dword,
            lpName: *const u16,
        ) -> Handle;
        fn MapViewOfFile(
            hFileMappingObject: Handle,
            dwDesiredAccess: Dword,
            dwFileOffsetHigh: Dword,
            dwFileOffsetLow: Dword,
            dwNumberOfBytesToMap: usize,
        ) -> *mut c_void;
        fn UnmapViewOfFile(lpBaseAddress: *const c_void) -> Bool;
        fn CloseHandle(hObject: Handle) -> Bool;
    }

    struct StatePlane {
        handle: Handle,
        view: *mut u8,
        total_size: usize,
        seq: u64,
    }

    unsafe impl Send for StatePlane {}

    impl StatePlane {
        fn create(mapping_name: &str) -> Option<Self> {
            let total_size = STATE_PLANE_HEADER_SIZE
                .saturating_add(STATE_PLANE_SLOT_COUNT.saturating_mul(STATE_PLANE_SLOT_SIZE));
            let wide_name = wide_null(mapping_name);
            let handle = unsafe {
                CreateFileMappingW(
                    INVALID_HANDLE_VALUE,
                    null_mut(),
                    PAGE_READWRITE,
                    0,
                    total_size.min(u32::MAX as usize) as Dword,
                    wide_name.as_ptr(),
                )
            };
            if handle.is_null() {
                eprintln!("[pi-unity-native] create state plane failed: {mapping_name}");
                return None;
            }
            let view = unsafe { MapViewOfFile(handle, FILE_MAP_WRITE, 0, 0, total_size) as *mut u8 };
            if view.is_null() {
                unsafe {
                    let _ = CloseHandle(handle);
                }
                eprintln!("[pi-unity-native] map state plane failed: {mapping_name}");
                return None;
            }

            let mut plane = Self {
                handle,
                view,
                total_size,
                seq: 0,
            };
            plane.write_header();
            Some(plane)
        }

        fn write_header(&mut self) {
            self.write_u32(0, STATE_PLANE_MAGIC);
            self.write_u16(4, STATE_PLANE_VERSION);
            self.write_u16(6, STATE_PLANE_SLOT_COUNT.min(u16::MAX as usize) as u16);
            self.write_u32(8, STATE_PLANE_SLOT_SIZE.min(u32::MAX as usize) as u32);
            self.write_u32(12, std::process::id());
            self.write_u64(16, 0);
            self.write_u64(24, now_ms().max(0) as u64);
        }

        fn write_json(&mut self, observed_at_ms: i64, payload_json: &str) {
            let bytes = payload_json.as_bytes();
            if bytes.is_empty() || bytes.len() > STATE_PLANE_MAX_PAYLOAD {
                eprintln!(
                    "[pi-unity-native] state plane payload too large: {} bytes",
                    bytes.len()
                );
                return;
            }

            self.seq = self.seq.saturating_add(1).max(1);
            let slot_index = ((self.seq - 1) as usize) % STATE_PLANE_SLOT_COUNT;
            let slot_offset = STATE_PLANE_HEADER_SIZE + slot_index * STATE_PLANE_SLOT_SIZE;
            let observed_at_ms = observed_at_ms.max(0) as u64;

            self.write_u64(slot_offset, 0);
            self.write_u64(slot_offset + 8, observed_at_ms);
            self.write_u32(slot_offset + 16, bytes.len().min(u32::MAX as usize) as u32);
            self.write_u32(slot_offset + 20, 0);
            self.write_bytes(slot_offset + STATE_PLANE_SLOT_PAYLOAD_OFFSET, bytes);
            self.write_u64(slot_offset, self.seq);
            self.write_u64(16, self.seq);
            self.write_u64(24, observed_at_ms);
        }

        fn write_bytes(&mut self, offset: usize, bytes: &[u8]) {
            if offset.saturating_add(bytes.len()) > self.total_size {
                return;
            }
            unsafe {
                std::ptr::copy_nonoverlapping(bytes.as_ptr(), self.view.add(offset), bytes.len());
            }
        }

        fn write_u16(&mut self, offset: usize, value: u16) {
            self.write_bytes(offset, &value.to_le_bytes());
        }

        fn write_u32(&mut self, offset: usize, value: u32) {
            self.write_bytes(offset, &value.to_le_bytes());
        }

        fn write_u64(&mut self, offset: usize, value: u64) {
            self.write_bytes(offset, &value.to_le_bytes());
        }
    }

    impl Drop for StatePlane {
        fn drop(&mut self) {
            unsafe {
                let _ = UnmapViewOfFile(self.view as *const c_void);
                let _ = CloseHandle(self.handle);
            }
        }
    }

    fn wide_null(value: &str) -> Vec<u16> {
        OsStr::new(value).encode_wide().chain(Some(0)).collect()
    }

    fn normalize_project_key(project_path: &str) -> String {
        let mut value = project_path.trim().replace('\\', "/");
        while value.ends_with('/') && value.len() > 3 {
            value.pop();
        }
        value.to_ascii_lowercase()
    }

    fn project_key_hash(project_path: &str) -> String {
        let normalized = normalize_project_key(project_path);
        let mut hash: u64 = 0xcbf29ce484222325;
        for byte in normalized.as_bytes() {
            hash ^= *byte as u64;
            hash = hash.wrapping_mul(0x100000001b3);
        }
        format!("{hash:016x}")
    }

    fn state_plane_name(project_path: &str) -> String {
        format!(r"Local\PiUnityHarnessState_{}", project_key_hash(project_path))
    }

    #[derive(Clone)]
    struct ManagedRequest {
        id: String,
        line: Vec<u8>,
        enqueued_at_ms: i64,
        delivered_at_ms: i64,
        timeout_ms: i64,
    }

    struct EditorObservation {
        status: String,
        focus_state: String,
        window_state: String,
    }

    struct Broker {
        pipe_name: String,
        project_path: String,
        state_plane_name: String,
        token: Mutex<String>,
        connected: AtomicBool,
        shutdown: AtomicBool,
        shutdown_notify: Notify,
        managed_state: AtomicI32,
        managed_generation: AtomicI64,
        last_heartbeat_ms: AtomicI64,
        editor_status: Mutex<String>,
        pending: Mutex<VecDeque<ManagedRequest>>,
        in_flight: Mutex<HashMap<String, ManagedRequest>>,
        writer: Mutex<Option<mpsc::Sender<Vec<u8>>>>,
        state_plane: Mutex<Option<StatePlane>>,
    }

    impl Broker {
        fn new(project_path: String, pipe_name: String, token: String) -> Self {
            let state_plane_name = state_plane_name(&project_path);
            let state_plane = StatePlane::create(&state_plane_name);
            Self {
                pipe_name,
                project_path,
                state_plane_name,
                token: Mutex::new(token),
                connected: AtomicBool::new(false),
                shutdown: AtomicBool::new(false),
                shutdown_notify: Notify::new(),
                managed_state: AtomicI32::new(MANAGED_STATE_INITIALIZING),
                managed_generation: AtomicI64::new(0),
                last_heartbeat_ms: AtomicI64::new(now_ms()),
                editor_status: Mutex::new("starting".to_string()),
                pending: Mutex::new(VecDeque::new()),
                in_flight: Mutex::new(HashMap::new()),
                writer: Mutex::new(None),
                state_plane: Mutex::new(state_plane),
            }
        }

        fn status_payload(&self) -> Value {
            let now = now_ms();
            let heartbeat_age_ms = self.heartbeat_age_ms(now);
            let editor = self.editor_observation();
            json!({
                "project": self.project_path,
                "processId": std::process::id(),
                "pipe": self.pipe_name,
                "statePlaneName": self.state_plane_name,
                "connected": self.connected.load(Ordering::SeqCst),
                "managedState": self.managed_state_name(),
                "managedGeneration": self.managed_generation.load(Ordering::SeqCst),
                "lastHeartbeatMs": self.last_heartbeat_ms.load(Ordering::SeqCst),
                "heartbeatAgeMs": heartbeat_age_ms,
                "heartbeatTimedOut": self.is_heartbeat_timed_out_at(now),
                "editorStatus": editor.status,
                "focusState": editor.focus_state,
                "windowState": editor.window_state,
                "pending": self.pending.lock().map(|q| q.len()).unwrap_or(0),
                "inFlight": self.in_flight.lock().map(|m| m.len()).unwrap_or(0),
                "capabilities": CAPABILITIES
            })
        }

        fn managed_state_name(&self) -> &'static str {
            match self.managed_state.load(Ordering::SeqCst) {
                MANAGED_STATE_INITIALIZING => "initializing",
                MANAGED_STATE_READY => "ready",
                MANAGED_STATE_RELOADING => "reloading",
                MANAGED_STATE_QUITTING => "quitting",
                _ => "unknown",
            }
        }

        fn managed_error(&self) -> &'static str {
            match self.managed_state.load(Ordering::SeqCst) {
                MANAGED_STATE_INITIALIZING => "managed_not_ready",
                MANAGED_STATE_RELOADING => "managed_reloading",
                MANAGED_STATE_QUITTING => "managed_quitting",
                _ => "managed_not_ready",
            }
        }

        fn editor_observation(&self) -> EditorObservation {
            let raw = self
                .editor_status
                .lock()
                .map(|s| s.clone())
                .unwrap_or_else(|_| "unknown".to_string());
            let mut parts = raw.split(';');
            let status = parts.next().unwrap_or("unknown").trim().to_string();
            let mut focus_state = "unknown".to_string();
            let mut window_state = "unknown".to_string();
            for part in parts {
                let mut kv = part.splitn(2, '=');
                let key = kv.next().unwrap_or("").trim();
                let value = kv.next().unwrap_or("").trim();
                match key {
                    "focus" if !value.is_empty() => focus_state = value.to_string(),
                    "window" if !value.is_empty() => window_state = value.to_string(),
                    _ => {}
                }
            }
            EditorObservation {
                status,
                focus_state,
                window_state,
            }
        }

        fn heartbeat_age_ms(&self, now: i64) -> i64 {
            let last = self.last_heartbeat_ms.load(Ordering::SeqCst);
            if last <= 0 {
                0
            } else {
                now.saturating_sub(last)
            }
        }

        fn is_heartbeat_timed_out_at(&self, now: i64) -> bool {
            self.managed_state.load(Ordering::SeqCst) == MANAGED_STATE_READY
                && self.heartbeat_age_ms(now) > HEARTBEAT_TIMEOUT_MS
        }

        fn reap_timeouts(&self) {
            let now = now_ms();
            let heartbeat_timed_out = self.is_heartbeat_timed_out_at(now);
            let mut lines: Vec<Vec<u8>> = Vec::new();

            if let Ok(mut pending) = self.pending.lock() {
                let mut kept = VecDeque::new();
                while let Some(request) = pending.pop_front() {
                    if now.saturating_sub(request.enqueued_at_ms) > request.timeout_ms {
                        lines.push(self.error_line(&request.id, "request_timeout_before_dispatch"));
                    } else {
                        kept.push_back(request);
                    }
                }
                *pending = kept;
            }

            if let Ok(mut in_flight) = self.in_flight.lock() {
                let expired: Vec<String> = in_flight
                    .iter()
                    .filter_map(|(id, request)| {
                        let age_start = if request.delivered_at_ms > 0 {
                            request.delivered_at_ms
                        } else {
                            request.enqueued_at_ms
                        };
                        if heartbeat_timed_out
                            && now.saturating_sub(age_start) > HEARTBEAT_TIMEOUT_MS
                        {
                            Some(id.clone())
                        } else if now.saturating_sub(age_start) > request.timeout_ms {
                            Some(id.clone())
                        } else {
                            None
                        }
                    })
                    .collect();
                for id in expired {
                    if in_flight.remove(&id).is_some() {
                        let error = if heartbeat_timed_out {
                            "managed_heartbeat_timeout"
                        } else {
                            "request_timeout_in_flight"
                        };
                        lines.push(self.error_line(&id, error));
                    }
                }
            }

            if lines.is_empty() {
                if heartbeat_timed_out {
                    self.publish_status_snapshot();
                }
                return;
            }
            for line in lines {
                self.send_line(line);
            }
            self.publish_status_snapshot();
        }

        fn publish_status_snapshot(&self) {
            let observed_at_ms = now_ms();
            let heartbeat_age_ms = self.heartbeat_age_ms(observed_at_ms);
            let editor = self.editor_observation();
            let payload = json!({
                "project": self.project_path,
                "processId": std::process::id(),
                "pipe": self.pipe_name,
                "statePlaneName": self.state_plane_name,
                "observedAtMs": observed_at_ms,
                "connected": self.connected.load(Ordering::SeqCst),
                "managedState": self.managed_state_name(),
                "managedGeneration": self.managed_generation.load(Ordering::SeqCst),
                "lastHeartbeatMs": self.last_heartbeat_ms.load(Ordering::SeqCst),
                "heartbeatAgeMs": heartbeat_age_ms,
                "heartbeatTimedOut": self.is_heartbeat_timed_out_at(observed_at_ms),
                "editorStatus": editor.status,
                "focusState": editor.focus_state,
                "windowState": editor.window_state,
                "pending": self.pending.lock().map(|q| q.len()).unwrap_or(0),
                "inFlight": self.in_flight.lock().map(|m| m.len()).unwrap_or(0),
                "capabilities": CAPABILITIES,
            })
            .to_string();
            if let Ok(mut guard) = self.state_plane.lock() {
                if let Some(plane) = guard.as_mut() {
                    plane.write_json(observed_at_ms, &payload);
                }
            }
        }

        fn set_managed_state(&self, state: i32, generation: i64, editor_status: Option<String>) {
            self.managed_state.store(state, Ordering::SeqCst);
            if generation > 0 {
                self.managed_generation.store(generation, Ordering::SeqCst);
            }
            if let Some(status) = editor_status {
                if let Ok(mut guard) = self.editor_status.lock() {
                    *guard = status;
                }
            }
            self.last_heartbeat_ms.store(now_ms(), Ordering::SeqCst);
            if state != MANAGED_STATE_READY {
                self.fail_all_pending(self.managed_error());
            }
            self.publish_status_snapshot();
        }

        fn heartbeat(&self, generation: i64) {
            if generation > 0 {
                self.managed_generation.store(generation, Ordering::SeqCst);
            }
            self.last_heartbeat_ms.store(now_ms(), Ordering::SeqCst);
            self.publish_status_snapshot();
        }

        fn update_token(&self, token: String) {
            if let Ok(mut guard) = self.token.lock() {
                *guard = token;
            }
            self.publish_status_snapshot();
        }

        fn fail_all_pending(&self, message: &str) {
            let mut lines: Vec<Vec<u8>> = Vec::new();
            if let Ok(mut pending) = self.pending.lock() {
                while let Some(request) = pending.pop_front() {
                    lines.push(self.error_line(&request.id, message));
                }
            }
            if let Ok(mut in_flight) = self.in_flight.lock() {
                for (id, _) in in_flight.drain() {
                    lines.push(self.error_line(&id, message));
                }
            }
            for line in lines {
                self.send_line(line);
            }
            self.publish_status_snapshot();
        }

        fn has_active_work(&self) -> bool {
            let pending = self.pending.lock().map(|q| q.len()).unwrap_or(0);
            let in_flight = self.in_flight.lock().map(|m| m.len()).unwrap_or(0);
            pending > 0 || in_flight > 0
        }

        fn send_line(&self, bytes: Vec<u8>) {
            if let Ok(guard) = self.writer.lock() {
                if let Some(tx) = guard.as_ref() {
                    let _ = tx.try_send(bytes);
                }
            }
        }

        fn ok_line(&self, reply_to: &str, result: Value) -> Vec<u8> {
            let mut bytes = serde_json::to_vec(&json!({
                "reply_to": reply_to,
                "ok": true,
                "result": result
            }))
            .unwrap_or_else(|_| b"{\"ok\":false,\"error\":\"serialize_failed\"}".to_vec());
            bytes.push(b'\n');
            bytes
        }

        fn error_line(&self, reply_to: &str, error: &str) -> Vec<u8> {
            let mut bytes = serde_json::to_vec(&json!({
                "reply_to": reply_to,
                "ok": false,
                "error": error
            }))
            .unwrap_or_else(|_| b"{\"ok\":false,\"error\":\"serialize_failed\"}".to_vec());
            bytes.push(b'\n');
            bytes
        }

        fn handle_line(&self, buf: &[u8]) {
            self.reap_timeouts();

            let text = match std::str::from_utf8(trim_ascii(buf)) {
                Ok(text) if !text.is_empty() => text,
                _ => return,
            };

            let value: Value = match serde_json::from_str(text) {
                Ok(value) => value,
                Err(_) => {
                    self.send_line(self.error_line("", "invalid_json"));
                    return;
                }
            };

            let id = value.get("id").and_then(Value::as_str).unwrap_or("");
            let req_type = value.get("type").and_then(Value::as_str).unwrap_or("");
            let token = value.get("token").and_then(Value::as_str).unwrap_or("");

            if id.is_empty() || req_type.is_empty() {
                self.send_line(self.error_line(id, "missing_id_or_type"));
                return;
            }

            let token_ok = self
                .token
                .lock()
                .map(|expected| expected.is_empty() || expected.as_str() == token)
                .unwrap_or(false);
            if !token_ok {
                self.send_line(self.error_line(id, "unauthorized"));
                return;
            }

            match req_type {
                "ping" => self.send_line(self.ok_line(id, json!({ "pong": true }))),
                "status" => self.send_line(self.ok_line(id, self.status_payload())),
                "bridge_capabilities" => self.send_line(self.ok_line(
                    id,
                    json!({
                        "protocolVersion": NATIVE_PROTOCOL_VERSION,
                        "capabilities": CAPABILITIES
                    }),
                )),
                _ => {
                    if self.managed_state.load(Ordering::SeqCst) != MANAGED_STATE_READY {
                        self.send_line(self.error_line(id, self.managed_error()));
                        return;
                    }
                    if text.len() > REQUEST_BUFFER_LIMIT {
                        self.send_line(self.error_line(id, "request_too_large"));
                        return;
                    }
                    let request = ManagedRequest {
                        id: id.to_string(),
                        line: text.as_bytes().to_vec(),
                        enqueued_at_ms: now_ms(),
                        delivered_at_ms: 0,
                        timeout_ms: request_timeout_ms(&value),
                    };
                    if let Ok(mut pending) = self.pending.lock() {
                        pending.push_back(request);
                    }
                    self.publish_status_snapshot();
                }
            }
        }

        fn poll_request(&self, buffer: *mut u8, buffer_len: i32, out_required_len: *mut i32) -> i32 {
            self.reap_timeouts();

            let request = {
                let mut pending = match self.pending.lock() {
                    Ok(pending) => pending,
                    Err(_) => return 0,
                };
                pending.pop_front()
            };

            let Some(request) = request else {
                return 0;
            };

            let required = request.line.len() as i32;
            unsafe {
                if !out_required_len.is_null() {
                    *out_required_len = required;
                }
            }
            if buffer.is_null() || buffer_len < required {
                if let Ok(mut pending) = self.pending.lock() {
                    pending.push_front(request);
                }
                return -1;
            }

            unsafe {
                std::ptr::copy_nonoverlapping(request.line.as_ptr(), buffer, request.line.len());
            }
            if let Ok(mut in_flight) = self.in_flight.lock() {
                let mut delivered = request;
                delivered.delivered_at_ms = now_ms();
                in_flight.insert(delivered.id.clone(), delivered);
            }
            self.publish_status_snapshot();
            1
        }

        fn complete_request(&self, id: &str, response: Vec<u8>) {
            let known_request = self
                .in_flight
                .lock()
                .map(|mut in_flight| in_flight.remove(id).is_some())
                .unwrap_or(false);
            if !known_request {
                self.publish_status_snapshot();
                return;
            }
            let mut line = response;
            if !line.ends_with(b"\n") {
                line.push(b'\n');
            }
            self.send_line(line);
            self.publish_status_snapshot();
        }
    }

    fn trim_ascii(line: &[u8]) -> &[u8] {
        let mut start = 0usize;
        let mut end = line.len();
        while start < end && line[start].is_ascii_whitespace() {
            start += 1;
        }
        while end > start && line[end - 1].is_ascii_whitespace() {
            end -= 1;
        }
        &line[start..end]
    }

    fn request_timeout_ms(value: &Value) -> i64 {
        let requested = value
            .get("timeoutMs")
            .and_then(Value::as_i64)
            .or_else(|| value.get("timeout_ms").and_then(Value::as_i64))
            .or_else(|| {
                value
                    .get("payload")
                    .and_then(|payload| payload.get("timeoutMs"))
                    .and_then(Value::as_i64)
            })
            .or_else(|| {
                value
                    .get("payload")
                    .and_then(|payload| payload.get("timeout_ms"))
                    .and_then(Value::as_i64)
            })
            .unwrap_or(REQUEST_TIMEOUT_MS);
        requested.saturating_add(5_000).clamp(1_000, 10 * 60_000)
    }

    fn now_ms() -> i64 {
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_millis() as i64)
            .unwrap_or(0)
    }

    static BROKER: OnceLock<Arc<Broker>> = OnceLock::new();

    fn broker() -> Option<&'static Arc<Broker>> {
        BROKER.get()
    }

    fn broker_cell() -> &'static OnceLock<Arc<Broker>> {
        &BROKER
    }

    async fn serve_connection(broker: &Arc<Broker>, server: NamedPipeServer) {
        let (read_half, mut write_half) = tokio::io::split(server);
        let (tx, mut rx) = mpsc::channel::<Vec<u8>>(WRITER_CHANNEL_LIMIT);
        if let Ok(mut guard) = broker.writer.lock() {
            *guard = Some(tx);
        }
        broker.connected.store(true, Ordering::SeqCst);
        broker.publish_status_snapshot();

        let writer = tokio::spawn(async move {
            while let Some(bytes) = rx.recv().await {
                if write_half.write_all(&bytes).await.is_err() {
                    break;
                }
                if write_half.flush().await.is_err() {
                    break;
                }
            }
        });

        let mut reader = BufReader::new(read_half);
        let mut buf = Vec::<u8>::with_capacity(4096);
        let mut maintenance = tokio::time::interval(Duration::from_millis(250));
        let mut last_client_activity_ms = now_ms();
        loop {
            let read_result = tokio::select! {
                _ = broker.shutdown_notify.notified() => break,
                _ = maintenance.tick() => {
                    broker.reap_timeouts();
                    let idle_ms = now_ms().saturating_sub(last_client_activity_ms);
                    if idle_ms > CLIENT_HEARTBEAT_TIMEOUT_MS && !broker.has_active_work() {
                        eprintln!("[pi-unity-native] closing silent client after {idle_ms}ms");
                        break;
                    }
                    None::<std::io::Result<usize>>
                }
                read = reader.read_until(b'\n', &mut buf) => Some(read),
            };
            match read_result {
                None => continue,
                Some(Ok(0)) => break,
                Some(Ok(_)) => {
                    last_client_activity_ms = now_ms();
                    broker.handle_line(&buf);
                    buf.clear();
                }
                Some(Err(error)) => {
                    eprintln!("[pi-unity-native] pipe read error: {error}");
                    break;
                }
            }
        }

        broker.connected.store(false, Ordering::SeqCst);
        broker.publish_status_snapshot();
        broker.fail_all_pending("client_disconnected");
        if let Ok(mut guard) = broker.writer.lock() {
            *guard = None;
        }
        let _ = writer.await;
    }

    async fn broker_main(broker: Arc<Broker>, pipe_name: String) {
        eprintln!("[pi-unity-native] broker listening on {pipe_name}");
        loop {
            if broker.shutdown.load(Ordering::SeqCst) {
                break;
            }
            let server = match ServerOptions::new()
                .access_inbound(true)
                .access_outbound(true)
                .pipe_mode(PipeMode::Byte)
                .create(&pipe_name)
            {
                Ok(server) => server,
                Err(error) => {
                    eprintln!("[pi-unity-native] create pipe failed: {error}");
                    tokio::time::sleep(std::time::Duration::from_millis(500)).await;
                    continue;
                }
            };

            tokio::select! {
                _ = broker.shutdown_notify.notified() => break,
                connect = server.connect() => match connect {
                    Ok(()) => serve_connection(&broker, server).await,
                    Err(error) => {
                        eprintln!("[pi-unity-native] wait-for-connection failed: {error}");
                        tokio::time::sleep(std::time::Duration::from_millis(200)).await;
                    }
                }
            }
        }
    }

    pub fn init(project_path: String, pipe_name: String, token: String, protocol_version: i32) -> i32 {
        if protocol_version != NATIVE_PROTOCOL_VERSION {
            eprintln!(
                "[pi-unity-native] protocol mismatch: managed={protocol_version}, native={NATIVE_PROTOCOL_VERSION}"
            );
        }

        let pipe_name = normalize_pipe_name(pipe_name);
        if let Some(broker) = broker() {
            broker.update_token(token);
            broker.set_managed_state(MANAGED_STATE_INITIALIZING, 0, None);
            return 0;
        }

        let broker = Arc::new(Broker::new(project_path, pipe_name.clone(), token));
        broker.publish_status_snapshot();
        if broker_cell().set(broker.clone()).is_err() {
            return 0;
        }

        match std::thread::Builder::new()
            .name("pi-unity-native-broker".to_string())
            .spawn(move || {
                let runtime = match tokio::runtime::Builder::new_current_thread()
                    .enable_all()
                    .build()
                {
                    Ok(runtime) => runtime,
                    Err(error) => {
                        eprintln!("[pi-unity-native] runtime build failed: {error}");
                        return;
                    }
                };
                runtime.block_on(broker_main(broker, pipe_name));
            })
        {
            Ok(_) => 0,
            Err(error) => {
                eprintln!("[pi-unity-native] broker thread spawn failed: {error}");
                -1
            }
        }
    }

    pub fn shutdown() {
        if let Some(broker) = broker() {
            broker.shutdown.store(true, Ordering::SeqCst);
            broker.shutdown_notify.notify_waiters();
            broker.set_managed_state(MANAGED_STATE_QUITTING, 0, Some("quitting".to_string()));
        }
    }

    pub fn set_managed_state(state: i32, generation: i64, editor_status: Option<String>) {
        if let Some(broker) = broker() {
            broker.set_managed_state(state, generation, editor_status);
        }
    }

    pub fn managed_heartbeat(generation: i64) {
        if let Some(broker) = broker() {
            broker.heartbeat(generation);
        }
    }

    pub fn poll_request(buffer: *mut u8, buffer_len: i32, out_required_len: *mut i32) -> i32 {
        broker()
            .map(|broker| broker.poll_request(buffer, buffer_len, out_required_len))
            .unwrap_or(0)
    }

    pub fn complete_request(id: &str, response: Vec<u8>) {
        if let Some(broker) = broker() {
            broker.complete_request(id, response);
        }
    }

    pub fn emit_event(event_type: &str, payload: String) {
        if let Some(broker) = broker() {
            let mut line = serde_json::to_vec(&json!({
                "type": "event",
                "event": event_type,
                "payload": payload,
            }))
            .unwrap_or_default();
            line.push(b'\n');
            broker.send_line(line);
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        fn test_broker(name: &str) -> Broker {
            Broker::new(
                format!("F:/Temp/PiUnityHarnessTests/{name}{}", now_ms()),
                format!(r"\\.\pipe\pi_unity_test_{name}"),
                "token".to_string(),
            )
        }

        fn test_request(
            id: &str,
            line: &str,
            enqueued_at_ms: i64,
            timeout_ms: i64,
        ) -> ManagedRequest {
            ManagedRequest {
                id: id.to_string(),
                line: line.as_bytes().to_vec(),
                enqueued_at_ms,
                delivered_at_ms: 0,
                timeout_ms,
            }
        }

        fn response_json(line: Vec<u8>) -> Value {
            serde_json::from_slice(trim_ascii(&line)).unwrap()
        }

        #[test]
        fn parses_editor_observation_from_status_suffix() {
            let broker = test_broker("status");
            broker.set_managed_state(
                MANAGED_STATE_READY,
                1,
                Some("editing;focus=background;window=minimized".to_string()),
            );

            let observation = broker.editor_observation();
            assert_eq!(observation.status, "editing");
            assert_eq!(observation.focus_state, "background");
            assert_eq!(observation.window_state, "minimized");
        }

        #[test]
        fn request_timeout_uses_client_budget_with_grace() {
            let value = json!({ "timeoutMs": 20_000 });
            assert_eq!(request_timeout_ms(&value), 25_000);
        }

        #[test]
        fn heartbeat_recovers_after_timeout_without_restart() {
            let broker = test_broker("heartbeat_recovery");
            broker.set_managed_state(MANAGED_STATE_READY, 1, Some("editing".to_string()));
            let now = now_ms();
            broker
                .last_heartbeat_ms
                .store(now - HEARTBEAT_TIMEOUT_MS - 1_000, Ordering::SeqCst);
            assert!(broker.is_heartbeat_timed_out_at(now));

            broker.heartbeat(2);

            assert!(!broker.is_heartbeat_timed_out_at(now_ms()));
            assert_eq!(broker.managed_generation.load(Ordering::SeqCst), 2);
        }

        #[test]
        fn execute_request_is_rejected_with_managed_error_when_not_ready() {
            for (state, expected_error) in [
                (MANAGED_STATE_INITIALIZING, "managed_not_ready"),
                (MANAGED_STATE_RELOADING, "managed_reloading"),
                (MANAGED_STATE_QUITTING, "managed_quitting"),
            ] {
                let broker = test_broker(expected_error);
                let (tx, mut rx) = mpsc::channel(4);
                *broker.writer.lock().unwrap() = Some(tx);
                broker.managed_state.store(state, Ordering::SeqCst);

                broker.handle_line(br#"{"id":"1","type":"execute_code","token":"token"}"#);

                let response = response_json(rx.try_recv().unwrap());
                assert_eq!(response["reply_to"], "1");
                assert_eq!(response["ok"], false);
                assert_eq!(response["error"], expected_error);
                assert_eq!(broker.pending.lock().unwrap().len(), 0);
            }
        }

        #[test]
        fn poll_request_dequeues_pending_requests_fifo() {
            let broker = test_broker("fifo");
            broker.set_managed_state(MANAGED_STATE_READY, 1, Some("editing".to_string()));
            let now = now_ms();
            let lines = [
                r#"{"id":"1","type":"execute_code"}"#,
                r#"{"id":"2","type":"execute_code"}"#,
                r#"{"id":"3","type":"execute_code"}"#,
            ];
            {
                let mut pending = broker.pending.lock().unwrap();
                for (index, line) in lines.iter().enumerate() {
                    pending.push_back(test_request(
                        &(index + 1).to_string(),
                        line,
                        now,
                        REQUEST_TIMEOUT_MS,
                    ));
                }
            }

            for expected in lines {
                let mut buffer = vec![0u8; 128];
                let mut required_len = 0;
                let result = broker.poll_request(
                    buffer.as_mut_ptr(),
                    buffer.len() as i32,
                    &mut required_len as *mut i32,
                );

                assert_eq!(result, 1);
                assert_eq!(required_len as usize, expected.len());
                assert_eq!(&buffer[..required_len as usize], expected.as_bytes());
            }
            assert_eq!(
                broker.poll_request(std::ptr::null_mut(), 0, std::ptr::null_mut()),
                0
            );
            assert_eq!(broker.pending.lock().unwrap().len(), 0);
            assert_eq!(broker.in_flight.lock().unwrap().len(), 3);
        }

        #[test]
        fn status_payload_reports_heartbeat_and_queue_fields() {
            let broker = test_broker("status_payload");
            broker.set_managed_state(MANAGED_STATE_READY, 7, Some("editing".to_string()));
            let now = now_ms();
            broker
                .last_heartbeat_ms
                .store(now - HEARTBEAT_TIMEOUT_MS - 1_000, Ordering::SeqCst);
            broker.pending.lock().unwrap().push_back(test_request(
                "pending-1",
                r#"{"id":"pending-1","type":"execute_code"}"#,
                now,
                REQUEST_TIMEOUT_MS,
            ));
            broker.pending.lock().unwrap().push_back(test_request(
                "pending-2",
                r#"{"id":"pending-2","type":"execute_code"}"#,
                now,
                REQUEST_TIMEOUT_MS,
            ));
            broker.in_flight.lock().unwrap().insert(
                "in-flight-1".to_string(),
                test_request(
                    "in-flight-1",
                    r#"{"id":"in-flight-1","type":"execute_code"}"#,
                    now,
                    REQUEST_TIMEOUT_MS,
                ),
            );

            let payload = broker.status_payload();

            assert_eq!(payload["managedState"], "ready");
            assert_eq!(payload["managedGeneration"], 7);
            assert_eq!(payload["pending"], 2);
            assert_eq!(payload["inFlight"], 1);
            assert_eq!(payload["heartbeatTimedOut"], true);
            assert!(payload["heartbeatAgeMs"].as_i64().unwrap() >= HEARTBEAT_TIMEOUT_MS + 1_000);
        }

        #[test]
        fn reap_timeouts_keeps_old_in_flight_request_when_heartbeat_is_fresh() {
            let broker = test_broker("fresh_heartbeat");
            broker.set_managed_state(MANAGED_STATE_READY, 1, Some("editing".to_string()));
            let now = now_ms();
            broker.last_heartbeat_ms.store(now, Ordering::SeqCst);
            let mut request = test_request(
                "1",
                r#"{"id":"1","type":"execute_code"}"#,
                now - HEARTBEAT_TIMEOUT_MS - 1_000,
                REQUEST_TIMEOUT_MS,
            );
            request.delivered_at_ms = now - HEARTBEAT_TIMEOUT_MS - 1_000;
            broker
                .in_flight
                .lock()
                .unwrap()
                .insert("1".to_string(), request);

            broker.reap_timeouts();

            assert_eq!(broker.in_flight.lock().unwrap().len(), 1);
        }

        #[test]
        fn reap_timeouts_expires_pending_request_before_dispatch() {
            let broker = test_broker("pending_timeout");
            let (tx, mut rx) = mpsc::channel(4);
            *broker.writer.lock().unwrap() = Some(tx);
            let now = now_ms();
            broker.pending.lock().unwrap().push_back(test_request(
                "1",
                r#"{"id":"1","type":"execute_code"}"#,
                now - 2_000,
                1_000,
            ));

            broker.reap_timeouts();

            assert_eq!(broker.pending.lock().unwrap().len(), 0);
            let response = response_json(rx.try_recv().unwrap());
            assert_eq!(response["reply_to"], "1");
            assert_eq!(response["ok"], false);
            assert_eq!(response["error"], "request_timeout_before_dispatch");
        }

        #[test]
        fn heartbeat_timeout_keeps_pending_request_for_wake_attempt() {
            let broker = test_broker("pending");
            broker.set_managed_state(MANAGED_STATE_READY, 1, Some("editing".to_string()));
            broker
                .last_heartbeat_ms
                .store(now_ms() - HEARTBEAT_TIMEOUT_MS - 1_000, Ordering::SeqCst);
            broker.pending.lock().unwrap().push_back(ManagedRequest {
                id: "1".to_string(),
                line: br#"{"id":"1","type":"execute_code"}"#.to_vec(),
                enqueued_at_ms: now_ms(),
                delivered_at_ms: 0,
                timeout_ms: REQUEST_TIMEOUT_MS,
            });

            broker.reap_timeouts();
            assert_eq!(broker.pending.lock().unwrap().len(), 1);
        }

        #[test]
        fn heartbeat_timeout_expires_stale_in_flight_request() {
            let broker = test_broker("inflight");
            broker.set_managed_state(MANAGED_STATE_READY, 1, Some("editing".to_string()));
            let now = now_ms();
            broker
                .last_heartbeat_ms
                .store(now - HEARTBEAT_TIMEOUT_MS - 1_000, Ordering::SeqCst);
            broker.in_flight.lock().unwrap().insert(
                "1".to_string(),
                ManagedRequest {
                    id: "1".to_string(),
                    line: br#"{"id":"1","type":"execute_code"}"#.to_vec(),
                    enqueued_at_ms: now - HEARTBEAT_TIMEOUT_MS - 1_000,
                    delivered_at_ms: now - HEARTBEAT_TIMEOUT_MS - 1_000,
                    timeout_ms: REQUEST_TIMEOUT_MS,
                },
            );

            broker.reap_timeouts();
            assert_eq!(broker.in_flight.lock().unwrap().len(), 0);
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn pi_unity_init(
    project: *const u8,
    project_len: i32,
    pipe: *const u8,
    pipe_len: i32,
    token: *const u8,
    token_len: i32,
    protocol_version: c_int,
) -> c_int {
    #[cfg(windows)]
    {
        return imp::init(
            string_from_raw(project, project_len),
            string_from_raw(pipe, pipe_len),
            string_from_raw(token, token_len),
            protocol_version,
        );
    }
    #[cfg(not(windows))]
    {
        let _ = (project, project_len, pipe, pipe_len, token, token_len, protocol_version);
        -1
    }
}

#[no_mangle]
pub extern "C" fn pi_unity_shutdown() {
    #[cfg(windows)]
    imp::shutdown();
}

#[no_mangle]
pub unsafe extern "C" fn pi_unity_set_managed_state(
    state: c_int,
    generation: i64,
    editor_status: *const u8,
    editor_status_len: i32,
) {
    #[cfg(windows)]
    {
        let status = if editor_status.is_null() {
            None
        } else {
            Some(string_from_raw(editor_status, editor_status_len))
        };
        imp::set_managed_state(state, generation, status);
    }
    #[cfg(not(windows))]
    {
        let _ = (state, generation, editor_status, editor_status_len);
    }
}

#[no_mangle]
pub extern "C" fn pi_unity_managed_heartbeat(generation: i64) {
    #[cfg(windows)]
    imp::managed_heartbeat(generation);
    #[cfg(not(windows))]
    {
        let _ = generation;
    }
}

#[no_mangle]
pub unsafe extern "C" fn pi_unity_poll_request(
    buffer: *mut u8,
    buffer_len: i32,
    out_required_len: *mut i32,
) -> c_int {
    #[cfg(windows)]
    {
        imp::poll_request(buffer, buffer_len, out_required_len)
    }
    #[cfg(not(windows))]
    {
        let _ = (buffer, buffer_len, out_required_len);
        0
    }
}

#[no_mangle]
pub unsafe extern "C" fn pi_unity_complete_request(
    id: *const u8,
    id_len: i32,
    response: *const u8,
    response_len: i32,
) {
    #[cfg(windows)]
    {
        imp::complete_request(
            &string_from_raw(id, id_len),
            slice_from_raw(response, response_len).to_vec(),
        );
    }
    #[cfg(not(windows))]
    {
        let _ = (id, id_len, response, response_len);
    }
}

#[no_mangle]
pub unsafe extern "C" fn pi_unity_emit_event(
    event_type: *const u8,
    event_type_len: i32,
    payload: *const u8,
    payload_len: i32,
) {
    #[cfg(windows)]
    {
        imp::emit_event(
            &string_from_raw(event_type, event_type_len),
            string_from_raw(payload, payload_len),
        );
    }
    #[cfg(not(windows))]
    {
        let _ = (event_type, event_type_len, payload, payload_len);
    }
}
