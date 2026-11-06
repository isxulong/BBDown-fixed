#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""release_reconcile.py —— 发布组装与对账的唯一入口（常驻，逐版复用，不再一版一脚本）。

设计约束（红队判定「发版靠人肉、版本号散落」的直接对策）：

1. 版本号唯一真值来源：src/BBDownHelper.Core/AppVersion.cs 的 `Release` 常量。
   本文件内**不含任何版本号字符串常量**——所有版本串都由 _read_release() 现读现算，
   压缩包名 / 生成器页面版本 / VERSION.txt / 使用说明页眉全部由它派生。
2. 三类对账：
   - hash   双安装包共同文件逐文件 sha256 比对
   - literals 全仓版本字面量位置清单（含文件+行号；豁免必须显式列出理由）
   - fileset 两包文件集合差异 + 与源码清单差异
3. 三类负样本：改错版本号 / 改坏文件哈希 / 少一个文件，每种都必须让对账单报红。
4. 退出码：全绿 0，任一项不绿 1，环境/参数错误 2。供电闸直接调用。

用法：
  python3 tools/release_reconcile.py assemble   --materials release-materials --out build/release
  python3 tools/release_reconcile.py reconcile  --out build/release
  python3 tools/release_reconcile.py selfcheck  --out build/release
  python3 tools/release_reconcile.py all        --materials release-materials --out build/release
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
import zipfile

# ---------------------------------------------------------------- 常量（无版本号）

REPO_ROOT_DEFAULT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERSION_SOURCE = os.path.join("src", "BBDownHelper.Core", "AppVersion.cs")
GENERATOR_HTML = os.path.join("tools", "bat下载命令生成器.html")
MV_RE = re.compile(r"当前版本 (v[0-9.]+)")   # 生成器页面页眉的可见版本行
GENERATOR_VERSION_RE = re.compile(r'(var\s+CURRENT_VERSION\s*=\s*")([^"]+)(")')
RELEASE_CONST_RE = re.compile(r'public\s+const\s+string\s+Release\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"')
LITERAL_RE = re.compile(r"1\.0\.[0-9]+")
PINNED_RELEASE_LINES = (20, 21, 22)   # UpdateCheckTests.cs 的 Release/ReleaseTag/WindowTitle 断言行
DOC_UPDATE_HEAD_RE = re.compile(r"v[0-9.]+ 更新(?:（[^）]*）)?：")   # 「v1.0.6 更新：」与「v1.0.6 更新（接着 v1.0.5…）：」两种写法都要认；只认前者会让带括号的正文标题在下一个版本静默漏改   # UpdateCheckTests.cs 的 Release/ReleaseTag/WindowTitle 断言行

# 组装布局（自 core 布局引入版起，EXE 版与 bat 版统一，见 package-extras/README.md）
EXE_LAYOUT = [
    ("BBDownHelper.exe",               "bin/BBDownHelper.exe"),
    ("bat下载命令生成器.html",          "@generator"),
    ("使用说明.txt",                    "pkgassets/使用说明-EXE版.txt"),
    ("登录.bat",                        "pkgassets/bats/登录.bat"),
    ("VERSION.txt",                    "@version"),
    (os.path.join("core", "BBDown.exe"), "bin/BBDown.exe"),
    (os.path.join("core", "aria2c.exe"), "bin/aria2c.exe"),
    (os.path.join("core", "ffmpeg.exe"), "bin/ffmpeg.exe"),
]
BAT_LAYOUT = [
    ("B站下载(修改AV号).bat",                    "pkgassets/bats/下载视频(默认).bat"),
    ("B站下载(修改AV号) - 登录.bat",              "pkgassets/bats/登录.bat"),
    ("B站下载(修改AV号) - 只看信息不下载.bat",      "pkgassets/bats/只看信息不下载.bat"),
    ("bat下载命令生成器.html",                    "@generator"),
    ("使用说明.txt",                            "pkgassets/使用说明-bat版.txt"),
    ("BBDown命令使用指南.txt",                    "pkgassets/BBDown命令使用指南.txt"),
    ("VERSION.txt",                            "@version"),
    (os.path.join("core", "BBDown.exe"),        "bin/BBDown.exe"),
    (os.path.join("core", "aria2c.exe"),        "bin/aria2c.exe"),
    (os.path.join("core", "ffmpeg.exe"),        "bin/ffmpeg.exe"),
]
PKG_DIR_NAME = "BBDown-fixed"

# 版本字面量豁免表：每条都必须写明理由，不许静默放过。规则按顺序匹配，先命中者生效。
# kind: exclude（不扫）/ whitelist（豁免但列出）/ whitelist_partial（仅指定行号豁免）/ pinned（必须等于当前版本）
EXEMPT_RULES = [
    (r"^BBDown-1\.6\.3/", "exclude",
     "BBDown 1.6.3 fork 子树（零字节红线，只读不改）；其中出现的版本串是上游 BBDown.Core 程序集版本，与本套件发布号无关"),
    (r"^(.*/)bin/|^(.*/)obj/", "exclude",
     "构建产物（dotnet 生成的 deps.json / AssemblyInfo / project.assets），每次构建重生成，不是版本来源"),
    (r"^src/BBDownHelper\.Core/AppVersion\.cs$", "whitelist",
     "唯一真值来源本身：脚本从这里读版本，它就是版本源头，不属于「散落复制」"),
    (r"^tests/.*UpdateCheckTests\.cs$", "pinned",
     "AppVersion 单一来源锁定用例：这三行把 Release/ReleaseTag/WindowTitle 钉在当前版本，版本变更时必须同步改，否则对账报红"),
    (r"^BUILD\.md$", "whitelist",
     "历史施工记录：按批次如实留存当时的版本引用，改写它等于篡改历史证据"),
    (r"^tools/bat下载命令生成器\.html$", "whitelist_partial",
     "页面里两处「core 布局自某个历史版本起」的说明文字（历史事实）；页面版本常量 CURRENT_VERSION 走独立钉死检查"),
    (r"^(src|tests)/.*\.(cs|csproj)$", "whitelist",
     "代码注释 / csproj 注释里的历史版本（迁移说明、批次记录）与测试夹具值；发布号只认 AppVersion.Release，注释与夹具不参与运行"),
    (r"^维护者手册\.md$", "whitelist",
     "接手知识文档：文中版本号是历史事实与事故记录（哪一版踩了哪个坑），不随发版变化；"
     "只有「现在已经发到哪一版」一节需要人工保持最新，脚本无法判断其对错，故整篇豁免并在此写明理由"),
    (r"^src/BBDownHelper/app\.manifest$", "whitelist",
     "Win32 清单程序集版本（四段式）是 Windows 加载器口径，与套件对外发布号是两个体系"),
]
# 生成器页面里两处「core 布局自某个历史版本起」的用户可见说明：按内容锚定，不按行号
# （行号锚定会在页面插入一行后静默失效，把真说明误判成版本散落——2026-11-06 实测过）。
GENERATOR_HISTORIC_ANCHORS = (
    "默认不勾：BBDown.exe 在 core 子文件夹（本套件 v1.0.2 起的压缩包布局）",
    "本套件压缩包（v1.0.2 起）自带 BBDown.exe，位于 core 文件夹",
)

# 逐字节比对里按形态「设计性不同」的文件：两包各出一份面向自身形态的版本，不能当同一文件比对。
PER_FLAVOR_DIFF = {
    "使用说明.txt":
        "EXE 版与 bat 版各自出一份面向自身形态的说明（首行标识与升级指引按形态分支），内容本就不同；"
        "它的版本注入由「版本注入 · 单一来源派生一致性」逐包校验，说明正文里的历史版本由「对账二」列清单",
}


# ---------------------------------------------------------------- 基础设施

def _sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _read_text(path, encoding="utf-8"):
    with open(path, "r", encoding=encoding, newline="") as f:
        return f.read()


def _write_text(path, text, encoding="utf-8", newline="\n"):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding=encoding, newline=newline) as f:
        f.write(text)


def _read_bytes(path):
    with open(path, "rb") as f:
        return f.read()


class Report(object):
    def __init__(self):
        self.sections = []   # [(name, ok, lines)]

    def add(self, name, ok, lines):
        self.sections.append((name, ok, lines))
        return ok

    @property
    def ok(self):
        return all(s[1] for s in self.sections)

    def red_count(self):
        return sum(1 for s in self.sections if not s[1])

    def render(self):
        out = []
        for name, ok, lines in self.sections:
            out.append("%s %s" % ("[GREEN]" if ok else "[RED]  ", name))
            for line in lines:
                out.append("         " + line)
        out.append("")
        out.append("VERDICT: %s（%d 项中 %d 项红）" % ("GREEN" if self.ok else "RED",
                                                    len(self.sections), self.red_count()))
        return "\n".join(out)

    def to_json(self):
        return {
            "ok": self.ok,
            "sections": [{"name": n, "ok": o, "lines": l} for n, o, l in self.sections],
        }


# ---------------------------------------------------------------- 版本单一来源

def read_release(repo_root):
    """从 AppVersion.cs 读发布版本号；同时证明全仓只有这一处 Release 定义。"""
    src = os.path.join(repo_root, VERSION_SOURCE)
    if not os.path.isfile(src):
        raise SystemExit("找不到版本源文件：%s" % src)
    text = _read_text(src)
    hits = [(i, line.strip()) for i, line in enumerate(text.split("\n"), 1)
            if RELEASE_CONST_RE.search(line)]
    if len(hits) != 1:
        raise SystemExit("版本源文件里 Release 定义不是恰好 1 处：%r" % hits)
    m = RELEASE_CONST_RE.search(text)
    return m.group(1), hits[0][0], hits[0][1]


def derive(release):
    """由唯一真值派生全部注入项——脚本里不存版本号字面量。"""
    tag = "v" + release
    return {
        "release": release,
        "tag": tag,
        "zip_exe": "BBDown-fixed-%s-EXE版.zip" % tag,
        "zip_bat": "BBDown-fixed-%s-bat版.zip" % tag,
        "zip_exe_ascii": "BBDown-fixed-%s-EXE.zip" % tag,   # GitHub 资产用 ASCII 名
        "zip_bat_ascii": "BBDown-fixed-%s-bat.zip" % tag,
        "version_file": tag + "\n",
    }


def inject_generator_html(html_text, tag):
    """把版本注入生成器页面：CURRENT_VERSION 常量 + 页眉可见版本行（幂等）。"""
    out, n = GENERATOR_VERSION_RE.subn(lambda m: m.group(1) + tag.lstrip("v") + m.group(3), html_text, count=1)
    if n != 1:
        raise RuntimeError("生成器页面没找到 CURRENT_VERSION 常量，无法注入")
    masthead_marker = '<p class="meta-line meta-dim">适合 BBDown 1.6.x</p>'
    version_line = '<p class="meta-line meta-dim">当前版本 %s</p>' % tag
    if version_line not in out:
        if masthead_marker not in out:
            raise RuntimeError("生成器页面没找到 masthead 锚点，无法注入页眉版本")
        out = out.replace(masthead_marker, version_line + "\n      " + masthead_marker, 1)
    return out


def inject_doc_header(doc_text, tag, flavor):
    """使用说明首行 + 「更新」小标题的版本注入（幂等，保留其余正文原样）。"""
    lines = doc_text.split("\n")
    if not lines:
        raise RuntimeError("说明文档为空")
    lines[0] = re.sub(r"BBDown-fixed v[0-9.]+（", "BBDown-fixed %s（" % tag, lines[0], count=1)
    hit = None
    for i, line in enumerate(lines[:12]):
        if DOC_UPDATE_HEAD_RE.match(line.strip()):
            hit = i
            break
    # 说明正文的「更新」标题必须跟着版本走。旧实现没命中就静默跳过、只改首行，
    # 结果是新版本号配着上一版的更新条目发出去（2026-11-06 实测踩到过）。
    if hit is None:
        raise RuntimeError("说明文档前 12 行里没找到「更新」小标题，无法注入版本：%s" % lines[0])
    # 保留标题里原有的括号补充说明，只替换版本号
    lines[hit] = re.sub(r"^v[0-9.]+", tag, lines[hit].strip())
    if flavor == "bat" and "bat 版" not in lines[0]:
        raise RuntimeError("bat 版说明首行没标识 bat 版：%r" % lines[0])
    if flavor == "exe" and "bat 版" in lines[0]:
        raise RuntimeError("EXE 版说明首行误标 bat 版：%r" % lines[0])
    return "\n".join(lines)


# ---------------------------------------------------------------- 组装

def _build_one(root, materials, pkg_dir, layout, info, flavor):
    os.makedirs(pkg_dir, exist_ok=True)
    gen = inject_generator_html(_read_text(os.path.join(root, GENERATOR_HTML)), info["tag"])
    doc_cache = {}
    produced = []
    for rel_dst, src_spec in layout:
        dst = os.path.join(pkg_dir, rel_dst)
        if src_spec == "@generator":
            _write_text(dst, gen)
        elif src_spec == "@version":
            _write_text(dst, info["version_file"], newline="\n")
        elif src_spec.startswith("pkgassets/使用说明-"):
            if src_spec not in doc_cache:
                doc_cache[src_spec] = inject_doc_header(
                    _read_text(os.path.join(materials, src_spec)), info["tag"], flavor)
            _write_text(dst, doc_cache[src_spec], newline="\n")
        else:
            src = os.path.join(materials, src_spec)
            if not os.path.isfile(src):
                raise RuntimeError("打包材料缺失：%s" % src)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(src, dst)
        produced.append(rel_dst)
    return produced


def cmd_assemble(root, materials, outdir, make_zips=True):
    release, src_line, src_text = read_release(root)
    info = derive(release)
    for need in ("bin/BBDownHelper.exe", "bin/BBDown.exe", "bin/aria2c.exe", "bin/ffmpeg.exe"):
        if not os.path.isfile(os.path.join(materials, need)):
            raise SystemExit("打包材料缺失：%s（在 %s 下）" % (need, materials))

    exe_dir = os.path.join(outdir, "EXE版", PKG_DIR_NAME)
    bat_dir = os.path.join(outdir, "bat版", PKG_DIR_NAME)
    for d in (exe_dir, bat_dir):
        if os.path.isdir(d):
            shutil.rmtree(d)
    exe_files = _build_one(root, materials, exe_dir, EXE_LAYOUT, info, "exe")
    bat_files = _build_one(root, materials, bat_dir, BAT_LAYOUT, info, "bat")

    made = []
    if make_zips:
        for d, name in ((exe_dir, info["zip_exe"]), (bat_dir, info["zip_bat"])):
            zpath = os.path.join(outdir, name)
            if os.path.isfile(zpath):
                os.remove(zpath)
            with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED, compresslevel=1) as z:
                for base, _dirs, files in os.walk(d):
                    for f in sorted(files):
                        full = os.path.join(base, f)
                        arc = os.path.join(PKG_DIR_NAME, os.path.relpath(full, d))
                        z.write(full, arc.replace(os.sep, "/"))
            made.append((zpath, os.path.getsize(zpath)))
    print("版本来源：%s:%d  %s" % (VERSION_SOURCE, src_line, src_text.strip()))
    print("派生版本：%s" % info["tag"])
    print("EXE 版 %d 个文件 / bat 版 %d 个文件" % (len(exe_files), len(bat_files)))
    for zpath, size in made:
        print("压缩包：%s  %d 字节" % (os.path.basename(zpath), size))
    return info


# ---------------------------------------------------------------- 对账

def _pkg_files(pkg_dir):
    out = {}
    for base, _dirs, files in os.walk(pkg_dir):
        for f in files:
            full = os.path.join(base, f)
            out[os.path.relpath(full, pkg_dir).replace(os.sep, "/")] = full
    return out


def check_hash(exe_dir, bat_dir, per_flavor=None):
    per_flavor = PER_FLAVOR_DIFF if per_flavor is None else per_flavor
    a, b = _pkg_files(exe_dir), _pkg_files(bat_dir)
    common = sorted(set(a) & set(b))
    lines, bad = [], []
    for rel in common:
        if rel in per_flavor:
            continue
        ha, hb = _sha256(a[rel]), _sha256(b[rel])
        same = ha == hb
        lines.append("%s  %s  %s%s" % ("OK " if same else "DIFF", rel, ha[:16], "" if same else " vs " + hb[:16]))
        if not same:
            bad.append(rel)
    exempted = sorted(set(common) & set(per_flavor))
    lines.insert(0, "共同文件 %d 个，其中 %d 个按形态设计性不同（豁免比对）、实比 %d 个"
                 % (len(common), len(exempted), len(common) - len(exempted)))
    if per_flavor:
        lines.append("豁免比对（逐条列理由，仅豁免「两包相等」，两包各自的内部一致性仍由其他节校验）：")
        for rel, reason in per_flavor.items():
            if rel in common:
                lines.append("  - %s   ← %s（EXE 版 %s / bat 版 %s）"
                             % (rel, reason, _sha256(a[rel])[:16], _sha256(b[rel])[:16]))
    if bad:
        lines.append("哈希不一致：%s" % ", ".join(bad))
    return Report_section("对账一 · 双包逐文件哈希", not bad, lines)


def Report_section(name, ok, lines):
    return (name, ok, lines)


def check_literals(root, release=None, skip_dirs=()):
    """扫全仓版本字面量。skip_dirs 是「非源码树」（产物目录、打包材料目录）——
    里面的字面量是 assemble 的产出/输入，不是版本落点，扫进来只会把自产物算成散落。"""
    lines, stale, whitelisted, pinned, pin_bad = [], [], [], [], []
    skipped = 0
    skip_abs = {os.path.abspath(d) for d in skip_dirs}
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in (".git",)
                   and os.path.abspath(os.path.join(base, d)) not in skip_abs]
        for f in sorted(files):
            full = os.path.join(base, f)
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            if os.path.splitext(f)[1].lower() in (".exe", ".dll", ".ico", ".png", ".zip", ".pdb", ".gz"):
                continue
            rule = _match_rule(rel)
            if rule and rule[1] == "exclude":
                skipped += 1
                continue
            try:
                text = _read_text(full)
            except (UnicodeDecodeError, OSError):
                continue
            for i, line in enumerate(text.split("\n"), 1):
                if not LITERAL_RE.search(line):
                    continue
                item = "%s:%d  %s" % (rel, i, line.strip()[:96])
                if rule and rule[1] == "pinned":
                    if i in PINNED_RELEASE_LINES:
                        # 钉死：值必须是当前版本，否则版本漂移
                        vals = re.findall(r'1\.0\.\d+', line)
                        if release is not None and all(v == release for v in vals):
                            pinned.append(item)
                        else:
                            pin_bad.append(item)
                    else:
                        whitelisted.append((item, "同文件其余历史夹具值（版本比较用例需要旧版本做输入）"))
                elif rule and rule[1] == "whitelist":
                    whitelisted.append((item, rule[2]))
                elif rule and rule[1] == "whitelist_partial":
                    gv = GENERATOR_VERSION_RE.search(line)
                    if gv:
                        # 生成器页面版本常量不是「散落副本」而是「声明式派生点」：必须等于 Release，否则报红
                        if release is not None and gv.group(2) == release:
                            pinned.append(item + "   ← 页面版本常量，必须等于 AppVersion.Release")
                        else:
                            pin_bad.append(item + "   ← 页面版本常量与 Release 不符")
                    elif _in_js_comment(line) \
                            or any(a in line for a in GENERATOR_HISTORIC_ANCHORS):
                        reason = ("JS 注释里的批次记录（不渲染给用户，不参与运行）" if _in_js_comment(line)
                                  else "页面里「core 布局自 v1.0.2 起」的历史说明文字（按内容锚定）")
                        whitelisted.append((item, reason))
                    elif MV_RE.search(line) and release is not None \
                            and MV_RE.search(line).group(1) == ("v" + release):
                        pinned.append(item + "   ← 页面页眉可见版本，必须等于当前 ReleaseTag")
                    else:
                        stale.append(item)   # 页面里的其他版本串出现即异常
                else:
                    stale.append(item)
    lines.append("命中 %d 处；排除构建产物 %d 个文件；豁免 %d 处；钉死 %d 处；钉死不符 %d 处；未归类 %d 处"
                 % (len(whitelisted) + len(pinned) + len(stale) + len(pin_bad), skipped,
                    len(whitelisted), len(pinned), len(pin_bad), len(stale)))
    lines.insert(0, "扫描范围：%s（已跳过非源码树：%s）"
                 % (root, ", ".join(sorted(os.path.relpath(d, root).replace(os.sep, "/")
                                          for d in skip_abs if os.path.exists(d))) or "无"))
    if pinned:
        lines.append("钉死（AppVersion 单一来源锁定，值=当前版本，变更必须同步改）：")
        lines.extend("  " + p for p in pinned)
    if pin_bad:
        lines.append("钉死不符（这些断言仍钉在别的版本，版本改了却没同步）：")
        lines.extend("  ! " + p for p in pin_bad)
    if whitelisted:
        lines.append("豁免（逐条列理由）：")
        for item, reason in whitelisted[:40]:
            lines.append("  - %s   ← %s" % (item, reason))
        if len(whitelisted) > 40:
            lines.append("  …其余 %d 条同规则豁免" % (len(whitelisted) - 40))
    if stale:
        lines.append("未归类（必须处置，否则视为版本散落）：")
        lines.extend("  ! " + s for s in stale)
    return Report_section("对账二 · 版本字面量位置清单", not stale and not pin_bad, lines)


def _in_js_comment(line):
    """该行是否处于 JS 单行注释（// ...）或注释块内（/* */）。
    用于生成器页面：注释里的版本号是批次记录，不渲染给用户、不参与运行，不应算版本散落。
    只认真正的注释起点，字符串里的 "//"（如 URL）不算。"""
    s = line.strip()
    if s.startswith("//") or s.startswith("/*") or s.startswith("*"):
        return True
    # 行内 `// ` 之后的内容：前面若没有引号包裹，视为注释尾
    idx = line.find("// ")
    if idx > 0:
        head = line[:idx]
        if head.count('"') % 2 == 0 and head.count("'") % 2 == 0:
            return True
    return False


def _match_rule(rel):
    for pat, kind, reason in EXEMPT_RULES:
        if re.match(pat, rel):
            return (pat, kind, reason)
    return None


def check_fileset(exe_dir, bat_dir, materials):
    a, b = _pkg_files(exe_dir), _pkg_files(bat_dir)
    lines = []
    ok = True
    lines.append("两包文件数：EXE %d / bat %d" % (len(a), len(b)))
    # 先逐包对声明清单做完整性比对：缺文件/多文件都直接点名，不依赖「两包互比」
    for pkg, files, layout in (("EXE 版", a, EXE_LAYOUT), ("bat 版", b, BAT_LAYOUT)):
        declared = {d for d, _ in layout}
        missing = sorted(declared - set(files))
        extra = sorted(set(files) - declared)
        if missing:
            ok = False
            lines.append("%s缺声明清单里的文件：%s" % (pkg, ", ".join(missing)))
        if extra:
            ok = False
            lines.append("%s多出声明清单之外的文件：%s" % (pkg, ", ".join(extra)))
        if not missing and not extra:
            lines.append("%s 文件与声明清单逐项一致（%d 个）" % (pkg, len(files)))

    only_exe = sorted(set(a) - set(b))
    only_bat = sorted(set(b) - set(a))
    expected_only_exe = sorted({d for d, _ in EXE_LAYOUT} - {d for d, _ in BAT_LAYOUT})
    expected_only_bat = sorted({d for d, _ in BAT_LAYOUT} - {d for d, _ in EXE_LAYOUT})
    if only_exe != expected_only_exe:
        ok = False
        lines.append("EXE 版独有文件与声明清单不符：实际 %s / 应为 %s" % (only_exe, expected_only_exe))
    else:
        lines.append("EXE 版独有 %d 个：%s" % (len(only_exe), ", ".join(only_exe) or "无"))
    if only_bat != expected_only_bat:
        ok = False
        lines.append("bat 版独有文件与声明清单不符：实际 %s / 应为 %s" % (only_bat, expected_only_bat))
    else:
        lines.append("bat 版独有 %d 个：%s" % (len(only_bat), ", ".join(only_bat) or "无"))

    # 与源码清单差异：包内每个文件都必须能追回某个源（材料目录 / 生成器 / 版本文件）
    src_extra, src_missing = [], []
    for rel, src_spec in EXE_LAYOUT + BAT_LAYOUT:
        if src_spec in ("@generator", "@version"):
            continue
        if not os.path.isfile(os.path.join(materials, src_spec)):
            src_missing.append("%s ← %s" % (rel, src_spec))
    for rel in sorted(set(a) | set(b)):
        declared = {d for d, _ in EXE_LAYOUT} | {d for d, _ in BAT_LAYOUT}
        if rel not in declared:
            src_extra.append(rel)
    if src_missing:
        ok = False
        lines.append("源码清单里缺失（无法追溯来源）：%s" % ", ".join(src_missing))
    if src_extra:
        ok = False
        lines.append("包内存在但源码清单没有声明的文件：%s" % ", ".join(src_extra))
    if not src_missing and not src_extra:
        lines.append("包内每个文件均可追溯到 %s 下的材料 / 生成器 / 版本文件" % materials)
    return Report_section("对账三 · 文件清单差异（两包 + 源码）", ok, lines)


def check_version_injection(info, exe_dir, bat_dir, root):
    lines, ok = [], True
    a, b = _pkg_files(exe_dir), _pkg_files(bat_dir)

    def note(cond, text):
        nonlocal ok
        lines.append(("OK   " if cond else "BAD  ") + text)
        if not cond:
            ok = False

    for pkg, files in (("EXE版", a), ("bat版", b)):
        vf = files.get("VERSION.txt")
        if not vf:
            note(False, "%s 缺 VERSION.txt" % pkg)
        else:
            got = _read_text(vf).strip()
            note(got == info["tag"], "%s VERSION.txt = %r（应 %r）" % (pkg, got, info["tag"]))
        html = files.get("bat下载命令生成器.html")
        if not html:
            note(False, "%s 缺生成器页面" % pkg)
        else:
            m = GENERATOR_VERSION_RE.search(_read_text(html))
            note(bool(m) and m.group(2) == info["release"],
                 "%s 生成器 CURRENT_VERSION = %r（应 %r）" % (pkg, m.group(2) if m else None, info["release"]))
            note(("当前版本 %s" % info["tag"]) in _read_text(html), "%s 生成器页眉含版本 %s" % (pkg, info["tag"]))
        doc = files.get("使用说明.txt")
        if doc:
            head = _read_text(doc).split("\n")[0]
            note(head.startswith("BBDown-fixed %s" % info["tag"]), "%s 说明首行 = %r" % (pkg, head))
    # 仓库内生成器页面的 CURRENT_VERSION 必须与 Release 同步（防漂移）
    m = GENERATOR_VERSION_RE.search(_read_text(os.path.join(root, GENERATOR_HTML)))
    note(bool(m) and m.group(2) == info["release"],
         "仓库生成器页面 CURRENT_VERSION = %r 与 AppVersion.Release 一致" % (m.group(2) if m else None))
    # 仓库页面上的「当前版本 vX」可见行同样必须跟 Release 走。
    # 只查 CURRENT_VERSION 不够：版本常量对齐了、页眉还挂着旧版本号时，用户看到的是旧的
    # （2026-11-06 实测：仓库此行为 v1.0.5、发布包里是 v1.0.6，而当时没有任何一条检查盯它）。
    gen_repo = _read_text(os.path.join(root, GENERATOR_HTML))
    note(("当前版本 %s" % info["tag"]) in gen_repo,
         "仓库生成器页面页眉含版本 %s" % info["tag"])
    # 压缩包名派生
    note(info["zip_exe"].startswith("BBDown-fixed-%s" % info["tag"]), "压缩包名 %s" % info["zip_exe"])
    note(info["zip_bat"].startswith("BBDown-fixed-%s" % info["tag"]), "压缩包名 %s" % info["zip_bat"])
    return Report_section("版本注入 · 单一来源派生一致性", ok, lines)


def check_bat_conformance(materials):
    """bat 编码/行尾/chcp/前缀守护（verify_bat.js 只查注入安全，这里补编码口径）。

    守护对象 = 真正会进入压缩包的源文件（EXE_LAYOUT/BAT_LAYOUT 引用的 bat），
    不是仓库里同名的历史残留。

    判定是「真断言」，不做 GBK 兜底：
      · 严格 UTF-8 解码（decode('utf-8') 抛 UnicodeDecodeError 即 BAD）+ 无 BOM
      · 行尾纯 CRLF、零裸 LF（旧实现只看 crlf>0 and lf==0，把普遍情况放过去了）
      · 含 chcp 65001 >nul，且位于 @echo off 之后、首个真实命令之前、BBDown 调用之前
      · BBDown 调用行是 "%~dp0core\\BBDown.exe" 前缀形态
    """
    lines, ok = [], True

    def note(cond, text):
        nonlocal ok
        lines.append(("OK   " if cond else "BAD  ") + text)
        if not cond:
            ok = False

    def _norm_lines(text):
        return text.replace("\r\n", "\n").split("\n")

    def _is_chcp(line):
        return re.match(r"^chcp\s+65001($|[\s>])", line.strip(), re.I) is not None

    def _is_comment(line):
        s = line.strip().lower()
        return s == "" or s == "rem" or s.startswith("rem ") or s.startswith("::")

    def _chcp_position(text):
        """返回 (ok, 描述)；chcp 必须在 @echo off 之后、首个真实命令与 BBDown 调用之前。"""
        ls = _norm_lines(text)
        echo_idx = next((i for i, l in enumerate(ls) if l.strip().lower() == "@echo off"), -1)
        if echo_idx < 0:
            return False, "找不到 @echo off"
        chcp_idx = next((i for i, l in enumerate(ls) if _is_chcp(l)), -1)
        if chcp_idx < 0:
            return False, "缺少 chcp 65001 >nul（中文输出在非 936 代码页会乱码）"
        if chcp_idx <= echo_idx:
            return False, "chcp 在第 %d 行，未排在 @echo off（第 %d 行）之后" % (chcp_idx + 1, echo_idx + 1)
        real_idx = -1
        for i in range(echo_idx + 1, len(ls)):
            if _is_comment(ls[i]) or _is_chcp(ls[i]):
                continue
            real_idx = i
            break
        if real_idx >= 0 and chcp_idx > real_idx:
            return False, ("chcp 在第 %d 行，已晚于第 %d 行的命令（%s）"
                           % (chcp_idx + 1, real_idx + 1, ls[real_idx].strip()))
        bb_idx = next((i for i, l in enumerate(ls)
                       if re.match(r'^\s*"%~dp0core\\BBDown\.exe"', l)), -1)
        if bb_idx >= 0 and chcp_idx > bb_idx:
            return False, "chcp 在第 %d 行，已晚于第 %d 行的 BBDown 调用" % (chcp_idx + 1, bb_idx + 1)
        return True, ("chcp 第 %d 行（@echo off 第 %d 行 / 首条命令第 %d 行 / BBDown 第 %s 行）"
                      % (chcp_idx + 1, echo_idx + 1, real_idx + 1,
                         bb_idx + 1 if bb_idx >= 0 else "—"))

    used = sorted({spec for _, spec in EXE_LAYOUT + BAT_LAYOUT
                   if spec.startswith("pkgassets/") and spec.endswith(".bat")})
    for spec in used:
        p = os.path.join(materials, spec)
        if not os.path.isfile(p):
            note(False, "%s 不存在" % spec)
            continue
        raw = _read_bytes(p)
        is_login = os.path.basename(spec).startswith("登录")

        # ① 严格 UTF-8 + 无 BOM（不再用 gbk 兜底：那等于永远不抛错，本节形同虚设）
        bom = raw[:3] == b"\xef\xbb\xbf"
        note(not bom, "%s 无 BOM（首三字节 %s）" % (spec, raw[:3].hex(" ")))
        try:
            text = raw.decode("utf-8")
            note(True, "%s 严格 UTF-8 解码通过（%d 字节）" % (spec, len(raw)))
        except UnicodeDecodeError as e:
            note(False, "%s 不是合法 UTF-8：%s（该文件仍是非 UTF-8，编码统一未完成）" % (spec, e))
            continue

        # ② 行尾纯 CRLF、零裸 LF
        crlf, total_lf = raw.count(b"\r\n"), raw.count(b"\n")
        bare = total_lf - crlf
        note(bare == 0 and crlf > 0,
             "%s 行尾纯 CRLF（crlf=%d 裸LF=%d）" % (spec, crlf, bare))

        # ③ chcp 存在且位置正确
        cp_ok, cp_desc = _chcp_position(text)
        note(cp_ok, "%s %s" % (spec, cp_desc))

        # ④ 调用行 core 前缀
        prefix = '"%~dp0core\\BBDown.exe"' in text
        bare_call = re.search(r'(?m)^\s*BBDown(\.exe)?(\s|$)', text) is not None
        note(prefix, "%s 用 \"%%~dp0core\\\\BBDown.exe\" 前缀调用%s"
             % (spec, ("（登录脚本同样走 core 前缀）" if is_login else "")
                + ("；检出裸 BBDown 调用行" if bare_call and not prefix else "")))

    # 仓库卫生（不阻断）：package-extras 下同名的长名 bat 是 core 布局前的历史版，
    # 现行实体在 bats/ 简化名下，包内只取后者。此处如实列出，避免下次误用。
    stale_repo = []
    repo_bats_dir = os.path.join(materials, "pkgassets", "bats")
    for name in sorted(os.listdir(repo_bats_dir)):
        if not name.endswith(".bat"):
            continue
        spec = os.path.join("pkgassets", "bats", name)
        if spec in used:
            continue
        raw = _read_bytes(os.path.join(repo_bats_dir, name))
        try:
            text = raw.decode("utf-8")
            utf8_ok = True
        except UnicodeDecodeError:
            utf8_ok = False
            text = ""
        if raw.count(b"\n") - raw.count(b"\r\n") > 0 or not utf8_ok \
                or '"%~dp0core\\BBDown.exe"' not in text:
            stale_repo.append(name)
    if stale_repo:
        lines.append("提示（不阻断）：仓库 package-extras 下 %d 个长名 bat 仍是 LF / 无 core 前缀的历史版，"
                     "现行实体在 bats/ 简化名下、包内只取后者；建议后续清理历史文件，避免误用"
                     % len(stale_repo))
        for n in stale_repo:
            lines.append("    · " + n)
    return Report_section("编码守护 · bat 编码/CRLF/chcp/前缀", ok, lines)


def cmd_reconcile(root, outdir, materials, quiet=False):
    release, src_line, src_text = read_release(root)
    info = derive(release)
    exe_dir = os.path.join(outdir, "EXE版", PKG_DIR_NAME)
    bat_dir = os.path.join(outdir, "bat版", PKG_DIR_NAME)
    for d in (exe_dir, bat_dir):
        if not os.path.isdir(d):
            raise SystemExit("对账目标不存在：%s（先跑 assemble）" % d)

    rep = Report()
    rep.add("版本单一来源 · %s" % VERSION_SOURCE,
            True, ["%s:%d  %s" % (VERSION_SOURCE, src_line, src_text.strip()),
                   "派生：%s / 压缩包 %s、%s" % (info["tag"], info["zip_exe"], info["zip_bat"]),
                   "脚本内不含版本号字符串常量（全部由 Release 现读现算）"])
    for section in (check_hash(exe_dir, bat_dir),
                    check_literals(root, release, skip_dirs=(outdir, materials)),
                    check_fileset(exe_dir, bat_dir, materials),
                    check_version_injection(info, exe_dir, bat_dir, root),
                    check_bat_conformance(materials)):
        rep.add(*section)
    if not quiet:
        print(rep.render())
    return rep, info


# ---------------------------------------------------------------- 负样本自检

def _hardlink_copy(src_tree, dst_tree):
    """用硬链接快速复制产物树（省 150MB 拷贝）；同分区不可用时退回实拷贝。"""
    os.makedirs(dst_tree, exist_ok=True)
    for base, _dirs, files in os.walk(src_tree):
        rel = os.path.relpath(base, src_tree)
        target = dst_tree if rel == "." else os.path.join(dst_tree, rel)
        os.makedirs(target, exist_ok=True)
        for f in files:
            s, d = os.path.join(base, f), os.path.join(target, f)
            if os.path.exists(d):
                continue
            try:
                os.link(s, d)
            except OSError:
                shutil.copy2(s, d)


def _mutate_wrong_version(exe_dir, release):
    """把 VERSION.txt 写成「真值 +1」的错误版本：错误值由真值现算，脚本内不留第二份版本号字面量。"""
    parts = release.split(".")
    wrong = ".".join(parts[:-1] + [str(int(parts[-1]) + 1)])
    p = os.path.join(exe_dir, "VERSION.txt")
    os.unlink(p)                                   # 先断硬链接，避免写穿到基线包
    _write_text(p, "v" + wrong + "\n", newline="\n")
    return "应 %s → 写成 %s" % (release, wrong)


def _mutate_broken_hash(bat_dir, release):
    """只翻转 bat 版 core\\aria2c.exe 的最后一个字节：大小不变、内容变，只有逐文件哈希能抓到。"""
    p = os.path.join(bat_dir, "core", "aria2c.exe")
    raw = bytearray(_read_bytes(p))
    raw[-1] = raw[-1] ^ 0xFF
    os.unlink(p)                                   # 先断硬链接，避免写穿到基线包
    with open(p, "wb") as f:
        f.write(bytes(raw))
    return "core/aria2c.exe 末字节翻转，大小不变"


def _mutate_missing_file(exe_dir, release):
    p = os.path.join(exe_dir, "core", "ffmpeg.exe")
    os.unlink(p)
    return "删除 core/ffmpeg.exe"


MUTATIONS = [
    ("负样本① 改错版本号（VERSION.txt 写成错版本）", _mutate_wrong_version, "EXE版", "版本注入"),
    ("负样本② 改坏一个文件哈希（bat 版 core\\aria2c.exe 翻 1 字节）", _mutate_broken_hash, "bat版", "对账一"),
    ("负样本③ 少一个文件（EXE 版删掉 core\\ffmpeg.exe）", _mutate_missing_file, "EXE版", "对账一|对账三"),
]


def cmd_selfcheck(root, outdir, materials):
    base_rep, info = cmd_reconcile(root, outdir, materials, quiet=True)
    print(base_rep.render())
    print("=== 基线（未改动）退出码：%d（应为 0） ===" % (0 if base_rep.ok else 1))
    if not base_rep.ok:
        print("基线不绿，负样本自检无意义，先修对账")
        return 1, base_rep.to_json()
    print("")

    results = []
    for title, fn, which, expect_red in MUTATIONS:
        # 临时目录必须与产物目录同一分区，硬链接才可用（/tmp 是另一块盘，os.link 会 Errno 18）
        with tempfile.TemporaryDirectory(prefix=".selfcheck-", dir=os.path.dirname(outdir) or None) as td:
            exe_dir = os.path.join(td, "EXE版", PKG_DIR_NAME)
            bat_dir = os.path.join(td, "bat版", PKG_DIR_NAME)
            _hardlink_copy(os.path.join(outdir, "EXE版", PKG_DIR_NAME), exe_dir)
            _hardlink_copy(os.path.join(outdir, "bat版", PKG_DIR_NAME), bat_dir)
            how = fn(exe_dir if which == "EXE版" else bat_dir, info["release"])
            rep = Report()
            for section in (check_hash(exe_dir, bat_dir),
                            check_literals(root, info["release"], skip_dirs=(outdir, materials, td)),
                            check_fileset(exe_dir, bat_dir, materials),
                            check_version_injection(info, exe_dir, bat_dir, root),
                            check_bat_conformance(materials)):
                rep.add(*section)
            red_names = [n for n, ok, _ in rep.sections if not ok]
            caught = bool(red_names)
            # 一个负样本可能被多节同时抓到（删文件既破坏哈希也破坏清单），期望项满足任一即算命中
            expects = [e.strip() for e in expect_red.split("|") if e.strip()]
            hits = [e for e in expects if any(e in n for n in red_names)]
            expect_hit = bool(hits)
            ok = caught and expect_hit
            results.append({"title": title, "mutation": how, "red": caught,
                            "red_sections": red_names, "expect": expect_red, "ok": ok,
                            "exit_code": 0 if not caught else 1})
            print("=== %s ===" % title)
            print("构造的错误：%s" % how)
            print(rep.render())
            print("报红小节：%s" % ("、".join(red_names) if red_names else "无"))
            print("期望报红小节：%s → 命中 %s"
                  % (expect_red, "、".join(hits) if hits else "无（不合格）"))
            print("退出码：%d%s" % (0 if not caught else 1,
                                  "（报红，符合预期）" if ok else "（不合格）"))
            print("")
    all_caught = all(r["ok"] for r in results)
    data = base_rep.to_json()
    data["negatives"] = results
    data["selfcheck_ok"] = all_caught
    print("=== 负样本自检结论：%s（%d/%d 个负样本让对账报红且命中期望小节） ==="
          % ("通过" if all_caught else "不通过",
             sum(1 for r in results if r["ok"]), len(results)))
    return (0 if all_caught else 1), data


# ---------------------------------------------------------------- 入口

def main(argv):
    ap = argparse.ArgumentParser(description="发布组装与对账唯一入口")
    ap.add_argument("mode", choices=["assemble", "reconcile", "selfcheck", "all"])
    ap.add_argument("--root", default=REPO_ROOT_DEFAULT, help="仓库根（含 src/ tools/）")
    ap.add_argument("--materials", default="release-materials", help="打包材料目录")
    ap.add_argument("--out", default="build/release", help="组装产物目录")
    ap.add_argument("--report", default="", help="把对账结果写成 JSON")
    ap.add_argument("--no-zips", action="store_true", help="只出目录不打压缩包")
    args = ap.parse_args(argv)

    root = os.path.abspath(args.root)
    materials = os.path.abspath(os.path.join(root, args.materials))
    outdir = os.path.abspath(os.path.join(root, args.out))
    os.makedirs(outdir, exist_ok=True)

    data = None
    rc = 0
    if args.mode in ("assemble", "all"):
        cmd_assemble(root, materials, outdir, make_zips=not args.no_zips)
        print("")
    if args.mode in ("reconcile", "all"):
        rep, _info = cmd_reconcile(root, outdir, materials)
        rc = 0 if rep.ok else 1
        data = rep.to_json()
        if args.report:
            _write_text(os.path.abspath(args.report), json.dumps(data, ensure_ascii=False, indent=2), newline="\n")
        print("退出码：%d" % rc)
    if args.mode == "selfcheck":
        rc, data = cmd_selfcheck(root, outdir, materials)
        if args.report:
            _write_text(os.path.abspath(args.report), json.dumps(data, ensure_ascii=False, indent=2), newline="\n")
        print("退出码：%d" % rc)
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
