//! CLI capture/observe 到既有 pipeline 参数的 adapter。
//! 路径解析在 C#。Base64 落盘在 output。这里不复制第二套目录或 result。
//! game 走 vision_capture_async；scene 保持同步 vision_capture。
//! --out 发 path，--interval 发 interval_ms。

use serde_json::{json, Map, Value};

use super::args::{CaptureArgs, CaptureMode, ObserveArgs, OverlayMode};

fn overlay_wire(mode: OverlayMode) -> &'static str {
    match mode {
        OverlayMode::Grid => "grid",
        OverlayMode::Annotations => "annotations",
        OverlayMode::Both => "both",
        OverlayMode::None => "none",
    }
}

/// 固定 game。interval 发 C# 声明名 interval_ms。
/// 不注入 total_timeout_ms；领域总超时沿用 C# 默认 5000。
pub(crate) fn observe_parameters(args: &ObserveArgs) -> Value {
    json!({
        "mode": "game",
        "frames": args.frames,
        "interval_ms": args.interval,
        "overlay": overlay_wire(args.overlay),
    })
}

/// 返回 (pipeline 命令名, 参数)。game 的领域 timeout_ms 与 --timeout 相同；
/// scene 同步命令没有 timeout_ms，不发。
pub(crate) fn capture_command(args: &CaptureArgs) -> (&'static str, Value) {
    let mut parameters = Map::new();
    if let Some(out) = &args.out {
        parameters.insert("path".into(), json!(out));
    }
    match args.mode {
        CaptureMode::Game => {
            parameters.insert("mode".into(), json!("game"));
            parameters.insert("timeout_ms".into(), json!(args.timeout));
            ("vision_capture_async", Value::Object(parameters))
        }
        CaptureMode::Scene => {
            parameters.insert("mode".into(), json!("scene"));
            ("vision_capture", Value::Object(parameters))
        }
    }
}
