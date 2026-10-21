#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
neo 桌面版 · rule-set 生成器
============================

把 v2fly 格式的 geosite.dat / geoip.dat 转换成 sing-box 用的二进制 .srs，
让 neo 的 sing-box(TUN) 路径与 xray 路径使用**同源同量**的分流清单。

为什么需要它
------------
实测（2026-10-18）：
  xray 路径吃 v2fly geosite.dat，CN 分类 111,415 条；
  sing-box 路径吃上游 sing-geosite 的精简 geosite-cn.srs，只有 9,302 条。
  差 12 倍 ⇒ 104,719 个国内域名后缀在 TUN 下被判为"非国内"而走代理。
  抽样 80 个缺口域名用国内 DNS 解析，80% 返回中国 IP（本该直连）。
A/B 对照（同一节点/同一规则，仅换清单）：6 个域名由 PROXY 翻为 DIRECT，被墙站 0 误伤。

用法
----
  python tools/gen-rulesets.py                    # 用 runtime 下的 .dat 重新生成全部 .srs
  python tools/gen-rulesets.py --check            # 只校验现有 .srs 是否达标，不写文件
  python tools/gen-rulesets.py --dat-dir <dir>    # 指定 .dat 所在目录
  python tools/gen-rulesets.py --out-dir <dir>    # 指定 .srs 输出目录

依赖：无第三方库（内置 protobuf 解析 + 调用同目录的 sing-box.exe 编译）
"""
from __future__ import annotations
import argparse
import json
import os
import socket
import subprocess
import sys
import tempfile

# ---------------------------------------------------------------- protobuf 小工具

def read_varint(buf: bytes, i: int):
    r = 0; s = 0
    while True:
        b = buf[i]; i += 1
        r |= (b & 0x7F) << s
        if not (b & 0x80):
            break
        s += 7
    return r, i


def iter_fields(buf: bytes, start: int, end: int):
    i = start
    while i < end:
        tag, i = read_varint(buf, i)
        fn = tag >> 3
        wt = tag & 7
        if wt == 0:
            v, i = read_varint(buf, i)
            yield fn, wt, v
        elif wt == 2:
            ln, i = read_varint(buf, i)
            yield fn, wt, buf[i:i + ln]
            i += ln
        elif wt == 5:
            yield fn, wt, buf[i:i + 4]
            i += 4
        elif wt == 1:
            yield fn, wt, buf[i:i + 8]
            i += 8
        else:
            raise ValueError("unknown wire type %d at offset %d" % (wt, i))


# ---------------------------------------------------------------- geosite 解析

def _parse_domain(b: bytes):
    t = 0; v = b""
    for fn, wt, val in iter_fields(b, 0, len(b)):
        if fn == 1 and wt == 0:
            t = val
        elif fn == 2 and wt == 2:
            v = val
    return t, v.decode("utf-8", "replace")


def load_geosite(path: str) -> dict:
    """返回 {分类名: [(type, value), ...]}；type: 0=Plain 1=Regex 2=Domain 3=Full"""
    data = open(path, "rb").read()
    cats: dict = {}
    for fn, wt, val in iter_fields(data, 0, len(data)):
        if fn != 1 or wt != 2:
            continue
        cc = ""
        doms = []
        for f2, w2, v2 in iter_fields(val, 0, len(val)):
            if f2 == 1 and w2 == 2:
                cc = v2.decode("utf-8", "replace")
            elif f2 == 2 and w2 == 2:
                doms.append(_parse_domain(v2))
        if cc:
            cats[cc] = doms
    return cats


def geosite_ruleset(cats: dict, category: str) -> dict:
    if category not in cats:
        raise SystemExit("geosite 分类不存在: %s" % category)
    full, suf, rex = set(), set(), set()
    for t, v in cats[category]:
        v = v.lower()
        if t == 3:
            full.add(v)
        elif t == 2:
            suf.add(v)
        elif t == 1:
            rex.add(v)
        else:                      # Plain / 未知，按后缀处理（保守）
            suf.add(v)
    rule: dict = {}
    if full:
        rule["domain"] = sorted(full)
    if suf:
        rule["domain_suffix"] = sorted(suf)
    if rex:
        rule["domain_regex"] = sorted(rex)
    return {"version": 1, "rules": [rule]}


# ---------------------------------------------------------------- geoip 解析

def _parse_cidr(b: bytes):
    ip = b""; prefix = 0
    for fn, wt, val in iter_fields(b, 0, len(b)):
        if fn == 1 and wt == 2:
            ip = val
        elif fn == 2 and wt == 0:
            prefix = val
    return ip, prefix


def load_geoip(path: str) -> dict:
    data = open(path, "rb").read()
    cats: dict = {}
    for fn, wt, val in iter_fields(data, 0, len(data)):
        if fn != 1 or wt != 2:
            continue
        cc = ""
        cidrs = []
        for f2, w2, v2 in iter_fields(val, 0, len(val)):
            if f2 == 1 and w2 == 2:
                cc = v2.decode("utf-8", "replace")
            elif f2 == 2 and w2 == 2:
                cidrs.append(_parse_cidr(v2))
        if cc:
            cats[cc] = cidrs
    return cats


def geoip_ruleset(cats: dict, category: str) -> dict:
    if category not in cats:
        raise SystemExit("geoip 分类不存在: %s" % category)
    out = []
    for ipraw, prefix in cats[category]:
        if prefix == 0:
            # v2fly 的压缩编码：prefix=0 表示沿用上一条
            if out:
                out.append(out[-1])
            continue
        if len(ipraw) == 4:
            a = socket.inet_ntop(socket.AF_INET, ipraw); bits = 32
        elif len(ipraw) == 16:
            a = socket.inet_ntop(socket.AF_INET6, ipraw); bits = 128
        else:
            continue
        if prefix > bits:
            continue
        out.append("%s/%d" % (a, prefix))
    seen = set(); uniq = []
    for c in out:
        if c not in seen:
            seen.add(c); uniq.append(c)
    return {"version": 1, "rules": [{"ip_cidr": uniq}]}


# ---------------------------------------------------------------- 编译 / 校验

def compile_ruleset(singbox: str, src_json: str, out_srs: str):
    subprocess.run([singbox, "rule-set", "compile", src_json, "-o", out_srs],
                   check=True, capture_output=True)


def ruleset_count(singbox: str, srs: str, tmpdir: str) -> dict:
    """反编译 .srs 并统计条目数"""
    out = os.path.join(tmpdir, os.path.basename(srs) + ".json")
    subprocess.run([singbox, "rule-set", "decompile", srs, "-o", out],
                   check=True, capture_output=True)
    j = json.load(open(out, "r", encoding="utf-8"))
    r = j["rules"][0]
    return {
        "domain": len(r.get("domain", [])),
        "domain_suffix": len(r.get("domain_suffix", [])),
        "domain_regex": len(r.get("domain_regex", [])),
        "ip_cidr": len(r.get("ip_cidr", [])),
    }


# ---------------------------------------------------------------- 任务表

# (输出文件名, 源 .dat, 分类, 说明)
#
#   AD 拦截清单（geosite-category-ads-all）**刻意不动**：
#   v2fly 的 CATEGORY-ADS-ALL 有 191,728 条（neo 现用上游版仅 911 条），
#   一次性扩大 210 倍属于超出「修复国内直连」范围的改动，误伤风险未评估。
#   若将来要换，先单独评估再启用。
JOBS = [
    ("geosite-cn.srs",
     "geosite.dat", "CN",
     "国内域名直连清单（必须与 xray 路径同源，否则 TUN 下国内域名会走代理）"),
    ("geoip-cn.srs",
     "geoip.dat", "CN",
     "国内 IP 直连清单（补齐缺失网段，兜住未收录域名的解析结果）"),
]

# 达标下限：低于此值说明清单被裁剪过（上游精简版 = 退化）
MINIMUMS = {
    "geosite-cn.srs": {"domain_suffix": 100000},
    "geoip-cn.srs": {"ip_cidr": 9000},
}


def main():
    ap = argparse.ArgumentParser(description="neo rule-set 生成器")
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ap.add_argument("--dat-dir", default=os.path.join(root, "runtime"))
    ap.add_argument("--out-dir", default=os.path.join(root, "runtime", "rule-sets"))
    ap.add_argument("--singbox", default=os.path.join(root, "runtime", "sing-box.exe"))
    ap.add_argument("--check", action="store_true", help="只校验，不生成")
    args = ap.parse_args()

    if not os.path.exists(args.singbox):
        raise SystemExit("找不到 sing-box.exe: %s" % args.singbox)

    tmpdir = tempfile.mkdtemp(prefix="neo-ruleset-")
    rc = 0
    try:
        if args.check:
            print("== 校验现有 rule-set ==")
            for name, _dat, _cat, _desc in JOBS:
                p = os.path.join(args.out_dir, name)
                if not os.path.exists(p):
                    print("  [缺失] %s" % name); rc = 1; continue
                cnt = ruleset_count(args.singbox, p, tmpdir)
                bad = []
                for k, v in MINIMUMS.get(name, {}).items():
                    if cnt.get(k, 0) < v:
                        bad.append("%s=%d<%d" % (k, cnt.get(k, 0), v))
                flag = "OK  " if not bad else "不足"
                print("  [%s] %-32s %s" % (flag, name, cnt))
                if bad:
                    print("        低于下限: %s" % ", ".join(bad)); rc = 1
            return rc

        print("== 生成 rule-set ==")
        print("  源 .dat 目录 : %s" % args.dat_dir)
        print("  输出 .srs    : %s" % args.out_dir)
        print()
        os.makedirs(args.out_dir, exist_ok=True)

        gs_cache: dict = {}
        gi_cache: dict = {}
        for name, dat, cat, desc in JOBS:
            dat_path = os.path.join(args.dat_dir, dat)
            if not os.path.exists(dat_path):
                raise SystemExit("缺少源文件: %s" % dat_path)

            if dat == "geosite.dat":
                if not gs_cache:
                    gs_cache.update(load_geosite(dat_path))
                doc = geosite_ruleset(gs_cache, cat)
            else:
                if not gi_cache:
                    gi_cache.update(load_geoip(dat_path))
                doc = geoip_ruleset(gi_cache, cat)

            src = os.path.join(tmpdir, name + ".src.json")
            with open(src, "w", encoding="utf-8") as f:
                json.dump(doc, f, ensure_ascii=False)

            dst = os.path.join(args.out_dir, name)
            compile_ruleset(args.singbox, src, dst)
            cnt = ruleset_count(args.singbox, dst, tmpdir)
            size = os.path.getsize(dst)
            print("  %-32s %8d B   %s" % (name, size, cnt))
            print("      %s" % desc)

        print()
        print("== 生成后自检 ==")
        for name, _d, _c, _x in JOBS:
            p = os.path.join(args.out_dir, name)
            if not os.path.exists(p):
                print("  [缺失] %s" % name); rc = 1; continue
            cnt = ruleset_count(args.singbox, p, tmpdir)
            bad = []
            for k, v in MINIMUMS.get(name, {}).items():
                if cnt.get(k, 0) < v:
                    bad.append("%s=%d<%d" % (k, cnt.get(k, 0), v))
            print("  [%s] %-32s %s" % ("OK  " if not bad else "不足", name, cnt))
            if bad:
                print("        低于下限: %s" % ", ".join(bad)); rc = 1
        return rc
    finally:
        import shutil
        shutil.rmtree(tmpdir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
