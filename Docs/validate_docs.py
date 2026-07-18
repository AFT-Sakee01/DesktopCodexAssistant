#!/usr/bin/env python3
"""文档治理校验 Gate。

检查四个项目 JSONL 的可解析性、唯一键、源码/文档路径和 CHANGELOG 类型。
此文件复用 Codex doc-governance skill 的标准校验器，便于项目内重复执行。
"""

import argparse
import collections
import json
import os
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

FILES = {
    "Docs/Indexes/FEATURE_INDEX.jsonl": "feature_id",
    "Docs/Interfaces/INTERFACE_INDEX.jsonl": "id",
    "Docs/Technical/INDEX.jsonl": "id",
    "Docs/Maintenance/CHANGELOG.jsonl": "id",
}

CHANGE_TYPES = {
    "feature", "fix", "behavior_change", "ui_change", "perf", "refactor",
    "documentation", "spec", "release", "deployment", "revert",
    "confirmed_issue", "correction",
}


def iter_jsonl(path):
    with open(path, encoding="utf-8") as handle:
        for lineno, line in enumerate(handle, 1):
            if line.strip():
                yield lineno, line


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".", help="项目根目录")
    parser.add_argument("--strict", action="store_true", help="change_type 越表也算失败")
    args = parser.parse_args()
    root = os.path.abspath(args.root)
    fail = False
    warn_count = 0
    parsed = {}

    for rel, key in FILES.items():
        path = os.path.join(root, rel)
        if not os.path.exists(path):
            print(f"SKIP {rel} (不存在)")
            continue
        rows = []
        seen = collections.Counter()
        for lineno, line in iter_jsonl(path):
            try:
                obj = json.loads(line)
            except Exception as exc:  # noqa: BLE001
                print(f"FAIL {rel}:{lineno} JSON 解析失败: {exc}")
                fail = True
                continue
            rows.append(obj)
            value = obj.get(key, "")
            if value:
                seen[value] += 1
        duplicate = [item for item, count in seen.items() if count > 1]
        if duplicate:
            print(f"FAIL {rel} 重复 {key}: {duplicate}")
            fail = True
        parsed[rel] = rows

    for obj in parsed.get("Docs/Indexes/FEATURE_INDEX.jsonl", []):
        if obj.get("status") == "removed":
            continue
        for file_path in obj.get("primary_files", []) or []:
            if not os.path.exists(os.path.join(root, file_path)):
                print(f"FAIL FEATURE_INDEX {obj.get('feature_id')} 引用缺失文件: {file_path}")
                fail = True

    for obj in parsed.get("Docs/Technical/INDEX.jsonl", []):
        for key in ("doc_path", "spec_path"):
            path = obj.get(key)
            if path and not os.path.exists(os.path.join(root, path)):
                print(f"FAIL Technical INDEX {obj.get('id')} 引用缺失文档: {path}")
                fail = True

    for obj in parsed.get("Docs/Maintenance/CHANGELOG.jsonl", []):
        change_type = obj.get("change_type")
        if change_type and change_type not in CHANGE_TYPES:
            label = "FAIL" if args.strict else "WARN"
            print(f"{label} CHANGELOG {obj.get('id')} change_type 越表: {change_type}")
            fail = fail or args.strict
            warn_count += 0 if args.strict else 1

    if fail:
        print("RESULT: FAIL")
        return 1
    suffix = f"(含 {warn_count} 条警告)" if warn_count else ""
    print(f"RESULT: PASS {suffix}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
