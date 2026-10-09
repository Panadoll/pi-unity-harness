# 命令契约 inventory（方向 D，只报告）

本文件说明维护工具如何审阅命令 schema / policy，以及什么**不是**契约真相源。工具不改变运行时，也不在 CI 里阻断产品构建。

## 真相源

| 数据 | 唯一可审阅来源 | 不能当成来源的东西 |
|---|---|---|
| 参数 `required` / `defaultValue` / `jsonType`、JSON Schema、`policy`、runtime 标记 | 未整形的 pipe `list_commands` / `BuildListCommandsResponse` JSON。`pi-unity list-commands --full --json` 只有 schema、parameters 和顶层 mutability | README、手工名单、源码扫描、CLI 瘦视图 |
| 程序集来源 | 响应里的 `assembly`。当前 `BuildCommandJson` 不输出该字段，inventory 标 `missingFields` 含 `assembly`，不编造 | 文件路径猜测 |
| 固定工具名 / 排除规则 | 解析 `.pi/extensions/pi-unity-harness/helpers.ts` 的 `export const` 与 `pipelineDynamicToolName`；解析失败则报无法解析来源，没有内置名单 | 在维护脚本里再抄一份 Set |
| skill 文案 | `native/src/bin/pi_unity/home.rs::static_skill_markdown()`，由实际 CLI `skills check --json` 校验 | 手工 diff 时忽略换行规则 |

比较忽略命令 `description` / `summary`、参数 C# `type` / `typeFullName`，以及 schema 节点上的 `description` / `title` / `x-command-metadata`。`properties` 里名为 `description` 或 `title` 的参数保留。缺 `parameters` 不会补成 `[]`，缺 `mutability` 不会默认 `write`。同名命令（官方与 harness 的 `package_*`）按稳定多重集保留，`diff` 不按名称覆盖。`duplicates` 只报告，不改运行时注册。

CLI 默认瘦视图会把 `commands` 收成摘要文本。那种 JSON **不能**生成 inventory，工具会直接失败，而不是补造 schema。

采集新的 `list-commands` 必须连接运行中的 Unity Editor，只有本地 exe 不够。读取已经保存的 JSON 不需要 Editor；`source-signature` 完全离线，但只代表源声明，不代表运行时 schema 或发现结果。

## 没有完整 metadata 时

`source-signature` 是正则扫描，不是 Roslyn/语法树。它只扫 `unity/com.pi.unity-harness` 的 `[CliCommand]` / `[CliArg]` / `[PiCommandPolicy]` 与 `command-policy.json`。输出 `kind=source-declaration-report`、`parser=regex`、`notRuntimeSchema=true`。嵌套括号、字符串伪属性、非常规属性位置和 `#if` 分支可能扫错。它不包含官方或 compat 包命令，没有 JSON Schema，不能拿去和 runtime inventory 假装等价。方向 A 的语法级 baseline 应另用 Roslyn。

无需 Unity 的声明盘点使用现有脚本，不再维护第二份生成器：

```bash
python scripts/command-inventory.py source-signature --output scratch/contract/command-inventory.json
```


未整形 pipe JSON 可离线生成完整（除 assembly）inventory：

```bash
python scripts/command-inventory.py inventory --input /path/to/saved-list-commands.json --output before.json
```

移动 harness 声明后比较源声明，或比较两份 inventory：

```bash
python scripts/command-inventory.py source-signature --output before.json
# 移动 harness 命令文件后
python scripts/command-inventory.py source-signature --output after.json
python scripts/command-inventory.py diff --before before.json --after after.json --check
```

runtime schema/policy 的前后比较用同一命令，输入换成两份 `inventory` 输出。

## skill 归一化

`static_skill_markdown()` 用 `format!` 替换 `{DESCRIPTION}` 与 `{VERSION}`。提交的 `skills/pi-unity/SKILL.md` 必须已经是替换后的文本。CLI `skills check` 的比较规则是：

1. CRLF 先变成 LF；
2. 去掉首尾空白；
3. 再与渲染结果做整段相等比较。

实现在 `commands.rs::normalize_skill_text`。维护报告执行的是实际二进制，不是重新实现这套规则：

```bash
python scripts/command-inventory.py skill-report --cli /path/to/pi-unity
```

没有二进制时报告 `executed=false`，不假装已校验。

## CI

`.github/workflows/ci.yml` 的 `contract-inventory` 只上传报告 artifact。缺 CLI 或 Editor JSON 时步骤仍成功，报告里写明限制。它不阻断 Rust / extension / runtime。

## 帮助

```bash
python scripts/command-inventory.py --help
python scripts/command-inventory.py inventory --help
python scripts/command-inventory.py diff --help
python scripts/command-inventory.py extension-rules --help
python scripts/command-inventory.py source-signature --help
python scripts/command-inventory.py skill-report --help
python scripts/command-inventory.py report --help
```
