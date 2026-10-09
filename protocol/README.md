# Protocol fixtures

These JSON files are the machine-readable companion to `docs/protocol.md`.
Rust, C#, and TypeScript contract tests load this same directory.

## Layout

- `request/`: client-to-broker pipe request frames. Requests use `id`.
- `response/`: broker-to-client pipe response frames. Responses use `reply_to` and must not use `id` as an alias.
- `status/`: status, capabilities, and command-list result payloads.
- `mux/`: CLI mux stdout envelopes. Mux envelopes use `id` and must not use `reply_to` as an alias.
- `normalized/`: reserved for shared normalization tables when a table is clearer than an individual fixture.

每个 fixture 包含 `description`、`direction`、`wire` 和 `expect`。它们由不同方向的真实消费者执行，不能把 pipe 预期直接当成 CLI/mux 预期。

- Rust pipe：`native/src/bin/pi_unity/wire.rs` 做方向分类。缺 `ok` 走错误分类；成功且缺 `result` 返回 null；只有匹配 `reply_to` 才完成请求。`reply_to` 不是 mux `id`。
- Rust domain：`domain.rs` 只抽取 Pipeline payload，并判断 job 终态失败。
- Rust presentation：`schema.rs` 做默认列和截断；`output.rs` 做 TOON/JSON 成功输出、mux id 包装、Base64 剥离和长输出落盘。
- TS exec JSON：`ok !== false`，缺 `ok` 不等于 pipe 的失败；缺 `result` 保留 undefined；仅透传显式 `error_type`，不分类 broker 裸码。
- TS mux：只有字符串 `id` 才能匹配请求；`reply_to` 不是别名。无 id 的损坏帧使在途请求返回 `in-flight-lost`，不重放已写请求。
- C#：测试执行出站 `Protocol/PipeEnvelope.Success` / `Error`，共享 `Capabilities/Shared/JsonText` 转义，验证 correlation、null、Unicode；没有伪造入站 parser。

pipe 错误分类优先级为 `error_type`、broker `error` 闭集、`execution_failed`。fixture 中的理想化期望不能覆盖现有消费者差异；方向 B 只拆内部层次，不改变这些行为。

新增 fixture 时同步 `docs/protocol.md` 和对应真实消费者测试；不再保留只验证文件存在或 JSON 形状的测试。
