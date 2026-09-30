#!/usr/bin/env python3
"""从 pi-unity L1 JSONL 调用日志生成聚合报告。"""

from __future__ import annotations

import argparse
import glob
import json
import os
import sys
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


ERROR_ORDER = ("bridge_not_found", "compile_error", "busy", "timeout", "usage", "execution_failed")
# 集成测试的 mock 工程建在系统临时目录下；旧版测试没隔离日志目录，混进了真实日志。
TEST_PROJECT_MARKERS = ("\\appdata\\local\\temp\\", "/tmp/")


def default_log_root() -> Path:
    """按 CLI 的平台优先级解析默认日志根目录。"""
    for name in ("PI_UNITY_LOG_DIR", "USERPROFILE", "HOME"):
        value = os.environ.get(name, "").strip()
        if value:
            return Path(value) / ".pi-unity" if name != "PI_UNITY_LOG_DIR" else Path(value)
    return Path(".pi-unity")


def event_paths(path: Path) -> list[Path]:
    """接受单个 JSONL、logs 目录或日志根目录，并包含轮转文件。"""
    if path.is_file():
        return [path]
    logs_dir = path / "logs" if path.name != "logs" else path
    return sorted(Path(item) for item in glob.glob(str(logs_dir / "events-*.jsonl")))


def load_events(paths: Iterable[Path]) -> tuple[list[dict[str, Any]], int, int]:
    events: list[dict[str, Any]] = []
    malformed = 0
    ignored = 0
    for path in paths:
        try:
            handle = path.open(encoding="utf-8")
        except OSError as exc:
            print(f"warning: cannot read {path}: {exc}", file=sys.stderr)
            continue
        with handle:
            for line_number, line in enumerate(handle, 1):
                if not line.strip():
                    continue
                try:
                    value = json.loads(line)
                except json.JSONDecodeError:
                    malformed += 1
                    continue
                if not isinstance(value, dict) or value.get("kind") != "call":
                    ignored += 1
                    continue
                events.append(value)
    return events, malformed, ignored


def is_test_event(event: dict[str, Any], traces_dir: Path) -> bool:
    """只有带 trace 的调用能判定：trace 里的工程根在临时目录下即视为测试调用。"""
    trace_id = event.get("traceId")
    if not isinstance(trace_id, str) or len(trace_id) < 8:
        return False
    day = f"{trace_id[:4]}-{trace_id[4:6]}-{trace_id[6:8]}"
    try:
        text = (traces_dir / day / f"{trace_id}.log").read_text(encoding="utf-8", errors="replace")
    except OSError:
        return False
    for line in text.splitlines():
        if "[discover] Project root:" in line:
            lowered = line.lower()
            return any(marker in lowered for marker in TEST_PROJECT_MARKERS)
    return False


def parse_time(value: str) -> datetime:
    normalized = value.strip().replace("Z", "+00:00")
    result = datetime.fromisoformat(normalized)
    if result.tzinfo is None:
        result = result.replace(tzinfo=timezone.utc)
    return result.astimezone(timezone.utc)


def event_time(event: dict[str, Any]) -> datetime | None:
    value = event.get("tsUtc")
    if not isinstance(value, str):
        return None
    try:
        return parse_time(value)
    except ValueError:
        return None


def duration(event: dict[str, Any]) -> int:
    value = event.get("durationMs", 0)
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) else 0


def error_name(event: dict[str, Any]) -> str | None:
    value = event.get("errorType")
    return value if isinstance(value, str) and value else None


def percentile(values: list[float], fraction: float) -> float:
    if not values:
        return 0
    ordered = sorted(values)
    index = min(len(ordered) - 1, int((len(ordered) - 1) * fraction))
    return ordered[index]


def ratio(numerator: int, denominator: int) -> float:
    return numerator / denominator * 100 if denominator else 0.0


def summarize(
    events: list[dict[str, Any]], malformed: int, ignored: int, paths: list[Path], test_rows: int = 0
) -> dict[str, Any]:
    failures = [event for event in events if event.get("exitCode") != 0]
    errors = Counter(error_name(event) for event in events if error_name(event))
    by_command: dict[str, list[dict[str, Any]]] = defaultdict(list)
    by_day: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for event in events:
        command = event.get("subcommand")
        by_command[command if isinstance(command, str) else "unknown"].append(event)
        timestamp = event_time(event)
        day = timestamp.date().isoformat() if timestamp else "unknown"
        by_day[day].append(event)

    command_rows = []
    for command, rows in by_command.items():
        command_failures = sum(row in failures for row in rows)
        values = [duration(row) for row in rows]
        command_rows.append({
            "subcommand": command,
            "count": len(rows),
            "failures": command_failures,
            "failureRatePct": round(ratio(command_failures, len(rows)), 1),
            "durationMs": sum(values),
            "p50Ms": percentile(values, 0.5),
            "p90Ms": percentile(values, 0.9),
            "maxMs": max(values, default=0),
        })
    command_rows.sort(key=lambda row: (-row["durationMs"], row["subcommand"]))

    day_rows = []
    for day, rows in by_day.items():
        day_failures = sum(row in failures for row in rows)
        day_rows.append({
            "date": day,
            "count": len(rows),
            "failures": day_failures,
            "failureRatePct": round(ratio(day_failures, len(rows)), 1),
            "durationMs": sum(duration(row) for row in rows),
        })
    day_rows.sort(key=lambda row: (-row["failureRatePct"], -row["failures"], row["date"]))

    reconnects = Counter()
    for event in events:
        flags = event.get("flags")
        value = flags.get("reconnects") if isinstance(flags, dict) else None
        if isinstance(value, int) and not isinstance(value, bool):
            reconnects[str(value)] += 1

    errors_without_project = Counter(
        error_name(event)
        for event in failures
        if not event.get("projectHash") and error_name(event)
    )
    return {
        "files": [str(path) for path in paths],
        "rows": len(events),
        "malformedRows": malformed,
        "ignoredRows": ignored,
        "testRows": test_rows,
        "firstUtc": min((event.get("tsUtc", "") for event in events), default=None),
        "lastUtc": max((event.get("tsUtc", "") for event in events), default=None),
        "clients": dict(Counter(event.get("client", "unknown") for event in events)),
        "sessions": sum(bool(event.get("sessionId")) for event in events),
        "hostSessions": sum(bool(event.get("hostSessionId")) for event in events),
        "failures": len(failures),
        "failureRatePct": round(ratio(len(failures), len(events)), 1),
        "durationMs": sum(duration(event) for event in events),
        "failureDurationMs": sum(duration(event) for event in failures),
        "errors": dict(errors.most_common()),
        "errorsWithoutProject": dict(errors_without_project.most_common()),
        "reconnects": dict(sorted(reconnects.items(), key=lambda item: int(item[0]))),
        "commands": command_rows,
        "days": day_rows,
    }


def format_duration(value: float) -> str:
    seconds = value / 1000
    if seconds < 60:
        return f"{seconds:.1f}s"
    return f"{seconds / 60:.1f}min"


def print_report(report: dict[str, Any], top: int) -> None:
    print(f"rows: {report['rows']}  failures: {report['failures']} ({report['failureRatePct']:.1f}%)")
    print(f"span: {report['firstUtc'] or '-'} .. {report['lastUtc'] or '-'}")
    print(f"duration: {format_duration(report['durationMs'])}  failure-duration: {format_duration(report['failureDurationMs'])}")
    print(f"clients: {', '.join(f'{key}={value}' for key, value in report['clients'].items()) or '-'}")
    print(f"session ids: {report['sessions']}  host session ids: {report['hostSessions']}")
    print("errors:")
    for name, count in report["errors"].items():
        no_project = report["errorsWithoutProject"].get(name, 0)
        suffix = f", no-project={no_project}" if no_project else ""
        print(f"  {name}: {count}{suffix}")
    print("top subcommands by total duration:")
    for row in report["commands"][:top]:
        print(
            f"  {row['subcommand']}: count={row['count']} duration={format_duration(row['durationMs'])} "
            f"failures={row['failures']} ({row['failureRatePct']:.1f}%) "
            f"p50={format_duration(row['p50Ms'])} p90={format_duration(row['p90Ms'])} max={format_duration(row['maxMs'])}"
        )
    print("reconnect buckets:")
    print("  " + (", ".join(f"{key}={value}" for key, value in report["reconnects"].items()) or "-") )
    if report["days"]:
        worst = report["days"][0]
        print(f"worst day by failure rate: {worst['date']} ({worst['failureRatePct']:.1f}%, {worst['failures']}/{worst['count']})")
    if report["malformedRows"] or report["ignoredRows"] or report["testRows"]:
        print(
            f"skipped: malformed={report['malformedRows']} ignored={report['ignoredRows']} "
            f"test={report['testRows']}"
        )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="聚合 pi-unity logs/events-*.jsonl 调用日志")
    parser.add_argument("path", nargs="?", type=Path, help="JSONL 文件、logs 目录或日志根目录")
    parser.add_argument("--since", type=parse_time, help="仅统计该 UTC 时间之后的事件")
    parser.add_argument("--until", type=parse_time, help="仅统计该 UTC 时间之前的事件")
    parser.add_argument("--top", type=int, default=10, help="显示耗时最高的命令数（默认 10）")
    parser.add_argument("--json", action="store_true", help="输出 JSON 报告")
    parser.add_argument("--include-test", action="store_true", help="保留工程根在临时目录下的测试调用")
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.top < 1:
        parser.error("--top 必须大于 0")
    path = args.path or (default_log_root() / "logs")
    paths = event_paths(path)
    if not paths:
        parser.error(f"找不到日志文件: {path}")
    events, malformed, ignored = load_events(paths)
    traces_dir = paths[0].parent / "traces"
    filtered = []
    test_rows = 0
    for event in events:
        timestamp = event_time(event)
        if args.since and (timestamp is None or timestamp < args.since):
            continue
        if args.until and (timestamp is None or timestamp > args.until):
            continue
        if not args.include_test and is_test_event(event, traces_dir):
            test_rows += 1
            continue
        filtered.append(event)
    report = summarize(filtered, malformed, ignored, paths, test_rows)
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True))
    else:
        print_report(report, args.top)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
