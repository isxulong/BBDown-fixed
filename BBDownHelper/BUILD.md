# 构建说明 + 识别逻辑本地测试运行记录

环境：Linux 沙箱（2 核），.NET SDK 8.0.427（dotnet-install.sh 装到 ~/.cli/dotnet），构建时间 2026-11-03。

## 一、构建步骤

```bash
export DOTNET_ROOT=/home/gem/.aily/.cli/dotnet
export PATH=$DOTNET_ROOT:$PATH
export NUGET_PACKAGES=/home/gem/.aily/.cli/nuget
export MSBUILDDISABLENODEREUSE=1

# 1) Linux 上跑识别逻辑测试（无界面，xUnit）
dotnet test tests/BBDownHelper.Core.Tests -p:UseSharedCompilation=false

# 2) 交叉发布 win-x64 单文件自包含 EXE
dotnet publish src/BBDownHelper -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:PublishAot=false -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish_win_single
```

发布结果（本沙箱实跑）：
- `dotnet publish` 成功，产出 `publish_win_single/BBDownHelper.exe`
- `file` 判定：`PE32+ executable (GUI) x86-64, for MS Windows`
- 大小 44 MB，单文件自包含（.NET 8 运行时 + Avalonia + SkiaSharp 全打包，用户机器无需装任何东西）
- 说明：`-p:IncludeNativeLibrariesForSelfExtract=true` 为在任务基线参数之上追加的参数，作用是把 SkiaSharp/HarfBuzz 原生 DLL 一并打进单文件（否则 Avalonia 在单文件发布下默认会留 3 个原生 DLL 在旁边）；启动时自动解压到临时目录，用户侧仍是"一个 EXE 拷进 BBDown 文件夹就能用"。
- 若不加该参数，发布同样成功（38MB EXE + av_libglesv2.dll/libHarfBuzzSharp.dll/libSkiaSharp.dll 三个伴生 DLL），两种方式本沙箱均已实跑验证。

## 二、识别逻辑测试运行记录（Linux 实跑，dotnet test）

时间：2026-11-03，SDK 8.0.427，结果：**35 通过 / 0 失败 / 0 跳过**（`Passed! - Failed: 0, Passed: 35, Skipped: 0, Total: 35, Duration: 428 ms`）。

覆盖 13 类以上输入（对应验收清单）：
1. BV 裸号（`Purify_BareBv_Extracted` / `Classify_BvToken`）
2. av 号、大小写（`Purify_AvCaseInsensitive` / `Classify_AvUrl`）
3. 番剧 ep / ss（`Classify_BangumiEpAndSs`）
4. 课程（`Classify_Cheese`）
5. 新版合集 lists?type=season（`Classify_NewListsSeason_ConvertsToCollectiondetail`）
6. 新版系列 lists?type=series（`Classify_NewListsSeries_ConvertsToSeriesdetail`）
7. 旧版合集 channel/collectiondetail（`Classify_OldCollection`）+ medialist business=space_collection（`Classify_MedialistCollection`）
8. 旧版系列 channel/seriesdetail（`Classify_OldSeries`）
9. 收藏夹 favlist（`Classify_Favlist`）
10. UP 主主页（`Classify_UpSpace_RequiresConfirm`，断言强制二次确认）
11. b23.tv 短链解析后重分类（`ShortLink_Resolved_ThenReclassified`，mock 302 → lists?type=season → 断言转换）
12. 纯数字单命中/多命中/零命中/误配（`BareNumber_*` 四个用例）
13. 分享话术文本净化（`Purify_ShareText_PicksFullUrl` / `Purify_ShareText_PicksB23First`）

三个关键断言（验收指定）：
- **新版 lists 链接转换**：`Classify_NewListsSeason/Series` 断言 FinalInput 必须是 `channel/collectiondetail?sid=...` / `channel/seriesdetail?sid=...`
- **多候选消歧触发**：`BareNumber_MultiHit_RequiresUserChoice_NeverSilentlyFirst` 断言 2735065 双命中时 NeedsUserChoice=true、候选=2、FinalInput 为空（禁止静默选第一个）
- **白名单重建不含原始输入**：`Safety_RawTextNeverReachesCommand` 用带注入意图的粘贴文本（`... & del /q C:\ & echo pwned`）断言最终参数数组不含任何原始文本片段，末参只有重建后的 BV；`Classify_EvilHost_Rejected` 断言非 bilibili 域名直接拒绝；`SelectPage_Whitelist` 断言 -p 写法白名单拒绝 `8; rm -rf /`

另有命令组装互斥用例：-mt 默认、-q 精确串、-info 时忽略其余、列表类才加 --delay-per-page/--save-archives-to-file、--cover-only 锁内容类选项、--work-dir 传值。

## 三、网络探测测试口径

裸数字三路探测（medialist/info type=5 / type=8 / view?aid=N）的判据按共享规格实网实测结论编码：
- code 永远为 0，判据是 `data != null && media_count > 0`（误配返回 media_count=0 不算命中）
- 测试通过注入 mock HTTP 委托（`GetStringOverride`/`GetRedirectOverride`）在无网环境验证决策树，真实网络路径在用户机器上执行时逻辑相同；探测请求自动携带同目录 BBDown.data 里的 cookie（存在时）。

### 四、-ia（交互选画质）的运行方式
BBDown 的 `-ia` 需要用户在控制台里输入序号选画质，隐藏的重定向子进程无法交互。因此 EXE 检测到勾选 `-ia` 时，自动改为用可见控制台窗口启动 BBDown（UseShellExecute），用户在那个黑窗口里选画质、看进度；其余所有情况仍走隐藏子进程 + 窗口内日志区 + 进度条。

## 五、未验证范围（如实说明）
- EXE 的窗口界面在 Linux 沙箱无法启动运行（Windows GUI 目标），已通过 Linux 侧编译验证 + 结构检查（PE32+ x86-64 GUI）；界面交互需在 Windows 机器上由人工/后续红蓝队走查确认。
- BBDown.exe 进程调用、扫码登录弹窗、文件夹对话框均依赖 Windows 环境，逻辑已按规格实现但未在本沙箱实际运行。


---

# v2（红蓝对抗 5 项必修修复，2026-11-03）

沙箱重建后 .NET SDK 重新安装（8.0.404，dotnet-install 官方源断点续传），修复后重跑：

## 测试记录（Linux 实跑，dotnet test）

- 修复中第一轮：46 用例 / 1 失败（`Classify_EvilHost_StillRejected_NoBvFallback`——RebuildUrl 字符校验先于 host 校验，evil.com 混 CJK 的链接被 BV 回退采纳）
- 修正：RebuildUrl 内 **host 校验优先于字符校验**（非 bilibili 域名永远 InvalidHost 拒绝，不给回退机会）
- 最终结果：**46 通过 / 0 失败 / 0 跳过**（`Passed! - Failed: 0, Passed: 46, Skipped: 0, Total: 46, Duration: 376 ms`）

## 发布记录

`dotnet publish` 同 v1 参数（PublishSingleFile + IncludeNativeLibrariesForSelfExtract）：
- 产出 `publish_win_single/BBDownHelper.exe`，45,280,249 字节
- `file` 判定：`PE32+ executable (GUI) x86-64, for MS Windows`

## v2 修复清单（对应裁决 5 项必修 + 2 顺手项）

1. **红 P2-1 分享话术容错**：UrlRegex 字符集收紧为白名单一致集（CJK/全角标点在 token 边界截断）；B23Regex `\w`→`[A-Za-z0-9]+`；RebuildUrl 增加 UrlRebuildError 输出，InvalidHost 拒绝、其他失败在 token 内回退搜 BV/av 再试；失败文案区分「域名不对」与「混入多余文字」，不再误报。
2. **红 P2-2 选集非法输入**：PageBox 输入实时校验，非法即红色提示 + 禁用开始按钮；OnStart 再兜一道明确文案；下载结束按校验状态恢复按钮。
3. **红蓝共识 P2 识别竞态**：Core 新增 GenerationGate（Interlocked 代际号），RecognizeNowAsync 发起取号、返回比对，过期结果丢弃。
4. **蓝 P2-1 audio-only 互斥**：CommandBuilder 在 AudioOnly/contentLocked 时整体跳过画质与编码分支，不再生成静默失效的 -q/--encoding-priority。
5. **蓝 P2-3 关窗杀进程**：OnClosing 时若子进程存活，先弹「下载还在进行，确定关闭？」确认，确认后 Kill(entireProcessTree: true) 再放行关窗；Exited 回调在强制关窗时不再碰 UI。
6. **红 P3-5**：裸数字 long.Parse → TryParse，>=19 位直接返回空候选。
7. **红 P3-1**：-info 自动勾选带 _infoAutoChecked 标记，换输入只复位自动勾的，用户手动勾的不动。

## 新增测试用例（11 个，35→46）

- CJK 容错：`Purify_CjkPunctuationAfterUrl_Excluded` / `Purify_CjkTextTouchingUrl_Excluded` / `Purify_B23WithCjkPunctuation_Excluded` / `ShareText_CjkWrapped_BvVideoRecognized` / `Classify_UrlTokenWithCjkTail_FallsBackToBv` / `Classify_InvalidCharFailure_MessageNotMisleading` / `Classify_EvilHost_StillRejected_NoBvFallback`
- audio-only 互斥：`Build_AudioOnly_SkipsQualityAndEncoding` / `Build_ContentLocked_SkipsQualityAndEncoding`
- 代际竞态：`GenerationGate_StaleGeneration_Discarded`
- 溢出兜底：`BareNumber_OverflowDigits_NoCrash_Unknown`

## v2 未验证范围（如实说明）

- 必修 2/5 与顺手 P3-1 为纯 UI 行为（实时标红、关窗确认弹窗、-info 复位），Linux 沙箱无法启动 Windows GUI，以代码审查 + 核心逻辑（IsValidSelectPage / GenerationGate）单测代替，窗口交互需在 Windows 机器人工走查。
- 扫码登录、-ia 可见控制台路径（该路径子进程本就用户可见，不在蓝 P2-3 范围）未在本沙箱实跑。



---

# v3（体验对齐：只解析置灰前置 + UP 主主页行为说明，2026-11-03）

在 v2 源码上修改，未另起项目。环境同 v2（.NET SDK 8.0.404，Linux 沙箱交叉编译）。

## 改动清单（对应用户「EXE 同步修改」要求）

1. **「只解析，不下载」前置 + 勾选置灰**（对齐 bat 生成器 v5 行为）
   - 开关从页面中部挪到下载设置区最顶部（「画质与编码」区块之上），加粗显示；
   - 勾选后画质、编码、内容勾选（音频/封面/字幕）、弹幕、多线程、选集、保存位置、间隔、断点跳过全部禁用，并显示说明行「已开启只解析：下面的下载设置全部不生效」；取消勾选立即恢复；
   - 置灰规则抽为 Core 层纯函数 `UiMutexPolicy.Compute(infoOnly, contentLocked, audioOnly)`：只解析优先级最高（全禁），其次 contentLocked，再次 audioOnly；`MainWindow.ApplyMutex` 只负责把结果落到控件 IsEnabled，规则本体可在 Linux 单测；
   - 命令生成保持现状（-info），只改交互。
2. **UP 主主页行为说明**（BBDown 1.6.3 只导出投稿清单 txt 后打红字退出）
   - 识别卡片说明（加粗醒目）改为如实描述：只导出『UP昵称的投稿视频.txt』清单、红字「暂不支持该功能」是固定提示不是出错、批量下载可用 bat下载命令生成器.html；
   - 二次确认弹窗文案同步改为同一事实口径（强制确认门槛保留）；
   - 运行结束（隐藏子进程与 -ia 可见控制台两条路径都覆盖）：不看退出码，状态显示「导出完成」并在日志指引清单文件位置 + bat 生成器提示，不标「失败/出错」；
   - 日志中检测到 BBDown 输出「暂不支持」时，就地追加一行说明「红字是固定提示，不是出错」。
   - 纯数字撞号候选列表、合集/系列等其他路径未动。

## 测试记录（Linux 实跑，dotnet test）

- 命令：`dotnet test tests/BBDownHelper.Core.Tests -p:UseSharedCompilation=false`
- 结果：**53 通过 / 0 失败 / 0 跳过**（`Passed! - Failed: 0, Passed: 53, Skipped: 0, Total: 53, Duration: 375 ms`）
- 46 个 v2 用例全部保持通过；新增 7 个：
  - 置灰联动 6 个（`UiMutexPolicyTests`）：只解析全禁+说明行、只解析优先级最高（与 contentLocked/audioOnly 全组合）、取消勾选说明行消隐、contentLocked 锁画质编码/多线程/音频、audioOnly 锁画质/弹幕/封面字幕、默认全开；
  - UP 主页文案 1 个（`Classify_UpSpace_MessageExplainsExportOnly_RedTextIsNotError`）：断言 Message 含「导出」「投稿视频.txt」「红字」「不是出错」「bat下载命令生成器.html」，且 RequiresConfirm 仍为 true、不再含 v2 的误导说法「耗时很久」。

## 发布记录

`dotnet publish` 沿用 v2 参数（Release / win-x64 / self-contained / PublishSingleFile / EnableCompressionInSingleFile / IncludeNativeLibrariesForSelfExtract）：
- 产出 `publish_win_single/BBDownHelper.exe` → 交付名 **BBDownHelper-v3.exe**，45,281,665 字节
- `file` 判定：`PE32+ executable (GUI) x86-64, for MS Windows`（单文件自包含，用户机器无需装运行时）

## v3 未验证范围（如实说明）

- GUI 行为（置灰视觉效果、说明行显隐、确认弹窗、日志就地说明、结束后状态文案）在 Linux 沙箱无法启动 Windows 窗口实测；置灰规则本体已抽纯逻辑并有 6 个单测覆盖，窗口侧为代码审查级确认，需在 Windows 机器人工走查。
- BBDown 对 UP 主主页的实际退出码未在本沙箱验证（无 BBDown.exe + 无网络环境）；v3 实现对 UpSpace 任务不看退出码，因此无论 BBDown 以何码退出都不会标失败。
- 扫码登录、-ia 可见控制台路径未实跑（同 v2）。


---

# v3.2（日志中文乱码修复 + 程序图标，2026-11-04）

在 v3.1 源码上修改，未另起项目。环境：Linux 沙箱（2 核），.NET SDK 8.0.404（沙箱重建后经 azureedge 断点续传重装）。

## 问题 1 诊断验证（先验证后修）

- 实机现象：BBDown banner「BBDown version 1.6.3」等 ASCII 行正常，中文行全部变成  —— 与「GBK 字节流被 UTF-8 解码器读出 U+FFFD」形态吻合（反向 UTF-8→GBK 乱码会是「鐧诲綍」错字而非 ）。
- 源码实锤：`MainWindow.axaml.cs` 唯一的重定向子进程（OnStart 隐藏窗口路径，下载与 -info 共用）显式写了 `StandardOutputEncoding = UTF8 / StandardErrorEncoding = UTF8`，与诊断描述的「按 UTF-8 解码」一致（诊断说的「未显式设置」实际形态是「显式设成了 UTF-8」，根因相同）。
- 其余拉起 BBDown.exe 的入口排查：-ia 交互选画质与扫码登录均走 `UseShellExecute` 可见控制台（无输出重定向，由控制台自身处理编码），BiliProbe 走 B 站 HTTP API（非子进程）——均不涉及，无需改动。
- 日志区核实：仅有窗口内只读 LogBox（可复制文本），无「写日志文件」功能；解码修正后复制出的即为正确文本。

## 改动清单

1. **新增 `src/BBDownHelper.Core/ConsoleEncoding.cs`**（纯函数可单测）：`Resolve(int ansiCodePage)` 注册 `CodePagesEncodingProvider` 后按代码页取编码，非法/不支持回落 GB18030（GBK 超集）；`Current` 懒解析 `CultureInfo.CurrentCulture.TextInfo.ANSICodePage`。Core 项目新增 `System.Text.Encoding.CodePages` 8.0.0 包引用（.NET 8 默认不带 GBK 代码页）。
2. **`src/BBDownHelper/MainWindow.axaml.cs`**：重定向子进程 stdout/stderr 编码 `UTF8` → `ConsoleEncoding.Current`。
3. **程序图标**：`Assets/BBDownHelper.ico`（16~256 多尺寸，用户提供的成品 ICO）；csproj 加 `<ApplicationIcon>Assets\BBDownHelper.ico</ApplicationIcon>`（exe 文件图标/任务栏/快捷方式）；`MainWindow.axaml` 的 Window 加 `Icon="avares://BBDownHelper/Assets/BBDownHelper.ico"`（标题栏运行时图标）。
4. 未按「让 BBDown 输出 UTF-8」方向改 BBDown 本体——bat 路线用户直接在 cmd 里跑会反向乱码。

## 测试记录（Linux 实跑，dotnet test）

- 命令：`dotnet test tests/BBDownHelper.Core.Tests -p:UseSharedCompilation=false`
- 修复中第一轮：66 用例 / 1 失败（`Current_ResolvesAndRoundTrips`——断言写错口径：本沙箱西文系统 ANSI=1252/20127 本就无法编码中文，属测试断言超系统能力，非产品缺陷）
- 修正断言为 ASCII 往返（中文往返由 Resolve(936)/Resolve(65001) 用例覆盖）后重跑：**66 通过 / 0 失败 / 0 跳过**（`Passed! - Failed: 0, Passed: 66, Skipped: 0, Total: 66, Duration: 445 ms`）
- 59 个 v3.1 用例全部保持通过；新增 7 个（`ConsoleEncodingTests`）：
  - `Resolve_Gbk936_GbkBytesDecodeToChinese`：GBK 字节样本（登录状态/视频标题/合并提示/下载完成）→ 正确中文，并断言样本确非合法 UTF-8（用例有效性自检）
  - `Resolve_Gbk936_AsciiBannerUnchanged`：ASCII banner 经 GBK 解码不退化
  - `Resolve_InvalidCodePage_FallsBackToGb18030`：0/-1/int.MinValue/999999 → gb18030
  - `Resolve_Gb18030Fallback_StillDecodesGbkBytes`：回落编码是 GBK 超集
  - `Resolve_Utf8CodePage65001_DecodesUtf8Chinese`：UTF-8 系统不误用 GBK
  - `Current_ResolvesAndAsciiRoundTrips`：系统解析不抛异常、ASCII 往返、实例幂等
  - `RegisterProvider_IsIdempotent`：重复注册不抛异常

## 发布记录

`dotnet publish` 沿用 v3.1 参数（Release / win-x64 / self-contained / PublishSingleFile / EnableCompressionInSingleFile / IncludeNativeLibrariesForSelfExtract）：
- 产出 `publish_win_single/BBDownHelper.exe` → 交付名 **BBDownHelper-v3.2.exe**，45,626,694 字节
- `file` 判定：`PE32+ executable (GUI) x86-64, for MS Windows`（单文件自包含）
- PE 资源实检：RT_ICON 7 条 + RT_GROUP_ICON 1 组（与 ICO 的 7 尺寸一致）→ exe 文件图标已内嵌；Avalonia 资源索引含 `Assets/BBDownHelper.ico` → 窗口运行时图标已打包

## v3.2 未验证范围（如实说明）

- 图标在 Windows 任务栏、窗口标题栏、桌面快捷方式的实际渲染效果：沙箱无 Windows GUI，无法实测；已验证 ICO 多尺寸资源已正确嵌入 EXE（RT_ICON×7/RT_GROUP_ICON×1）与 Avalonia 资源包，需用户实机确认观感。
- 乱码修复的端到端效果（真实 BBDown.exe GBK 输出 → 窗口日志显示中文）：沙箱无 Windows + 无 BBDown.exe 运行环境；解码逻辑本体以 GBK 字节样本单测覆盖，系统代码页解析在用户机器上首次运行时生效。



---

# v3.3（core 子文件夹改造：防误双击 BBDown.exe，2026-11-04）

在 v3.2 源码上修改，未另起项目。环境：Linux 沙箱（2 核），.NET SDK 8.0.404（沙箱重建后经 azureedge 重装，本版实装校验 `dotnet --version` = 8.0.404）。

## 背景（用户决策：方案 B）

实机反馈：顶层 BBDown.exe 与 BBDownHelper.exe 并列，BBDown.exe 无图标、名字更像主程序，零基础用户误双击后黑窗一闪以为程序坏了。拍板方案 B：BBDown.exe / aria2c.exe / ffmpeg.exe 收进 `core\` 子文件夹，顶层只留 BBDownHelper.exe、bat下载命令生成器.html、使用说明.txt。打包结构由组长负责。

## BBDown 侧核实结论（isxulong/BBDown-fixed main 分支源码实读，未改 BBDown）

- `Program.cs:32`：`APP_DIR = Path.GetDirectoryName(Environment.ProcessPath)` —— BBDown 把 BBDown.data / BBDownTV.data / BBDown.refresh_token / BBDown.archives 一律读写在**自身 exe 目录**（core\），布局改造后口径天然一致，不受影响。
- `BBDownUtil.cs` `FindExecutable`：aria2c/ffmpeg 查找顺序 = `Environment.CurrentDirectory` → `Program.APP_DIR` → `PATH`。exe 放 core\ 后 APP_DIR 命中，**不受子文件夹影响**（我们侧也未依赖 PATH）。
- `SpaceVideoFetcher.cs:37`：UP 主投稿清单 `{userName}的投稿视频.txt` 用相对路径写在**进程工作目录**——因此 GUI 侧三处 ProcessStartInfo 的 WorkingDirectory 保持顶层（`_baseDir`）不动，清单仍落顶层，与界面指引文案一致。

## 改动清单（文件:行号以 v3.3 源码为准）

1. **新增 `src/BBDownHelper.Core/CoreLayout.cs`**：
   - `CoreLayout.BbdownPath(baseDir)` / `BbdownDataPath(baseDir)` / `CoreDir(baseDir)`——core\ 路径解析唯一口径；
   - `CoreLayout.PlanMigration(topFiles, coreFiles)` 纯函数——迁移计划：顶层存在且 core\ 没有的 `BBDown.data`/`BBDown.refresh_token`/`BBDownTV.data` 才搬，core\ 已有的不覆盖，清单外文件不碰；
   - `CoreDataMigrator.Execute(baseDir)` 执行器——core\ 不存在（残缺安装）时返回 Empty 不动用户数据；单文件迁移失败（占用/权限）记入 Failed/Errors 继续搬下一个，绝不抛出。
2. **`src/BBDownHelper/MainWindow.axaml.cs`**：
   - 构造函数（L59-66）：启动时先执行迁移再刷新登录状态，成功/失败均写日志（失败按未登录态继续，不崩）；
   - L218 `BbdownPath()` → `CoreLayout.BbdownPath(_baseDir)`；
   - L248 / L425 两处报错文案 → 「没找到 core\BBDown.exe。请使用 v1.0.2 完整压缩包解压，不要只拷贝单个 exe；core 文件夹里应包含 BBDown.exe。」；
   - L448 `UpdateLoginStatus` / L454 `ReadCookie` → 读 `core\BBDown.data`（BiliProbe cookie 链路即走 ReadCookie，同步）；
   - L271 / L299 / L435 三处子进程 WorkingDirectory 保持 `_baseDir`（理由见上「BBDown 侧核实」），补注释说明依据。
3. **全解决方案 grep `BBDown.exe` / `_baseDir` / `BBDown.data`**：除上述各点与文档（README/BUILD）外无第二处硬编码路径；日志区展示的命令文本本来就是 `BBDown ...`（文件名未变），无需改。
4. 版本标识：界面 Title 为「BBDown 下载助手」、日志无版本号输出——无升级项，如实说明。
5. README.md 更新使用方法（core\ 布局）并新增 v3.3 变更小节。

## 测试记录（Linux 实跑，dotnet test）

- 命令：`dotnet test tests/BBDownHelper.Core.Tests -p:UseSharedCompilation=false`
- 第一轮：77 用例 / 1 失败（`Execute_FailureRecorded_NotThrown_OtherFilesStillMoved`——**测试自身的失败注入方式无效**：用「顶层同名目录」模拟占用，但 `EnumerateFiles` 不列目录、根本不进迁移计划；产品逻辑无缺陷）。改为「core\BBDown.data 处同名目录」使 File.Move 真实抛 IOException 后重跑。
- 最终结果：**77 通过 / 0 失败 / 0 跳过**（`Passed! - Failed: 0, Passed: 77, Skipped: 0, Total: 77, Duration: 429 ms`）
- 66 个 v3.2 用例全部保持通过；新增 11 个（`CoreLayoutTests`）：
  - 路径解析 2：`BbdownPath_ResolvesIntoCoreSubfolder` / `BbdownDataPath_ResolvesIntoCoreSubfolder`
  - 迁移计划纯函数 5：全量三文件 / core 已有不覆盖只搬其余 / 新装无事可做 / 清单外文件不碰 / 大小写不敏感
  - 执行器 IO 4：临时目录实测「三文件搬入 core、用户文件原样保留」/「core 不存在为 NoOp 且不创建 core」/「core 已有凭据不覆盖顶层旧文件保留」/「单文件失败记录不抛、其余照搬」

## 发布记录

`dotnet publish` 沿用 v3.2 参数（Release / win-x64 / self-contained / PublishSingleFile / EnableCompressionInSingleFile / IncludeNativeLibrariesForSelfExtract）：
- 产出 `publish_win_single/BBDownHelper.exe` → 交付名 **BBDownHelper-v3.3.exe**，45,632,688 字节
- `file` 判定：`PE32+ executable (GUI) x86-64, for MS Windows`（单文件自包含）
- PE 资源实检：RT_ICON ×7 + RT_GROUP_ICON ×1（与 v3.2 相同的 7 尺寸图标组）→ 文件图标保留

## v3.3 未验证范围（如实说明）

- 迁移与 core\ 路径在 Windows 实机的端到端行为（真实从 v1.0.1 目录升级、扫码登录后凭据落 core\BBDown.data、误双击场景消失）：沙箱无 Windows GUI / 无 BBDown.exe，逻辑本体以 11 个单测覆盖（含真实 IO 的临时目录迁移），需组长打包后实机走查。
- BBDown 侧结论基于 isxulong/BBDown-fixed main 分支源码静态阅读，未实际运行验证。


---

# v3.4 —— 输入后自动预览卡（标题+封面）+ v1.0.2 质检 P3 批处理

## 背景

用户拍板：输入网址/BV号后自动联网显示视频标题和封面预览卡。v3.3 的 BiliApiProbe 已具备联网探测地基（UA/Referer/cookie 管线、mock 注入、短链解析、medialist/view 两套探测），本版补 bvid 入口、pic/cover 字段与界面展示；一并并入 v1.0.2 质检记录的 4 个 P3 小待办。

## 功能口径（2026-11-04 实测 API）

- `view?bvid=` / `view?aid=` 均可用；`data.pic` 为封面 URL（注意是 `http://` 前缀，统一规范成 https）、`data.pages` 为数组（分 P 数）、`data.owner.name` 为 UP 主。
- 视频不存在/删除 → `code=-400`（请求错误）；权限不足 → `code=-403`——两者统一走「这个视频可能不存在或已删除」温和提示。
- `medialist/info?type=&biz_id=` 有 `data.cover`（部分合集/系列为空串）+ `data.upper.name` + `data.media_count`，无需再调 acc/info。
- 触发链：输入 → 识别管线（400ms 防抖）→ 识别完成 → 预览取数（500ms 防抖 + CancellationToken 取消旧请求 + 代次校验过期不上屏）。裸数字多候选在用户选定前不出预览卡；UP 主主页输入不出预览卡（沿用现有生成器引导）；网络失败/超时静默不显示；任何异常路径不影响识别→解析→下载主流程。

## 改动清单（文件:行号以 v3.4 源码为准）

1. **`src/BBDownHelper.Core/Models.cs`**：L79 新增 `VideoPreview`（Kind/Title/UpName/CoverUrl/PageCount/MediaCount/NotFound）。
2. **`src/BBDownHelper.Core/BiliProbe.cs`**：L17 导出 `UserAgent`；L31 `GetStringAsync` 增加 CancellationToken 重载（取消即返 null，静默）；L168 `ProbeViewPreviewAsync`（view 预览）；L177 `ViewUrlFor`（BV→`view?bvid=`、av→`view?aid=`，其余形态不探测）；L193 `ParseViewPreview`（code!=0→NotFound；pic http→https；pages 数组计数）；L231 `ProbeMediaListPreviewAsync`（medialist 预览，cover 可空串，失败静默）；L264 `NormalizeCoverUrl`。
3. **`src/BBDownHelper.Core/PreviewService.cs`**（新文件，52 行）：识别结果→预览门面。仅 SingleVideo/Collection/Series 出预览；Collection/Series 从 URL 取 sid/business_id（type 5/8）；其余 Kind 返回 null。
4. **`src/BBDownHelper.Core/DebounceGate.cs`**（新文件，39 行）：500ms 防抖闸 = 版本代次 + 可注入延迟函数 + CancellationTokenSource（Submit 取消旧一轮等待与在途请求）。
5. **`src/BBDownHelper.Core/InputRecognizer.cs`**：L334 `AvFullRegex` 私有→公开（ViewUrlFor 复用，与公开的 BvFullRegex 同形态）。
6. **`src/BBDownHelper.Core/CoreLayout.cs`**（质检 P3-2/P3-3）：L22-27 `LegacyDataFiles` 白名单加入 `BBDown.archives`（对应 BBDown Program.Methods.cs:252/262 的「已下载跳过」记录读写 APP_DIR\BBDown.archives）；L65-66 `Execute(baseDir)` 公开口径改走 L71 可注入 `Execute(baseDir, listFiles)` 内部重载；L99-112 `FileNames` 目录枚举包 try/catch，异常返回空数组（守住「绝不崩」）。
7. **`src/BBDownHelper.Core/BBDownHelper.Core.csproj`**：L15-18 `InternalsVisibleTo` 给测试工程（调内部 Execute 重载）。
8. **`src/BBDownHelper/MainWindow.axaml`**：L17-37 输入框下方预览卡（封面 120×75 占位+Image、类别行、标题两行截断、UP 主行）+ L35-37 NotFound 温和提示行。
9. **`src/BBDownHelper/MainWindow.axaml.cs`**：L23-24 预览服务/防抖闸字段；L96 清空输入撤下预览卡；L104 识别完成后异步取预览；L168 裸数字候选选定后取预览；L178-192 `UpdatePreviewAsync`（Submit→隐藏→500ms→取数→代次校验→上屏，全程吞异常）；L194-200 `HidePreview`；L202-223 `ShowPreview`（合集/系列「合集 · 共 N 个视频」、多 P 显示分 P 行、NotFound 提示行）；L225-240 `LoadCoverAsync`（HttpClient 10s 超时，UA/Referer 同探测口径，失败留占位色块）。
10. **`src/BBDownHelper/BBDownHelper.csproj`**：L14 `<Version>3.4.0</Version>`。
11. **`tools/bat下载命令生成器.html`**（质检 P3-4）：bin 命令段新增 `bt`（bat 专用文本）字段——下载的 .bat 里 BBDown 调用行改为 `"%~dp0core\BBDown.exe"`（勾选 flat 布局时 `"%~dp0BBDown.exe"`），页面展示与复制文本保持 `core\BBDown.exe` / `BBDown` 不变；`buildCommand` 返回值新增 `bat` 字段，`downloadBat` 优先取之（`built.bat || batContent(built.text)` 兜底不变）。
12. **`BUILD.md`**（质检 P3-1）：v3.3 段行号漂移修正——L445→L448、L453→L454、L269/L298/L433→L271/L299/L435（对应上面 v3.3 段内文）。

## 测试记录（Linux 实跑，dotnet test）

- 命令：`dotnet test tests/BBDownHelper.Core.Tests -p:UseSharedCompilation=false`（.NET SDK 8.0.404）
- 结果：**Passed! Failed: 0, Passed: 117, Skipped: 0, Total: 117**——v3.3 的 77 项全绿 + v3.4 新增 40 项全绿（PreviewTests.cs 337 行约 30 项：view URL 形态/解析/NotFound/静默/取消、bvid 与 aid 入口 mock、medialist 预览、PreviewService 各 Kind 门控、DebounceGate 语义；CoreLayoutTests 新增 5 项：archives 白名单计划、顶层枚举异常不崩、core 枚举异常按空处理全搬、archives 实迁、原「清单外文件」测试改用 BBDown.log 让位）。
- GUI 编译门：`dotnet build src/BBDownHelper`（Avalonia 11.2.2 XAML 编译 + MainWindow 预览逻辑）0 Warning 0 Error。
- 生成器门（Node 实跑，沙箱 node 22）：提取页面核心脚本 eval 后跑 14 项断言全过——页面/复制文本保持 `core\BBDown.exe` 不变、下载 .bat 两处 bin 行均为 `"%~dp0core\BBDown.exe"`（flat 对应 `"%~dp0BBDown.exe"`）、up 脚本 `cd /d %~dp0` 保留、`batContent` 兜底接口不变。

## 发布记录

`dotnet publish` 沿用 v3.2/v3.3 参数（Release / win-x64 / self-contained / PublishSingleFile / EnableCompressionInSingleFile / IncludeNativeLibrariesForSelfExtract）：
- 产出 → 交付名 **BBDownHelper-v3.4.exe**，45,640,060 字节
- `file` 判定：`PE32+ executable (GUI) x86-64, for MS Windows`；PE 资源实检：RT_ICON ×7 + RT_GROUP_ICON ×1 + RT_VERSION ×1（与 v3.2/v3.3 相同的 7 尺寸图标组，图标保留）

## v3.4 未验证范围（如实说明）

- 沙箱无 Windows GUI、无真实 BBDown.exe：预览卡视觉效果（封面 16:10 裁切观感、两行截断、配色协调）与 B 站图床真实联网下载封面未实测，逻辑本体以 mock 注入单测覆盖，需组长打包后实机走查。
- view/medialist 接口口径基于 2026-11-04 匿名 curl 实测（bvid 单视频、马督工系列 medialist 的 cover 为空串等），非登录态长链/合集真实形态未逐一实测。
- 生成器 .bat 未在 Windows 实机双击运行（静态断言 + 与三件套 bat 形态一致）。

# v3.5 —— 版本号显示 + 启动检查更新（并入 v1.0.3 发布）

## 背景

用户拍板：①窗口要显示版本号；②加「检查更新+提示」，**不做**自动下载替换自己；③与 v3.4 预览卡合并由组长统一打一个 v1.0.3 发布。本版为纯增量：检查更新任何异常不得影响启动与主流程。

## 改动清单（文件:行号以 v3.5 源码为准）

1. **`src/BBDownHelper.Core/AppVersion.cs`**（新文件，21 行）：发布版本号单一来源。`Release="1.0.3"` / `ReleaseTag="v1.0.3"` / `WindowTitle="BBDown-fixed 下载助手 v1.0.3"` / `CornerLabel`。窗口标题、角落版本小字、检查更新的「当前版本」全部从此取，界面与代码里不再有第二处版本字面量。
2. **`src/BBDownHelper.Core/UpdateCheck.cs`**（新文件，222 行）：
   - `UpdateCheckLogic`（纯函数，可单测）：`TryParseSemVer`（v 前缀可选/最多 3 段缺段补 0/预发布后缀忽略）、`CompareSemVer`（不可解析按 0.0.0）、`ShouldPrompt`（latest 必须可解析且严格大于当前，同版本/更旧/非法一律不提示）、`IsCacheFresh`（24h 间隔；负差值即时钟回拨按新鲜处理）、`ParseReleaseJson`（tag_name+html_url，字段缺失/JSON 非法返回 null）、`SerializeCache`/`DeserializeCache`。
   - `UpdateChecker`（执行器）：`CheckAsync(currentTag, cachePath)` 三步——缓存新鲜则不发请求直接按缓存判定；否则请求 `https://api.github.com/repos/isxulong/BBDown-fixed/releases/latest`（HttpClient 5s 超时、UA 沿用 `BiliApiProbe.UserAgent`、Accept `application/vnd.github+json`），成功后写缓存；网络失败/超时不写缓存（下次启动再试）。`GetStringOverride`/`NowUtc`/`ReadTextOverride`/`WriteTextOverride` 全部可注入（与 BiliApiProbe 同一 mock 模式）。任何异常外层兜底返回 null，绝不抛出。
3. **`src/BBDownHelper.Core/CoreLayout.cs`**：L16 `UpdateCheckCacheName=".update-check.json"` 常量；L37-39 `UpdateCheckCachePath(baseDir)` → `core\.update-check.json`。
4. **`src/BBDownHelper/BBDownHelper.csproj`**：L15 `<Version>3.5.0</Version>`（内部程序集版本，exe 文件属性可见）。
5. **`src/BBDownHelper/MainWindow.axaml`**：L7-9 根布局改 `Grid RowDefinitions="*,Auto"`（ScrollViewer + 底部常驻条）；L12-20 顶部「发现新版本 vX.Y.Z → 点此查看下载」提示条（默认 IsVisible=False）；L173-174 界面右下角常驻版本小字 `VersionCorner`；Window 属性里不再写死 Title。
6. **`src/BBDownHelper/MainWindow.axaml.cs`**：L33-34 `_updateChecker`/`_updateUrl` 字段；L40-41 构造时 `Title=AppVersion.WindowTitle`、`VersionCorner.Text=AppVersion.CornerLabel`；L75-78 启动末尾异步 `_ = CheckForUpdateAsync()`（不 await、不阻塞启动）；L83-98 `CheckForUpdateAsync`（结果非空才在 UI 线程上屏，全程吞异常）；L100-109 `OnUpdateBannerClick`（Process.Start + UseShellExecute 打开发布页，打不开静默）。
7. **`tools/bat下载命令生成器.html`**：L1216 masthead 文案「不上传 · 可离线 · 启动时检查一次更新」（原「不联网」已不准确，如实改）；L175-191 `.update-banner` 样式（默认 display:none）；L1223-1227 提示条 DOM（默认隐藏）；L2759 `init()` 里调用 `checkUpdate()`；L2771-2772 `CURRENT_VERSION="1.0.3"`（页面版本单一来源）与 `LATEST_RELEASE_API`；L2774-2798 `parseVer`/`isNewer`（与 C# 同口径：v 前缀可选/3 段/预发布忽略/解析不了宁可不提示）；L2800-2814 `checkUpdate()`（fetch latest release，isNewer 才显示提示条并填版本号与链接；网络失败/非 2xx/无 fetch 全部静默）；L2825-2827 `parseVer`/`isNewer`/`currentVersion` 挂到 `window.BBDownCore` 供测试复用。零外部依赖不变（无 script src、CSP connect-src * 允许该 fetch）。
8. **`tests/BBDownHelper.Core.Tests/UpdateCheckTests.cs`**（新文件，338 行，61 个用例）：见下方测试记录。

## 测试记录（Linux 实跑，dotnet test）

- 命令：`dotnet test tests/BBDownHelper.Core.Tests -p:UseSharedCompilation=false`（.NET SDK 8.0.404）
- 结果：**Passed! Failed: 0, Passed: 178, Skipped: 0, Total: 178**——v3.4 的 117 项全绿 + v3.5 新增 61 项全绿：
  - 版本单一来源锁定（`AppVersion_SingleSource_Pinned`：Release/ReleaseTag/WindowTitle/CornerLabel 四字段钉死）
  - SemVer 解析 6 合法形态 + 9 非法形态；比较 7 组；ShouldPrompt 6 判定（同版本/更旧/非法 latest/同版本预发布均不提示）
  - 缓存新鲜度 5 判定（0s/23h59m/恰 24h/24h01m/时钟回拨负差值）
  - release JSON 解析 1 全字段 + 8 非法/缺失；缓存序列化往返 + 5 非法
  - `CheckAsync` 决策树 11 场景（mock 注入）：无缓存取数并写缓存、同版本不提示但仍写缓存、网络失败不写缓存、HTTP 异常静默、缓存新鲜零请求、缓存非更新版不提示且零请求、缓存过期重取并刷新时间戳、缓存读失败降级取数、缓存写失败仍返回结论、缓存损坏按缺失处理、真实文件 IO 落盘后二次启动命中缓存零请求
  - `CoreLayout_UpdateCheckCachePath` 路径口径（平台无关断言）
- GUI 编译门：`dotnet build src/BBDownHelper`（Avalonia XAML 编译，Grid 重布局 + 提示条 + 版本小字）0 Warning 0 Error。
- 生成器门（Node 实跑，沙箱 node 22）：32 项断言全过——HTML 静态 7 项（提示条存在/默认无 show/masthead 文案/GitHub 接口/零外部脚本/版本常量/脚本可提取）+ eval 场景 25 项（旧导出未破坏、parseVer 6、isNewer 6、bat 回归 1、有新版本显示且链接正确 3、同版本/网络失败/fetch 缺失/HTTP 非 2xx 四场景均不显示不崩 4 等）。
- **GitHub API 实网验证（沙箱可测，已测）**：`curl https://api.github.com/repos/isxulong/BBDown-fixed/releases/latest` 返回 `tag_name: "v1.0.2"`、`html_url: https://github.com/isxulong/BBDown-fixed/releases/tag/v1.0.2`，限流余量正常；另以临时 console 程序跑 `UpdateChecker` 真实 HTTP 路径（同 UA/Accept/5s 超时代码）：状态 200、耗时约 1.1s、解析出 v1.0.2、`ShouldPrompt("v1.0.3","v1.0.2")=False`（线上比当前旧，不提示——正确）、缓存文件成功落盘且第二次运行命中缓存不再发请求。即「当前无新版本」分支端到端实测通过；「有新版本」分支以 mock 单测覆盖（等组长发 v1.0.3 之后的下一个 release 后会自然走真实分支）。

## 发布记录

`dotnet publish` 沿用 v3.2/v3.3/v3.4 参数（Release / win-x64 / self-contained / PublishSingleFile / EnableCompressionInSingleFile / IncludeNativeLibrariesForSelfExtract）：
- 产出 → 交付名 **BBDownHelper-v3.5.exe**，45,648,653 字节（43.53 MB）
- `file` 判定：`PE32+ executable (GUI) x86-64, for MS Windows`；PE 资源实检：UTF-16 `3.5.0` 版本资源存在（6 处）、窗口标题常量「BBDown-fixed 下载助手 v1.0.3」存在、图标资源保留；bundle 清单含 BBDownHelper.Core.dll / BBDownHelper.dll / System.Text.Json.dll（检查更新代码随 Core 打进单文件；托管 DLL 经压缩打包，明文字符串不可直接 grep，属预期）

## v3.5 未验证范围（如实说明）

- 沙箱无 Windows GUI：窗口标题/右下角版本小字/顶部提示条的视觉效果与 Avalonia Grid 重布局的实机渲染未实测，XAML 已过编译门，需组长打包后实机走查；`Process.Start + UseShellExecute` 打开发布页未实机验证（Windows 标准用法）。
- GitHub API 实测的是「无新版本」分支（线上最新 v1.0.2 < 当前 v1.0.3）；「有新版本 → 显示提示条 → 点击打开发布页」全链路以 mock 单测覆盖，待下一个真实 release 发布后自然验证。
- 生成器页面在真实浏览器中的 fetch 跨域行为未实测（GitHub API 官方支持 CORS，CSP connect-src * 亦放行；Node eval 已覆盖渲染/静默逻辑）。
- bat 三件套、BBDown 本体、使用说明 txt 均未动（任务边界）。



# v3.6 —— 红蓝对抗两项 P2 发版前修复（封面竞态 + 412 风控静默拦截）

在 v3.5 源码上修改，未另起项目。环境：Linux 沙箱（2 核），.NET SDK 8.0.404（沙箱重建后经 azureedge 断点续传重装，本版实装校验 `dotnet --version` = 8.0.404）。对外发布号保持 1.0.3 不变（AppVersion.Release 未动），内部程序集版本升为 3.6.0。

## 修复 1（红队 P2-A）：封面竞态

- 攻击场景：快速连续输入两个 BV 号，A 的封面响应慢于 B 的元数据到达 → UI 呈现「B 的标题 + A 的封面」错配卡。原缺陷：LoadCoverAsync 游离在 DebounceGate 代次机制外，`CoverImage.Source = bmp` 无条件赋值（v3.5 的 MainWindow L264-285）。
- 修法（与预览元数据同一代次源 DebounceGate）：
  - `MainWindow.axaml.cs` L228 / L242 / L261：代次 gen 从 UpdatePreviewAsync → ShowPreview(pv, gen) → LoadCoverAsync(url, gen) 贯穿传入；
  - `MainWindow.axaml.cs` L269-287 LoadCoverAsync：在途下载改用 `_previewDebounce.Token`（新输入时 Submit 取消旧令牌）；字节到达后 `IsCurrent(gen)` 校验、解码后再校验一次，过期封面一律不上屏；
  - 新增 `src/BBDownHelper.Core/CoverLoader.cs`（49 行）：封面 HTTP 下载移入 Core（取消令牌贯穿 HTTP 层），UI 侧只解码上屏；失败/取消返回 null 保持占位色块（静默契约不变）。

## 修复 2（蓝队 P2-B）：预览请求被 B 站 412 风控静默拦截

- 现象（蓝队实测）：带浏览器 UA 但缺 Accept / Accept-Language / sec-ch-ua / sec-fetch-* 时，api.bilibili.com/x/web-interface/view 返回 412，GetStringAsync 判非成功返回 null → 预览卡静默不出。
- 修法（`src/BBDownHelper.Core/BiliProbe.cs`）：
  - L81-95 `ApplyBrowserHeaders`（internal，两档）：首试补齐 Chrome 全家桶头（UA + Accept + Accept-Language + sec-ch-ua/-mobile/-platform + sec-fetch-dest/mode/site + Referer）；降级重试档只留 Referer、不带 UA（蓝队实测同 URL 无 UA 可 200）；
  - L41-49 `GetStringAsync`：首试全头 → 412/403 则降级重试一次（无 UA）；其余失败（超时/404/网络异常）不重试；维持「静默失败返回 null」契约，无弹窗无阻塞；
  - L29 新增 `SendOverride` 注入（internal，测试用）；L51-72 `TryGetAsync` 单次 GET，412/403 返回 Blocked 允许上层降级重试；
  - medialist 端点走同一 GetStringAsync 管线，同逻辑自动覆盖（测试钉住）。

### 412 场景沙箱 curl header 矩阵复现（2026-11-04 实网实测）

| # | 请求形态 | 结果 |
|---|---|---|
| 1 | 无 UA 无任何头 | 200 |
| 2 | 只有 UA + Referer（蓝队 412 形态） | **412**（复现） |
| 3 | v3.6 首试：浏览器全家桶头 | **412** |
| 4 | v3.6 降级重试：无 UA 留 Referer | **200** |
| 5 | 全家桶头 + Edge UA | 412 |
| 6 | 412 响应体 | HTML 风控挑战页 |
| 7 | medialist 端点 + UA | 200（未受风控） |

结论：本沙箱（数据中心 IP）上浏览器 UA 一律 412、无 UA 一律 200——首试补头救不回，**真正起作用的是无 UA 降级重试**（矩阵 #4）。居民宽带 IP 上首试可能直接 200（沙箱无法验证）。两档设计保证两种 IP 环境下预览卡都能出：首试头最接近真实浏览器（居民 IP 友好），被拦则降级重试兜底。

### 真实 HTTP 路径端到端实测（沙箱实跑）

临时 console 程序引用 Core 项目跑 `ProbeViewPreviewAsync("BV1GJ411x7h7")` 真实网络路径（生产代码无 mock）：结果返回完整预览（title=【官方 MV】Never Gonna Give You Up - Rick Astley / up=索尼音乐中国 / cover=https://i1.hdslb.com/...jpg），耗时 371ms——即「首试 412 → 降级重试 200 → 解析成功」全链路在生产代码上实测通过。

## 改动清单（文件:行号以 v3.6 源码为准）

1. `src/BBDownHelper.Core/BiliProbe.cs`：L29 SendOverride；L41-49 GetStringAsync 重试流；L51-72 TryGetAsync；L81-95 ApplyBrowserHeaders。
2. `src/BBDownHelper.Core/CoverLoader.cs`（新文件，49 行）：封面下载器（GetBytesOverride/SendOverride 可注入）。
3. `src/BBDownHelper/MainWindow.axaml.cs`：L25 _coverLoader 字段；L228/L242/L261 代次贯穿；L269-287 LoadCoverAsync（Token + 双重 IsCurrent 校验）。
4. `src/BBDownHelper/BBDownHelper.csproj`：L16 `<Version>3.6.0</Version>`（AppVersion.Release 保持 "1.0.3"）。
5. `tests/BBDownHelper.Core.Tests/RiskControlAndRaceTests.cs`（新文件，15 个用例）。
6. 生成器 html 未改（见「未验证范围」下方说明）。

## 测试记录（Linux 实跑，dotnet test）

- 命令：`dotnet test tests/BBDownHelper.Core.Tests -p:UseSharedCompilation=false`（.NET SDK 8.0.404）
- 修复中第一轮：193 用例 / 1 失败（`MedialistPreview_412_SameRetryPolicy`——**测试自身夹具口径错**：给 medialist 端点塞了 view 形态 JSON（无 media_count 字段），ProbeMediaListPreviewAsync 按判据正确返回 null；产品逻辑无缺陷）。补 MediaListJson 夹具后重跑。
- 最终结果：**Passed! Failed: 0, Passed: 193, Skipped: 0, Total: 193, Duration: 427 ms**——v3.5 的 178 项全绿 + v3.6 新增 15 项全绿：
  - 蓝队 P2（9 项）：首试浏览器全家桶头逐项断言（UA/Accept/Accept-Language/sec-ch-ua×3/sec-fetch×3/Referer）、412→降级重试一次且第二档无 UA、403 同策略、412×2 静默 null 且无第三次请求、404 不重试、网络异常不重试、cookie 两档都透传、预取消零请求、medialist 同管线同策略。
  - 红队 P2-A（6 项）：慢响应场景（A 封面晚于 B 元数据到达）IsCurrent(genA)=false 必须拦、HTTP 层取消在途返回 null、预取消零请求、非成功 null、成功返回字节且 UA/Referer 口径与探测一致、空 URL 零请求。
- GUI 编译门：`dotnet build src/BBDownHelper -p:UseSharedCompilation=false` **0 Warning 0 Error**。

## 发布记录

`dotnet publish` 沿用 v3.2~v3.5 参数（Release / win-x64 / self-contained / PublishSingleFile / EnableCompressionInSingleFile / IncludeNativeLibrariesForSelfExtract）：
- 产出 → 交付名 **BBDownHelper-v3.6.exe**，45,646,425 字节
- `file` 判定：`PE32+ executable (GUI) x86-64, for MS Windows`；版本资源实检：UTF-16 `3.6.0` 存在（程序集版本）、` v1.0.3` 窗口标题常量存在（对外发布号未变）

## 生成器 html 未改的原因

`tools/bat下载命令生成器.html` 只调用 GitHub releases API 做启动检查更新（浏览器原生 fetch + 页面自身的 UA），不调用任何 B 站 api 接口，不存在 412 风控场景；封面竞态修复只涉及 GUI 的预览卡链路（WebView 无关）。生成器的 CURRENT_VERSION="1.0.3" 与本版对外发布号一致，无需改动。

## v3.6 未验证范围（如实说明）

- 沙箱无 Windows GUI：封面错配卡在实机上的消除效果（快速连续输入两个 BV 号的实机复现）、占位色块/取消后的视觉表现未实测；代次校验逻辑本体（DebounceGate.IsCurrent + CoverLoader 取消语义）以单测覆盖，窗口侧为代码审查级确认，需实机走查。
- 412 矩阵基于沙箱数据中心 IP：居民宽带 IP 下首试全家桶头能否直接 200 未验证（两档设计已保证被拦时降级重试兜底）；B 站风控策略随 IP/时间变动，若两端点未来对无 UA 请求也 412，预览卡将退回「静默不出卡」契约（主流程不受影响）。
- 封面图床（i0/i1.hdslb.com）在 v3.6 未改请求口径（UA+Referer 同 v3.4，实测 200），未对图床做 412 矩阵（蓝队报告仅涉 view 端点）。
- dotnet test / build / publish 均在 Linux 侧验证；EXE 未在 Windows 实机运行。



## v3.8（v1.0.4 批次 2）变更与验证记录

范围：地基三件套（LogStore / AppConfig / ErrorMapper + CrashHandler）先行，其上落地
P2-1 启动失败假死修复与 R2.1~R2.11（R2.9 已由 LogStore/Export 天然覆盖，无单独改动项）。

- 测试门：`dotnet test tests/BBDownHelper.Core.Tests` **342/342 通过**
  （268 基线 + 74 新增：LogStore 8 / AppConfig 7 / ErrorMapper 14 / CompletionPolicy 10 /
  StorageGuard 9 / InstallPathGuard 5 / LogDisplayPolicy 6 / LoginReceiptPolicy+DiagnosticExport+CrashHandler 10 /
  SingleInstanceGuard 5）。
- 编译门：`dotnet build src/BBDownHelper -p:UseSharedCompilation=false` **0 Warning 0 Error**。
- 发布：`dotnet publish` 参数沿用（Release / win-x64 / self-contained / PublishSingleFile /
  EnableCompressionInSingleFile / IncludeNativeLibrariesForSelfExtract）；
  产出 **BBDownHelper-v3.8.exe**，45,678,229 字节；内部程序集版本 3.8.0；对外发布号仍为 v1.0.3
  （AppVersion.Release 不动，发版批统一改）。

### v3.8 高风险自检（R2.1 / R2.2 状态机）

- 取消确认时序：CancelBtn → ConfirmDialog（与关窗同一组件同一标准）→ 确认后才置 `_cancelling=true`
  并 Kill；确认框打开期间进程若自然结束，确认返回后先复检 `_proc is null` 直接返回，不误伤新任务。
  取消判定全链路只用显式 bool `_cancelling`，不再比较 StatusText 字符串；Exited 里读完即复位。
- -ia 与正常下载互斥：两条路径共用同一个 `_proc` 槽位与同一个 `OnProcExited` 出口；
  `_proc` 非空期间 StartButtonPolicy 一律禁用开始钮（-ia 也禁用），不可能同时存在两个任务进程；
  Exited 先挂事件再 Start，杜绝秒退丢完成态。
- 关窗拦截路径：`OnClosing` 判定 `_proc is not null`（-ia 进程同样登记 `_proc`，因此 -ia 运行中
  关窗同样弹确认并 Kill），确认后 `_forceClosing=true` 使 Exited 不再碰 UI；登录进程走独立
  `_loginProc` 槽位，不触发关窗拦截（与 v3.7 口径一致）。

### v3.8 未验证范围（如实说明）

- 沙箱无 Windows GUI：黄条/失败醒目条/完成操作行/日志高亮的实机视觉、MessageBeep 提示音、
  单实例激活已有窗口的实机表现、-ia 可见控制台的 Kill 行为，均未实机走查；状态机判定逻辑本体
  以 CompletionPolicy 等纯函数单测覆盖，UI 侧为代码审查级确认。
- 崩溃钩子（CrashHandler）的进程级触发路径未在实机制造崩溃验证（写文件逻辑 WriteCrashFile 有单测）。
- 登录回执按 core\BBDown.data 存在性判定：BBDown 若未来改凭据文件位置/名称需同步调整。
- dotnet test / build / publish 均在 Linux 侧交叉验证；EXE 未在 Windows 实机运行。



## v3.9（红队复检批次：P2-1 / P2-2 修复）变更与验证记录

范围：仅修红队审查（v3.8 批次 2 报告）命中的 2 项 P2，不夹带其他改动；P3-1～P3-4 记待办不在本批。
基线 v3.8 = 342 测试；对外发布号 AppVersion.Release 仍 1.0.3 不动，内部程序集版本升至 3.9.0。

- P2-1 求助导出脱敏加宽（DiagnosticExport.cs SecretRx / Scrub）：命中敏感键名
  （SESSDATA|bili_jct|DedeUserID|refresh_token|access_key|cookie，忽略大小写）后其后的值一律打码，
  覆盖三类分隔形态——= / : 键值（键后可带 JSON 引号，如 "SESSDATA":"..."）、tab 分隔、≥2 空格分隔；
  引号包裹的带空格值整体打码并保留引号（"SESSDATA":"***"）。同步把导出文案「不含 Cookie 值」
  的绝对化宣称改为按实际覆盖能力如实描述（DiagnosticExport.BuildContent 说明行 +
  MainWindow 导出回执）。
- P2-2 LogStore 落盘竞态修复（LogStore.cs WriteFileNoThrow）：写路径整体纳入 _gate 串行化
  （合帧 Timer 线程与 UI 线程 Flush 并发安全，StreamWriter 不再被并发写）；落盘失败改退避重试——
  本次放弃、下次合帧自动重开 writer，写成功清零计数；连续失败 3 次
  （MaxConsecutiveFileFailures）才停用落盘，并在内存环形缓冲留一行「落盘已停用」标记，
  任何失败路径下内存缓冲与上屏保持完整可用。
- 测试门：`dotnet test tests/BBDownHelper.Core.Tests` **350/350 通过**（342 基线 + 8 新增：
  Scrub JSON 形态 / JSON 键带引号冒号后空格 / tab 分隔 / 多空白分隔 / 引号包裹带空格值 5 条 +
  落盘连续失败停用后环形缓冲完整且留标记 / 瞬时失败退避恢复 / 并发 Flush+Append 行不丢不重 3 条）。
- 编译门：`dotnet build src/BBDownHelper -c Release` **0 Warning 0 Error**。
- 发布：`dotnet publish` 参数沿用（Release / win-x64 / self-contained / PublishSingleFile /
  EnableCompressionInSingleFile / IncludeNativeLibrariesForSelfExtract）；
  产出 **BBDownHelper-v3.9.exe**，45,678,698 字节；内部程序集版本 3.9.0；对外发布号仍为 v1.0.3。

### v3.9 未验证范围（如实说明）

- dotnet test / build / publish 均在 Linux 侧交叉验证；EXE 未在 Windows 实机运行。
- 打码按「命中键名后的值」规则工作，无法保证覆盖一切书写形态（如键名被变形拼写）；
  导出文案已如实注明，发出前仍需用户自行过目。
- P3-1～P3-4（取消弹窗误标、CrashHandler 哨兵、登录回执假阳性、ErrorMapper 宽松子串）
  按红队建议记入待办，本批未动。


## v4.0（v1.0.4 批次 3：界面结构与交互体验 R3.1~R3.13 + 可达性基线）

范围：批次3任务书 R3.1~R3.13 + 可达性基线；批次1/2 已定型机制（进程槽/取消/NetFacade 冷却/
预览竞态 GenerationGate+DebounceGate）只调用不重构；批次2 遗留 P3-1～P3-4 记待办不在本批。
基线 v3.9 = 350 测试；对外发布号 AppVersion.Release 仍 1.0.3 不动，内部程序集版本升至 4.0.0。

- R3.1 固定底部操作条（MainWindow.axaml Grid *,Auto）：开始按钮 + 登录区 + 进度条 + 状态文案
  常驻底栏不随日志滚动；右下角版本角标撤下（版本号只在标题栏 AppVersion.WindowTitle）。
- R3.2 主按钮层级：开始独占主色（#005A9E，对比度 7.10）；下载中原地同位切换为红色
  「取消下载」（#A4262C，7.26），独立取消按钮撤下；OnStartButton 统一路由（点击/Enter/Alt+S）；
  下载中登录/浏览降次级禁用（SetSecondaryEnabled），结束按互斥策略恢复。
- R3.3 批量进度（Core/ProgressTracker.cs 纯逻辑）：锚定 BBDown-fixed Program.cs 日志口径——
  「共计 N 个分P」「开始解析P{i}: ... (n of N)」「下载P{i}完毕」「P{i}: [cid] [title] [dur]」
  「任务完成」；Done 以解析序号推进为准（跳过条目无完毕行）；界面显示
  「已完成 x/N · 正在下载：<标题>」；进度条由 x/N（条目完成度）自驱动（helper 必然重定向
  fork 输出，fork 在重定向下不输出百分比行，PercentRx 兜底仅在可见百分比行时生效）。
- R3.3 补充（重定向下进度条的如实口径，不暗示有百分比）：helper 以隐藏子进程运行 BBDown 时
  `RedirectStandardOutput = true`，而 fork 的 ProgressBar 在 Console.IsOutputRedirected 为真时
  既不启动刷新定时器（ProgressBar.cs L44）、也在 UpdateText 首行直接 return（L115）；
  fork 全树唯一输出进度百分比文本的位置（ProgressBar.cs L96-99）因此永不落地，
  日志里不会出现由进度条输出的百分比行（aria2 的 `403MiB`、`(26%)`、`x/N` 一类文本不属于进度条百分比行，仍可能出现）。据此各场景的实际表现（逐行复刻渲染路径实测）：
  单P 视频（fork 打「共计 1 个分P」，Total=1 不构成批量）与多P 全选的批量模式均符合预期——
  前者全程不确定态、结束置 100%，后者按 x/N 逐条推进到 100%。
  -p 只选一个分P（已修复）：fork 先打过滤前的「共计 12 个分P」（12>1 使 Total 一度为 12、
  IsBatch 为真），随后打过滤后的「(1 of 1)」把 Total 覆盖为 1、IsBatch 转假。
  进度条落值统一由 Core.ProgressBarPolicy.Decide 判定、由 Core.ProgressBarPresenter 落笔：
  非批量一律返回不确定态，且「定值 → 不确定态」这次状态切换本身计为一次必须落地的写入
  （ProgressBarPresenter.Apply 的 Changed=true），界面据此把 IsIndeterminate 写回 true，
  先前的 0% 定值不会残留。界面剩余的状态只有两种：下载期间不确定态动画，结束时由完成态
  出口置 100%（成功）/ 归 0%（失败或取消）。界面侧不再直接读 OverallPercent，也不再自行
  判 IsBatch 分支（MainWindow.RenderLogLines 统一走 Decide + Presenter）。
  该落值判定与写入端均可用例单独驱动（单视频/单个分P 不定态、尚未拿到计数、批量 x/N、
  可见百分比兜底、定值转不定态的写入、-p 单分P 真实日志行端到端，共 14 条，
  tests/BBDownHelper.Core.Tests/FixBatchTests.cs）。注意：ProgressBarPresenter 是纯逻辑，
  用例覆盖的是「界面该写入什么」，Avalonia 控件属性本身的赋值不参与单测。
  本批后测试门 435/435 通过。
- R3.4 拖拽支持：窗口 AllowDrop + DragOver/Drop，放下文本经 InputRecognizer.ExtractToken
  提取链接/BV 号填入输入框并触发识别；下载中不接。
- R3.5 Enter 快捷键：InputBox AcceptsReturn=False，回车走 OnStartButton
  （识别中/文本不一致先触发识别，多候选先选人——OnStart 双保险沿用）。
- R3.6 预览卡分P勾选（Core/PageSelectionPolicy.cs 纯逻辑）：多P/多集预览卡展开勾选行，
  默认全选；勾选 ⇄ 选集文本双向同步（_pageSyncing 递归守卫），部分选压缩连段（1-3,5），
  手写非法/越界输入不动勾选；选择结果经 PageBox 进入 CommandBuilder -p 参数。
- R3.7~R3.9 预览卡类型扩展（BiliProbe + PreviewService）：番剧 pgc view/web/season
  （ss→season_id / ep→ep_id，root result）、课程 pugv view/web/season（root data，
  ep 链接取不到 season 静默 null 走降级）、收藏夹 x/v3/fav/folder/info?media_id=
  （data null 静默）；view 接口补 pages[].part 分P标题。
- R3.10 预览降级 UX：支持预览的类型识别通过即骨架卡「正在获取预览…」；3 秒无结果
  （ShowDegradedAfterDelayAsync）、取数失败（412 风控冷却/断网/其他）换降级提示
  「预览没加载出来，不影响下载，可直接点『开始下载』」，按 InRiskCooldown/LastNetworkFailed
  归因文案；预览区不再静默空白（吸收 v3.7 R1.6 断网提示为归因分支之一）。
- R3.11 AppConfig 扩展（沿用原子写 tmp+move、损坏静默回退）：SaveDir（空串归一 null）+
  Quality/Encoding/Danmaku（白名单回退）+ MultiThread/AudioOnly/CoverOnly/SubOnly/
  DelayPerPage/SaveArchives 全持久化；启动 RestoreOptionsFromConfig 恢复（_restoringConfig
  守卫恢复期间不回写）；InfoBox 不持久（会话级模式，注释文档化）；保存目录失效回退程序目录
  并记日志；首启（无 SaveDir 或失效）亮引导条「用『下载』文件夹 / 自己浏览选择」。
- R3.12 登录态三态（Core/LoginStatePolicy.cs 纯逻辑）：未登录(灰 #606060)/已登录(绿 #107C10)/
  登录失效(橙 #B54708)，颜色点+文案+登录按钮文案（失效→「重新登录」）；
  归并=凭据文件 × 探测结果；启动后台 nav 接口轻量探测（不阻塞、失败静默保持二态）；
  登录回执（扫码进程退出凭据存在→已登录）与下载失败信号（ErrorMapper.IsLoginRelated
  命中 403/需要登录/未登录/凭据/cookie/付费 → 标记失效）联动刷新。
- R3.13 UP 主链路（Core/GeneratorLink.cs 纯逻辑）：UP 导出清单成功 →
  「打开清单所在文件夹」「打开批量生成器」入口；生成器经 file URI + #up=<url-encoded>
  预填本次 UP 主链接；生成器缺失/打不开浏览器有明确兜底文案。
- 可达性基线（架构 §13）：主操作区文字对比度 ≥4.5:1（Python 实测：#005A9E 7.10 / #A4262C 7.26 /
  #107C10 5.37 / #B54708 5.43 / #606060 6.29 / #8F5A00 5.78 替换 #CC8800 2.96 /
  #C92A2A 5.46 替换 #E05555 3.75 / #00567A 8.04 替换 #3385AD 4.12 / #B3261E 6.54）；
  提示文字 ≥12px；按钮最小点击高度 ≥32px（主按钮 40）；主控件 AutomationProperties.Name；
  键盘 Alt+S 开始/取消、Alt+L 登录、Alt+B 浏览 + Tab 走通 + 回车开始。
- 测试门：`dotnet test tests/BBDownHelper.Core.Tests` **435/435 通过**（429 + 6 条本批新增）
  = 421（= 350 基线 + 59 + 修复批 12）+ 8 条进度条落值决策补修用例（单视频不定态 / 尚未拿到计数
  不定态 / -p 单选一个分P 不定态 / 批量 x/N 两档 / Done 超 Total 封顶 100 / 可见百分比单条 /
  批量取较大防回跳 / -p 单分P 先落 0% 定值的现状刻画）+ 6 条本批界面落值端用例
  （批量转单条的序列返不定态 / 单视频初始不重复写 / 批量推进与去重 / 定值转不定态后再回定值必重写 /
  -p 单分P 真实 fork 日志行端到端 / 多P 全选端到端不退化）。
  先红后绿：上一批 8 条在纯函数 API 缺失时 8 处 CS0103（旧口径下 3 条断言红）；本批 6 条在
  ProgressBarPresenter 缺失时 4 处 CS0246，按上一批旧口径（只按 Percent 落定值、IsBatch 转假后不再写）
  实现时 4 条断言红，改为「非批量 ⇒ 不确定态、状态切换必写」后 6/6 通过。
  全部用例见 tests/BBDownHelper.Core.Tests/FixBatchTests.cs。
- 编译门：`dotnet build src/BBDownHelper -c Debug` **0 Warning 0 Error**。

### v4.0 未验证范围（如实说明）

- 沙箱无 Windows GUI：界面类（固定底栏视觉、两态按钮配色、拖拽手感、回车/Alt 快捷键、
  分P勾选行交互、骨架卡/降级提示视觉、三态颜色点、首启引导条）以代码级 + 纯逻辑测试验收，
  未实机目视验证。
- 预览卡番剧/课程/收藏夹接口返回结构以样本 JSON（2026-11-05 实网抓取）锚定；
  课程 ep 链接（/cheese/play/epN）pugv season 接口取不到，静默走降级提示（不阻塞下载）。
- 生成器 html 改动（#up= 预填 / 04 拿走命令 / 折叠默认态 / 程序位置二选一卡片）以 Node
  断言验证脚本逻辑，未在浏览器实机点开验证视觉效果。
- P3 待办沿用：批次2 遗留 P3-1～P3-4 不在本批。
