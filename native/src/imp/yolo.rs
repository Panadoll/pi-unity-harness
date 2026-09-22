use std::cell::Cell;
use super::modal_probe::ModalWindowInfo;
use super::windows::{EnumChildWindows, GetClassNameW, GetWindowTextLengthW, GetWindowTextW, IsWindowVisible, SendMessageW, BM_CLICK};

pub(super) const AUTO_CLICK_COOLDOWN_MS: i64 = 8_000;
#[derive(Clone, Copy, PartialEq)]
pub(super) enum YoloMode { Off = 0, Detect = 1, SafeAuto = 2 }
impl YoloMode {
    pub(super) fn from_i32(value: i32) -> Self { match value { 1 => Self::Detect, 2 => Self::SafeAuto, _ => Self::Off } }
    pub(super) fn as_str(&self) -> &'static str { match self { Self::Off => "off", Self::Detect => "detect", Self::SafeAuto => "safe-auto" } }
}

pub(super) fn try_auto_dismiss(mode: YoloMode, last_click_ms: i64, now: i64, windows: &[ModalWindowInfo]) -> Option<i64> {
    if mode != YoloMode::SafeAuto || (last_click_ms > 0 && now.saturating_sub(last_click_ms) < AUTO_CLICK_COOLDOWN_MS) { return None; }
    for window in windows { if let Some(text) = resolve_yolo_button(window) { if click_button_by_text(window.hwnd, &text) { return Some(now); } } }
    None
}

pub(super) fn resolve_yolo_button(window: &ModalWindowInfo) -> Option<String> {
    let title = window.title.to_lowercase(); let buttons: Vec<String> = window.buttons.iter().map(|button| button.to_lowercase()).collect();
    if title.contains("scene") && title.contains("modified") {
        if buttons.iter().any(|button| button == "don't save") { return Some("Don't Save".into()); }
        if buttons.iter().any(|button| button == "save") { return Some("Save".into()); }
    }
    if title.contains("import") && buttons.iter().any(|button| button == "apply") { return Some("Apply".into()); }
    if buttons.iter().any(|button| button == "ok") { return Some("OK".into()); }
    if buttons.iter().any(|button| button == "yes") { return Some("Yes".into()); }
    window.buttons.first().cloned()
}

struct FindButtonContext { target: String, found: Cell<isize> }
fn click_button_by_text(parent: isize, text: &str) -> bool {
    let context = FindButtonContext { target: text.to_lowercase(), found: Cell::new(0) };
    unsafe { EnumChildWindows(parent, find_button, &context as *const FindButtonContext as isize); }
    let hwnd = context.found.get(); if hwnd == 0 { false } else { unsafe { SendMessageW(hwnd, BM_CLICK, 0, 0); } true }
}
unsafe extern "system" fn find_button(hwnd: isize, lparam: isize) -> i32 {
    if hwnd == 0 || IsWindowVisible(hwnd) == 0 { return 1; }
    let mut class = vec![0u16; 128]; let n = GetClassNameW(hwnd, class.as_mut_ptr(), class.len() as i32); if n <= 0 || !String::from_utf16_lossy(&class[..n as usize]).eq_ignore_ascii_case("button") { return 1; }
    let len = GetWindowTextLengthW(hwnd); let mut text = vec![0u16; len.max(0) as usize + 1]; let n = GetWindowTextW(hwnd, text.as_mut_ptr(), text.len() as i32);
    let context = &*(lparam as *const FindButtonContext); if n > 0 && String::from_utf16_lossy(&text[..n as usize]).to_lowercase() == context.target { context.found.set(hwnd); return 0; } 1
}
