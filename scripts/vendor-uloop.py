#!/usr/bin/env python3
"""把 unity-cli-loop 的 Packages/src 子集重制到 unity/com.pi.unity-harness/Vendor/Uloop。

用法:
    python scripts/vendor-uloop.py --ref v3.6.3
    python scripts/vendor-uloop.py --ref v3.6.3 --src ../unity-cli-loop
    python scripts/vendor-uloop.py --ref v3.6.3 --dry-run

设计约束:
- 目标布局与上游 Packages/src 同构（Vendor/Uloop/<相对路径>），升级时可整树 diff。
- 只做三件转换: 排除 Skill/DESIGN/meta 之外的噪音、asmdef 的 GUID 引用转程序集名并丢掉
  未 vendor 的引用、重放 harness 本地补丁（InternalsVisibleTo / publicize / 包根路径字面量）。
- harness 自有文件放在 scripts/vendor-extras/，按 vendor 相对路径复制回来，不会被覆盖。
"""

from __future__ import annotations

import argparse
import filecmp
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
MANIFEST = REPO_ROOT / "scripts" / "vendor-uloop.manifest.json"
EXTRAS_DIR = REPO_ROOT / "scripts" / "vendor-extras"
VENDOR_ROOT = REPO_ROOT / "unity" / "com.pi.unity-harness" / "Vendor" / "Uloop"
UPSTREAM_SUBDIR = "Packages/src"
SKIP_DIR_NAMES = {"Skill"}
SKIP_FILE_NAMES = {"DESIGN.md"}
SKIP_SUFFIXES = (".meta",)


def run(cmd: list[str], cwd: Path | None = None) -> str:
    result = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"命令失败: {' '.join(cmd)}\n{result.stderr}")
    return result.stdout


def extract_upstream(src: Path, ref: str, dest: Path) -> None:
    archive = dest / "src.tar"
    with open(archive, "wb") as handle:
        handle.write(subprocess.run(
            ["git", "-C", str(src), "archive", ref, UPSTREAM_SUBDIR],
            capture_output=True, check=True).stdout)
    with tarfile.open(archive) as tar:
        try:
            tar.extractall(dest, filter="data")
        except TypeError:
            tar.extractall(dest)
    archive.unlink()


def load_asmdefs(root: Path) -> tuple[dict[str, Path], dict[str, str]]:
    by_name: dict[str, Path] = {}
    guid: dict[str, str] = {}
    for path in root.rglob("*.asmdef"):
        try:
            data = json.loads(path.read_text(encoding="utf-8-sig"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            continue
        name = data.get("name")
        if not name:
            continue
        by_name[name] = path.parent
        meta = path.with_suffix(".asmdef.meta")
        if meta.exists():
            found = re.search(r"guid:\s*([0-9a-f]+)", meta.read_text(encoding="utf-8"))
            if found:
                guid[found.group(1)] = name
    return by_name, guid


def matches_any(rel: str, patterns: list[str]) -> bool:
    import fnmatch
    return any(fnmatch.fnmatch(rel, pattern) for pattern in patterns)


def should_skip_dir(rel_dir: str) -> bool:
    return Path(rel_dir).name in SKIP_DIR_NAMES


def collect_files(root: Path, entry: dict, nested_asmdefs: set[str],
                  manifest_names: set[str], global_exclude: list[str]) -> tuple[list[str], list[str]]:
    """返回 (要复制的相对文件列表, 被清单跳过的嵌套 asmdef 目录名列表)。"""
    exclude = entry.get("exclude", [])
    files: list[str] = []
    skipped_nested: list[str] = []
    for dirpath, dirnames, filenames in os.walk(root):
        rel_dir = os.path.relpath(dirpath, root)
        rel_dir = "" if rel_dir == "." else rel_dir
        keep: list[str] = []
        for name in sorted(dirnames):
            child = os.path.join(rel_dir, name) if rel_dir else name
            if should_skip_dir(child):
                continue
            if name.endswith("~"):
                keep.append(name)
                continue
            child_asmdefs = {p.stem for p in (Path(dirpath) / name).glob("*.asmdef")}
            if child_asmdefs & nested_asmdefs:
                skipped_nested.append(child)
                if not child_asmdefs <= manifest_names:
                    sys.exit(f"嵌套 asmdef {child_asmdefs} 不在清单里，拒绝静默跳过: {child}")
                continue
            keep.append(name)
        dirnames[:] = keep
        for name in sorted(filenames):
            rel = os.path.join(rel_dir, name) if rel_dir else name
            if name in SKIP_FILE_NAMES or name.endswith(SKIP_SUFFIXES):
                continue
            if matches_any(rel, global_exclude) or matches_any(rel, exclude):
                continue
            files.append(rel)
    return files, skipped_nested


def convert_asmdef(source: Path, target: Path, vendor_names: set[str],
                   externals: set[str], guid_map: dict[str, str],
                   external_guids: dict[str, str]) -> list[str]:
    data = json.loads(source.read_text(encoding="utf-8-sig"))
    kept: list[str] = []
    dropped: list[str] = []
    for ref in data.get("references", []):
        if ref.startswith("GUID:"):
            guid = ref.split(":", 1)[1]
            name = guid_map.get(guid) or external_guids.get(guid) or ref
        else:
            name = ref
        if name in vendor_names or name in externals:
            if name in kept:
                continue
            if ref.startswith("GUID:"):
                kept.append(name)
            else:
                kept.append(ref)
        else:
            dropped.append(name)
    data["references"] = kept
    data["autoReferenced"] = data.get("autoReferenced", False)
    target.write_text(json.dumps(data, indent=4, ensure_ascii=False) + "\n", encoding="utf-8")
    return dropped


def apply_patches(path: Path, rel_to_vendor: str, patches: dict) -> None:
    if path.suffix != ".cs":
        return
    text = original = path.read_text(encoding="utf-8-sig")
    name = path.name
    if name == "AssemblyInfo.cs":
        block = "".join(
            f'\n[assembly: InternalsVisibleTo("{assembly}")]'
            for assembly in patches["internalsVisibleTo"]
            if f'InternalsVisibleTo("{assembly}")' not in text)
        if block:
            text = text.rstrip("\n") + "\n" + block + "\n"
            text = text.replace(
                "using System.Runtime.CompilerServices;\n\n\n",
                "using System.Runtime.CompilerServices;\n\n")
    for glob, needles in patches["publicize"].items():
        import fnmatch
        if not fnmatch.fnmatch(rel_to_vendor, glob):
            continue
        for needle in needles:
            text = text.replace(needle, needle.replace("internal", "public"))
    for namespace in patches["stripUsings"]:
        text = re.sub(rf"^using {re.escape(namespace)};\r?\n", "", text, flags=re.MULTILINE)
    prefix = patches["pathLiteralPrefix"]
    text = text.replace(prefix["from"], prefix["to"])
    if text != original:
        path.write_text(text, encoding="utf-8")


def copy_meta(source_file: Path, target_file: Path) -> None:
    # .dll.meta 故意不复制：沿用上游 GUID 会让 Unity 复用旧路径的导入 artifact。
    if source_file.suffix == ".dll":
        return
    meta = source_file.with_name(source_file.name + ".meta")
    if meta.exists():
        shutil.copy2(meta, target_file.with_name(target_file.name + ".meta"))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--ref", required=True, help="unity-cli-loop 的 git ref，例如 v3.6.3")
    parser.add_argument("--src", default=str(REPO_ROOT.parent / "unity-cli-loop"))
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    src = Path(args.src).resolve()
    if not (src / ".git").exists():
        sys.exit(f"不是 git 仓库: {src}")
    commit = run(["git", "-C", str(src), "rev-parse", args.ref]).strip()
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    entries = manifest["assemblies"]
    manifest_names = {entry["name"] for entry in entries}
    externals = set(manifest["externals"])
    global_exclude = manifest.get("globalExclude", [])
    vendor_names = set(manifest_names)

    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        extract_upstream(src, args.ref, tmp_path)
        upstream_root = tmp_path / UPSTREAM_SUBDIR
        upstream_asmdefs, guid_map = load_asmdefs(upstream_root)

        missing = manifest_names - set(upstream_asmdefs)
        if missing:
            sys.exit(f"上游 {args.ref} 缺少清单中的 asmdef: {sorted(missing)}")

        if args.dry_run:
            for entry in entries:
                files, nested = collect_files(upstream_asmdefs[entry["name"]], entry,
                                              manifest_names, manifest_names, global_exclude)
                print(f"[dry-run] {entry['name']}: {len(files)} 文件, 跳过嵌套 {len(nested)}")
            return

        report: list[str] = []
        dropped_refs: dict[str, list[str]] = {}
        for root in ("Editor", "Runtime"):
            shutil.rmtree(VENDOR_ROOT / root, ignore_errors=True)

        for entry in entries:
            name = entry["name"]
            source_dir = upstream_asmdefs[name]
            rel_dir = source_dir.relative_to(upstream_root)
            target_dir = VENDOR_ROOT / rel_dir
            files, nested = collect_files(source_dir, entry, manifest_names, manifest_names,
                                          global_exclude)
            for rel in files:
                source_file = source_dir / rel
                target_file = target_dir / rel
                target_file.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source_file, target_file)
                copy_meta(source_file, target_file)
            asmdef = next(source_dir.glob("*.asmdef"))
            dropped = convert_asmdef(asmdef, target_dir / asmdef.name, vendor_names,
                                     externals, guid_map, manifest.get("externalGuids", {}))
            copy_meta(asmdef, target_dir / asmdef.name)
            if dropped:
                dropped_refs[name] = dropped
            report.append(f"| `{name}` | `{rel_dir}` | {len(files)} |")

        for path in VENDOR_ROOT.rglob("*.cs"):
            apply_patches(path, str(path.relative_to(VENDOR_ROOT)), manifest["patches"])

        if EXTRAS_DIR.exists():
            for source_file in EXTRAS_DIR.rglob("*"):
                if source_file.is_dir():
                    continue
                rel = source_file.relative_to(EXTRAS_DIR)
                target_file = VENDOR_ROOT / rel
                target_file.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source_file, target_file)

        write_record(args.ref, commit, report, dropped_refs, entries)
        print(f"vendor 完成: ref={args.ref} commit={commit[:8]} 程序集={len(entries)}")
        for name, dropped in dropped_refs.items():
            print(f"  丢弃未 vendor 的引用 {name} -> {dropped}")


def write_record(ref: str, commit: str, report: list[str],
                 dropped_refs: dict[str, list[str]], entries: list[dict]) -> None:
    body = f"""# Vendor/Uloop 来源与本地补丁

本目录是 [unity-cli-loop](https://github.com/hatayama/unity-cli-loop)（uloop，MIT）`Packages/src`
的子集快照，由 `scripts/vendor-uloop.py` 重制。不要手改：改动会被下一次重制覆盖，
harness 自有内容请放 `scripts/vendor-extras/`，行为补丁请改 `scripts/vendor-uloop.manifest.json`。

- 上游 ref: `{ref}`
- 上游 commit: `{commit}`
- 重制命令: `python scripts/vendor-uloop.py --ref {ref}`

## 复制范围

| asmdef | 上游路径（相对 Packages/src） | 文件数 |
| --- | --- | --- |
{chr(10).join(report)}

不做 vendor 的上游程序集：`UnityCLILoop.Application`、`UnityCLILoop.Domain`、
`UnityCLILoop.Infrastructure`、`UnityCLILoop.Presentation`、`UnityCLILoop.CompositionRoot`，
以及 uloop 自己的工具层（`FirstPartyTools/` 下除清单外的目录、`ExecuteDynamicCode` 的
tool/use-case/schema 文件）。

## 本地补丁（由脚本自动重放）

1. `AssemblyInfo.cs`：追加 `InternalsVisibleTo`，让 harness 程序集能访问 vendor 内部类型。
2. `*EditorStartup.cs` 与 `ToolContracts/MainThreadSwitcher.cs`：把 `internal static`
   放开成 `public static`，harness 从 `[CliCommand]` 包装里直接调用。
3. 包根路径字面量：`"Editor/FirstPartyTools/` 前缀改为 `"Vendor/Uloop/Editor/FirstPartyTools/`，
   让 `HotReloadConstants.WorkerSourcePackageRelativePath` 等常量指向实际位置。
4. asmdef 的 `GUID:` 引用转成程序集名，并丢弃未被 vendor 的程序集引用。

## 未 vendor 的引用（上游 asmdef 里有、但对应程序集没有复制）

{chr(10).join(f"- `{name}`: {', '.join(refs)}" for name, refs in dropped_refs.items()) or "- 无"}
"""
    (VENDOR_ROOT / "VENDOR.md").write_text(body, encoding="utf-8")


if __name__ == "__main__":
    main()
