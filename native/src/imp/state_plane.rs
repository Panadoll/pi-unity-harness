//! State plane layout from docs/protocol.md §8.
//! Header offsets: magic @0, version @4, slots @6, slot size @8, PID @12,
//! writer sequence @16, creation time @24. Slots begin at @64; each slot has
//! seq @0, observed time @8, payload length @16, payload @24.
//! Writers commit a slot by clearing its seq, writing metadata and payload,
//! then publishing the slot seq and header sequence last; readers use the
//! double-slot sequence to avoid torn snapshots.

use std::ffi::c_void;
use std::ptr::null_mut;
use std::os::windows::ffi::OsStrExt;
use std::ffi::OsStr;

use super::windows::{CloseHandle, CreateFileMappingW, MapViewOfFile, UnmapViewOfFile, Dword, Handle, FILE_MAP_WRITE, INVALID_HANDLE_VALUE, PAGE_READWRITE};

pub(super) const STATE_PLANE_MAGIC: u32 = 0x4855_4950;
pub(super) const STATE_PLANE_VERSION: u16 = 1;
pub(super) const STATE_PLANE_HEADER_SIZE: usize = 64;
pub(super) const STATE_PLANE_SLOT_COUNT: usize = 2;
pub(super) const STATE_PLANE_SLOT_SIZE: usize = 64 * 1024;
pub(super) const STATE_PLANE_SLOT_PAYLOAD_OFFSET: usize = 24;
pub(super) const STATE_PLANE_MAX_PAYLOAD: usize = STATE_PLANE_SLOT_SIZE - STATE_PLANE_SLOT_PAYLOAD_OFFSET;

pub(super) struct StatePlane { handle: Handle, view: *mut u8, total_size: usize, seq: u64 }
unsafe impl Send for StatePlane {}

impl StatePlane {
    pub(super) fn create(mapping_name: &str) -> Option<Self> {
        let total_size = STATE_PLANE_HEADER_SIZE.saturating_add(STATE_PLANE_SLOT_COUNT.saturating_mul(STATE_PLANE_SLOT_SIZE));
        let wide_name: Vec<u16> = OsStr::new(mapping_name).encode_wide().chain(Some(0)).collect();
        let handle = unsafe { CreateFileMappingW(INVALID_HANDLE_VALUE, null_mut(), PAGE_READWRITE, 0, total_size.min(u32::MAX as usize) as Dword, wide_name.as_ptr()) };
        if handle.is_null() { return None; }
        let view = unsafe { MapViewOfFile(handle, FILE_MAP_WRITE, 0, 0, total_size) as *mut u8 };
        if view.is_null() { unsafe { CloseHandle(handle); } return None; }
        let mut plane = Self { handle, view, total_size, seq: 0 };
        plane.write_header();
        Some(plane)
    }
    fn write_header(&mut self) { self.write_u32(0, STATE_PLANE_MAGIC); self.write_u16(4, STATE_PLANE_VERSION); self.write_u16(6, STATE_PLANE_SLOT_COUNT as u16); self.write_u32(8, STATE_PLANE_SLOT_SIZE as u32); self.write_u32(12, std::process::id()); self.write_u64(16, 0); self.write_u64(24, super::now_ms().max(0) as u64); }
    pub(super) fn write_json(&mut self, observed_at_ms: i64, payload_json: &str) {
        let bytes = payload_json.as_bytes(); if bytes.is_empty() || bytes.len() > STATE_PLANE_MAX_PAYLOAD { return; }
        self.seq = self.seq.saturating_add(1).max(1); let slot = ((self.seq - 1) as usize) % STATE_PLANE_SLOT_COUNT; let offset = STATE_PLANE_HEADER_SIZE + slot * STATE_PLANE_SLOT_SIZE;
        self.write_u64(offset, 0); self.write_u64(offset + 8, observed_at_ms.max(0) as u64); self.write_u32(offset + 16, bytes.len() as u32); self.write_u32(offset + 20, 0); self.write_bytes(offset + STATE_PLANE_SLOT_PAYLOAD_OFFSET, bytes); self.write_u64(offset, self.seq); self.write_u64(16, self.seq); self.write_u64(24, observed_at_ms.max(0) as u64);
    }
    fn write_bytes(&mut self, offset: usize, bytes: &[u8]) { if offset.saturating_add(bytes.len()) <= self.total_size { unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), self.view.add(offset), bytes.len()); } } }
    fn write_u16(&mut self, offset: usize, value: u16) { self.write_bytes(offset, &value.to_le_bytes()); }
    fn write_u32(&mut self, offset: usize, value: u32) { self.write_bytes(offset, &value.to_le_bytes()); }
    fn write_u64(&mut self, offset: usize, value: u64) { self.write_bytes(offset, &value.to_le_bytes()); }
}
impl Drop for StatePlane { fn drop(&mut self) { unsafe { UnmapViewOfFile(self.view as *const c_void); CloseHandle(self.handle); } } }
