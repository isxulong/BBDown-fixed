#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""gate_bats.py —— 组包前的脚本形态闸门。

存在理由（真实事故）：
    编码统一批的修复成果只落在验收目录，没有回灌到将要发布的材料目录。
    于是 stage_materials.py 如实搬出未修的 GBK 旧版 bat，对账仍然全绿
    （因为去掉 chcp 行后内容与新版逐字相同），发出去等于把刚修好的编码问题原样退回。

本闸门把「材料是否回灌」变成可执行的检查：
    发布集（package-extras/bats/*.bat）必须全部是
        - UTF-8 无 BOM
        - 纯 CRLF（无裸 LF、无孤立 CR）
        - 含 chcp 65001 且紧跟在 @echo off 之后（允许中间有空行/注释）
        - 调用 core 前缀（"%~dp0core\\BBDown.exe"）或明确标注为不需要 core 的命令
    任一不合规 → 非零退出，禁止继续组包。

历史遗留（package-extras 根目录下的长名 bat）单独报 warn、不算失败：
    它们是基线遗留的旧文件，处置方式待定，不应因此卡住发版；
    但必须打印出来，避免「眼不见为净」。

用法：
    python3 gate_bats.py --pkgassets <package-extras 或 pkgassets 目录>
    python3 gate_bats.py --pkgassets <dir> --quiet
"""

import argparse
import os
import sys

CHCP = "chcp 65001".encode("ascii")
CORE_PREFIX = '"%~dp0core\\BBDown.exe"'.encode("ascii")
BOM = b"\xef\xbb\xbf"


def probe(path):
    raw = open(path, "rb").read()
    facts = {
        "size": len(raw),
        "bom": raw[:3] == BOM,
        "crlf": raw.count(b"\r\n"),
        "bare_lf": raw.count(b"\n") - raw.count(b"\r\n"),
        "lone_cr": raw.count(b"\r") - raw.count(b"\r\n"),
        "has_chcp": CHCP in raw,
        "has_core_prefix": CORE_PREFIX in raw,
        "utf8": None,
    }
    try:
        raw.decode("utf-8")
        facts["utf8"] = True
    except UnicodeDecodeError as ex:
        facts["utf8"] = False
        facts["utf8_error"] = str(ex)
    # chcp 是否紧跟在 @echo off 之后（跳过空行与 rem 注释）
    if facts["utf8"]:
        lines = [l.strip() for l in raw.decode("utf-8").replace("\r\n", "\n").split("\n")]
    else:
        lines = []
    idx_echo = next((i for i, l in enumerate(lines) if l.lower().startswith("@echo off")), None)
    idx_chcp = next((i for i, l in enumerate(lines) if "chcp 65001" in l), None)
    facts["echo_line"] = idx_echo
    facts["chcp_line"] = idx_chcp
    facts["chcp_adjacent"] = (
        idx_echo is not None and idx_chcp is not None and idx_chcp > idx_echo
        and all(lines[i] == "" or lines[i].lower().startswith("rem")
                for i in range(idx_echo + 1, idx_chcp))
    )
    return facts


def judge(facts, need_core):
    """返回违规原因列表；空列表 = 合规。"""
    problems = []
    if not facts["utf8"]:
        problems.append("不是合法 UTF-8（%s）" % facts.get("utf8_error", "解码失败"))
    if facts["bom"]:
        problems.append("含 UTF-8 BOM（cmd 会把 BOM 当命令字符报错）")
    if facts["crlf"] == 0:
        problems.append("没有任何 CRLF 行尾")
    if facts["bare_lf"] > 0:
        problems.append("含 %d 处裸 LF" % facts["bare_lf"])
    if facts["lone_cr"] > 0:
        problems.append("含 %d 处孤立 CR" % facts["lone_cr"])
    if not facts["has_chcp"]:
        problems.append("缺 chcp 65001 代码页声明")
    elif not facts["chcp_adjacent"]:
        problems.append("chcp 未紧跟在 @echo off 之后（实际在第 %s 行）" % facts["chcp_line"])
    if need_core and not facts["has_core_prefix"]:
        problems.append('缺 core 前缀调用 "%~dp0core\\BBDown.exe"')
    return problems


def main(argv):
    ap = argparse.ArgumentParser(description="组包前校验 bat 脚本形态")
    ap.add_argument("--pkgassets", required=True, help="package-extras 或 pkgassets 目录")
    ap.add_argument("--quiet", action="store_true", help="只打印违规项与总结")
    args = ap.parse_args(argv)

    pkg = os.path.abspath(args.pkgassets)
    if not os.path.isdir(pkg):
        print("[FAIL] 目录不存在：%s" % pkg)
        return 2

    # 发布集：bats/ 子目录；兼容 pkgassets 直接放 bat 的情况
    bats_dir = os.path.join(pkg, "bats")
    release_dir = bats_dir if os.path.isdir(bats_dir) else pkg
    release_bats = sorted(n for n in os.listdir(release_dir) if n.lower().endswith(".bat"))

    if not release_bats:
        print("[FAIL] 发布集里没有任何 .bat：%s" % release_dir)
        return 2

    failures = []
    print("=== 发布集（必须合规）：%s ===" % release_dir)
    if not os.path.isdir(bats_dir):
        print("  [提示] 未找到 bats/ 子目录，按 pkgassets 根目录当发布集检查")
    for name in release_bats:
        path = os.path.join(release_dir, name)
        f = probe(path)
        probs = judge(f, need_core=True)
        if probs:
            failures.append((name, probs))
            print("  [FAIL] %-34s %5dB  %s" % (name, f["size"], "；".join(probs)))
        elif not args.quiet:
            print("  [ OK ] %-34s %5dB  chcp@L%s core前缀=有"
                  % (name, f["size"], f["chcp_line"]))

    # 历史遗留：package-extras 根目录下、不在发布集里的 .bat
    legacy_dir = os.path.dirname(bats_dir) if os.path.isdir(bats_dir) else None
    legacy = []
    if legacy_dir and os.path.isdir(legacy_dir):
        legacy = sorted(
            n for n in os.listdir(legacy_dir)
            if n.lower().endswith(".bat")
            and not os.path.join(legacy_dir, n).startswith(release_dir + os.sep)
        )
    if legacy:
        print()
        print("=== 历史遗留（不在发布集，仅提示，不阻断）：%d 个 ===" % len(legacy))
        for name in legacy:
            f = probe(os.path.join(legacy_dir, name))
            print("  [注意] %-40s %4dB utf8=%-5s crlf=%-2d 裸LF=%-2d chcp=%-5s core前缀=%s"
                  % (name, f["size"], f["utf8"], f["crlf"], f["bare_lf"],
                     f["has_chcp"], f["has_core_prefix"]))
        print("  处置方式待定：它们不进发布包，但留在仓库里会误导人、且谁改谁错。")

    print()
    if failures:
        print("[FAIL] 发布集 %d 个文件中 %d 个不合规——禁止组包。" % (len(release_bats), len(failures)))
        print("       原因很可能是「修复成果没回灌到 package-extras/bats/」，请先回灌再组包。")
        return 1
    print("[PASS] 发布集 %d 个文件全部合规。" % len(release_bats))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
