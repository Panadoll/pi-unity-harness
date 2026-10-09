#!/usr/bin/env python3
"""命令 / schema / policy 的 normalized inventory 与报告模式。

真相源是真实 CLI 的 list-commands JSON（可离线消费已保存文件），不是源码扫描。
源码扫描只生成「源声明」签名，供方向 A 比较迁移前后不变量，不能冒充 Editor schema。
固定工具名与排除规则从 extension 声明导出，不在本脚本里再抄一份注册表。

本工具只报告 drift，不改产品运行时，也不阻断 CI。
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[1]
HELPERS = REPO_ROOT / ".pi/extensions/pi-unity-harness/helpers.ts"
INDEX = REPO_ROOT / ".pi/extensions/pi-unity-harness/index.ts"
POLICY = REPO_ROOT / "unity/com.pi.unity-harness/Editor/Pipeline/command-policy.json"
HARNESS_COMMANDS = REPO_ROOT / "unity/com.pi.unity-harness"
SKILL = REPO_ROOT / "skills/pi-unity/SKILL.md"

CONTRACT_PARAM_FIELDS = ("name", "required", "defaultValue", "jsonType")
IGNORED_FIELDS = ("description", "summary", "type", "typeFullName")


class InventoryError(Exception):
    """输入无法解释为契约 JSON。"""


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except FileNotFoundError as exc:
        raise InventoryError(f"找不到 JSON：{path}") from exc
    except json.JSONDecodeError as exc:
        raise InventoryError(f"JSON 无法解析：{path}: {exc}") from exc


def unwrap_command_list(payload: Any) -> tuple[dict[str, Any], str]:
    """接受 pipe 信封、CLI `--json` 信封，或裸 command_list。"""
    if not isinstance(payload, dict):
        raise InventoryError("list-commands JSON 必须是对象")
    if payload.get("ok") is False:
        raise InventoryError(
            "list-commands 失败："
            + str(payload.get("error") or payload.get("error_type") or "unknown")
        )
    current: Any = payload
    if "result" in payload and isinstance(payload["result"], (dict, str)):
        current = payload["result"]
    if isinstance(current, str):
        try:
            current = json.loads(current)
        except json.JSONDecodeError as exc:
            raise InventoryError(f"result 不是 command_list JSON：{exc}") from exc
    if not isinstance(current, dict):
        raise InventoryError("找不到 command_list 对象")
    commands = current.get("commands")
    if isinstance(commands, str):
        raise InventoryError(
            "这是 CLI 默认瘦视图（commands 被收成摘要文本）。"
            "请保存未整形的 pipe/BuildListCommandsResponse JSON，或使用 `pi-unity list-commands --full --json`。"
            "瘦视图没有参数 required/default/jsonType，不能当 schema inventory。"
        )
    if not isinstance(commands, list):
        raise InventoryError("command_list 缺少 commands 数组")
    return current, "runtime-list-commands"


def canonical(value: Any) -> Any:
    if isinstance(value, dict):
        return {key: canonical(value[key]) for key in sorted(value)}
    if isinstance(value, list):
        return [canonical(item) for item in value]
    return value


def normalize_parameter(raw: Any) -> dict[str, Any]:
    if not isinstance(raw, dict) or not isinstance(raw.get("name"), str) or not raw["name"]:
        raise InventoryError(f"参数缺少 name：{raw!r}")
    default = raw.get("defaultValue", None)
    return {
        "name": raw["name"],
        "required": bool(raw.get("required")),
        "defaultValue": canonical(default),
        "jsonType": raw.get("jsonType") if isinstance(raw.get("jsonType"), str) else None,
    }


def normalize_schema(schema: Any) -> Any:
    """丢掉描述类字段，但保留 properties 里名为 description/title 的参数。"""
    if isinstance(schema, list):
        return [normalize_schema(item) for item in schema]
    if not isinstance(schema, dict):
        return schema
    kept = {}
    for key, value in schema.items():
        if key in {"description", "title", "x-command-metadata"}:
            continue
        kept[key] = normalize_schema(value) if key != "properties" else {
            name: normalize_schema(prop) for name, prop in value.items()
        } if isinstance(value, dict) else normalize_schema(value)
    return {key: kept[key] for key in sorted(kept)}


def normalize_command(raw: Any) -> tuple[dict[str, Any], list[str]]:
    if not isinstance(raw, dict) or not isinstance(raw.get("name"), str) or not raw["name"]:
        raise InventoryError(f"命令缺少 name：{raw!r}")
    missing = []
    command: dict[str, Any] = {"name": raw["name"]}
    if "parameters" in raw:
        parameters = raw["parameters"]
        if not isinstance(parameters, list):
            raise InventoryError(f"{raw['name']} 的 parameters 不是数组")
        command["parameters"] = sorted(
            (normalize_parameter(item) for item in parameters),
            key=lambda item: item["name"],
        )
    else:
        missing.append("parameters")
    if "schema" in raw:
        command["schema"] = normalize_schema(raw.get("schema"))
    else:
        missing.append("schema")
    if isinstance(raw.get("policy"), dict):
        policy = raw["policy"]
        command["policy"] = {
            key: policy.get(key)
            for key in ("mutability", "thread", "runtime", "source")
            if key in policy
        }
        if "mutability" not in policy:
            missing.append("policy.mutability")
    elif "mutability" in raw:
        command["mutability"] = raw.get("mutability")
        missing.append("policy")
    else:
        missing.append("mutability")
        missing.append("policy")
    for field in ("mainThreadRequired", "runtimeOnly"):
        if field in raw:
            command[field] = bool(raw.get(field))
        else:
            missing.append(field)
    if isinstance(raw.get("assembly"), str):
        command["assembly"] = raw["assembly"]
    else:
        missing.append("assembly")
    return command, missing


def normalize_inventory(command_list: dict[str, Any], source_kind: str) -> dict[str, Any]:
    commands = []
    missing_by_command = []
    for item in command_list.get("commands", []):
        command, missing = normalize_command(item)
        commands.append(command)
        if missing:
            missing_by_command.append({"name": command["name"], "missing": missing})
    commands.sort(key=command_sort_key)
    missing_by_command.sort(key=lambda item: item["name"])
    names = [item["name"] for item in commands]
    duplicates = sorted({name for name in names if names.count(name) > 1})
    missing_fields = sorted({field for item in missing_by_command for field in item["missing"]})
    complete = not missing_fields and command_list.get("pipelineAvailable") is not False
    limitation = None
    if command_list.get("pipelineAvailable") is False or not commands:
        limitation = (
            "没有真实 Editor metadata：pipelineAvailable 为 false 或 commands 为空。"
            "本工具不会用源码扫描补造 schema。"
        )
    elif missing_fields:
        limitation = (
            "metadataCompleteness=partial。输入可见字段已保留；缺失 "
            + ", ".join(missing_fields)
            + "。缺参数不会补成 []，缺 mutability 不会默认 write，缺 assembly 不会编造。"
            "这不是完整契约，不能称完整 schema/policy inventory。"
            "BuildListCommandsResponse 才有 parameters/policy/runtime；"
            "pi-unity list-commands --full 只有 schema、parameters 与顶层 mutability，没有 policy/assembly。"
        )
    return {
        "kind": "runtime-inventory",
        "sourceKind": source_kind,
        "pipelineAvailable": command_list.get("pipelineAvailable"),
        "metadataCompleteness": "complete" if complete else "partial",
        "count": len(commands),
        "duplicates": duplicates,
        "missingFields": missing_fields,
        "missingByCommand": missing_by_command,
        "ignoredNonContractFields": list(IGNORED_FIELDS),
        "commands": commands,
        "limitation": limitation,
    }

def command_sort_key(item: dict[str, Any]) -> tuple[str, str]:
    return (item["name"], json.dumps(item, ensure_ascii=False, sort_keys=True))


def diff_inventories(before: dict[str, Any], after: dict[str, Any]) -> dict[str, Any]:
    """按名称的稳定多重集比较。同名多条都保留，不按字典覆盖。"""
    before_rows = sorted(before.get("commands", []), key=command_sort_key)
    after_rows = sorted(after.get("commands", []), key=command_sort_key)
    before_counts: dict[str, int] = {}
    after_counts: dict[str, int] = {}
    for item in before_rows:
        before_counts[item["name"]] = before_counts.get(item["name"], 0) + 1
    for item in after_rows:
        after_counts[item["name"]] = after_counts.get(item["name"], 0) + 1
    names = sorted(set(before_counts) | set(after_counts))
    added = []
    removed = []
    changed = []
    for name in names:
        left = [item for item in before_rows if item["name"] == name]
        right = [item for item in after_rows if item["name"] == name]
        if not left:
            added.extend(right)
            continue
        if not right:
            removed.extend(left)
            continue
        if left != right:
            changed.append({"name": name, "before": left, "after": right})
    return {
        "equal": before_rows == after_rows,
        "added": added,
        "removed": removed,
        "changed": changed,
        "duplicateCounts": {
            name: {"before": before_counts.get(name, 0), "after": after_counts.get(name, 0)}
            for name in names
            if before_counts.get(name, 0) > 1 or after_counts.get(name, 0) > 1
        },
    }

SET_RE = re.compile(
    r"export const (?P<name>[A-Z0-9_]+) = new Set\(\[(?P<body>.*?)\]\)",
    re.S,
)
REGISTER_RE = re.compile(
    r"pi\.registerTool\(\{\s*name:\s*\"(?P<name>unity_[a-z0-9_]+)\"",
    re.S,
)


def parse_string_set(source: str, const_name: str) -> list[dict[str, str]]:
    match = None
    for candidate in SET_RE.finditer(source):
        if candidate.group("name") == const_name:
            match = candidate
            break
    if match is None:
        raise InventoryError(f"helpers.ts 没有导出 {const_name}")
    values = re.findall(r"\"([^\"]+)\"", match.group("body"))
    return [
        {"value": value, "source": f".pi/extensions/pi-unity-harness/helpers.ts:{const_name}"}
        for value in values
    ]


def extension_rules() -> dict[str, Any]:
    if not HELPERS.exists() or not INDEX.exists():
        raise InventoryError("无法解析来源：缺少 helpers.ts 或 index.ts")
    helpers = HELPERS.read_text(encoding="utf-8")
    index = INDEX.read_text(encoding="utf-8")
    try:
        reserved = parse_string_set(helpers, "RESERVED_PIPELINE_TOOL_NAMES")
        exclude = parse_string_set(helpers, "PIPELINE_TOOL_EXCLUDE")
        shortcuts = parse_string_set(helpers, "PIPELINE_SHORTCUT_COMMANDS")
    except InventoryError as exc:
        raise InventoryError(f"无法解析来源：{exc}") from exc
    normalize = re.search(
        r"export function normalizePipelineToolName\(commandName: string\): string \{\s*"
        r"return `unity_\$\{commandName\.replace\(/\[\^a-zA-Z0-9_\]/g, \"_\"\)\.toLowerCase\(\)\}`;\s*\}",
        helpers,
    )
    dynamic = re.search(
        r"export function pipelineDynamicToolName\(commandName: string\): string \{(?P<body>.*?)\n\}",
        helpers,
        re.S,
    )
    if normalize is None or dynamic is None:
        raise InventoryError("无法解析来源：helpers.ts 的 pipelineDynamicToolName / normalizePipelineToolName 已变化")
    reserved_names = {item["value"] for item in reserved}
    registered = []
    for match in REGISTER_RE.finditer(index):
        name = match.group("name")
        line = index[: match.start()].count("\n") + 1
        registered.append(
            {
                "name": name,
                "source": f".pi/extensions/pi-unity-harness/index.ts:{line}",
                "declaredReserved": name in reserved_names,
            }
        )
    registered.sort(key=lambda item: item["name"])
    missing = sorted(reserved_names - {item["name"] for item in registered})
    return {
        "kind": "extension-declaration-export",
        "note": "由 helpers.ts 的 export const 与函数体导出，不是手工注册表。解析失败不会回退到内置名单。",
        "naming": {
            "source": ".pi/extensions/pi-unity-harness/helpers.ts:pipelineDynamicToolName",
            "normalize": normalize.group(0),
            "dynamic": "pipelineDynamicToolName" + dynamic.group("body"),
        },
        "exclude": exclude,
        "shortcuts": shortcuts,
        "reservedToolNames": reserved,
        "staticRegisteredTools": registered,
        "reservedWithoutStaticRegistration": [
            {"name": name, "source": ".pi/extensions/pi-unity-harness/helpers.ts:RESERVED_PIPELINE_TOOL_NAMES"}
            for name in missing
        ],
    }


CLI_COMMAND_RE = re.compile(
    r"\[CliCommand\(\s*\"(?P<name>[^\"]+)\"(?P<attrs>[^\]]*)\]\s*"
    r"(?:public\s+static\s+)?(?P<ret>[\w.<>,\s\?]+?)\s+(?P<method>\w+)\s*\((?P<params>.*?)\)\s*\{",
    re.S,
)
CLI_ARG_RE = re.compile(
    r"\[CliArg\(\s*\"(?P<name>[^\"]+)\"[^\]]*?(?:Required\s*=\s*(?P<required>true|false))?[^\]]*\)\]\s*"
    r"(?P<type>[\w.<>,\?\[\]]+)\s+(?P<ident>\w+)\s*(?:=\s*(?P<default>[^,)]+))?",
    re.S,
)
POLICY_RE = re.compile(r"\[PiCommandPolicy(?:\(\s*\"(?P<value>[^\"]+)\"\s*\))?\]")
RUNTIME_ONLY_RE = re.compile(r"RuntimeOnly\s*=\s*true")
MAIN_THREAD_RE = re.compile(r"MainThreadRequired\s*=\s*(true|false)")


def csharp_json_type(type_name: str) -> str | None:
    bare = type_name.strip().removesuffix("?")
    mapping = {
        "bool": "boolean",
        "byte": "integer",
        "sbyte": "integer",
        "short": "integer",
        "ushort": "integer",
        "int": "integer",
        "uint": "integer",
        "long": "integer",
        "ulong": "integer",
        "float": "number",
        "double": "number",
        "decimal": "number",
        "string": "string",
    }
    if bare in mapping:
        return mapping[bare]
    if bare.endswith("[]") or bare.startswith("List<") or bare.startswith("IEnumerable<"):
        return "array"
    return None


def source_declarations(root: Path) -> dict[str, Any]:
    """只扫描自有 harness 源。compat/官方包不在此生成 schema。"""
    policy = load_json(POLICY) if POLICY.exists() else {}
    commands = []
    ambiguous = []
    for path in sorted(root.rglob("*.cs")):
        if "Tests" in path.parts:
            continue
        text = path.read_text(encoding="utf-8")
        relative = path.relative_to(REPO_ROOT).as_posix()
        for match in CLI_COMMAND_RE.finditer(text):
            attrs = match.group("attrs")
            params = []
            for arg in CLI_ARG_RE.finditer(match.group("params")):
                required_text = arg.group("required")
                has_default = arg.group("default") is not None
                required = required_text == "true" if required_text else not has_default
                params.append(
                    {
                        "name": arg.group("name"),
                        "required": required,
                        "csharpType": arg.group("type").strip(),
                        "jsonTypeHint": csharp_json_type(arg.group("type")),
                        "hasDefault": has_default,
                    }
                )
            policy_match = POLICY_RE.search(attrs)
            sidecar = policy.get(match.group("name"), {})
            if policy_match:
                mutability = policy_match.group("value") or "write"
                source = "attribute"
            elif isinstance(sidecar, dict) and sidecar.get("mutability"):
                mutability = sidecar["mutability"]
                source = "sidecar"
            else:
                mutability = "write"
                source = "default"
            main = MAIN_THREAD_RE.search(attrs)
            commands.append(
                {
                    "name": match.group("name"),
                    "method": match.group("method"),
                    "returnsTask": "Task" in match.group("ret"),
                    "runtimeOnlyDeclared": RUNTIME_ONLY_RE.search(attrs) is not None,
                    "mainThreadRequiredDeclared": None if main is None else main.group(1) == "true",
                    "mutabilityDeclared": mutability,
                    "policySourceDeclared": source,
                    "parameters": params,
                    "source": f"{relative}:{text[: match.start()].count(chr(10)) + 1}",
                }
            )
    commands.sort(key=lambda item: (item["name"], item["source"]))
    names = [item["name"] for item in commands]
    duplicates = sorted({name for name in names if names.count(name) > 1})
    return {
        "kind": "source-declaration-report",
        "parser": "regex",
        "notRuntimeSchema": True,
        "limitation": (
            "正则扫描，不是 Roslyn/语法树。嵌套括号、字符串伪属性、非常规属性位置和 #if 分支可能扫错。"
            "这是自有 harness 源声明，不是 Editor TypeCache/BuildListCommandsResponse。"
            "没有 JSON Schema，也不能代表官方/compat 包里的命令。缺 Editor 时不要把本报告当成 runtime schema。"
        ),
        "scannedRoot": root.relative_to(REPO_ROOT).as_posix() if root.is_relative_to(REPO_ROOT) else str(root),
        "count": len(commands),
        "duplicates": duplicates,
        "ambiguous": ambiguous,
        "commands": commands,
    }


def run_skills_check(cli: Path) -> dict[str, Any]:
    if not cli.exists():
        return {
            "kind": "skill-check",
            "executed": False,
            "normalized": False,
            "limitation": f"未执行：CLI 不存在 {cli}",
        }
    completed = subprocess.run(
        [str(cli), "skills", "check", "--json"],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=60,
        check=False,
    )
    stdout = completed.stdout.strip()
    parsed: Any = None
    if stdout:
        try:
            parsed = json.loads(stdout)
        except json.JSONDecodeError:
            parsed = None
    ok = (
        completed.returncode == 0
        and isinstance(parsed, dict)
        and parsed.get("ok") is True
        and isinstance(parsed.get("result"), dict)
        and parsed["result"].get("check") == "ok"
    )
    return {
        "kind": "skill-check",
        "executed": True,
        "cli": str(cli),
        "exitCode": completed.returncode,
        "normalized": ok,
        "rules": {
            "source": "native/src/bin/pi_unity/home.rs::static_skill_markdown",
            "placeholders": "format! 替换 {DESCRIPTION} 与 {VERSION}；提交的 SKILL.md 必须已是替换后的文本",
            "newline": "CRLF 先归一为 LF",
            "trim": "比较前去掉首尾空白",
            "implementation": "native/src/bin/pi_unity/commands.rs::normalize_skill_text",
        },
        "cliResult": parsed if parsed is not None else {"unparsedStdout": stdout, "stderr": completed.stderr.strip()},
        "blocksRuntime": False,
    }


def write_output(payload: dict[str, Any], output: Path | None) -> None:
    text = json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    if output is None:
        sys.stdout.write(text)
        return
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(text, encoding="utf-8", newline="\n")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="生成命令 schema/policy inventory 报告。默认只报告，不阻断运行时。"
    )
    sub = parser.add_subparsers(dest="mode", required=True)

    inventory = sub.add_parser("inventory", help="把真实 list-commands JSON 归一化为可审阅 inventory")
    inventory.add_argument("--input", required=True, type=Path, help="pipe 信封、CLI --json 或裸 command_list")
    inventory.add_argument("--output", type=Path)

    diff = sub.add_parser("diff", help="比较两份 inventory 或两份源声明的契约字段")
    diff.add_argument("--before", required=True, type=Path)
    diff.add_argument("--after", required=True, type=Path)
    diff.add_argument("--output", type=Path)
    diff.add_argument("--check", action="store_true", help="有 drift 时退出码 1；仍只影响本维护命令，不改 runtime")

    rules = sub.add_parser("extension-rules", help="从 extension 声明导出固定工具名与排除规则")
    rules.add_argument("--output", type=Path)

    source = sub.add_parser("source-signature", help="生成自有 harness 源声明签名（非 runtime schema）")
    source.add_argument("--root", type=Path, default=HARNESS_COMMANDS)
    source.add_argument("--output", type=Path)

    skill = sub.add_parser("skill-report", help="执行实际 CLI skills check --json，并记录归一化规则")
    skill.add_argument("--cli", required=True, type=Path, help="pi-unity 可执行文件路径")
    skill.add_argument("--output", type=Path)

    report = sub.add_parser("report", help="汇总 runtime inventory、extension 规则、源声明与 skill 检查")
    report.add_argument("--input", type=Path, help="真实 list-commands JSON；缺省时只记录限制")
    report.add_argument("--cli", type=Path, help="提供则执行 skills check --json")
    report.add_argument("--output", type=Path)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.mode == "inventory":
            command_list, kind = unwrap_command_list(load_json(args.input))
            payload = normalize_inventory(command_list, kind)
            write_output(payload, args.output)
            return 0
        if args.mode == "diff":
            payload = diff_inventories(load_json(args.before), load_json(args.after))
            write_output(payload, args.output)
            return 1 if args.check and not payload["equal"] else 0
        if args.mode == "extension-rules":
            write_output(extension_rules(), args.output)
            return 0
        if args.mode == "source-signature":
            write_output(source_declarations(args.root), args.output)
            return 0
        if args.mode == "skill-report":
            payload = run_skills_check(args.cli)
            write_output(payload, args.output)
            return 0
        if args.input:
            command_list, kind = unwrap_command_list(load_json(args.input))
            inventory = normalize_inventory(command_list, kind)
        else:
            inventory = {
                "kind": "runtime-inventory",
                "sourceKind": "missing",
                "count": 0,
                "commands": [],
                "limitation": "未提供真实 list-commands JSON，也没有 Editor。不会补造 schema。",
            }
        payload = {
            "kind": "maintenance-report",
            "blocksRuntime": False,
            "inventory": inventory,
            "extensionRules": extension_rules(),
            "sourceDeclaration": source_declarations(HARNESS_COMMANDS),
            "skill": run_skills_check(args.cli) if args.cli else {
                "kind": "skill-check",
                "executed": False,
                "normalized": False,
                "limitation": "未提供 --cli，未执行 skills check。",
            },
            "skillFilePresent": SKILL.exists(),
        }
        write_output(payload, args.output)
        return 0
    except InventoryError as exc:
        sys.stderr.write(f"command-inventory: {exc}\n")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
