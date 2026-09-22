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
        fn lifecycle_snapshot_derives_modal_stale_responsive_and_unknown() {
            let broker = test_broker("lifecycle");
            broker.set_managed_state(MANAGED_STATE_READY, 3, Some("editing".to_string()));
            broker.connected.store(true, Ordering::SeqCst);
            assert_eq!(broker.derive_lifecycle(now_ms()).main_thread, "responsive");

            broker.last_heartbeat_ms.store(now_ms() - HEARTBEAT_TIMEOUT_MS - 1, Ordering::SeqCst);
            let stale = broker.derive_lifecycle(now_ms());
            assert_eq!(stale.main_thread, "stale");
            assert_eq!(stale.reason, Some("heartbeat_timeout"));

            broker.modal_observation.lock().unwrap().present = true;
            let modal = broker.derive_lifecycle(now_ms());
            assert_eq!(modal.main_thread, "blocked_modal");
            assert_eq!(modal.reason, Some("modal_dialog"));

            broker.modal_observation.lock().unwrap().present = false;
            broker.managed_state.store(MANAGED_STATE_RELOADING, Ordering::SeqCst);
            assert_eq!(broker.derive_lifecycle(now_ms()).main_thread, "unknown");
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

