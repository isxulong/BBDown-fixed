# package-extras：发行包内的非二进制材料

这里存放 Release 压缩包中除可执行文件外的全部材料，两条路线共用：

- `使用说明-EXE版.txt` / `使用说明-bat版.txt`：两个压缩包各自的使用说明
- `BBDown命令使用指南.txt`：零基础命令教程（bat 版附带），示例命令统一为 `core\BBDown.exe` 形态
- `bats/`：bat 版附带的 3 个基础 bat（登录 / 默认下载 / 只看信息不下载），
  全部改为 `"%~dp0core\BBDown.exe"` 调用；保持 `cd /d %~dp0` 把工作目录锚定在包根目录、
  **不 cd 进 core**，下载产物与 UP 投稿清单仍落在包根目录；
  BBDown.data / BBDownTV.data 等数据文件由 BBDown 按自身 exe 所在目录（core\）读写，不受影响；
  仓库内使用简化文件名，压缩包内保留带场景说明的长文件名
- bat 下载命令生成器（HTML 单文件）在 `tools/` 目录：默认生成 `core\BBDown.exe ...` 前缀命令，
  页面提供「BBDown.exe 就在当前文件夹（官方原版布局）」开关，勾上后输出不带前缀的旧格式

可执行文件（BBDown.exe / BBDownHelper.exe / aria2c.exe / ffmpeg.exe）
不进仓库，只在 Release 里以压缩包形式提供。

## 压缩包目录结构（v1.0.2 起，EXE 版与 bat 版统一）

```
解压后的文件夹/                    ← 包根目录（.bat 与使用说明都在这里）
├── BBDownHelper.exe               EXE 版界面程序（仅 EXE 版压缩包附带）
├── *.bat                          bat 版入口文件（仅 bat 版压缩包附带，放包根目录）
├── 使用说明.txt / BBDown命令使用指南.txt
└── core/                          ← BBDown 本体三件套，防止误双击
    ├── BBDown.exe
    ├── aria2c.exe
    └── ffmpeg.exe
```

说明：

- BBDown.data（登录凭据）等运行期文件由 BBDown 自动生成在 core\ 内；
- bat 通过 `"%~dp0core\BBDown.exe"` 调用程序，工作目录保持在包根目录，
  因此下载的视频、UP 投稿清单 txt 仍落在包根目录，行为与旧版一致；
- 旧版（v1.0.1 及以前）顶层目录里的 BBDown.exe / aria2c.exe / ffmpeg.exe，
  升级 v1.0.2 时把三件套移入 core\ 即可；若顶层已有 BBDown.data（登录凭据），
  一并移入 core\ 可保留登录状态（如有 BBDown.refresh_token、BBDownTV.data 等同理一并移入）。
  注意：自动迁移只有 EXE 界面版提供，bat 版需要手动移动。
