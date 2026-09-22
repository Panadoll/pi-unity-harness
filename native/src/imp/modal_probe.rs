use super::windows::{EnumChildWindows, EnumWindows, GetClassNameW, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsWindowVisible};

#[derive(Clone, Default)]
pub(super) struct ModalWindowInfo { pub hwnd: isize, pub title: String, pub class_name: String, pub buttons: Vec<String> }
#[derive(Clone, Default)]
pub(super) struct ModalObservation { pub present: bool, pub detected_at_ms: i64, pub windows: Vec<ModalWindowInfo> }

pub(super) fn refresh() -> Vec<ModalWindowInfo> {
    let mut windows: Vec<ModalWindowInfo> = Vec::new();
    unsafe { EnumWindows(enum_top_level, &mut windows as *mut Vec<ModalWindowInfo> as isize); }
    windows.retain(|window| window.class_name == "#32770");
    for window in &mut windows { unsafe { EnumChildWindows(window.hwnd, enum_child_buttons, &mut window.buttons as *mut Vec<String> as isize); } }
    windows
}

unsafe extern "system" fn enum_top_level(hwnd: isize, lparam: isize) -> i32 {
    if hwnd == 0 || IsWindowVisible(hwnd) == 0 { return 1; }
    let mut pid = 0; GetWindowThreadProcessId(hwnd, &mut pid);
    if pid != std::process::id() { return 1; }
    let class_name = read_text(hwnd, true); if class_name.is_empty() { return 1; }
    let title = read_text(hwnd, false);
    (*(lparam as *mut Vec<ModalWindowInfo>)).push(ModalWindowInfo { hwnd, title, class_name, buttons: Vec::new() }); 1
}
unsafe extern "system" fn enum_child_buttons(hwnd: isize, lparam: isize) -> i32 {
    if hwnd == 0 || IsWindowVisible(hwnd) == 0 || !read_text(hwnd, true).eq_ignore_ascii_case("button") { return 1; }
    let text = read_text(hwnd, false); if !text.is_empty() { (*(lparam as *mut Vec<String>)).push(text); } 1
}
unsafe fn read_text(hwnd: isize, class_name: bool) -> String {
    let mut buffer = vec![0u16; if class_name { 128 } else { GetWindowTextLengthW(hwnd).max(0) as usize + 1 }];
    let copied = if class_name { GetClassNameW(hwnd, buffer.as_mut_ptr(), buffer.len() as i32) } else { GetWindowTextW(hwnd, buffer.as_mut_ptr(), buffer.len() as i32) };
    if copied <= 0 { String::new() } else { String::from_utf16_lossy(&buffer[..copied as usize]) }
}
