#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""迁移对拍护栏（ADR-010 决策 6 / §5 S1 ④，一次性工具）。

对比同一 sample.json 数据下 C# 命令式版式（DocumentPdfService，基线）与
layout.json 解释层（QuestPdfLayoutRenderer）的渲染文本：逐元素内容与顺序必须一致。
对拍快照由 LayoutMigrationSnapshotTests 生成到 logs/layout-migration/{module}/。

对拍通过后 C# 版式退役（决策 6），本脚本与快照测试随之退役，不再持续运行。

用法（仓库根，Windows 控制台需先设置 $env:PYTHONIOENCODING='utf-8'）：
    python scripts/check-layout-migration.py
"""
from __future__ import annotations

import difflib
import pathlib
import sys

import pdfplumber


ROOT = pathlib.Path(__file__).resolve().parents[1]
MIGRATION_DIR = ROOT / "logs" / "layout-migration"

# 24 张内置版式（DocumentLayoutProfiles.All 全量）
MODULE_IDS = [
    "1404", "1604", "1405", "1406", "170101", "170102", "170201", "170202",
    "1607", "1615", "1606", "1408", "170103", "170203", "1407", "1409",
    "1608", "1612", "1610", "1503", "1514", "1504", "1515", "1616",
]


def extract_texts(pdf_path: pathlib.Path) -> list[str]:
    """提取 PDF 全部页面的文本行（内容 + 阅读顺序），忽略坐标。"""
    lines: list[str] = []
    with pdfplumber.open(str(pdf_path)) as pdf:
        for page in pdf.pages:
            lines.extend(line["text"] for line in page.extract_text_lines())
    return lines


def main() -> int:
    if not MIGRATION_DIR.exists():
        print(f"FAIL 未找到对拍快照目录：{MIGRATION_DIR}")
        print("      先运行 LayoutMigrationSnapshotTests 生成 legacy.pdf / interpreter.pdf。")
        return 1

    failures = 0
    for module_id in MODULE_IDS:
        legacy_path = MIGRATION_DIR / module_id / "legacy.pdf"
        interpreter_path = MIGRATION_DIR / module_id / "interpreter.pdf"
        if not legacy_path.exists() or not interpreter_path.exists():
            print(f"FAIL {module_id}  缺少 legacy/interpreter PDF")
            failures += 1
            continue
        legacy = extract_texts(legacy_path)
        interpreter = extract_texts(interpreter_path)
        if legacy == interpreter:
            print(f"PASS {module_id}")
            continue
        print(f"DIFF {module_id}  （内容或顺序不一致，需人工核对）")
        for line in difflib.unified_diff(
            legacy, interpreter, fromfile="legacy", tofile="interpreter", lineterm="", n=0
        ):
            print("    " + line)
        failures += 1

    if failures == 0:
        print(f"PASS 全部 {len(MODULE_IDS)} 张内置版式对拍一致")
        return 0
    print(f"FAIL {failures}/{len(MODULE_IDS)} 张不一致")
    return 1


if __name__ == "__main__":
    sys.exit(main())
