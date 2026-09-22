use std::ffi::c_void;

pub(super) type Bool = i32;
pub(super) type Dword = u32;
pub(super) type Handle = *mut c_void;

pub(super) const INVALID_HANDLE_VALUE: Handle = -1isize as Handle;
pub(super) const PAGE_READWRITE: Dword = 0x04;
pub(super) const FILE_MAP_WRITE: Dword = 0x0002;
pub(super) const BM_CLICK: u32 = 0x00F5;

unsafe extern "system" {
    pub(super) fn CreateFileMappingW(hFile: Handle, lpFileMappingAttributes: *mut c_void, flProtect: Dword, dwMaximumSizeHigh: Dword, dwMaximumSizeLow: Dword, lpName: *const u16) -> Handle;
    pub(super) fn MapViewOfFile(hFileMappingObject: Handle, dwDesiredAccess: Dword, dwFileOffsetHigh: Dword, dwFileOffsetLow: Dword, dwNumberOfBytesToMap: usize) -> *mut c_void;
    pub(super) fn UnmapViewOfFile(lpBaseAddress: *const c_void) -> Bool;
    pub(super) fn CloseHandle(hObject: Handle) -> Bool;
}

#[link(name = "user32")]
unsafe extern "system" {
    pub(super) fn EnumWindows(lpEnumFunc: unsafe extern "system" fn(isize, isize) -> i32, lParam: isize) -> i32;
    pub(super) fn GetWindowThreadProcessId(hWnd: isize, lpdwProcessId: *mut u32) -> u32;
    pub(super) fn IsWindowVisible(hWnd: isize) -> i32;
    pub(super) fn GetClassNameW(hWnd: isize, lpClassName: *mut u16, nMaxCount: i32) -> i32;
    pub(super) fn GetWindowTextW(hWnd: isize, lpString: *mut u16, nMaxCount: i32) -> i32;
    pub(super) fn GetWindowTextLengthW(hWnd: isize) -> i32;
    pub(super) fn EnumChildWindows(hWnd: isize, lpEnumFunc: unsafe extern "system" fn(isize, isize) -> i32, lParam: isize) -> i32;
    pub(super) fn SendMessageW(hWnd: isize, msg: u32, wParam: usize, lParam: isize) -> isize;
}
