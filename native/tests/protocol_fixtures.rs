use serde_json::Value;
use std::fs;
use std::path::{Path, PathBuf};

fn fixture_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../protocol/fixtures")
}

fn load_all(dir: &str) -> Vec<(PathBuf, Value)> {
    let path = fixture_root().join(dir);
    let mut files: Vec<_> = fs::read_dir(&path)
        .unwrap_or_else(|e| panic!("cannot read {}: {e}", path.display()))
        .map(|entry| entry.unwrap().path())
        .filter(|p| p.extension().and_then(|s| s.to_str()) == Some("json"))
        .collect();
    files.sort();
    files
        .into_iter()
        .map(|file| {
            let value: Value = serde_json::from_str(&fs::read_to_string(&file).unwrap())
                .unwrap_or_else(|e| panic!("invalid fixture {}: {e}", file.display()));
            (file, value)
        })
        .collect()
}

fn assert_fixture_shape(dir: &str, expected_direction: &str, correlation: &str) {
    let fixtures = load_all(dir);
    assert!(!fixtures.is_empty(), "fixture directory {dir} must not be empty");
    for (path, fixture) in fixtures {
        assert_eq!(fixture["direction"], expected_direction, "{}", path.display());
        assert!(fixture["wire"].is_object(), "{} missing wire", path.display());
        assert!(fixture["expect"].is_object(), "{} missing expect", path.display());
        let wire = fixture["wire"].as_object().unwrap();
        if dir == "response" && path.file_name().unwrap() != "missing_reply_to.json" {
            assert!(wire.contains_key(correlation), "{} missing reply_to", path.display());
            assert!(!wire.contains_key("id"), "{} uses pipe id alias", path.display());
        }
        if dir == "mux" && path.file_name().unwrap() != "err_missing_id.json" {
            assert!(wire.contains_key(correlation), "{} missing id", path.display());
            assert!(!wire.contains_key("reply_to"), "{} uses mux reply_to alias", path.display());
        }
    }
}

#[test]
fn shared_fixture_tree_has_expected_wire_surfaces() {
    assert_fixture_shape("request", "request", "id");
    assert_fixture_shape("response", "response", "reply_to");
    assert_fixture_shape("status", "status", "id");
    assert_fixture_shape("mux", "mux", "id");
}

#[test]
fn protocol_mismatch_fixture_has_structured_versions() {
    let fixture = load_all("response")
        .into_iter()
        .find(|(path, _)| path.ends_with("err_protocol_mismatch.json"))
        .unwrap().1;
    assert_eq!(fixture["wire"]["error_type"], "protocol_mismatch");
    assert_eq!(fixture["wire"]["result"]["expected"], 1);
    assert_eq!(fixture["wire"]["result"]["actual"], 2);
}
