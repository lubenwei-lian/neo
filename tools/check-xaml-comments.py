#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
neo XAML 注释检查器
===================

XML 规范禁止注释内出现连续两个减号（`--`），XAML 编译器会报：
    WMC9997: XML 注释中不能包含"--"，并且"-"不能是最后一个字符

这个坑在 2026-10-19 的 UI 重构中**反复踩了 4 次**，来源分别是：
  ① 中文破折号 `——`
  ② `====...` 分隔线注释块结尾紧接 `-->` 时形成 `--`
  ③ 直接抄 CSS 变量写法 `var(--accent)`
  ④ 抄原型 CSS 的 `44 - 20 - 3 - 3` 之类算式后紧跟中文时不构成 `--`，但
     `top:3 left:3` 这类连写若带减号就会出现

用法：
    python tools/check-xaml-comments.py           # 检查 src 下全部 .xaml
    python tools/check-xaml-comments.py --fix     # 自动把注释内的 `--` 替换为 `—`（不改变代码）
退出码：0 = 通过，1 = 有违规
"""
from __future__ import annotations
import argparse
import os
import re
import sys

COMMENT_RE = re.compile(r"<!--(.*?)-->", re.S)

# 分隔线注释块：`<!--` 之后紧跟一串 = 或 -，结尾 `-->` 会与线中的 `-` 拼成 `--`
SEP_LINE_RE = re.compile(r"(=\s*)$|(-\s*)$", re.M)


def scan(path: str):
    """返回 [(行号, 片段, 说明)]"""
    with open(path, "r", encoding="utf-8") as f:
        src = f.read()
    out = []
    for m in COMMENT_RE.finditer(src):
        inner = m.group(1)
        if "--" not in inner:
            continue
        # 起始行号（1-based）
        base = src.count("\n", 0, m.start()) + 1
        for i, seg in enumerate(inner.split("\n")):
            if "--" in seg:
                idx = seg.find("--")
                ctx = seg[max(0, idx - 24): idx + 26].strip()
                why = "CSS 变量写法" if "var(" in seg else (
                    "破折号/分隔线" if "—" in seg or "=" in seg else "连续减号")
                out.append((base + i, ctx, why))
    return out


def fix(path: str) -> int:
    """把注释内的 `--` 换成中文破折号 `—`，仅限注释内部。"""
    with open(path, "r", encoding="utf-8") as f:
        src = f.read()

    changed = 0

    def repl(m):
        nonlocal changed
        inner = m.group(1)
        if "--" not in inner:
            return m.group(0)
        new_inner = inner.replace("--", "—")
        changed += 1
        return "<!--" + new_inner + "-->"

    new_src = COMMENT_RE.sub(repl, src)
    if changed:
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(new_src)
    return changed


def main():
    ap = argparse.ArgumentParser(description="XAML 注释 -- 检查器")
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ap.add_argument("--src", default=os.path.join(root, "src"))
    ap.add_argument("--fix", action="store_true", help="自动把注释内的 -- 换成 —")
    args = ap.parse_args()

    files = []
    for base, _dirs, names in os.walk(args.src):
        if f"{os.sep}obj{os.sep}" in base or f"{os.sep}bin{os.sep}" in base:
            continue
        for n in names:
            if n.endswith(".xaml"):
                files.append(os.path.join(base, n))

    total = 0
    for p in sorted(files):
        if args.fix:
            n = fix(p)
            if n:
                total += n
                print("  已修正 %-52s %d 处注释" % (os.path.relpath(p, root), n))
            continue
        hits = scan(p)
        if hits:
            total += len(hits)
            print("  [违规] %s" % os.path.relpath(p, root))
            for line, ctx, why in hits:
                print("      行 %-5d %-14s %s" % (line, why, ctx))

    if args.fix:
        print("共修正 %d 处" % total)
        return 0

    if total == 0:
        print("OK：XAML 注释检查通过（未发现连续两个减号）")
        return 0
    print()
    print("发现 %d 处违规。请运行：python tools/check-xaml-comments.py --fix" % total)
    return 1


if __name__ == "__main__":
    sys.exit(main())
