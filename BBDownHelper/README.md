# BBDown 下载助手（EXE 界面版）

给零基础中文用户用的 BBDown 图形界面：粘贴链接 → 自动识别 → 勾需求 → 选位置 → 开始下载，全程在一个窗口里完成。程序内部调起 **core\ 子文件夹下的 BBDown.exe** 干活（v3.3 起）。

## 使用方法
1. 用完整压缩包整体解压：顶层为 `BBDownHelper.exe`，`core\` 文件夹里是 `BBDown.exe`（1.6.3 登录修复版）/ `aria2c.exe` / `ffmpeg.exe`；
2. 双击 `BBDownHelper.exe`（无需安装任何运行时，自带 .NET 8）；
3. 粘贴 B 站链接 / BV号 / av号 / 整段分享文字 —— 自动识别并显示结果卡片；
4. 需要登录画质（1080P高码率/4K）时先点「扫码登录」，用手机 B 站 App 扫码；
5. 勾选需求 → 「浏览…」选保存文件夹 → 「开始下载」，日志区实时显示进度，「取消下载」可中断。

## 项目结构
- `src/BBDownHelper.Core/` —— 识别与命令组装类库（**与 UI 解耦**，可在 Linux 上跑测试）
  - `InputRecognizer.cs` 净化 + 11 类分类 + 白名单 URL 重建（安全红线 P1-②）
  - `BiliProbe.cs` b23.tv 302 解析 + 裸数字三路探测（type=5/type=8/view?aid，判据 data!=null && media_count>0）
  - `RecognitionService.cs` 识别门面（短链解析后重分类、多候选消歧、单命中提示可反悔）
  - `CommandBuilder.cs` 参数组装（全部出自 BBDown CommandLineInvoker.cs 源码）
- `src/BBDownHelper/` —— Avalonia 11 界面（识别卡片/候选选择/选项互斥/进度日志/取消/登录）
- `tests/BBDownHelper.Core.Tests/` —— 77 个 xUnit 用例（v3.3：v3.2 的 66 个 + core 布局路径与迁移 11 个），Linux 上实跑通过

## v3.3 变更（core 子文件夹改造：防误双击 BBDown.exe）
- **核心路径**：BBDown.exe 唯一位置改为 `<程序所在目录>\core\BBDown.exe`（Core 新增 `CoreLayout`，MainWindow 全部改走它，无第二处硬编码）。报错文案同步指引 v1.0.2 完整压缩包解压
- **登录凭据**：BBDown.data 的存在检测与 cookie 读取（BiliProbe 链路）改读 `core\BBDown.data`——与 BBDown 1.6.3 的 APP_DIR 口径一致（它把凭据写在自身 exe 目录）
- **首次启动自动迁移**：老用户从 v1.0.0/v1.0.1 升级时，顶层的 `BBDown.data` / `BBDown.refresh_token` / `BBDownTV.data` 自动移入 core\（core\ 已有的不覆盖，清单外文件不碰，core\ 不存在时不动用户数据）。迁移逻辑在 Core 层（`CoreLayout.PlanMigration` 纯函数 + `CoreDataMigrator.Execute` 执行器，失败只记日志不崩、按未登录态继续）
- **子进程工作目录保持顶层**：BBDown 侧已核实（isxulong/BBDown-fixed 源码）——aria2c/ffmpeg 按「当前目录 → 自身 exe 目录(core\) → PATH」顺序查找（BBDownUtil.FindExecutable），UP 主投稿清单 txt 写在进程工作目录；两者在 core\ 布局下均不受影响
- 界面/日志中无版本号展示（Title 为「BBDown 下载助手」），无升级项

## v3.2 变更（日志中文乱码修复 + 程序图标）
- **日志中文乱码**：BBDown 按系统 ANSI 代码页写 stdout（中文 Windows=GBK/936），v3.1 硬按 UTF-8 解码把中文全变成 U+FFFD（）。新增 Core 纯函数 `ConsoleEncoding.Resolve(ansiCodePage)`（注册 CodePagesEncodingProvider，取系统 ANSI 代码页、取不到回落 GB18030），重定向子进程的 StandardOutputEncoding/StandardErrorEncoding 改用 `ConsoleEncoding.Current`；-ia/扫码登录走可见控制台无重定向不受影响，BiliProbe 走 HTTP 不涉及；日志区只在窗口内（无写文件），解码修正后即为正确文本
- **程序图标**：csproj 加 `<ApplicationIcon>Assets\BBDownHelper.ico</ApplicationIcon>`（exe 文件图标 + 任务栏 + 桌面快捷方式），MainWindow.axaml 加 `Icon="avares://BBDownHelper/Assets/BBDownHelper.ico"`（窗口标题栏运行时图标）

## v3.1 变更（质检复核 P2 必修 + 2 条 P3）
- **P2**：`InputRecognizer.cs` 全部数字正则 `\d` → `[0-9]`（DigitsRegex/AvSearchRegex/BareNumberRegex 为必修三处，AvFullRegex/AvInUrlOrBare/EpSsInPath/NewListsRegex/SpaceMidRegex 同根同修）。根因：.NET `\d` 匹配 Unicode 全角数字，`IsValidSelectPage("８")` 曾返回 True，全角 `-p ８` 可穿透校验原样传给 BBDown
- **P3**：b23 短链判定由 `Contains("b23.tv")` 改为锚定 `^https://b23\.tv/`，`b23.tv.evil.com` 仿冒域不再误判
- **P3**：-info 自动勾选标记在用户手动改动时立即复位（新增 Core 纯函数 `InfoFlagPolicy.AfterToggle`，MainWindow 用 `_infoSettingProgrammatically` 区分程序化设置）

## 关键设计
- 新版 space lists 链接强制转换为 collectiondetail/seriesdetail 旧格式再交给 BBDown（直接喂会下载整个 UP 主页，已实锤的坑）
- 裸数字多命中（系列/合集 sid 两套命名空间会碰撞，实测 2735065 双命中）弹候选列表用户单选，不预选、不静默采用
- UP 主主页识别 → 强制二次确认弹窗 + 默认勾「只解析不下载」（红队 P1-④）；v3 起界面与弹窗如实说明 BBDown 1.6.3 的真实行为：只导出『UP昵称的投稿视频.txt』清单后打印红字「暂不支持该功能」退出（固定提示、不是出错），运行结束指引清单位置并推荐 bat下载命令生成器.html 做批量下载，红字出现时不标「失败/出错」
- 「只解析，不下载」开关位于下载设置区最顶部，勾选后画质/编码/内容勾选/弹幕/多线程/选集/保存位置/间隔/断点跳过全部置灰并显示说明行（规则在 Core 的 UiMutexPolicy，只解析优先级高于 contentLocked 与 audioOnly，可单测）
- 用户原始粘贴文本永不进命令行：只把提取 token 经白名单校验后重建，起子进程用 ArgumentList 传参（无 shell 注入面）
