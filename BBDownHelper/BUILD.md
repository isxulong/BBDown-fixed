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
