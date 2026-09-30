use std::process::Command;

/// 集成测试启动 pi-unity 只能走这里：日志写进临时目录，不能污染真实的 ~/.pi-unity/logs。
pub fn cli() -> Command {
    let mut cmd = Command::new(env!("CARGO_BIN_EXE_pi-unity"));
    cmd.env(
        "PI_UNITY_LOG_DIR",
        std::env::temp_dir().join(format!("pi-unity-test-logs-{}", std::process::id())),
    );
    cmd
}
