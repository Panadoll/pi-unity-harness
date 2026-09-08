//! TOON v4.1 编码器（仅编码，内部数据仍是 JSON）。

use serde_json::{Map, Number, Value};

const INDENT: &str = "  ";

pub fn encode(value: &Value) -> String {
    match value {
        Value::Null => "null".to_string(),
        Value::Bool(true) => "true".to_string(),
        Value::Bool(false) => "false".to_string(),
        Value::Number(n) => encode_number(n),
        Value::String(s) => encode_string(s, ','),
        Value::Array(arr) => encode_root_array(arr),
        Value::Object(map) => {
            if map.is_empty() {
                String::new()
            } else if keyed_tabular_fields(map).is_some() {
                encode_keyed_tabular(None, map, 0)
            } else {
                encode_object_fields(map, 0)
            }
        }
    }
}

fn encode_object_fields(map: &Map<String, Value>, depth: usize) -> String {
    let mut lines = Vec::new();
    for (key, val) in map {
        lines.push(encode_field(key, val, depth));
    }
    lines.join("\n")
}

fn encode_field(key: &str, val: &Value, depth: usize) -> String {
    let pad = INDENT.repeat(depth);
    let key_text = encode_key(key);
    match val {
        Value::Object(map) if map.is_empty() => format!("{pad}{key_text}:"),
        Value::Object(map) => {
            if let Some(fields) = keyed_tabular_fields(map) {
                let header = keyed_header(&key_text, map.len(), &fields);
                let mut out = format!("{pad}{header}");
                out.push('\n');
                out.push_str(&encode_keyed_rows(map, &fields, depth + 1));
                out
            } else {
                let nested = encode_object_fields(map, depth + 1);
                format!("{pad}{key_text}:\n{nested}")
            }
        }
        Value::Array(arr) if arr.is_empty() => format!("{pad}{key_text}: []"),
        Value::Array(arr) => encode_array_field(&pad, &key_text, arr, depth),
        primitive => format!(
            "{pad}{key_text}: {}",
            encode_primitive(primitive, ',')
        ),
    }
}

fn encode_array_field(pad: &str, key_text: &str, arr: &[Value], depth: usize) -> String {
    if let Some(fields) = tabular_fields(arr) {
        let header = format!("{key_text}[{}]{{{}}}:", arr.len(), fields.join(","));
        let mut out = format!("{pad}{header}");
        if !arr.is_empty() {
            out.push('\n');
            out.push_str(&encode_tabular_rows(arr, &fields, depth + 1));
        }
        out
    } else if arr.iter().all(is_primitive) {
        let values: Vec<String> = arr.iter().map(|v| encode_primitive(v, ',')).collect();
        format!("{pad}{key_text}[{}]: {}", arr.len(), values.join(","))
    } else {
        let mut lines = vec![format!("{pad}{key_text}[{}]:", arr.len())];
        for item in arr {
            lines.push(encode_list_item(item, depth + 1));
        }
        lines.join("\n")
    }
}

fn encode_root_array(arr: &[Value]) -> String {
    if arr.is_empty() {
        return "[]".to_string();
    }
    if let Some(fields) = tabular_fields(arr) {
        let header = format!("[{}]{{{}}}:", arr.len(), fields.join(","));
        let rows = encode_tabular_rows(arr, &fields, 1);
        format!("{header}\n{rows}")
    } else if arr.iter().all(is_primitive) {
        let values: Vec<String> = arr.iter().map(|v| encode_primitive(v, ',')).collect();
        format!("[{}]: {}", arr.len(), values.join(","))
    } else {
        let mut lines = vec![format!("[{}]:", arr.len())];
        for item in arr {
            lines.push(encode_list_item(item, 1));
        }
        lines.join("\n")
    }
}

fn encode_list_item(item: &Value, depth: usize) -> String {
    let pad = INDENT.repeat(depth);
    match item {
        Value::Object(map) if map.is_empty() => format!("{pad}-"),
        Value::Object(map) => {
            let mut fields = map.iter();
            let Some((first_key, first_val)) = fields.next() else {
                return format!("{pad}-");
            };
            let first_line = encode_field(first_key, first_val, 0);
            let mut out = format!("{pad}- {first_line}");
            for (key, val) in fields {
                out.push('\n');
                out.push_str(&encode_field(key, val, depth + 1));
            }
            out
        }
        Value::Array(inner) if inner.iter().all(is_primitive) && !inner.is_empty() => {
            let values: Vec<String> = inner.iter().map(|v| encode_primitive(v, ',')).collect();
            format!("{pad}- [{}]: {}", inner.len(), values.join(","))
        }
        Value::Array(inner) if inner.is_empty() => format!("{pad}- [0]:"),
        primitive => format!("{pad}- {}", encode_primitive(primitive, ',')),
    }
}

fn encode_tabular_rows(arr: &[Value], fields: &[String], depth: usize) -> String {
    let pad = INDENT.repeat(depth);
    let mut lines = Vec::new();
    for item in arr {
        let obj = item.as_object().expect("tabular 行必须是对象");
        let cells: Vec<String> = fields
            .iter()
            .map(|f| encode_primitive(obj.get(f).unwrap_or(&Value::Null), ','))
            .collect();
        lines.push(format!("{pad}{}", cells.join(",")));
    }
    lines.join("\n")
}

fn encode_keyed_tabular(key: Option<&str>, map: &Map<String, Value>, depth: usize) -> String {
    let fields = keyed_tabular_fields(map).expect("keyed tabular");
    let header = match key {
        Some(k) => keyed_header(k, map.len(), &fields),
        None => format!("[{}:]{{{}}}:", map.len(), fields.join(",")),
    };
    let pad = INDENT.repeat(depth);
    format!(
        "{pad}{header}\n{}",
        encode_keyed_rows(map, &fields, depth + 1)
    )
}

fn keyed_header(key: &str, n: usize, fields: &[String]) -> String {
    format!("{key}[{n}:]{{{}}}:", fields.join(","))
}

fn encode_keyed_rows(map: &Map<String, Value>, fields: &[String], depth: usize) -> String {
    let pad = INDENT.repeat(depth);
    let mut lines = Vec::new();
    for (entry_key, val) in map {
        let obj = val.as_object().expect("keyed 行必须是对象");
        let cells: Vec<String> = fields
            .iter()
            .map(|f| encode_primitive(obj.get(f).unwrap_or(&Value::Null), ','))
            .collect();
        lines.push(format!(
            "{pad}{}: {}",
            encode_key(entry_key),
            cells.join(",")
        ));
    }
    lines.join("\n")
}

fn tabular_fields(arr: &[Value]) -> Option<Vec<String>> {
    if arr.is_empty() {
        return None;
    }
    let first = arr[0].as_object()?;
    if first.is_empty() {
        return None;
    }
    let fields: Vec<String> = first.keys().cloned().collect();
    for item in arr {
        let obj = item.as_object()?;
        if obj.len() != fields.len() || obj.keys().any(|k| !first.contains_key(k)) {
            return None;
        }
        if obj.values().any(|v| !is_primitive(v)) {
            return None;
        }
        if obj.is_empty() {
            return None;
        }
    }
    Some(fields)
}

fn keyed_tabular_fields(map: &Map<String, Value>) -> Option<Vec<String>> {
    if map.len() < 2 {
        return None;
    }
    let mut iter = map.values();
    let first = iter.next()?.as_object()?;
    if first.is_empty() {
        return None;
    }
    let fields: Vec<String> = first.keys().cloned().collect();
    for val in map.values() {
        let obj = val.as_object()?;
        if obj.len() != fields.len() || obj.keys().any(|k| !first.contains_key(k)) {
            return None;
        }
        if obj.values().any(|v| !is_primitive(v)) {
            return None;
        }
    }
    Some(fields)
}

fn is_primitive(val: &Value) -> bool {
    !matches!(val, Value::Array(_) | Value::Object(_))
}

fn encode_primitive(val: &Value, delim: char) -> String {
    match val {
        Value::Null => "null".to_string(),
        Value::Bool(true) => "true".to_string(),
        Value::Bool(false) => "false".to_string(),
        Value::Number(n) => encode_number(n),
        Value::String(s) => encode_string(s, delim),
        Value::Array(_) | Value::Object(_) => {
            encode_string(&val.to_string(), delim)
        }
    }
}

fn encode_number(n: &Number) -> String {
    if let Some(i) = n.as_i64() {
        return i.to_string();
    }
    if let Some(u) = n.as_u64() {
        return u.to_string();
    }
    let f = n.as_f64().unwrap_or(0.0);
    canonical_float(f)
}

fn canonical_float(f: f64) -> String {
    if !f.is_finite() {
        return "null".to_string();
    }
    if f == 0.0 {
        return "0".to_string();
    }
    let abs = f.abs();
    if abs < 1e-6 || abs >= 1e21 {
        let s = format!("{f:e}");
        return s.replace('E', "e");
    }
    let mut s = format!("{f}");
    if s.contains('e') || s.contains('E') {
        s = trim_float(f);
    }
    if s.contains('.') {
        while s.ends_with('0') {
            s.pop();
        }
        if s.ends_with('.') {
            s.pop();
        }
    }
    s
}

fn trim_float(f: f64) -> String {
    let mut s = format!("{f:.15}");
    if s.contains('.') {
        while s.ends_with('0') {
            s.pop();
        }
        if s.ends_with('.') {
            s.pop();
        }
    }
    s
}

fn encode_key(key: &str) -> String {
    if is_bare_key(key) {
        key.to_string()
    } else {
        format!("\"{}\"", escape_quoted(key))
    }
}

fn is_bare_key(key: &str) -> bool {
    let mut chars = key.chars();
    match chars.next() {
        Some(c) if c.is_ascii_alphabetic() || c == '_' => {}
        _ => return false,
    }
    chars.all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '.')
}

fn encode_string(s: &str, delim: char) -> String {
    if needs_quotes(s, delim) {
        format!("\"{}\"", escape_quoted(s))
    } else {
        s.to_string()
    }
}

fn needs_quotes(s: &str, delim: char) -> bool {
    if s.is_empty() {
        return true;
    }
    let first = s.as_bytes()[0];
    let last = s.as_bytes()[s.len() - 1];
    if first == b' ' || first == b'\t' || last == b' ' || last == b'\t' {
        return true;
    }
    if s == "true" || s == "false" || s == "null" {
        return true;
    }
    if is_numeric_like(s) {
        return true;
    }
    if s.contains(':')
        || s.contains('"')
        || s.contains('\\')
        || s.contains('[')
        || s.contains(']')
        || s.contains('{')
        || s.contains('}')
        || s.contains(delim)
        || s.starts_with('-')
        || s.starts_with('#')
        || s.chars().any(|c| c <= '\u{001F}')
    {
        return true;
    }
    false
}

fn is_numeric_like(s: &str) -> bool {
    let bytes = s.as_bytes();
    if bytes.is_empty() {
        return false;
    }
    let mut i = 0;
    if bytes[0] == b'+' || bytes[0] == b'-' {
        i = 1;
        if bytes.len() == 1 {
            return false;
        }
    }
    if i >= bytes.len() || !bytes[i].is_ascii_digit() {
        return false;
    }
    while i < bytes.len() && bytes[i].is_ascii_digit() {
        i += 1;
    }
    if i < bytes.len() && bytes[i] == b'.' {
        i += 1;
        if i >= bytes.len() || !bytes[i].is_ascii_digit() {
            return false;
        }
        while i < bytes.len() && bytes[i].is_ascii_digit() {
            i += 1;
        }
    }
    if i < bytes.len() && (bytes[i] == b'e' || bytes[i] == b'E') {
        i += 1;
        if i < bytes.len() && (bytes[i] == b'+' || bytes[i] == b'-') {
            i += 1;
        }
        if i >= bytes.len() || !bytes[i].is_ascii_digit() {
            return false;
        }
        while i < bytes.len() && bytes[i].is_ascii_digit() {
            i += 1;
        }
    }
    i == bytes.len()
}

fn escape_quoted(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '\\' => out.push_str("\\\\"),
            '"' => out.push_str("\\\""),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            ch if ('\u{0000}'..='\u{001F}').contains(&ch) => {
                out.push_str(&format!("\\u{:04x}", ch as u32));
            }
            ch => out.push(ch),
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn encodes_tabular_list_commands() {
        let v = json!({
            "count": "2 of 2 total",
            "commands": [
                {"name": "gameobject_find", "summary": "按名字查找"},
                {"name": "run_tests", "summary": "跑测试"}
            ]
        });
        let out = encode(&v);
        assert_eq!(
            out,
            "count: 2 of 2 total\ncommands[2]{name,summary}:\n  gameobject_find,按名字查找\n  run_tests,跑测试"
        );
        assert!(!out.contains('\r'));
        assert!(!out.ends_with('\n'));
    }

    #[test]
    fn encodes_status_object() {
        let v = json!({
            "editor": "ready",
            "generation": 12,
            "play": false,
            "modal": "none"
        });
        assert_eq!(
            encode(&v),
            "editor: ready\ngeneration: 12\nplay: false\nmodal: none"
        );
    }

    #[test]
    fn encodes_error_and_help() {
        let v = json!({
            "error": "eval 需要 CODE 或 --file",
            "help": [
                {"run": "pi-unity eval \"<code>\""},
                {"run": "pi-unity eval -f Temp/PiUnityHarness/AgentScratch/probe.repl"}
            ]
        });
        let out = encode(&v);
        assert!(out.starts_with("error: eval 需要 CODE 或 --file\n"));
        assert!(out.contains("help[2]{run}:"));
        assert!(out.contains("pi-unity eval \"<code>\""));
    }

    #[test]
    fn encodes_empty_state_as_explicit_string() {
        let v = json!({
            "commands": "0 个已注册的 pipeline 命令"
        });
        assert_eq!(encode(&v), "commands: 0 个已注册的 pipeline 命令");
        assert!(!encode(&v).contains("[]"));
    }

    #[test]
    fn quotes_when_required() {
        let v = json!({
            "path": "Foo:Bar",
            "empty": "",
            "numlike": "42",
            "hyphen": "-n"
        });
        let out = encode(&v);
        assert!(out.contains("path: \"Foo:Bar\""));
        assert!(out.contains("empty: \"\""));
        assert!(out.contains("numlike: \"42\""));
        assert!(out.contains("hyphen: \"-n\""));
    }

    #[test]
    fn canonical_numbers_have_no_trailing_zeros() {
        let v = json!({"ratio": 1.5, "zero": 0, "intish": 1.0});
        let out = encode(&v);
        assert!(out.contains("ratio: 1.5"));
        assert!(out.contains("zero: 0"));
        assert!(out.contains("intish: 1"));
        assert!(!out.contains("1.0"));
    }
}
