pub const MANAGED_STATE_INITIALIZING: i32 = 0;
pub const MANAGED_STATE_READY: i32 = 1;
pub const MANAGED_STATE_RELOADING: i32 = 2;
pub const MANAGED_STATE_QUITTING: i32 = 3;
pub const NATIVE_PROTOCOL_VERSION: i32 = 1;

pub const BUILD_INFO_JSON: &str = concat!(
    "{\"crate\":\"", env!("CARGO_PKG_VERSION"),
    "\",\"gitRev\":\"", env!("PI_UNITY_GIT_REV"),
    "\",\"dirty\":", env!("PI_UNITY_GIT_DIRTY"),
    ",\"protocol\":", env!("PI_UNITY_PROTOCOL_VERSION"),
    ",\"builtUtc\":\"", env!("PI_UNITY_BUILD_UTC"), "\"}"
);

#[used]
#[no_mangle]
pub static PIUH_BUILD_INFO_MARKER: &[u8] = concat!(
    "PIUH_BUILD_INFO:{\"crate\":\"", env!("CARGO_PKG_VERSION"),
    "\",\"gitRev\":\"", env!("PI_UNITY_GIT_REV"),
    "\",\"dirty\":", env!("PI_UNITY_GIT_DIRTY"),
    ",\"protocol\":", env!("PI_UNITY_PROTOCOL_VERSION"),
    ",\"builtUtc\":\"", env!("PI_UNITY_BUILD_UTC"), "\"}\\0"
).as_bytes();

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
mod imp;

mod ffi;
