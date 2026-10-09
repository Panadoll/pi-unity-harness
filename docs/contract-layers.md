# JSON 与协议契约分层

## 所有权

| 层 | C# | Rust | TypeScript |
|---|---|---|---|
| Wire | `Editor/Protocol`：出站 pipe 信封，`reply_to` | `wire.rs`：pipe 错误分类、CLI 错误模型；匹配由 `client.rs` I/O 负责 | `wire/cli-envelope.ts`：exec/mux parser 与唯一 `CliExecutionResult` |
| Domain | InputJson、UiTreeJson、VisionJson、Pipeline/Models：各自字段、版本和 null 语义 | `domain.rs`：Pipeline result 解包与领域语义 | `helpers.ts`：命令 schema、动态名称与过滤规则 |
| Presentation | 不生成 CLI 合成字段 | `schema.rs`：列选择/字段 shaping；`output.rs`：TOON/JSON、长输出、Base64 落盘 | `presentation/tool-result.ts`：工具 text/details 和错误结果 |

共享 JSON primitive 只负责转义、数值和字符串构造，不统一领域字段。Input 空字符串、Vision metadata 与 Pipeline null-ignore 各自保留。旧 helper/parser 不留兼容别名，实际调用者迁到唯一所有者。

## 不得合并的边界

- pipe response 使用 `reply_to`；mux 使用字符串 `id`，不是别名。
- Rust pipe 缺 `ok` 按失败分类；exec parser 缺 `ok` 仍使用既有 `ok !== false`。结构重构不静默改错误优先级。
- Domain `result` 不携带 CLI `text`、`truncated`、`savedScratchPath`；它们属于传输适配或展示。
- Base64 fallback `Temp/PiUnityHarness/Captures/` 属于 CLI，不是 Unity 单帧 capture 默认路径。
- 同名 Pipeline 命令由当前 registry 决定；inventory 按多重集比较，不能用名称字典遮掉一条。

## Capture 内部契约

`Vision/CapturePathService.cs` 接收显式工程根，分别保留 Harness、Playtest observe/after、旧 Camera 路径规则；旧 Camera 的显式相对路径仍以进程 cwd 为基准。CLI Base64 不进入这个服务。失败的单帧 JSON 统一由 `VisionJson.BuildCaptureFailedJson` 生成。

Rust `capture.rs` 把 CLI 参数映射成 pipeline 命令名和参数。`capture` 默认 game 走 `vision_capture_async`（`--out` → `path`，领域 `timeout_ms` 等于 `--timeout`）；`scene` 保持同步 `vision_capture`，不发 `timeout_ms`。`observe` 的 `--interval` 发 `interval_ms`，不注入 `total_timeout_ms`。传输等待始终是 `--timeout`，不另加固定余量。见 `skills/pi-unity/references/capture.md` 和 `observe.md`。

## 验证

共享 `protocol/fixtures` 交给对应真实消费者，参考 `protocol/README.md`。命令 metadata 由 `scripts/command-inventory.py` 消费，缺来源字段明确报告，不编造。Rust/TS tests、Unity references 外部编译、实际消费者 smoke 的证据各自记录；外部编译不代表 Editor import、domain reload 或 PlayMode 已验证。

## 控制字符转义验证

`VisionJson.BuildCaptureJson` 使用共享 JSON primitive。独立严格消费者已用全部 32 个 U+0000–U+001F 控制字符、引号、反斜杠和 Unicode 输入验证：输出中无原始控制字节，严格 JSON parse 后 path 与输入逐字符相同。保留语义回归，不因终端/NUnit 的控制字符显示误判而添加二次转义；重复转义会改变消费者看到的路径。