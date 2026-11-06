#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""stage_materials.py —— 把「真实仓库根目录」摆成 release_reconcile.py 期望的「发布工作区」。

背景：
    release_reconcile.py 是发布组装与对账的唯一入口，它按固定布局找文件：
        --root      里要有  src/BBDownHelper.Core/AppVersion.cs
                          tools/bat下载命令生成器.html
                          tests/
        --materials 里要有  pkgassets/   （= 仓库 package-extras/ 的内容，含 bats/）
                          bin/         （BBDownHelper.exe / BBDown.exe / aria2c.exe / ffmpeg.exe）
    而真实仓库 isxulong/BBDown-fixed 的布局与之不同：
        BBDownHelper/src/   BBDownHelper/tests/   package-extras/   tools/
    本脚本负责把后者摆成前者——它只做「搬运 + 记账」，不做版本注入、不做打包。

设计约束：
1. 每一步都打印搬了什么 / 跳过什么 / 为什么跳过——**不允许静默跳过**。
   缺材料（例如 core 三件套尚未提供）必须显式报出来并以非零退出码收尾，
   免得对账时才发现材料缺失、错误地归咎于对账脚本。
2. 幂等：目标已存在且内容一致 → 跳过并说明「已存在且一致」；
   目标已存在但内容不同 → 覆盖并说明「已存在但内容不同，按源覆盖」。
3. 只搬、不改：复制过程逐字节保持源文件内容（本脚本不做任何文本改写）。
4. 不碰 fork 子树：仓库根的 BBDown/、BBDown.Core/ 等上游子树一律不搬、不读、不改。

用法：
    python3 tools/stage_materials.py --repo-root <真实仓库根> --root <工作区根> --bin <bin 目录>
    python3 tools/stage_materials.py --repo-root <仓库根> --root <工作区根>   # bin 缺失则跳过并报出来
"""

import argparse
import filecmp
import os
import shutil
import sys

# ---------------------------------------------------------------- 布局定义

# 仓库内相对路径 -> 工作区内相对路径
SRC_TREE = [
    (os.path.join("BBDownHelper", "src"), "src"),
    (os.path.join("BBDownHelper", "tests"), "tests"),
]
SRC_FILES = [
    (os.path.join("tools", "bat下载命令生成器.html"), os.path.join("tools", "bat下载命令生成器.html")),
]
# 仓库 package-extras/ 整目录 -> release-materials/pkgassets/（bats/ 子目录随之带过去）
SRC_DIRS = [
    ("package-extras", os.path.join("release-materials", "pkgassets")),
]
# 工作区 bin/ 需要哪些可执行文件（由构建产出 + 外部提供）
BIN_FILES = [
    "BBDownHelper.exe",   # 本脚本调用方用 dotnet publish 产出
    "BBDown.exe",         # 外部提供（core 三件套）
    "aria2c.exe",         # 外部提供（core 三件套）
    "ffmpeg.exe",         # 外部提供（core 三件套）
]

# 明确不搬的东西（打印出来，免得以为漏了）
NOT_STAGED = [
    (os.path.join("BBDownHelper", "BUILD.md"),
     "仓库内构建记录留在仓库，不进发布工作区（工作区只保留 assemble 需要的布局）"),
    (os.path.join("BBDownHelper", "README.md"),
     "仓库说明文档留在仓库，不进发布工作区"),
    ("BBDown", "BBDown fork 子树：只读不改，不搬入发布工作区"),
    ("BBDown.Core", "BBDown fork 子树：只读不改，不搬入发布工作区"),
    ("BBDown.LoginTests", "BBDown fork 子树：只读不改，不搬入发布工作区"),
    ("BBDown.sln", "fork 子树的解决方案文件，不进发布工作区"),
]


class Log(object):
    """把每一步都落到 stdout，并统计计数，便于脚本尾部给出总结。"""

    def __init__(self):
        self.copied = 0
        self.skipped_same = 0
        self.overwritten = 0
        self.missing = []

    def say(self, text):
        print(text, flush=True)

    def section(self, title):
        self.say("")
        self.say("=== %s ===" % title)

    def ok(self, text):
        self.say("  [搬]   %s" % text)
        self.copied += 1

    def same(self, text):
        self.say("  [跳过] %s（已存在且内容一致）" % text)
        self.skipped_same += 1

    def replaced(self, text):
        self.say("  [覆盖] %s（已存在但内容不同，按源覆盖）" % text)
        self.overwritten += 1

    def miss(self, text, reason):
        self.say("  [缺失] %s —— %s" % (text, reason))
        self.missing.append(text)

    def note(self, text):
        self.say("  [说明] %s" % text)


def _same_content(src, dst):
    """逐字节比较两个文件；任一不可读时返回 False（保守：当作不同，走覆盖）。"""
    try:
        if os.path.getsize(src) != os.path.getsize(dst):
            return False
        return filecmp.cmp(src, dst, shallow=False)
    except OSError:
        return False


def _copy_file(log, src, dst):
    """单文件搬运：一致则跳过，不同则覆盖。"""
    if not os.path.isfile(src):
        log.miss(os.path.relpath(dst), "源文件不存在：%s" % src)
        return False
    if os.path.isfile(dst):
        if _same_content(src, dst):
            log.same(os.path.relpath(dst))
            return True
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        log.replaced(os.path.relpath(dst))
        return True
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src, dst)
    log.ok("%s  (%d 字节)" % (os.path.relpath(dst), os.path.getsize(dst)))
    return True


def _copy_tree(log, src_dir, dst_dir):
    """整目录搬运：保留相对结构；逐文件做幂等判断。"""
    if not os.path.isdir(src_dir):
        log.miss(os.path.relpath(dst_dir), "源目录不存在：%s" % src_dir)
        return False
    files = 0
    for base, dirs, names in os.walk(src_dir):
        dirs.sort()
        for name in sorted(names):
            src = os.path.join(base, name)
            rel = os.path.relpath(src, src_dir)
            dst = os.path.join(dst_dir, rel)
            if _copy_file(log, src, dst):
                files += 1
    log.note("%s 共处理 %d 个文件" % (os.path.relpath(dst_dir), files))
    return True


def stage(repo_root, root, bin_dir):
    log = Log()

    log.section("阶段 0 · 入参与源根校验")
    log.say("  仓库根   : %s" % repo_root)
    log.say("  工作区根 : %s" % root)
    log.say("  bin 目录 : %s" % (bin_dir or "(未提供)"))
    for probe in (os.path.join(repo_root, "BBDownHelper", "src"),
                  os.path.join(repo_root, "BBDownHelper", "tests"),
                  os.path.join(repo_root, "package-extras"),
                  os.path.join(repo_root, "tools", "bat下载命令生成器.html")):
        ok = os.path.exists(probe)
        log.say("  [%s] %s" % ("有" if ok else "无", os.path.relpath(probe, repo_root)))
        if not ok:
            log.say("  源根结构与预期不符，仓库根可能选错。")

    os.makedirs(root, exist_ok=True)

    log.section("阶段 1 · 源码与测试搬入工作区")
    for rel_src, rel_dst in SRC_TREE:
        log.say("  %s/  ->  %s/" % (rel_src, rel_dst))
        _copy_tree(log, os.path.join(repo_root, rel_src), os.path.join(root, rel_dst))

    log.section("阶段 2 · 工具文件搬入工作区")
    for rel_src, rel_dst in SRC_FILES:
        log.say("  %s  ->  %s" % (rel_src, rel_dst))
        _copy_file(log, os.path.join(repo_root, rel_src), os.path.join(root, rel_dst))

    log.section("阶段 3 · 打包素材 package-extras/ -> release-materials/pkgassets/")
    for rel_src, rel_dst in SRC_DIRS:
        log.say("  %s/  ->  %s/" % (rel_src, rel_dst))
        _copy_tree(log, os.path.join(repo_root, rel_src), os.path.join(root, rel_dst))
    bats = os.path.join(root, "release-materials", "pkgassets", "bats")
    if os.path.isdir(bats):
        names = sorted(os.listdir(bats))
        log.note("pkgassets/bats/ 已就位：%s" % ("、".join(names) if names else "空目录"))
    else:
        log.miss("release-materials/pkgassets/bats", "bats/ 子目录未随 package-extras/ 搬过来")

    log.section("阶段 4 · 可执行文件 -> release-materials/bin/")
    dst_bin = os.path.join(root, "release-materials", "bin")
    os.makedirs(dst_bin, exist_ok=True)
    for name in BIN_FILES:
        if not bin_dir:
            log.miss("release-materials/bin/%s" % name, "未提供 --bin 目录，本脚本不猜测来源")
            continue
        src = os.path.join(bin_dir, name)
        log.say("  %s  ->  release-materials/bin/%s" % (src, name))
        _copy_file(log, src, os.path.join(dst_bin, name))
    have = sorted(os.listdir(dst_bin))
    log.note("release-materials/bin/ 当前内容：%s" % ("、".join(have) if have else "空"))

    log.section("阶段 5 · 明确不搬的内容（打印出来，免得以为漏了）")
    for rel, why in NOT_STAGED:
        exists = os.path.exists(os.path.join(repo_root, rel))
        log.say("  [%s] %s —— %s" % ("存在" if exists else "仓库无", rel, why))

    log.section("总结")
    log.say("  新搬 %d 个文件 / 覆盖 %d 个 / 内容一致跳过 %d 个" % (log.copied, log.overwritten, log.skipped_same))
    if log.missing:
        log.say("  缺失 %d 项：" % len(log.missing))
        for m in log.missing:
            log.say("    - %s" % m)
        log.say("  结论：材料不全，工作区尚未满足 assemble 的前置条件。")
        return 1
    log.say("  结论：全部就位，工作区满足 assemble 的前置条件。")
    return 0


def main(argv):
    ap = argparse.ArgumentParser(description="把真实仓库摆成 release_reconcile.py 期望的发布工作区")
    ap.add_argument("--repo-root", required=True, help="真实仓库根目录（含 BBDownHelper/ package-extras/ tools/）")
    ap.add_argument("--root", required=True, help="发布工作区根目录")
    ap.add_argument("--bin", default="", help="可执行文件所在目录（BBDownHelper.exe / BBDown.exe / aria2c.exe / ffmpeg.exe）")
    args = ap.parse_args(argv)

    repo_root = os.path.abspath(args.repo_root)
    root = os.path.abspath(args.root)
    bin_dir = os.path.abspath(args.bin) if args.bin else ""
    return stage(repo_root, root, bin_dir)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))