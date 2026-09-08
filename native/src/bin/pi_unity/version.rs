//! 版本号叶子模块。`--version` 快路径只读这里，不触发工程发现或握手。

pub const VERSION: &str = env!("CARGO_PKG_VERSION");
