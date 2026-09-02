use std::os::raw::c_int;

use super::{slice_from_raw, string_from_raw};

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
        return crate::imp::init(
            string_from_raw(project, project_len),
            string_from_raw(pipe, pipe_len),
            string_from_raw(token, token_len),
            protocol_version,
        );
    }
    #[cfg(not(windows))]
    {
        let _ = (
            project,
            project_len,
            pipe,
            pipe_len,
            token,
            token_len,
            protocol_version,
        );
        -1
    }
}

#[no_mangle]
pub extern "C" fn pi_unity_shutdown() {
    #[cfg(windows)]
    crate::imp::shutdown();
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
        crate::imp::set_managed_state(state, generation, status);
    }
    #[cfg(not(windows))]
    {
        let _ = (state, generation, editor_status, editor_status_len);
    }
}

#[no_mangle]
pub extern "C" fn pi_unity_managed_heartbeat(generation: i64) {
    #[cfg(windows)]
    crate::imp::managed_heartbeat(generation);
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
        crate::imp::poll_request(buffer, buffer_len, out_required_len)
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
        crate::imp::complete_request(
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
        crate::imp::emit_event(
            &string_from_raw(event_type, event_type_len),
            string_from_raw(payload, payload_len),
        );
    }
    #[cfg(not(windows))]
    {
        let _ = (event_type, event_type_len, payload, payload_len);
    }
}
