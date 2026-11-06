# tools：发版与校验工具

这里是维护者发版时用的脚本，`BBDown.exe` 等可执行文件不在仓库里，只在 Release 压缩包里提供。
日常使用这个项目的用户**不需要看这个目录**，直接到 [Releases](../../releases/latest) 下载压缩包即可。

发版流程按顺序跑下面这些脚本；每个脚本都是「不合规就非零退出」，不会静默放过问题。

## 1. `bat下载命令生成器.html`

单文件离线网页，帮不想记命令行的用户拼出下载命令。默认生成 `core\BBDown.exe ...` 前缀；
页面里有「BBDown.exe 就在当前文件夹（官方原版布局）」开关，勾上后输出不带前缀的旧格式。
这个文件会被原样打进两个压缩包，所以改动它等于改动用户拿到的页面。

## 2. `stage_materials.py` —— 把仓库布局摆成发版工作区

发版脚本按固定布局找文件，而仓库的实际布局与之不同，本脚本只做「搬运 + 记账」：

```
python3 tools/stage_materials.py --repo-root . --root <发版工作区> --bin <可执行文件目录>
```

它把 `BBDownHelper/src`、`BBDownHelper/tests`、`tools/`、`package-extras/` 搬到工作区对应位置，
每一步都打印「搬了什么 / 跳过了什么 / 为什么跳过」。缺材料会显式报错并以非零退出码收尾，
避免等到对账时才发现材料缺失、还错怪对账脚本。

## 3. `gate_bats.py` —— 组包前的脚本形态闸门

这个闸门来自一次真实事故：编码修复只落在验收目录、没有回灌到发版材料目录，
结果发出去的还是旧版 bat，而对账全绿、看不出问题。

```
python3 tools/gate_bats.py --pkgassets package-extras
```

`package-extras/bats/*.bat` 必须全部满足：UTF-8 无 BOM、纯 CRLF、`chcp 65001` 紧跟 `@echo off`、
调用 `"%~dp0core\BBDown.exe"`。任一不合规即非零退出，禁止继续组包。

## 4. `release_reconcile.py` —— 组装与对账的唯一入口

```
python3 tools/release_reconcile.py all --root <工作区> --materials <材料目录>
```

它负责两件事：按布局组装两个压缩包；然后跑对账——两包共同文件逐文件哈希比对、
全仓版本字面量位置清单（豁免必须写明理由）、包内文件能否追溯到源、bat 编码形态。
`mode` 可选 `assemble` / `reconcile` / `selfcheck` / `all`；加 `--no-zips` 只出目录不打压缩包。

版本号唯一真值来源是 `BBDownHelper/src/BBDownHelper.Core/AppVersion.cs` 的 `Release` 常量，
压缩包名 / 页面版本 / `VERSION.txt` / 说明页眉全部由它派生，脚本自身不写死任何版本串。
