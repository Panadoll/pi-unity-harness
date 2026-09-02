    use std::collections::{HashMap, VecDeque};
    use std::ffi::{c_void, OsStr};
    use std::fs::{self, OpenOptions};
    use std::io::{Read, Seek, SeekFrom, Write};
    use std::os::windows::ffi::OsStrExt;
    use std::path::PathBuf;
    use std::ptr::null_mut;
    use std::sync::atomic::{AtomicBool, AtomicI32, AtomicI64, AtomicU64, Ordering};
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
    const STATE_PLANE_MAX_PAYLOAD: usize = STATE_PLANE_SLOT_SIZE - STATE_PLANE_SLOT_PAYLOAD_OFFSET;
    const HEARTBEAT_TIMEOUT_MS: i64 = 5_000;
    const REQUEST_TIMEOUT_MS: i64 = 60_000;
    const CLIENT_HEARTBEAT_TIMEOUT_MS: i64 = 15_000;
    const AUDIT_SCHEMA_VERSION: u64 = 1;
    const AUDIT_FILE_MAX_BYTES: u64 = 5 * 1024 * 1024;
    const AUDIT_VALUE_MAX_CHARS: usize = 4096;
    const AUDIT_QUERY_MAX_EVENTS: usize = 20_000;
    const AUDIT_QUERY_DEFAULT_LIMIT: usize = 50;
    const AUDIT_QUERY_MAX_LIMIT: usize = 200;
    const CAPABILITIES: [&str; 12] = [
        "native-broker",
        "direct-status",
        "reload-stable-pipe",
        "state-plane-v1",
        "background-runner",
        "focus-state",
        "heartbeat-timeout",
        "request-timeout",
        "client-heartbeat-timeout",
        "context-snapshot-v1",
        "action-timeline-v1",
        "modal-probe-v1",
    ];

    const MODAL_PROBE_INTERVAL_MS: i64 = 500;
    const YOLO_AUTO_CLICK_COOLDOWN_MS: i64 = 8_000;

    /// YOLO mode: off (no action), detect (report only), safe-auto (click whitelisted buttons).
    #[derive(Clone, Copy, PartialEq)]
    enum YoloMode {
        Off = 0,
        Detect = 1,
        SafeAuto = 2,
    }

    impl YoloMode {
        fn from_i32(value: i32) -> Self {
            match value {
                1 => YoloMode::Detect,
                2 => YoloMode::SafeAuto,
                _ => YoloMode::Off,
            }
        }

        fn as_str(&self) -> &'static str {
            match self {
                YoloMode::Off => "off",
                YoloMode::Detect => "detect",
                YoloMode::SafeAuto => "safe-auto",
            }
        }
    }

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

    #[link(name = "user32")]
    unsafe extern "system" {
        fn EnumWindows(lpEnumFunc: unsafe extern "system" fn(isize, isize) -> i32, lParam: isize) -> i32;
        fn GetWindowThreadProcessId(hWnd: isize, lpdwProcessId: *mut u32) -> u32;
        fn IsWindowVisible(hWnd: isize) -> i32;
        fn GetClassNameW(hWnd: isize, lpClassName: *mut u16, nMaxCount: i32) -> i32;
        fn GetWindowTextW(hWnd: isize, lpString: *mut u16, nMaxCount: i32) -> i32;
        fn GetWindowTextLengthW(hWnd: isize) -> i32;
        fn EnumChildWindows(
            hWnd: isize,
            lpEnumFunc: unsafe extern "system" fn(isize, isize) -> i32,
            lParam: isize,
        ) -> i32;
        fn SendMessageW(hWnd: isize, msg: u32, wParam: usize, lParam: isize) -> isize;
    }

    const BM_CLICK: u32 = 0x00F5;

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
            let view =
                unsafe { MapViewOfFile(handle, FILE_MAP_WRITE, 0, 0, total_size) as *mut u8 };
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
        format!(
            r"Local\PiUnityHarnessState_{}",
            project_key_hash(project_path)
        )
    }

    #[derive(Clone)]
    struct ManagedRequest {
        id: String,
        line: Vec<u8>,
        enqueued_at_ms: i64,
        delivered_at_ms: i64,
        timeout_ms: i64,
        audit_action_id: u64,
        request_type: String,
        action: String,
    }

    struct EditorObservation {
        status: String,
        focus_state: String,
        window_state: String,
    }

    #[derive(Clone, Default)]
    struct ModalWindowInfo {
        hwnd: isize,
        title: String,
        class_name: String,
        buttons: Vec<String>,
    }

    #[derive(Clone, Default)]
    struct ModalObservation {
        present: bool,
        detected_at_ms: i64,
        windows: Vec<ModalWindowInfo>,
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
        next_audit_action_id: AtomicU64,
        audit_io: Mutex<()>,
        modal_observation: Mutex<ModalObservation>,
        yolo_mode: AtomicI32,
        last_auto_click_ms: AtomicI64,
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
                next_audit_action_id: AtomicU64::new((now_ms().max(0) as u64).saturating_mul(1000)),
                audit_io: Mutex::new(()),
                modal_observation: Mutex::new(ModalObservation::default()),
                yolo_mode: AtomicI32::new(YoloMode::Off as i32),
                last_auto_click_ms: AtomicI64::new(0),
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
                "capabilities": CAPABILITIES,
                "modalObservation": self.modal_observation_json(),
                "yoloMode": YoloMode::from_i32(self.yolo_mode.load(Ordering::SeqCst)).as_str()
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

        fn modal_observation_json(&self) -> Value {
            match self.modal_observation.lock() {
                Ok(guard) => {
                    let windows: Vec<Value> = guard
                        .windows
                        .iter()
                        .map(|w| {
                            json!({
                                "hwnd": w.hwnd as u64,
                                "title": w.title,
                                "class": w.class_name,
                                "buttons": w.buttons,
                            })
                        })
                        .collect();
                    json!({
                        "present": guard.present,
                        "detectedAtMs": guard.detected_at_ms,
                        "windows": windows,
                    })
                }
                Err(_) => json!({
                    "present": false,
                    "detectedAtMs": 0,
                    "windows": [],
                }),
            }
        }

        fn refresh_modal_observation(&self) {
            let now = now_ms();
            let mut windows: Vec<ModalWindowInfo> = Vec::new();

            // Enumerate top-level windows
            let ctx_ptr = (&mut windows) as *mut Vec<ModalWindowInfo> as isize;
            unsafe {
                EnumWindows(modal_probe_enum_top_level, ctx_ptr);
            }

            // Filter: visible, owned by our PID, class == "#32770"
            windows.retain(|w| {
                if w.class_name != "#32770" {
                    return false;
                }
                true
            });

            // For each dialog, enumerate child buttons
            for w in &mut windows {
                self.probe_dialog_buttons(w);
            }

            let has_modal = !windows.is_empty();
            if let Ok(mut guard) = self.modal_observation.lock() {
                guard.present = has_modal;
                guard.detected_at_ms = now;
                guard.windows = windows.clone();
            }
            // Safe-auto: try to dismiss whitelisted dialogs
            if has_modal {
                self.try_auto_dismiss(&windows);
            }
            // Also update editor_status to include mainThreadStale when modal
            let editor = self.editor_observation();
            if editor.status == "editing" || editor.status == "playing" {
                let main_thread_stale = self.heartbeat_age_ms(now) > HEARTBEAT_TIMEOUT_MS;
                if main_thread_stale {
                    if let Ok(mut guard) = self.editor_status.lock() {
                        if !guard.contains("mainThreadStale=1") {
                            *guard = format!("{};mainThreadStale=1", *guard);
                        }
                    }
                }
            }
        }

        fn probe_dialog_buttons(&self, window: &mut ModalWindowInfo) {
            let buttons_ptr = &mut window.buttons as *mut Vec<String> as isize;
            unsafe {
                EnumChildWindows(
                    window.hwnd,
                    modal_probe_enum_child_buttons,
                    buttons_ptr,
                );
            }
        }

        fn try_auto_dismiss(&self, windows: &[ModalWindowInfo]) {
            let mode = YoloMode::from_i32(self.yolo_mode.load(Ordering::SeqCst));
            if mode != YoloMode::SafeAuto {
                return;
            }
            let now = now_ms();
            let last_click = self.last_auto_click_ms.load(Ordering::SeqCst);
            if last_click > 0 && now.saturating_sub(last_click) < YOLO_AUTO_CLICK_COOLDOWN_MS {
                return;
            }
            for window in windows {
                if let Some(target_text) = self.resolve_yolo_button(window) {
                    if self.click_button_by_text(window.hwnd, &target_text) {
                        self.last_auto_click_ms.store(now, Ordering::SeqCst);
                        eprintln!(
                            "[pi-unity-native] yolo safe-auto clicked \"{}\" on \"{}\" [{}]",
                            target_text,
                            window.title,
                            window.buttons.join(", ")
                        );
                        return;
                    }
                }
            }
        }

        /// Resolve the safest button to click based on dialog title and button whites.
        fn resolve_yolo_button(&self, window: &ModalWindowInfo) -> Option<String> {
            let title_lower = window.title.to_lowercase();
            let buttons: Vec<String> = window
                .buttons
                .iter()
                .map(|b| b.to_lowercase())
                .collect();

            // Scene-save dialog: prefer "Don't Save" (non-destructive), fallback to "Save"
            if title_lower.contains("scene") && title_lower.contains("modified") {
                if buttons.iter().any(|b| b == "don't save") {
                    return Some("Don't Save".to_string());
                }
                if buttons.iter().any(|b| b == "save") {
                    return Some("Save".to_string());
                }
            }

            // Import settings changed: prefer "Apply"
            if title_lower.contains("import") {
                if buttons.iter().any(|b| b == "apply") {
                    return Some("Apply".to_string());
                }
            }

            // Generic Unity dialog with OK
            if buttons.iter().any(|b| b == "ok") {
                return Some("OK".to_string());
            }

            // Generic yes/no dialog: prefer "Yes"
            if buttons.iter().any(|b| b == "yes") {
                return Some("Yes".to_string());
            }

            // Fallback: first button
            if !window.buttons.is_empty() {
                return Some(window.buttons[0].clone());
            }

            None
        }

        fn click_button_by_text(&self, parent_hwnd: isize, text: &str) -> bool {
            let ctx = FindButtonContext {
                target_text_lower: text.to_lowercase(),
                found_hwnd: std::cell::Cell::new(0isize),
            };
            unsafe {
                EnumChildWindows(
                    parent_hwnd,
                    find_button_by_text_callback,
                    &ctx as *const FindButtonContext as isize,
                );
            }
            let hwnd = ctx.found_hwnd.get();
            if hwnd == 0 {
                return false;
            }
            unsafe {
                SendMessageW(hwnd, BM_CLICK, 0, 0);
            }
            true
        }

        fn reap_timeouts(&self) {
            let now = now_ms();
            let heartbeat_timed_out = self.is_heartbeat_timed_out_at(now);
            let mut lines: Vec<Vec<u8>> = Vec::new();

            if let Ok(mut pending) = self.pending.lock() {
                let mut kept = VecDeque::new();
                while let Some(request) = pending.pop_front() {
                    if now.saturating_sub(request.enqueued_at_ms) > request.timeout_ms {
                        let response =
                            self.error_line(&request.id, "request_timeout_before_dispatch");
                        self.append_audit_completed(&request, &response);
                        lines.push(response);
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
                    if let Some(request) = in_flight.remove(&id) {
                        let error = if heartbeat_timed_out {
                            "managed_heartbeat_timeout"
                        } else {
                            "request_timeout_in_flight"
                        };
                        let response = self.error_line(&id, error);
                        self.append_audit_completed(&request, &response);
                        lines.push(response);
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
                "modalObservation": self.modal_observation_json(),
                "yoloMode": YoloMode::from_i32(self.yolo_mode.load(Ordering::SeqCst)).as_str(),
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
                    let response = self.error_line(&request.id, message);
                    self.append_audit_completed(&request, &response);
                    lines.push(response);
                }
            }
            if let Ok(mut in_flight) = self.in_flight.lock() {
                for (id, request) in in_flight.drain() {
                    let response = self.error_line(&id, message);
                    self.append_audit_completed(&request, &response);
                    lines.push(response);
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
                "status" => {
                    let request = self.start_direct_audit(id, req_type, "status", &value);
                    let response = self.ok_line(id, self.status_payload());
                    self.complete_direct_audit(&request, &response);
                    self.send_line(response);
                }
                "timeline" => {
                    let result = match self.query_timeline(&value) {
                        Ok(result) => result,
                        Err(error) => {
                            self.send_line(self.error_line(id, &error));
                            return;
                        }
                    };
                    self.send_line(self.ok_line(id, result));
                }
                "set_yolo" => {
                    let mode = value
                        .get("payload")
                        .and_then(|p| p.get("mode"))
                        .and_then(|m| m.as_str())
                        .unwrap_or("off");
                    let mode_i32 = match mode {
                        "off" => YoloMode::Off as i32,
                        "safe-auto" => YoloMode::SafeAuto as i32,
                        _ => YoloMode::Detect as i32,
                    };
                    self.yolo_mode.store(mode_i32, Ordering::SeqCst);
                    let request = self.start_direct_audit(id, "set_yolo", "set_yolo", &value);
                    let response = self.ok_line(
                        id,
                        json!({ "yoloMode": YoloMode::from_i32(mode_i32).as_str() }),
                    );
                    self.complete_direct_audit(&request, &response);
                    self.send_line(response);
                }
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
                    let action = request_action(&value);
                    let audit_action_id = self.next_audit_action_id.fetch_add(1, Ordering::Relaxed);
                    let request = ManagedRequest {
                        id: id.to_string(),
                        line: text.as_bytes().to_vec(),
                        enqueued_at_ms: now_ms(),
                        delivered_at_ms: 0,
                        timeout_ms: request_timeout_ms(&value),
                        audit_action_id,
                        request_type: req_type.to_string(),
                        action,
                    };
                    self.append_audit_started(&request, &value);
                    if let Ok(mut pending) = self.pending.lock() {
                        pending.push_back(request);
                    }
                    self.publish_status_snapshot();
                }
            }
        }

        fn poll_request(
            &self,
            buffer: *mut u8,
            buffer_len: i32,
            out_required_len: *mut i32,
        ) -> i32 {
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
            let request = self
                .in_flight
                .lock()
                .ok()
                .and_then(|mut in_flight| in_flight.remove(id));
            let Some(request) = request else {
                self.publish_status_snapshot();
                return;
            };
            self.append_audit_completed(&request, &response);
            let mut line = response;
            if !line.ends_with(b"\n") {
                line.push(b'\n');
            }
            self.send_line(line);
            self.publish_status_snapshot();
        }

        fn start_direct_audit(
            &self,
            id: &str,
            request_type: &str,
            action: &str,
            value: &Value,
        ) -> ManagedRequest {
            let request = ManagedRequest {
                id: id.to_string(),
                line: Vec::new(),
                enqueued_at_ms: now_ms(),
                delivered_at_ms: now_ms(),
                timeout_ms: request_timeout_ms(value),
                audit_action_id: self.next_audit_action_id.fetch_add(1, Ordering::Relaxed),
                request_type: request_type.to_string(),
                action: action.to_string(),
            };
            self.append_audit_started(&request, value);
            request
        }

        fn complete_direct_audit(&self, request: &ManagedRequest, response: &[u8]) {
            self.append_audit_completed(request, response);
        }

        fn append_audit_started(&self, request: &ManagedRequest, value: &Value) {
            let input = audit_input(value);
            self.append_audit_event(&json!({
                "schemaVersion": AUDIT_SCHEMA_VERSION,
                "event": "started",
                "actionId": request.audit_action_id,
                "requestId": request.id,
                "requestType": request.request_type,
                "action": request.action,
                "timestampMs": request.enqueued_at_ms,
                "timestampUtc": timestamp_utc(request.enqueued_at_ms),
                "input": input,
            }));
        }

        fn append_audit_completed(&self, request: &ManagedRequest, response: &[u8]) {
            let completed_at_ms = now_ms();
            let response_value: Value = serde_json::from_slice(trim_ascii(response))
                .unwrap_or_else(|_| json!({ "ok": false, "error": "invalid_managed_response" }));
            let success = response_value
                .get("ok")
                .and_then(Value::as_bool)
                .unwrap_or(false);
            let mut event = json!({
                "schemaVersion": AUDIT_SCHEMA_VERSION,
                "event": "completed",
                "actionId": request.audit_action_id,
                "requestId": request.id,
                "requestType": request.request_type,
                "action": request.action,
                "timestampMs": completed_at_ms,
                "timestampUtc": timestamp_utc(completed_at_ms),
                "durationMs": completed_at_ms.saturating_sub(request.enqueued_at_ms),
                "success": success,
            });
            if let Some(object) = event.as_object_mut() {
                if success {
                    object.insert(
                        "result".to_string(),
                        bounded_value(redact_sensitive(
                            response_value.get("result").cloned().unwrap_or(Value::Null),
                        )),
                    );
                } else {
                    object.insert(
                        "errorType".to_string(),
                        response_value
                            .get("error_type")
                            .cloned()
                            .unwrap_or_else(|| json!("unknown")),
                    );
                    object.insert(
                        "error".to_string(),
                        bounded_value(
                            response_value
                                .get("error")
                                .cloned()
                                .unwrap_or_else(|| json!("unity request failed")),
                        ),
                    );
                }
            }
            self.append_audit_event(&event);
        }

        fn append_audit_event(&self, value: &Value) {
            let _guard = self.audit_io.lock().ok();
            let path = self.audit_file_path();
            if let Some(parent) = path.parent() {
                let _ = fs::create_dir_all(parent);
            }
            if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(path) {
                if let Ok(mut bytes) = serde_json::to_vec(value) {
                    bytes.push(b'\n');
                    let _ = file.write_all(&bytes);
                }
            }
        }

        fn audit_directory(&self) -> PathBuf {
            PathBuf::from(&self.project_path)
                .join("Temp")
                .join("PiUnityHarness")
                .join("ActionTimeline")
        }

        fn audit_file_path(&self) -> PathBuf {
            let directory = self.audit_directory();
            let date = date_utc(now_ms());
            let base = directory.join(format!("{date}.jsonl"));
            if file_len(&base) < AUDIT_FILE_MAX_BYTES {
                return base;
            }
            for index in 1..1000 {
                let path = directory.join(format!("{date}-{index:03}.jsonl"));
                if file_len(&path) < AUDIT_FILE_MAX_BYTES {
                    return path;
                }
            }
            directory.join(format!("{date}-overflow.jsonl"))
        }

        fn query_timeline(&self, value: &Value) -> Result<Value, String> {
            let payload = value.get("payload").unwrap_or(&Value::Null);
            let limit = payload
                .get("limit")
                .and_then(Value::as_u64)
                .unwrap_or(AUDIT_QUERY_DEFAULT_LIMIT as u64)
                .clamp(1, AUDIT_QUERY_MAX_LIMIT as u64) as usize;
            let request_type = payload
                .get("requestType")
                .and_then(Value::as_str)
                .unwrap_or("");
            let action = payload.get("action").and_then(Value::as_str).unwrap_or("");
            let success_filter = payload
                .get("success")
                .and_then(Value::as_str)
                .unwrap_or("all");
            let success = match success_filter.to_ascii_lowercase().as_str() {
                "" | "all" => None,
                "success" | "true" => Some(true),
                "failure" | "false" => Some(false),
                _ => return Err("timeline success must be all, success, or failure".to_string()),
            };

            let _guard = self.audit_io.lock().ok();
            let mut files: Vec<PathBuf> = fs::read_dir(self.audit_directory())
                .map(|entries| {
                    entries
                        .filter_map(Result::ok)
                        .map(|entry| entry.path())
                        .filter(|path| {
                            path.extension().and_then(|ext| ext.to_str()) == Some("jsonl")
                        })
                        .collect()
                })
                .unwrap_or_default();
            files.sort_by(|a, b| file_modified_ms(b).cmp(&file_modified_ms(a)));

            let mut events = Vec::new();
            for path in files {
                if events.len() >= AUDIT_QUERY_MAX_EVENTS {
                    break;
                }
                read_recent_jsonl_events(&path, AUDIT_QUERY_MAX_EVENTS - events.len(), &mut events);
            }
            events.sort_by_key(event_timestamp_ms);

            let mut actions: HashMap<String, Value> = HashMap::new();
            let mut order: Vec<String> = Vec::new();
            for event in events {
                merge_timeline_event(&mut actions, &mut order, event);
            }

            let mut filtered: Vec<Value> = order
                .into_iter()
                .filter_map(|key| actions.remove(&key))
                .filter(|item| timeline_matches(item, request_type, action, success))
                .collect();
            filtered.sort_by(|a, b| activity_timestamp_ms(b).cmp(&activity_timestamp_ms(a)));
            filtered.truncate(limit);

            let captured_at_ms = now_ms();
            Ok(json!({
                "schemaVersion": AUDIT_SCHEMA_VERSION,
                "capturedAtMs": captured_at_ms,
                "capturedAtUtc": timestamp_utc(captured_at_ms),
                "directory": self.audit_directory().to_string_lossy().replace('\\', "/"),
                "count": filtered.len(),
                "actions": filtered,
            }))
        }
    }

    fn merge_timeline_event(
        actions: &mut HashMap<String, Value>,
        order: &mut Vec<String>,
        event: Value,
    ) {
        let request_id = event.get("requestId").and_then(Value::as_str).unwrap_or("");
        if request_id.is_empty() {
            return;
        }
        let action_id = event.get("actionId").and_then(Value::as_u64).unwrap_or(0);
        let key = format!("{action_id}:{request_id}");
        if !actions.contains_key(&key) {
            actions.insert(
                key.clone(),
                json!({
                    "actionId": action_id,
                    "requestId": request_id,
                    "status": "pending"
                }),
            );
            order.push(key.clone());
        }
        let Some(item) = actions.get_mut(&key).and_then(Value::as_object_mut) else {
            return;
        };
        copy_json_field(&event, item, "requestType", "requestType");
        copy_json_field(&event, item, "action", "action");
        match event.get("event").and_then(Value::as_str) {
            Some("started") => {
                copy_json_field(&event, item, "timestampUtc", "startedAtUtc");
                copy_json_field(&event, item, "timestampMs", "startedAtMs");
                copy_json_field(&event, item, "input", "input");
            }
            Some("completed") => {
                item.insert("status".to_string(), json!("completed"));
                copy_json_field(&event, item, "timestampUtc", "completedAtUtc");
                copy_json_field(&event, item, "timestampMs", "completedAtMs");
                copy_json_field(&event, item, "durationMs", "durationMs");
                copy_json_field(&event, item, "success", "success");
                copy_json_field(&event, item, "result", "result");
                copy_json_field(&event, item, "errorType", "errorType");
                copy_json_field(&event, item, "error", "error");
            }
            _ => {}
        }
    }

    fn read_recent_jsonl_events(path: &PathBuf, limit: usize, events: &mut Vec<Value>) {
        let Ok(file) = fs::File::open(path) else {
            return;
        };
        let Ok(length) = file.metadata().map(|metadata| metadata.len()) else {
            return;
        };
        let mut reader = std::io::BufReader::new(file);
        let mut position = length;
        let mut carry = Vec::new();
        const CHUNK_SIZE: usize = 64 * 1024;

        while position > 0 && events.len() < limit {
            let read_size = usize::try_from(position.min(CHUNK_SIZE as u64)).unwrap_or(CHUNK_SIZE);
            position -= read_size as u64;
            if reader.seek(SeekFrom::Start(position)).is_err() {
                return;
            }
            let mut chunk = vec![0u8; read_size];
            if reader.read_exact(&mut chunk).is_err() {
                return;
            }
            chunk.extend_from_slice(&carry);
            let mut end = chunk.len();
            for index in (0..chunk.len()).rev() {
                if chunk[index] != b'\n' {
                    continue;
                }
                if index + 1 < end {
                    let line = trim_ascii(&chunk[index + 1..end]);
                    if !line.is_empty() {
                        if let Ok(event) = serde_json::from_slice::<Value>(line) {
                            events.push(event);
                            if events.len() >= limit {
                                return;
                            }
                        }
                    }
                }
                end = index;
            }
            carry = chunk[..end].to_vec();
        }

        if events.len() < limit && !carry.is_empty() {
            if let Ok(event) = serde_json::from_slice::<Value>(trim_ascii(&carry)) {
                events.push(event);
            }
        }
    }

    fn event_timestamp_ms(event: &Value) -> i64 {
        event
            .get("timestampMs")
            .and_then(Value::as_i64)
            .unwrap_or(0)
    }

    fn request_action(value: &Value) -> String {
        let request_type = value
            .get("type")
            .and_then(Value::as_str)
            .unwrap_or("unknown");
        if request_type == "command" {
            if let Some(name) = value
                .get("payload")
                .and_then(|payload| payload.get("name"))
                .and_then(Value::as_str)
            {
                return name.to_string();
            }
        }
        request_type.to_string()
    }

    fn audit_input(value: &Value) -> Value {
        let request_type = value
            .get("type")
            .and_then(Value::as_str)
            .unwrap_or("unknown");
        let payload = value.get("payload").unwrap_or(&Value::Null);
        match request_type {
            "execute_code" | "validate_execute_code" | "validate_code" => {
                let code = payload.get("code").and_then(Value::as_str).unwrap_or("");
                json!({
                    "codeLength": code.chars().count(),
                    "codeRecorded": false,
                })
            }
            "execute_file" | "validate_execute_file" | "validate_file" => json!({
                "filePath": payload.get("filePath").cloned().unwrap_or(Value::Null)
            }),
            "command" => {
                let parameters_json = payload
                    .get("parametersJson")
                    .and_then(Value::as_str)
                    .unwrap_or("{}");
                let parameters = serde_json::from_str::<Value>(parameters_json)
                    .unwrap_or_else(|_| bounded_text(parameters_json));
                json!({
                    "command": payload.get("name").cloned().unwrap_or(Value::Null),
                    "parameters": bounded_value(redact_sensitive(parameters)),
                })
            }
            "context_snapshot" => bounded_value(redact_sensitive(payload.clone())),
            _ => json!({}),
        }
    }

    fn bounded_text(value: &str) -> Value {
        let char_count = value.chars().count();
        if char_count <= AUDIT_VALUE_MAX_CHARS {
            return json!(value);
        }
        truncated_preview(value, char_count)
    }

    fn bounded_value(value: Value) -> Value {
        let Ok(text) = serde_json::to_string(&value) else {
            return Value::Null;
        };
        let char_count = text.chars().count();
        if char_count <= AUDIT_VALUE_MAX_CHARS {
            return value;
        }
        truncated_preview(&text, char_count)
    }

    fn truncated_preview(text: &str, char_count: usize) -> Value {
        let preview: String = text.chars().take(AUDIT_VALUE_MAX_CHARS).collect();
        json!({
            "preview": preview,
            "truncated": true,
            "originalLength": char_count,
        })
    }

    fn redact_sensitive(value: Value) -> Value {
        match value {
            Value::Object(mut object) => {
                for (key, item) in object.iter_mut() {
                    if is_sensitive_key(key) {
                        *item = json!("[REDACTED]");
                    } else {
                        *item = redact_sensitive(item.take());
                    }
                }
                Value::Object(object)
            }
            Value::Array(items) => Value::Array(items.into_iter().map(redact_sensitive).collect()),
            other => other,
        }
    }

    fn is_sensitive_key(key: &str) -> bool {
        const SENSITIVE_KEYS: &[&str] = &[
            "token",
            "accesstoken",
            "refreshtoken",
            "apikey",
            "password",
            "secret",
            "authorization",
            "credential",
            "credentials",
        ];
        let normalized: String = key
            .chars()
            .filter(|character| character.is_ascii_alphanumeric())
            .flat_map(|character| character.to_lowercase())
            .collect();
        SENSITIVE_KEYS.contains(&normalized.as_str())
    }

    fn copy_json_field(
        source: &Value,
        target: &mut serde_json::Map<String, Value>,
        source_name: &str,
        target_name: &str,
    ) {
        if let Some(value) = source.get(source_name) {
            target.insert(target_name.to_string(), value.clone());
        }
    }

    fn timeline_matches(
        item: &Value,
        request_type: &str,
        action: &str,
        success: Option<bool>,
    ) -> bool {
        field_eq_ignore_case(item, "requestType", request_type)
            && field_eq_ignore_case(item, "action", action)
            && success.map_or(true, |expected| {
                item.get("success").and_then(Value::as_bool) == Some(expected)
            })
    }

    fn field_eq_ignore_case(item: &Value, field: &str, expected: &str) -> bool {
        expected.is_empty()
            || item
                .get(field)
                .and_then(Value::as_str)
                .map(|value| value.eq_ignore_ascii_case(expected))
                .unwrap_or(false)
    }

    fn activity_timestamp_ms(item: &Value) -> i64 {
        item.get("completedAtMs")
            .and_then(Value::as_i64)
            .or_else(|| item.get("startedAtMs").and_then(Value::as_i64))
            .unwrap_or(0)
    }

    fn file_len(path: &PathBuf) -> u64 {
        fs::metadata(path)
            .map(|metadata| metadata.len())
            .unwrap_or(0)
    }

    fn file_modified_ms(path: &PathBuf) -> u128 {
        fs::metadata(path)
            .and_then(|metadata| metadata.modified())
            .ok()
            .and_then(|modified| modified.duration_since(UNIX_EPOCH).ok())
            .map(|duration| duration.as_millis())
            .unwrap_or(0)
    }

    fn timestamp_utc(timestamp_ms: i64) -> String {
        let (year, month, day, hour, minute, second, millis) = utc_parts(timestamp_ms);
        format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{millis:03}Z")
    }

    fn date_utc(timestamp_ms: i64) -> String {
        let (year, month, day, _, _, _, _) = utc_parts(timestamp_ms);
        format!("{year:04}-{month:02}-{day:02}")
    }

    fn utc_parts(timestamp_ms: i64) -> (i64, u32, u32, u32, u32, u32, u32) {
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

    // Howard Hinnant's civil_from_days algorithm: converts a Unix day count to a Gregorian date.
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

    // ── Modal probe helpers ────────────────────────────────────────────

    unsafe extern "system" fn modal_probe_enum_top_level(hwnd: isize, lparam: isize) -> i32 {
        if hwnd == 0 {
            return 1;
        }
        let windows = &mut *(lparam as *mut Vec<ModalWindowInfo>);
        if IsWindowVisible(hwnd) == 0 {
            return 1;
        }
        let mut pid: u32 = 0;
        GetWindowThreadProcessId(hwnd, &mut pid as *mut u32);
        if pid != std::process::id() {
            return 1;
        }
        let class_name = read_window_class(hwnd);
        if class_name.is_empty() {
            return 1;
        }
        let title = read_window_text(hwnd);
        windows.push(ModalWindowInfo {
            hwnd,
            title,
            class_name,
            buttons: Vec::new(),
        });
        1
    }

    unsafe extern "system" fn modal_probe_enum_child_buttons(hwnd: isize, lparam: isize) -> i32 {
        if hwnd == 0 {
            return 1;
        }
        let class_name = read_window_class(hwnd);
        // Common button class names in Win32 dialogs
        let is_button = class_name.eq_ignore_ascii_case("button");
        if !is_button {
            return 1;
        }
        if IsWindowVisible(hwnd) == 0 {
            return 1;
        }
        let text = read_window_text(hwnd);
        if text.is_empty() {
            return 1;
        }
        let buttons = &mut *(lparam as *mut Vec<String>);
        buttons.push(text);
        1
    }

    fn read_window_text(hwnd: isize) -> String {
        unsafe {
            let len = GetWindowTextLengthW(hwnd);
            if len <= 0 {
                return String::new();
            }
            let mut buf: Vec<u16> = vec![0u16; (len as usize) + 1];
            let copied = GetWindowTextW(hwnd, buf.as_mut_ptr(), buf.len() as i32);
            if copied <= 0 {
                return String::new();
            }
            String::from_utf16_lossy(&buf[..copied as usize])
        }
    }

    fn read_window_class(hwnd: isize) -> String {
        unsafe {
            let mut buf: Vec<u16> = vec![0u16; 128];
            let copied = GetClassNameW(hwnd, buf.as_mut_ptr(), buf.len() as i32);
            if copied <= 0 {
                return String::new();
            }
            String::from_utf16_lossy(&buf[..copied as usize])
        }
    }

    struct FindButtonContext {
        target_text_lower: String,
        found_hwnd: std::cell::Cell<isize>,
    }

    unsafe extern "system" fn find_button_by_text_callback(hwnd: isize, lparam: isize) -> i32 {
        if hwnd == 0 {
            return 1;
        }
        let ctx = &*(lparam as *const FindButtonContext);
        let class_name = read_window_class(hwnd);
        if !class_name.eq_ignore_ascii_case("button") {
            return 1;
        }
        if IsWindowVisible(hwnd) == 0 {
            return 1;
        }
        let text = read_window_text(hwnd);
        if text.to_lowercase() == ctx.target_text_lower {
            ctx.found_hwnd.set(hwnd);
            return 0; // stop enumeration
        }
        1
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
        // Background modal probe task: independently detect Unity dialogs.
        let broker_clone = broker.clone();
        tokio::spawn(async move {
            loop {
                tokio::time::sleep(Duration::from_millis(MODAL_PROBE_INTERVAL_MS as u64)).await;
                broker_clone.refresh_modal_observation();
            }
        });
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

    pub fn init(
        project_path: String,
        pipe_name: String,
        token: String,
        protocol_version: i32,
    ) -> i32 {
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
            }) {
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
                audit_action_id: 1,
                request_type: "execute_code".to_string(),
                action: "execute_code".to_string(),
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
        fn status_request_is_audited_by_native_broker() {
            let broker = test_broker("status_audit");
            let (tx, mut rx) = mpsc::channel(4);
            *broker.writer.lock().unwrap() = Some(tx);
            broker.handle_line(br#"{"id":"status-1","type":"status","token":"token"}"#);

            let response = response_json(rx.try_recv().unwrap());
            assert_eq!(response["ok"], true);
            let timeline = broker
                .query_timeline(&json!({ "payload": { "requestType": "status" } }))
                .unwrap();
            assert_eq!(timeline["count"], 1);
            assert_eq!(timeline["actions"][0]["status"], "completed");
        }

        #[test]
        fn timeline_query_is_not_added_to_audit_log() {
            let broker = test_broker("timeline_no_recursion");
            let (tx, mut rx) = mpsc::channel(4);
            *broker.writer.lock().unwrap() = Some(tx);
            broker.handle_line(
                br#"{"id":"timeline-1","type":"timeline","token":"token","payload":{"limit":10}}"#,
            );

            let response = response_json(rx.try_recv().unwrap());
            assert_eq!(response["ok"], true);
            assert_eq!(response["result"]["count"], 0);
            assert!(!broker.audit_directory().exists());
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
                audit_action_id: 1,
                request_type: "execute_code".to_string(),
                action: "execute_code".to_string(),
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
                    audit_action_id: 1,
                    request_type: "execute_code".to_string(),
                    action: "execute_code".to_string(),
                },
            );

            broker.reap_timeouts();
            assert_eq!(broker.in_flight.lock().unwrap().len(), 0);
        }

        #[test]
        fn timeline_merges_started_and_completed_events() {
            let broker = test_broker("timeline_merge");
            let request_value = json!({
                "id": "req-1",
                "type": "command",
                "payload": { "name": "scene_get_data", "parametersJson": "{}" }
            });
            let request =
                broker.start_direct_audit("req-1", "command", "scene_get_data", &request_value);
            broker.complete_direct_audit(
                &request,
                br#"{"reply_to":"req-1","ok":true,"result":{"value":42}}"#,
            );

            let result = broker
                .query_timeline(&json!({ "payload": { "limit": 10 } }))
                .unwrap();
            assert_eq!(result["count"], 1);
            assert_eq!(result["actions"][0]["requestId"], "req-1");
            assert_eq!(result["actions"][0]["action"], "scene_get_data");
            assert_eq!(result["actions"][0]["status"], "completed");
            assert_eq!(result["actions"][0]["success"], true);
            assert_eq!(result["actions"][0]["result"]["value"], 42);
        }

        #[test]
        fn timeline_filters_failures_without_auditing_query() {
            let broker = test_broker("timeline_filter");
            let success = broker.start_direct_audit("1", "status", "status", &json!({}));
            broker.complete_direct_audit(&success, br#"{"ok":true,"result":{}}"#);
            let failure = broker.start_direct_audit("2", "command", "bad_command", &json!({}));
            broker.complete_direct_audit(
                &failure,
                br#"{"ok":false,"error_type":"command_error","error":"boom"}"#,
            );

            let result = broker
                .query_timeline(&json!({
                    "payload": { "limit": 10, "success": "failure" }
                }))
                .unwrap();
            assert_eq!(result["count"], 1);
            assert_eq!(result["actions"][0]["requestId"], "2");
            assert_eq!(result["actions"][0]["errorType"], "command_error");
        }

        #[test]
        fn audit_input_excludes_token_and_raw_code() {
            let code = "x".repeat(AUDIT_VALUE_MAX_CHARS + 10);
            let input = audit_input(&json!({
                "type": "execute_code",
                "token": "secret-token",
                "payload": { "code": code }
            }));

            assert!(input.get("token").is_none());
            assert!(input.get("code").is_none());
            assert_eq!(input["codeLength"], AUDIT_VALUE_MAX_CHARS + 10);
            assert_eq!(input["codeRecorded"], false);
        }

        #[test]
        fn audit_redacts_sensitive_pipeline_parameters_and_results() {
            let input = audit_input(&json!({
                "type": "command",
                "payload": {
                    "name": "provider_configure",
                    "parametersJson": "{\"apiKey\":\"input-secret\",\"nested\":{\"password\":\"pw\"},\"safe\":42}"
                }
            }));
            assert_eq!(input["parameters"]["apiKey"], "[REDACTED]");
            assert_eq!(input["parameters"]["nested"]["password"], "[REDACTED]");
            assert_eq!(input["parameters"]["safe"], 42);

            let redacted = redact_sensitive(json!({
                "access_token": "result-secret",
                "items": [{ "authorization": "Bearer secret", "name": "safe" }]
            }));
            assert_eq!(redacted["access_token"], "[REDACTED]");
            assert_eq!(redacted["items"][0]["authorization"], "[REDACTED]");
            assert_eq!(redacted["items"][0]["name"], "safe");
        }

        #[test]
        fn reads_only_recent_jsonl_events_from_large_file() {
            let broker = test_broker("recent_jsonl");
            let directory = broker.audit_directory();
            fs::create_dir_all(&directory).unwrap();
            let path = directory.join("2026-01-01.jsonl");
            let mut text = String::new();
            for index in 0..100 {
                text.push_str(&json!({ "timestampMs": index, "requestId": index }).to_string());
                text.push('\n');
            }
            fs::write(&path, text).unwrap();

            let mut events = Vec::new();
            read_recent_jsonl_events(&path, 3, &mut events);

            assert_eq!(events.len(), 3);
            assert_eq!(events[0]["requestId"], 99);
            assert_eq!(events[1]["requestId"], 98);
            assert_eq!(events[2]["requestId"], 97);
        }

        #[test]
        fn utc_timestamp_and_date_are_iso_formatted() {
            assert_eq!(timestamp_utc(0), "1970-01-01T00:00:00.000Z");
            assert_eq!(date_utc(0), "1970-01-01");
            assert_eq!(timestamp_utc(951_782_400_123), "2000-02-29T00:00:00.123Z");
            assert_eq!(date_utc(951_782_400_123), "2000-02-29");
        }
    }
