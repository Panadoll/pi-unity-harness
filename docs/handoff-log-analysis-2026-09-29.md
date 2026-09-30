# 交接说明：harness 运行日志分析与优化清单（2026-09-29）

> 交接对象：接手「从 `~/.pi-unity` 运行日志里找 harness 优化点」这件事的人 / 另一个会话。
> 来源会话：2026-09-29 的 pi 会话（只做了只读分析，**没有改任何代码**）。
> 状态：**分析已完成并已在对话中汇报；结论未落盘成独立产物，未建 issue，未改代码。**

---

## 一、目标（在解决什么问题）

pi-unity-harness 已经落了 L0/L1 观测日志（`~/.pi-unity/logs/`），设计文档
`docs/observability-logging-design.md` 明确把 L2「聚合分析」留空。这件事的目标：

1. 找到并读懂已经采集到的历史日志，确认它是否可用；
2. 从真实调用数据里定位 harness 的失败模式与耗时大户（不靠猜、不靠单次会话印象）；
3. 产出一份「按影响排序、每条都有日志证据和代码位置」的优化清单，供后续立项。

**这不是** `docs/session-2026-09-16-harness-optimization.md` 那件事的重复。那件事的
O1–O9 已经有一批落地（见 `docs/session-optimization-implementation.md`），本任务是拿
**09-02 → 09-29 的真实调用日志**去验证余下的、更底层的问题。

---

## 二、进度

### 已完成

- 定位到日志真身：**`<HOME>\.pi-unity\logs\`**
  （WSL 路径 `<HOME>/.pi-unity/logs/`）。
  - `events-2026-09.jsonl`：分析时 2542 条 `kind=call` 事件，时间跨度 2026-09-02 → 2026-09-29
    （2026-09-29T12:09Z 快照；该文件持续追加，复现时行数与下面所有数字都会更多）。
  - `traces/YYYY-MM-DD/`：236 个过程日志，只有 2026-09-22 / 09-23 / 09-28 / 09-29 四天。
- 用临时 python（inline heredoc，未落盘）完成统计，确认日志字段可用。
- 形成优化清单，按「耗时 × 证据强度」排序（结论见下）。

### 未做（接手人不要误以为已有）

- **没有写任何文件**：分析脚本、报告都不在仓库里。本文件是这次唯一新增产物。
- **没有改任何代码**，没有跑 `cargo test` / `node --test` / Unity 测试。
- **没有实现 `pi-unity analyze`**（L2）。命令行里确认不存在该子命令：
  `native/src/bin/pi_unity/args.rs` 的 `Commands` 枚举没有 `Analyze` 分支，
  全仓 grep `"analyze"` 在 `native/src/bin/pi_unity/` 下无命中。
- **没有建 issue、没有提交**。

### 关键证据（都在上面那份日志里，可复现）

| 指标 | 数值 |
|---|---|
| 调用总数 | 2542（分析时快照；`client=cli` 2363 / `client=pi-ext` 179） |
| 全部 `version` | `0.1.0`（无法用版本字段区分代码改动） |
| `sessionId` / `hostSessionId` 非空 | **0 / 0**；`agentId` 非空仅 155 |
| 累计 `durationMs` | 9765s；其中失败调用 705 次占 3656s（约 37%） |
| 错误分布 | `bridge_not_found` 354、`compile_error` 99、`busy` 73、`timeout` 71、`usage` 56、`execution_failed` 40 |
| 耗时 top | `run-tests` 4728s / `compile` 1807s / `eval` 1251s / `home` 889s / busy 空转 597s |
| 最差单日 | 2026-09-22，失败率 77.2%（`bridge_not_found` 91 + `usage` 19 + `compile_error` 19） |

### 已确认的 4 个高优机会（含真实代码位置）

1. **单客户端管道 `busy` 是纯忙等**：73 次 / 597s。日志里有一批调用
   `reconnects=80`、耗时约 5s、最终 `errorType=busy`，与
   `native/src/bin/pi_unity/client.rs:500`（`ERROR_PIPE_BUSY / 231` 分支：每 50ms 退避、
   直到调用预算耗尽）完全对得上；`client.rs:503` 才返回 `CliError::Busy`。
   80 × 50ms ≈ 4s，即 agent 付满超时后才拿到一个没有下一步的错误。

2. **每次成功调用都多吃一次管道重连（1667 / 1837 次成功调用 `reconnects=1`）**。
   one-shot CLI 走 `HarnessClient::new`（`native/src/bin/pi_unity/main.rs:237`），
   `persistent` 默认 false（`native/src/bin/pi_unity/client.rs:169`）；
   而连接只在 `persistent` 时才保留（`client.rs:455`）。于是
   `handshake()`（`client.rs:218`）开一次管道、紧接着业务 exchange 再开一次，
   第二次撞 231 → 50ms 退避 → `reconnects++`（`client.rs:508`）。
   `new_persistent` 目前只被 mux 路径使用（`main.rs:563`），业务命令路径没用。

3. **`bridge_not_found` 354 次里 202 次根本没有项目上下文**（`projectHash=null`），
   178 次来自 `status` 探测。trace 显示典型形态是「`bridge.json` 还在、broker PID 已死」
   的陈旧文件（例：`logs/traces/2026-09-29/20260929-032830-45804-status.log`，
   加载 `pid=24592` 后立刻 `os error 2`）。

4. **`run-tests` 一个命令吃掉 48% 总时长**：144 次、p50 23.8s、p90 54s、max 330s
   （命中默认 330000ms 预算），20 次输出 `truncated`。同步阻塞跑测试让 agent 全程干等。

其余中优项（`eval` 49.9% 失败率且 `usage` 56 次全部来自 eval、`compile` 34.8% 错误率、
5s 内同命令连发 440 次的轮询、`home` 105 次各 8.5s）见对话记录；日志自身的字段缺口
（无 pipeline 命令名、无输出字节数、`reconnects` 语义把「正常 1 次」和「busy storm 80 次」
混在一起）也一并成立。

### ⚠️ git 工作区现状（与本任务无关，但必须知道）

`git rev-parse HEAD` = `404afbd`（`master`），**工作区是脏的**：48 files changed,
2002 insertions, 335 deletions。这是一批**本会话之前就已存在**的未提交改动，主题是
「Unity 6 官方 com.unity.pipeline 0.8 集成 + 动态命令引导」，**不是本任务产生的**，
本次会话没有碰过它们。主要内容：

- `.pi/extensions/pi-unity-harness/index.ts`（动态工具名/引导文案、
  `PIPELINE_PACKAGE_VERSION` 改为 `0.8.0-exp.1`、`formatResult` 透传 `result`）、
  `helpers.ts`、`helpers.test.ts`、`mux.test.ts`；
- `native/src/bin/pi_unity/client.rs`（新增 `CliError::JobFinished`、`capabilities` 缓存）、
  `commands.rs`、`main.rs`、`schema.rs`、`args.rs`、`usage.rs`；
- `unity/com.pi.unity-harness/**`、`unity/com.pi.pipeline.compat/**`
  （含删除 `CliArgAttribute.cs` / `CliCommandAttribute.cs`、asmdef 引用与 versionDefines
  区间调整、`UPSTREAM_VERSION`）；
- `CHANGELOG.md`、`README.md`、`docs/pipeline-integration-design.md`、`skills/pi-unity/**`。

`docs/session-optimization-implementation.md` 的「验证轮补记」提到 `bin/pi-unity.exe` /
`dist/` 是 gitignore 的构建产物——**接手人要注意工作区 `bin/` 里的二进制不一定和当前
源码一致**，改 native 后必须重新构建。

我**没有**验证这批改动是否能编译、测试是否通过。

---

## 三、下一步（接手第一件事）

按顺序做，别跳：

1. **先确认基线，别在脏工作区上叠加改动。**
   ```bash
   cd <WORKSPACE>/unity-ai-tool/pi-unity-harness
   git status --short
   git diff --stat
   cargo test --manifest-path native/Cargo.toml
   node --test .pi/extensions/pi-unity-harness/*.test.ts
   ```
   先判断上面那批 0.8 集成的改动处于什么状态（谁的、能不能提交、测试过没有）。
   这决定了两条路线：
   - 若那批改动未完成 → 先把它的状态问清楚/收尾，再动 logs 优化项；
   - 若可以提交 → 先落一个独立 commit 固化基线，再开本任务的改动。

2. **把本分析落成可复现的产物**（本任务真正的缺口）：
   - 建议先把对话里的统计脚本固化为 `scripts/analyze-logs.py` 或直接实现
     `pi-unity analyze`（对应 `docs/session-2026-09-16-harness-optimization.md:163` 的 O9、
     `docs/observability-logging-design.md:76` 明确未做的 L2）。
   - 日志路径注意 WSL 陷阱：`HOME=<HOME>` 下 `~/.pi-unity` 是**空的**，
     真正的日志在 `%USERPROFILE%`，即 `<HOME>/.pi-unity/logs/`。

3. **然后做 P0-2**（收益/改动比最好）：让 one-shot CLI 复用同一条连接，
   消掉每次调用的多余重连。落点：
   - `native/src/bin/pi_unity/main.rs:237`（业务命令路径用 `HarnessClient::new`）；
   - `native/src/bin/pi_unity/client.rs:169`（`persistent: false`）与
     `client.rs:455`（只在 persistent 时保存连接）。
   改完用 `cargo test --manifest-path native/Cargo.toml` 验证，
   并注意 `client.rs:589` 已有的 persistent 复用测试。

4. 回报前请遵守仓库的 Unity 验证约定（改 C# 才需要 `unity_recompile`；
   本任务前三步是 Rust/JS，不需要 Unity）。

---

## 四、复现分析的命令（原始日志在，随时可重跑）

```bash
L=<HOME>/.pi-unity/logs/events-2026-09.jsonl
python3 - "$L" <<'EOF'
import json,sys,collections
rows=[json.loads(l) for l in open(sys.argv[1],encoding='utf-8') if l.strip()]
print("rows",len(rows))
print("errors",collections.Counter(r['errorType'] for r in rows if r.get('errorType')))
print("time_by_subcmd",collections.Counter(
    {k:sum(x['durationMs'] for x in rows if x['subcommand']==k) for k in {r['subcommand'] for r in rows}}))
print("reconnects",collections.Counter(r['flags']['reconnects'] for r in rows))
EOF
```

---

## 五、不要再重复的坑

- 日志目录随平台走 `%USERPROFILE%`，不要在 WSL 的 `~` 下找。
- `events-*.jsonl` 每行字段是扁平的、`kind` 全是 `call`；`session` / `skill.used` 事件
  一次都没产生过，`sessionId` 全为 null——**不能**按任务/agent 维度做分析。
- `reconnects` 字段语义混杂（正常握手一次 vs busy 忙等几十次），解读时要配合
  `errorType` 和 `durationMs`。
- 别把 `unity_pipeline` 的 `jobId` 和官方 `wait_id` 混为一谈
  （`docs/pipeline-integration-design.md` 工作区版本第 3.6 节有说明）。
