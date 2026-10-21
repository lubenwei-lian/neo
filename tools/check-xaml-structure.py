#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
XAML 结构自检：标签是否配平、属性引用的 StaticResource 是否都存在。

为什么需要它：WinUI 的 XAML 编译器对「结构错误」报错行号常常指向块首而非真正那一行，
排查很费时；而 StaticResource 写错名字要到运行时才抛异常（编译期不一定拦得住）。
这个脚本在上千行的 UI 文件重构后做一遍，比人眼数标签可靠。

用法: python tools/check-xaml-structure.py <Page.xaml> [资源字典.xaml ...]
退出码 0 = 通过；1 = 有问题。
"""
import re
import sys
from pathlib import Path

VOID_TAGS = {"Run"}  # 非自闭合但也允许出现在单行的元素极少，按需扩展

def strip_comments(s: str) -> str:
    return re.sub(r"<!--.*?-->", "", s, flags=re.S)


def check_balance(p: Path) -> list[str]:
    s = strip_comments(p.read_text(encoding="utf-8-sig"))
    problems: list[str] = []
    # 标签栈：记录 (名字, 行号)
    stack: list[tuple[str, int]] = []
    # 逐个匹配 <tag ...>、<tag .../>、</tag>
    pat = re.compile(r"<(/?)([A-Za-z_][\w.:]*)((?:\"[^\"]*\"|'[^']*'|[^>\"'])*?)(/?)>", re.S)
    for m in pat.finditer(s):
        closing, name, attrs, selfclose = m.group(1), m.group(2), m.group(3), m.group(4)
        line = s.count("\n", 0, m.start()) + 1
        if name.startswith("!") or name.startswith("?"):
            continue
        if closing:
            if not stack:
                problems.append(f"第 {line} 行: </{name}> 没有对应的开始标签")
            else:
                top, tline = stack.pop()
                if top != name:
                    problems.append(f"第 {line} 行: </{name}> 与第 {tline} 行的 <{top}> 不匹配")
        elif selfclose:
            continue
        else:
            stack.append((name, line))
    for name, line in stack:
        problems.append(f"第 {line} 行: <{name}> 没有闭合")
    return problems


def collect_resource_keys(files: list[Path]) -> set[str]:
    """从资源字典里收集所有 x:Key（含主题字典内的）。"""
    keys: set[str] = set()
    for f in files:
        s = strip_comments(f.read_text(encoding="utf-8-sig"))
        keys |= set(re.findall(r'x:Key="([^"]+)"', s))
    return keys


def check_static_refs(p: Path, available: set[str]) -> list[str]:
    """检查 StaticResource 引用是否都能在给定的键集合里找到。"""
    s = strip_comments(p.read_text(encoding="utf-8-sig"))
    problems: list[str] = []
    for m in re.finditer(r'\{(?:StaticResource|ThemeResource)\s+([^}\s]+)\s*\}', s):
        key = m.group(1)
        line = s.count("\n", 0, m.start()) + 1
        if key not in available:
            problems.append(f"第 {line} 行: 引用了不存在的资源键 '{key}'")
    return problems


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    target = Path(sys.argv[1])
    dicts = [Path(a) for a in sys.argv[2:]]

    rc = 0
    bal = check_balance(target)
    print(f"== 标签配平: {'通过' if not bal else f'{len(bal)} 处问题'} ==")
    for x in bal:
        print("   " + x)
    if bal:
        rc = 1

    if dicts:
        avail = collect_resource_keys(dicts) | collect_resource_keys([target])
        refs = check_static_refs(target, avail)
        print(f"== 资源键引用: {'通过' if not refs else f'{len(refs)} 处问题'} ==")
        for x in refs:
            print("   " + x)
        if refs:
            rc = 1
        print(f"   （可用键 {len(avail)} 个，来自 {len(dicts)} 个字典 + 本文件）")

    return rc


if __name__ == "__main__":
    sys.exit(main())
