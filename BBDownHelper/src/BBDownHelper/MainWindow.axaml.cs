using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BBDownHelper.Core;

namespace BBDownHelper;

public partial class MainWindow : Window
{
    private readonly string _baseDir = AppContext.BaseDirectory;
    // v3.7（架构 §1）NetFacade 薄层：四个联网组件统一注入 NetFacade.Shared——
    // 共享 HttpClient + 统一 UA/超时 + 412 全局冷却（任一组件收 412 → 60s 内各组件降级）
    private readonly NetFacade _net = NetFacade.Shared;
    private readonly BiliApiProbe _probe;   // v4.0（R3.12）：登录态探测与识别/预览共用实例（同一冷却）
    private readonly RecognitionService _recognizer;
    private readonly GenerationGate _recogGate = new();   // 红蓝共识 P2：识别竞态代际闸
    private readonly PreviewService _previewService;   // v3.4：预览卡取数
    private readonly DebounceGate _previewDebounce = new();   // v3.4：预览防抖闸（500ms + 旧请求取消）
    private readonly ProgressTracker _progress = new();   // v4.0（R3.3）：批量/多P 总体进度
    private readonly CoverLoader _coverLoader;   // v3.6 红队 P2-A：封面下载（代次取消贯穿 HTTP 层）
    private readonly UpdateChecker _updateChecker;   // v3.5：检查更新
    // v3.8（地基三件套）：日志走 LogStore（环形缓冲+合帧上屏+滚动落盘），配置走 AppConfig
    private readonly LogStore _log;
    private readonly AppConfig _config;
    private readonly string _configPath;
    private RecognitionResult? _current;
    private Process? _proc;
    private Process? _loginProc;    // v3.8（R2.6）：登录进行中防重入
    private CancellationTokenSource? _debounce;
    private readonly ProgressBarPresenter _presenter = new();   // v1.0.4 v2：进度条落值端（Decide → 界面写入）
    private bool _pageInvalid;      // 红 P2-2：选集非法时禁用开始按钮
    private bool _infoAutoChecked;  // 红 P3-1：-info 是否由我们自动勾上（换输入时只复位自动勾的）
    private bool _infoSettingProgrammatically;  // v3.1 P3：区分程序化设置与用户手动改动 InfoBox
    private bool _forceClosing;     // 蓝 P2-3：确认关窗后跳过 Exited 的 UI 更新
    private string? _updateUrl;     // v3.5：新版本发布页地址（提示条按钮用）
    private bool _recognizing;       // v3.7 真 bug ②（R1.2）：输入变化→识别完成前的窗口期标志
    private string? _recognizedText; // v3.7 真 bug ②（R1.2）：最近一次完成识别时的输入文本（开始前比对）
    private DateTime _procStartedAt = DateTime.MinValue;   // v3.7 真 bug ①：UP 清单验证的运行时间窗下限
    private bool _cancelling;        // v3.8（R2.1）：显式取消标志，替代 StatusText 字符串比较
    private bool _procIsInteractive; // v3.8（R2.2）：当前 _proc 是否为 -ia 可见控制台进程
    private bool? _loginProbeResult; // v4.0（R3.12）：登录态探测结果（null=未探测/探测失败保持二态）
    private bool _pageSyncing;       // v4.0（R3.6）：分P勾选行 ⇄ 选集框双向同步的递归守卫
    private bool _restoringConfig;   // v4.0（R3.11）：启动恢复配置期间不回写（防无谓原子写）
    private string? _listPath;       // v4.0（R3.13）：UP 导出清单路径（「打开清单所在文件夹」用）
    private string? _lastUpUrl;      // v4.0（R3.13）：最近一次 UP 主主页链接（生成器预填用）

    private static readonly Regex PercentRx = new(@"(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        // v3.7（架构 §1）：识别/预览共用同一探测实例（同一 facade → 同一冷却），封面与更新各自注入
        _probe = new BiliApiProbe(_net);
        _recognizer = new RecognitionService(_probe);
        _previewService = new PreviewService(_probe);
        _coverLoader = new CoverLoader(_net);
        _updateChecker = new UpdateChecker(_net);
        // v3.8（地基）：日志通道（内存环形缓冲 + core\logs\helper-日期.log 滚动落盘 + 80ms 合帧上屏）
        _log = new LogStore(CoreLayout.LogDir(_baseDir));
        _log.LinesAdded += batch =>
        {
            try { Dispatcher.UIThread.Post(() => RenderLogLines(batch)); } catch { /* 窗口已关 */ }
        };
        // v3.8（地基）：界面配置，损坏回退默认、原子写、失败静默
        // v4.0（R3.11）：扩展到保存目录 + 全部下载勾选项持久化，重启恢复
        _configPath = CoreLayout.ConfigPath(_baseDir);
        _config = AppConfig.Load(_configPath);
        VerboseLogBox.IsChecked = _config.VerboseLog;
        RestoreOptionsFromConfig();   // v4.0（R3.11）：恢复保存目录与全部勾选项（含首启引导条）
        // v3.8（QC#3）：崩溃兜底接上日志尾部与尽力通知
        CrashHandler.TailProvider = () => _log.Recent(80);
        CrashHandler.Notifier = msg =>
        {
            try { Dispatcher.UIThread.Post(() => { AppendLog("——— " + msg); StatusText.Text = msg; }); }
            catch { /* 崩溃期通知失败就算了 */ }
        };
        // v3.5：窗口标题从 AppVersion 取（版本号单一来源，XAML 不写死）。
        // v4.0（R3.1）：右下角版本角标撤下，版本号只在标题栏。
        Title = AppVersion.WindowTitle;
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) DebouncedRecognize();
        };
        PageBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                ValidatePageInput();
                SyncPageChecksFromText();   // v4.0（R3.6）：手写选集 → 同步预览卡勾选
            }
        };
        BrowseBtn.Click += OnBrowse;
        // v4.0（R3.2）：主按钮两态路由——待机=开始，下载中=红色「取消下载」（原地切换）
        StartBtn.Click += OnStartButton;
        LoginBtn.Click += OnLogin;
        CandidateOkBtn.Click += OnCandidateOk;
        RetryBtn.Click += OnRetryRecognize;   // v3.7（R1.4）：识别失败态的「重新识别」
        // v4.0（R3.5）：输入框内回车 = 识别/开始（多候选时先选人，OnStart 内有双保险）
        InputBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == Avalonia.Input.Key.Enter)
            {
                ke.Handled = true;
                OnStartButton(StartBtn, new RoutedEventArgs());
            }
        };
        // v4.0（R3.4）：拖拽链接（b23/BV/URL 文本）进窗口即填入并触发识别
        Avalonia.Input.DragDrop.SetAllowDrop(this, true);
        AddHandler(Avalonia.Input.DragDrop.DragOverEvent, OnDragOverLink);
        AddHandler(Avalonia.Input.DragDrop.DropEvent, OnDropLink);
        // v4.0（R3.13）：UP 导出清单后的闭环入口
        OpenListDirBtn.Click += OnOpenListDir;
        OpenGeneratorBtn.Click += OnOpenGenerator;
        // v4.0（R3.6）：预览卡分P勾选行的「全选」
        PageSelectAllBtn.Click += OnPageSelectAll;
        // v4.0（R3.11）：首启引导条——一键用「下载」文件夹 / 浏览选择
        UseDownloadsBtn.Click += OnUseDownloads;
        FirstRunBrowseBtn.Click += OnBrowse;
        // v3.8（R2.5/R2.7/R2.8）：完成闭环操作 + 日志工具条
        OpenDirBtn.Click += OnOpenDir;
        AgainBtn.Click += OnAgain;
        CopyLogBtn.Click += OnCopyLog;
        ExportHelpBtn.Click += OnExportHelp;
        VerboseLogBox.IsCheckedChanged += (_, _) =>
        {
            if (_restoringConfig) return;
            _config.VerboseLog = VerboseLogBox.IsChecked == true;
            SaveConfig();
            RerenderAllLogs();   // 简洁/详细切换：重渲整个缓冲区（折叠不销毁信息）
        };
        // v4.0（R3.11）：全部下载勾选项变化即持久化（InfoBox 不持久——会话级模式）
        MtBox.IsCheckedChanged += (_, _) => PersistOptions();
        QAuto.IsCheckedChanged += (_, _) => PersistOptions();
        QIa.IsCheckedChanged += (_, _) => PersistOptions();
        Q1080.IsCheckedChanged += (_, _) => PersistOptions();
        Q4k.IsCheckedChanged += (_, _) => PersistOptions();
        Q480.IsCheckedChanged += (_, _) => PersistOptions();
        EAuto.IsCheckedChanged += (_, _) => PersistOptions();
        EHevc.IsCheckedChanged += (_, _) => PersistOptions();
        EAvc.IsCheckedChanged += (_, _) => PersistOptions();
        DNone.IsCheckedChanged += (_, _) => PersistOptions();
        DDd.IsCheckedChanged += (_, _) => PersistOptions();
        DOnly.IsCheckedChanged += (_, _) => PersistOptions();
        AudioOnlyBox.IsCheckedChanged += (_, _) => PersistOptions();
        CoverOnlyBox.IsCheckedChanged += (_, _) => PersistOptions();
        SubOnlyBox.IsCheckedChanged += (_, _) => PersistOptions();
        DelayBox.IsCheckedChanged += (_, _) => PersistOptions();
        SaveArchivesBox.IsCheckedChanged += (_, _) => PersistOptions();
        // v3.1 P3：用户手动改动 InfoBox 时复位 _infoAutoChecked，
        // 修掉「自动勾上 → 用户手动取消再勾回 → 换输入误清用户手勾」的微泄漏
        InfoBox.IsCheckedChanged += (_, _) =>
        {
            _infoAutoChecked = InfoFlagPolicy.AfterToggle(_infoSettingProgrammatically, _infoAutoChecked);
            ApplyMutex();
        };
        AudioOnlyBox.IsCheckedChanged += (_, _) => ApplyMutex();
        CoverOnlyBox.IsCheckedChanged += (_, _) => ApplyMutex();
        SubOnlyBox.IsCheckedChanged += (_, _) => ApplyMutex();
        DOnly.IsCheckedChanged += (_, _) => ApplyMutex();
        // v3.3：老用户从 v1.0.0/v1.0.1 升级时，BBDown.data 等还留在顶层——先搬进 core\，
        // 再刷新登录状态。迁移失败（占用/权限）不崩：写日志后按未登录态继续。
        var migration = CoreDataMigrator.Execute(_baseDir);
        foreach (var name in migration.Moved)
            AppendLog("已把老版本留下的 " + name + " 移入 core 文件夹。");
        foreach (var err in migration.Errors)
            AppendLog("老文件移入 core 失败，已跳过（" + err + "）。登录状态可能显示为未登录。");
        UpdateLoginStatus();
        // v3.8（R2.11 架构#1）：安放路径自检——Temp/同步盘特征目录给一次性黄条提醒
        var installWarn = InstallPathGuard.Check(_baseDir);
        if (installWarn is not null)
        {
            WarnBannerText.Text = installWarn;
            WarnBanner.IsVisible = true;
        }
        // v3.7（R1.2）：初始无识别结果 → 开始钮禁用（文案+悬浮原因），状态从此统一走 StartButtonPolicy
        ApplyStartButton();
        // v3.5：启动后异步检查一次更新（24h 缓存在 core\.update-check.json）。
        // 纯增量：任何失败（网络/超时/被墙/缓存读写失败）一律静默，不显示、不阻塞启动。
        UpdateBannerBtn.Click += OnUpdateBannerClick;
        _ = CheckForUpdateAsync();
        // v4.0（R3.12）：启动后台轻量探测凭据有效性（不阻塞启动；失败静默保持二态）
        _ = ProbeLoginStateAsync();
    }

    private void SaveConfig()
    {
        try { _config.Save(_configPath); } catch { /* 配置写失败不影响使用 */ }
    }

    // ---------- v4.0（R3.11）：配置恢复与持久化 ----------

    /// <summary>
    /// 启动时把 config 里的保存目录 + 全部勾选项恢复到控件（一次性，恢复期间不回写）。
    /// 保存目录失效（被删/移动）→ 回退程序所在文件夹并记日志；config 没有 SaveDir → 首启引导条。
    /// InfoBox（只解析）不恢复——会话级模式，重启回到实际下载。
    /// </summary>
    private void RestoreOptionsFromConfig()
    {
        _restoringConfig = true;
        try
        {
            bool dirValid = !string.IsNullOrEmpty(_config.SaveDir) && Directory.Exists(_config.SaveDir);
            if (!string.IsNullOrEmpty(_config.SaveDir) && !dirValid)
                AppendLog("上次设置的保存文件夹（" + _config.SaveDir + "）已不存在，已回退为程序所在文件夹。");
            DirText.Text = dirValid
                ? _config.SaveDir!
                : _baseDir.TrimEnd(Path.DirectorySeparatorChar);
            FirstRunDirBanner.IsVisible = !dirValid;   // 首启（或目录失效）引导选保存位置

            QAuto.IsChecked = _config.Quality == "auto";
            QIa.IsChecked = _config.Quality == "ia";
            Q1080.IsChecked = _config.Quality == "q1080";
            Q4k.IsChecked = _config.Quality == "q4k";
            Q480.IsChecked = _config.Quality == "q480";
            EAuto.IsChecked = _config.Encoding == "auto";
            EHevc.IsChecked = _config.Encoding == "hevc";
            EAvc.IsChecked = _config.Encoding == "avc";
            DNone.IsChecked = _config.Danmaku == "none";
            DDd.IsChecked = _config.Danmaku == "dd";
            DOnly.IsChecked = _config.Danmaku == "only";
            MtBox.IsChecked = _config.MultiThread;
            AudioOnlyBox.IsChecked = _config.AudioOnly;
            CoverOnlyBox.IsChecked = _config.CoverOnly;
            SubOnlyBox.IsChecked = _config.SubOnly;
            DelayBox.IsChecked = _config.DelayPerPage;
            SaveArchivesBox.IsChecked = _config.SaveArchives;
        }
        finally { _restoringConfig = false; }
        // 蓝 P2-② 修复：构造器恢复配置先于事件挂接，程序化勾选不触发 IsCheckedChanged，
        // 重启后互斥置灰从未执行——恢复完立即补调一次 ApplyMutex 落盘到控件。
        ApplyMutex();
    }

    /// <summary>勾选项变化即写回 config 并原子落盘（恢复期间与互斥程序化改动都不触发）。</summary>
    private void PersistOptions()
    {
        if (_restoringConfig) return;
        var o = CollectOptions();
        _config.Quality = o.Quality;
        _config.Encoding = o.Encoding;
        _config.Danmaku = o.Danmaku;
        _config.MultiThread = o.MultiThread;
        _config.AudioOnly = o.AudioOnly;
        _config.CoverOnly = o.CoverOnly;
        _config.SubOnly = o.SubOnly;
        _config.DelayPerPage = o.DelayPerPage;
        _config.SaveArchives = o.SaveArchives;
        SaveConfig();
    }

    // ---------- v4.0（R3.2/R3.5）：主按钮两态路由与快捷键 ----------

    /// <summary>主按钮唯一入口：下载中=取消（红色），其余=开始。鼠标点击、Enter、Alt+S 都走这里。</summary>
    private void OnStartButton(object? sender, RoutedEventArgs e)
    {
        if (_proc is not null) OnCancelClick(sender, e);
        else OnStart(sender, e);
    }

    /// <summary>
    /// v4.0（R3.2）：下载中其余操作降次级禁用（登录/浏览），结束后按互斥策略恢复。
    /// enabled=false 时直接禁用；enabled=true 时走 ApplyMutex 恢复（BrowseBtn 受只解析互斥约束）。
    /// </summary>
    private void SetSecondaryEnabled(bool enabled)
    {
        LoginBtn.IsEnabled = enabled && _loginProc is null;
        if (enabled) ApplyMutex();
        else BrowseBtn.IsEnabled = false;
    }

    /// <summary>键盘可达性：Alt+S 开始/取消、Alt+L 登录、Alt+B 浏览（按钮上有 AutomationProperties 提示）。</summary>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) return;
        switch (e.Key)
        {
            case Avalonia.Input.Key.S:
                if (StartBtn.IsEnabled) { OnStartButton(StartBtn, new RoutedEventArgs()); e.Handled = true; }
                break;
            case Avalonia.Input.Key.L:
                if (LoginBtn.IsEnabled) { OnLogin(LoginBtn, new RoutedEventArgs()); e.Handled = true; }
                break;
            case Avalonia.Input.Key.B:
                if (BrowseBtn.IsEnabled) { OnBrowse(BrowseBtn, new RoutedEventArgs()); e.Handled = true; }
                break;
        }
    }

    // ---------- v4.0（R3.4）：拖拽链接进窗口 ----------

    private void OnDragOverLink(object? sender, Avalonia.Input.DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(Avalonia.Input.DataFormats.Text)
            ? Avalonia.Input.DragDropEffects.Copy
            : Avalonia.Input.DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>放下文本 → 提取链接/BV号填进输入框（经 DebouncedRecognize 自动触发识别）。下载中不接。</summary>
    private void OnDropLink(object? sender, Avalonia.Input.DragEventArgs e)
    {
        e.Handled = true;
        if (_proc is not null) { StatusText.Text = "下载进行中，先完成或取消当前任务再拖新链接。"; return; }
        var text = e.Data.GetText();
        if (string.IsNullOrWhiteSpace(text)) return;
        var token = InputRecognizer.ExtractToken(text);   // 整段分享文字里取链接/BV号
        InputBox.Text = token ?? text.Trim();
        InputBox.Focus();
    }

    // ---------- v4.0（R3.11）：首启引导「用『下载』文件夹」 ----------

    /// <summary>
    /// v1.0.6（真机反馈 #1）：这里原先是硬拼 %USERPROFILE%\Downloads，
    /// 用户改过系统「下载」文件夹位置（如搬到 E:\Downloads）时会指向 C 盘那个空目录。
    /// 改为经 <see cref="DownloadsFolderLocator"/> 取系统真实「下载」已知文件夹。
    /// </summary>
    private void OnUseDownloads(object? sender, RoutedEventArgs e)
    {
        try
        {
            var resolution = DownloadsFolderLocator.Resolve();
            var dir = resolution.Path;
            Directory.CreateDirectory(dir);   // 少数系统没有 Downloads 文件夹：顺手建好
            DirText.Text = dir;
            _config.SaveDir = dir;
            SaveConfig();
            FirstRunDirBanner.IsVisible = false;
            AppendLog("保存位置已设为：" + dir);
            // 走了兜底（没读到系统已知文件夹）时说明一句，避免用户以为系统「下载」就在这儿
            if (resolution.From == DownloadsFolderLocator.Source.Fallback)
                AppendLog("（没读到系统的「下载」文件夹位置，已退回用户目录下的 Downloads；"
                          + "如不对请点「自己浏览选择…」手动指定。）");
        }
        catch
        {
            StatusText.Text = "没能定位系统「下载」文件夹，请点「自己浏览选择…」手动选一个。";
        }
    }

    // ---------- v4.0（R3.13）：UP 导出后的闭环入口 ----------

    /// <summary>「打开清单所在文件夹」：系统文件管理器打开清单所在目录（正常=程序根目录）。</summary>
    private void OnOpenListDir(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = _listPath is not null ? Path.GetDirectoryName(_listPath) : null;
            if (string.IsNullOrEmpty(dir)) dir = _baseDir;
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { /* 打不开文件夹就算了 */ }
    }

    /// <summary>「打开批量生成器」：浏览器打开同目录生成器 html，经 #up= 预填本次 UP 主链接。</summary>
    private void OnOpenGenerator(object? sender, RoutedEventArgs e)
    {
        if (!GeneratorLink.Exists(_baseDir))
        {
            StatusText.Text = "没找到 " + GeneratorLink.GeneratorFileName
                + "（应在本程序同一文件夹里）。请使用完整压缩包解压。";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(
                GeneratorLink.BuildUri(_baseDir, _lastUpUrl)) { UseShellExecute = true });
        }
        catch
        {
            StatusText.Text = "打不开浏览器，请手动双击本程序文件夹里的 " + GeneratorLink.GeneratorFileName + "。";
        }
    }

    // ---------- v4.0（R3.6）：预览卡分P勾选行 ----------

    /// <summary>多P/多集预览卡展开勾选行（默认全选；选集框已有合法手写值时按它初始化）。</summary>
    private void BuildPageSelectRow(VideoPreview pv)
    {
        PageSelectPanel.Children.Clear();
        PageSelectRow.IsVisible = false;
        if (pv.PageTitles.Count <= 1) return;
        var checks = PageSelectionPolicy.ToCheckedState(PageBox.Text, pv.PageTitles.Count)
                     ?? Enumerable.Repeat(true, pv.PageTitles.Count).ToArray();
        _pageSyncing = true;
        try
        {
            for (int i = 0; i < pv.PageTitles.Count; i++)
            {
                var cb = new CheckBox
                {
                    Content = $"P{i + 1} {pv.PageTitles[i]}",
                    IsChecked = checks[i],
                    FontSize = 12,
                    MinHeight = 28,
                    Margin = new Avalonia.Thickness(0, 0, 14, 0),
                    MaxWidth = 400,
                };
                cb.IsCheckedChanged += (_, _) => SyncPageTextFromChecks();
                PageSelectPanel.Children.Add(cb);
            }
            PageSelectRow.IsVisible = true;
        }
        finally { _pageSyncing = false; }
    }

    /// <summary>勾选变化 → 回写选集框（全选/全不选=留空；部分选压缩成 1-3,5 这类写法）。</summary>
    private void SyncPageTextFromChecks()
    {
        if (_pageSyncing) return;
        var states = PageSelectPanel.Children.OfType<CheckBox>()
            .Select(c => c.IsChecked == true).ToArray();
        if (states.Length == 0) return;
        _pageSyncing = true;
        try { PageBox.Text = PageSelectionPolicy.ToSelectPageText(states); }
        finally { _pageSyncing = false; }
    }

    /// <summary>手写选集 → 同步勾选行。非法/越界输入不动勾选（红字校验已提示，用户中间态不被打回）。</summary>
    private void SyncPageChecksFromText()
    {
        if (_pageSyncing || !PageSelectRow.IsVisible) return;
        int count = PageSelectPanel.Children.Count;
        var checks = PageSelectionPolicy.ToCheckedState(PageBox.Text, count);
        if (checks is null || checks.Length != count) return;
        _pageSyncing = true;
        try
        {
            for (int i = 0; i < count; i++)
                ((CheckBox)PageSelectPanel.Children[i]).IsChecked = checks[i];
        }
        finally { _pageSyncing = false; }
    }

    private void OnPageSelectAll(object? sender, RoutedEventArgs e)
    {
        _pageSyncing = true;
        try
        {
            foreach (var cb in PageSelectPanel.Children.OfType<CheckBox>())
                cb.IsChecked = true;
        }
        finally { _pageSyncing = false; }
        SyncPageTextFromChecks();
    }

    // ---------- v3.5 检查更新（纯增量：任何异常不得影响启动与主流程） ----------

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var info = await _updateChecker.CheckAsync(
                AppVersion.ReleaseTag, CoreLayout.UpdateCheckCachePath(_baseDir));
            if (info is null) return;   // 无新版本 / 失败静默
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _updateUrl = info.ReleaseUrl;
                UpdateBannerText.Text = $"发现新版本 {info.LatestTag} →";
                UpdateBanner.IsVisible = true;
            });
        }
        catch { /* 检查更新绝不拖垮主流程 */ }
    }

    private void OnUpdateBannerClick(object? sender, RoutedEventArgs e)
    {
        // 点「点此查看下载」→ 打开发布页；打不开（无浏览器等）静默忽略
        if (string.IsNullOrEmpty(_updateUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch { /* 打不开就算了，不报错 */ }
    }

    // ---------- 识别 ----------

    /// <summary>
    /// v3.7（R1.2）：开始按钮的唯一状态出口（识别中 / 下载中 / 选集非法 / 识别失败 / 正常就绪）。
    /// 规则集中在 Core.StartButtonPolicy（纯逻辑可单测），UI 层不再各自为政地改 IsEnabled。
    /// v3.8（P2-1/R2.2）：_proc 同时覆盖正常下载与 -ia 控制台进程；启动失败后必须 _proc=null
    /// 再调本方法，否则开始钮会永久停在「下载中」禁用态。
    /// </summary>
    private void ApplyStartButton()
    {
        var s = StartButtonPolicy.Compute(_recognizing, _proc is not null, _pageInvalid, _current);
        StartBtn.IsEnabled = s.Enabled;
        StartBtn.Content = s.Label;
        Avalonia.Controls.ToolTip.SetTip(StartBtn, s.Hint);
        // v4.0（R3.2）：两态配色——待机主色蓝（#005A9E 对比度 7.10），下载中取消红（#A4262C 7.26）
        StartBtn.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(s.Cancel ? "#A4262C" : "#005A9E"));
        StartBtn.Foreground = Avalonia.Media.Brushes.White;
    }

    private void DebouncedRecognize()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        string text = InputBox.Text ?? "";
        // v3.7 真 bug ②（R1.2）：输入一变即进入「识别中」窗口期——开始钮置灰显示「识别中…」，
        // 配合 OnStart 双保险，杜绝窗口期点开始下载到上一个识别结果（错视频）。
        _recognizing = true;
        ApplyStartButton();
        Task.Run(async () =>
        {
            try { await Task.Delay(400, token); } catch { return; }
            if (token.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() => _ = RecognizeNowAsync(text));
        });
    }

    private async Task RecognizeNowAsync(string text)
    {
        // 红蓝共识 P2：代际号防竞态——识别期间用户又改了输入时，慢返回的旧结果直接丢弃
        int gen = _recogGate.Next();
        CandidateCard.IsVisible = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            ResultCard.IsVisible = false; _current = null;
            _recognizedText = null;
            RetryBtn.IsVisible = false;
            ListInfoNoteText.IsVisible = false;
            _recognizing = false;
            ApplyStartButton();
            _ = UpdatePreviewAsync(null);   // v3.4：清空输入时一并撤下预览卡
            return;
        }
        try
        {
            StatusText.Text = "正在识别…";
            var r = await _recognizer.RecognizeAsync(text, ReadCookie());
            if (!_recogGate.IsCurrent(gen)) return;   // 过期结果不得覆盖新卡片
            _recognizedText = text;
            ShowResult(r);
            StatusText.Text = "就绪";
            _ = UpdatePreviewAsync(r);   // v3.4：识别完成后自动取预览（纯增强，失败静默）
        }
        catch
        {
            // v3.7（R1.3）识别管线外层兜底：任何异常都不得把状态栏永久停在「正在识别…」，
            // 落成明确的失败结果卡（带重新识别按钮），界面可继续操作。
            if (_recogGate.IsCurrent(gen))
            {
                ShowResult(new RecognitionResult
                {
                    Kind = InputKind.Unknown, KindName = "无法识别",
                    Message = "识别失败，请检查链接或网络后重试。"
                });
                StatusText.Text = "识别失败，请检查链接或网络后重试";
            }
        }
        finally
        {
            // v3.7 真 bug ②（R1.2）：识别结束复位窗口期标志。
            // 双重条件：仍是当前代（用户没再改输入）且输入框文本与本次识别文本一致——
            // 只要其一不成立，说明新一轮识别已在路上（DebouncedRecognize 已重新置 true），保持禁用。
            if (_recogGate.IsCurrent(gen) && (InputBox.Text ?? "") == text)
            {
                _recognizing = false;
                ApplyStartButton();
            }
        }
    }

    /// <summary>v3.7（R1.4）：识别失败态的「重新识别」按钮——对当前输入框文本直接重跑识别。</summary>
    private async void OnRetryRecognize(object? sender, RoutedEventArgs e)
    {
        // 立即进入识别中态（按钮置灰反馈），识别对当前输入框文本直接重跑
        _recognizing = true;
        ApplyStartButton();
        await RecognizeNowAsync(InputBox.Text ?? "");
    }

    private void ShowResult(RecognitionResult r)
    {
        _current = r;
        ResultCard.IsVisible = true;
        TypeText.Text = "识别为：" + r.KindName;
        TitleText.IsVisible = !string.IsNullOrEmpty(r.Title);
        TitleText.Text = r.MediaCount is int n ? $"{r.Title}（共 {n} 个视频）" : r.Title ?? "";
        LinkText.Text = string.IsNullOrEmpty(r.FinalInput) ? "" : "用于下载的链接：" + r.FinalInput;
        MsgText.IsVisible = !string.IsNullOrEmpty(r.Message);
        MsgText.Text = r.Message;
        // v3.7（R1.7）：合集/系列标题联网补全失败 → 灰字补一句说明（不影响下载，不改变可用性）
        ListInfoNoteText.IsVisible = r.ListInfoFailed;
        // v3.7（R1.4）：识别失败态（Unknown，含外层兜底/断网归因/半成品短链）→ 出「重新识别」按钮
        RetryBtn.IsVisible = r.Kind == InputKind.Unknown;
        // UP 主主页说明是关键提示（BBDown 真实行为与直觉相反），加粗醒目
        MsgText.FontWeight = r.Kind == InputKind.UpSpace
            ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;
        ListGroup.IsVisible = r.IsListKind;

        if (r.NeedsUserChoice)
        {
            CandidateList.Children.Clear();
            foreach (var c in r.Candidates)
            {
                var rb = new RadioButton { Content = c.Describe(), GroupName = "cand", Tag = c };
                rb.IsCheckedChanged += (_, _) =>
                {
                    if (rb.IsChecked == true) CandidateOkBtn.IsEnabled = true;
                };
                CandidateList.Children.Add(rb);
            }
            CandidateOkBtn.IsEnabled = false;   // 禁止默认选第一个：没点选之前确认按钮不可用
            CandidateCard.IsVisible = true;
        }

        // UP 主主页：强制二次确认 + 默认勾 -info（红队 P1-④）
        // 红 P3-1：换成其他输入时，只复位「我们自动勾上的」-info，用户自己勾的不动
        if (r.RequiresConfirm && InfoBox.IsChecked != true)
        {
            _infoSettingProgrammatically = true;
            InfoBox.IsChecked = true;
            _infoSettingProgrammatically = false;
            _infoAutoChecked = true;
        }
        else if (!r.RequiresConfirm && _infoAutoChecked)
        {
            _infoSettingProgrammatically = true;
            InfoBox.IsChecked = false;
            _infoSettingProgrammatically = false;
            _infoAutoChecked = false;
        }

        // 视频 URL 上带 ?p=N 的，自动填进选集框
        if (!string.IsNullOrEmpty(r.SelectPageSuggestion))
            PageBox.Text = r.SelectPageSuggestion;

        // v3.7（R1.2）：识别结果落卡后按统一策略重算开始按钮（识别失败/多候选等禁用态由此落定）
        ApplyStartButton();
    }

    private async void OnCandidateOk(object? sender, RoutedEventArgs e)
    {
        var chosen = CandidateList.Children.OfType<RadioButton>()
            .FirstOrDefault(rb => rb.IsChecked == true)?.Tag as NumberCandidate;
        if (chosen is null) { StatusText.Text = "请先在上面选一个内容。"; return; }
        CandidateCard.IsVisible = false;
        ShowResult(RecognitionService.FromCandidate(chosen));
        StatusText.Text = "就绪";
        _ = UpdatePreviewAsync(_current);   // v3.4：裸数字候选选定后才出预览卡
        await Task.CompletedTask;
    }

    // ---------- v3.4 预览卡（纯增强：任何异常都不得影响识别→解析→下载主流程） ----------

    /// <summary>
    /// 识别完成后自动取预览。防抖约 500ms；输入再变化时 DebounceGate.Submit 取消旧等待与在途请求，
    /// 过期结果（IsCurrent=false）一律不上屏。
    /// v4.0（R3.10）：支持预览的类型识别通过即骨架卡「正在获取预览…」；3 秒无结果、412 风控、
    /// 断网等失败一律换降级提示「预览没加载出来，不影响下载」——预览区不再静默空白。
    /// </summary>
    private async Task UpdatePreviewAsync(RecognitionResult? r)
    {
        int gen = _previewDebounce.Submit();   // 取消旧一轮（等待 + 在途请求）
        HidePreview();
        if (r is null) return;
        try
        {
            if (!await _previewDebounce.ElapsedAsync(gen)) return;   // 防抖窗口内输入又变了
            bool supported = PreviewService.SupportsPreview(r);
            if (supported)
            {
                PreviewSkeleton.IsVisible = true;   // R3.10：识别通过即骨架卡
                _ = ShowDegradedAfterDelayAsync(gen);   // 3 秒无结果 → 降级提示
            }
            var pv = await _previewService.GetPreviewAsync(r, ReadCookie(), _previewDebounce.Token);
            if (!_previewDebounce.IsCurrent(gen)) return;            // 过期结果不得上屏
            PreviewSkeleton.IsVisible = false;
            ShowPreview(pv, gen);
            // R3.10：取不到预览（412/断网/超时/无权限等）→ 明确降级提示，不再静默空白
            if (supported && pv is null) ShowDegraded();
        }
        catch (OperationCanceledException) { }
        catch { /* 预览是纯增强：吞掉一切异常 */ }
    }

    /// <summary>R3.10：骨架卡挂 3 秒还没出结果 → 撤骨架换降级提示（不影响下载）。</summary>
    private async Task ShowDegradedAfterDelayAsync(int gen)
    {
        try
        {
            await Task.Delay(3000, _previewDebounce.Token);
            if (!_previewDebounce.IsCurrent(gen)) return;
            if (PreviewSkeleton.IsVisible)   // 仍停在骨架态 = 3 秒无结果
            {
                PreviewSkeleton.IsVisible = false;
                ShowDegraded();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>R3.10：预览降级提示，按 412 风控冷却 / 断网 / 其他归因给文案（复用 NetFacade 冷却态）。</summary>
    private void ShowDegraded()
    {
        if (_previewService.InRiskCooldown)
            PreviewDegradedText.Text = "预览暂时被 B 站风控拦住了（约 1 分钟后恢复），不影响下载，可直接点「开始下载」。";
        else if (_previewService.LastNetworkFailed)
            PreviewDegradedText.Text = "预览没加载出来（网络不通），不影响下载，可直接点「开始下载」。";
        else
            PreviewDegradedText.Text = "预览没加载出来，不影响下载，可直接点「开始下载」。";
        PreviewDegradedText.IsVisible = true;
    }

    private void HidePreview()
    {
        PreviewCard.IsVisible = false;
        PreviewNotFoundText.IsVisible = false;
        PreviewSkeleton.IsVisible = false;     // v4.0（R3.10）
        PreviewDegradedText.IsVisible = false; // v4.0（R3.10，吸收 v3.7 R1.6 断网提示）
        PageSelectRow.IsVisible = false;       // v4.0（R3.6）
        PageSelectPanel.Children.Clear();
        CoverImage.Source = null;
        CoverPlaceholder.IsVisible = true;
    }

    private void ShowPreview(VideoPreview? pv, int gen)
    {
        if (pv is null) return;   // 取不到：由调用方走降级提示
        PreviewDegradedText.IsVisible = false;   // 蓝 P2-①：真实卡出现即撤降级文案，防与慢网提示同屏矛盾
        if (pv.NotFound)
        {
            // 接口明确 code != 0：内容可能不存在或已删除——温和提示，不报错不阻塞
            PreviewNotFoundText.IsVisible = true;
            return;
        }
        // v4.0（R3.7-9）：类型扩展——番剧/课程/收藏夹
        PreviewKindText.Text = pv.Kind switch
        {
            InputKind.Collection => $"合集 · 共 {pv.MediaCount} 个视频",
            InputKind.Series => $"系列 · 共 {pv.MediaCount} 个视频",
            InputKind.Bangumi => pv.PageTitles.Count > 1 ? $"番剧 · 共 {pv.PageTitles.Count} 集" : "番剧",
            InputKind.Cheese => pv.PageTitles.Count > 1 ? $"课程 · 共 {pv.PageTitles.Count} 课时" : "课程",
            InputKind.Favorites => $"收藏夹 · 共 {pv.MediaCount} 个视频",
            _ => pv.PageCount > 1 ? $"视频 · 共 {pv.PageCount} 个分P" : "视频"
        };
        PreviewTitleText.Text = pv.Title;
        PreviewUpText.IsVisible = !string.IsNullOrEmpty(pv.UpName);
        PreviewUpText.Text = pv.Kind == InputKind.Favorites
            ? "创建者：" + pv.UpName
            : "UP 主：" + pv.UpName;
        PreviewCard.IsVisible = true;
        BuildPageSelectRow(pv);   // v4.0（R3.6）：多P/多集展开勾选行
        _ = LoadCoverAsync(pv.CoverUrl, gen);
    }

    /// <summary>
    /// 封面下载：B 站图床 https 直链 → Bitmap；失败/无封面显示占位色块。
    /// v3.6 红队 P2-A：与预览元数据同一代次源（DebounceGate）——在途下载随新输入取消（Token），
    /// 慢响应回来先验 IsCurrent(gen) 再上屏，杜绝「B 的标题 + A 的封面」错配卡。
    /// </summary>
    private async Task LoadCoverAsync(string url, int gen)
    {
        CoverImage.Source = null;
        CoverPlaceholder.IsVisible = true;
        if (string.IsNullOrEmpty(url)) return;   // 接口没给封面字段：保持占位色块
        try
        {
            var bytes = await _coverLoader.GetCoverBytesAsync(url, _previewDebounce.Token);
            if (!_previewDebounce.IsCurrent(gen)) return;   // 过期封面不得覆盖新卡片
            if (bytes is null) return;                      // 下载失败/取消：保持占位色块
            using var ms = new MemoryStream(bytes);
            var bmp = new Avalonia.Media.Imaging.Bitmap(ms);   // 解码失败抛异常 → catch → 占位
            if (!_previewDebounce.IsCurrent(gen)) return;   // 解码期间输入又变了：再验一次
            CoverImage.Source = bmp;
            CoverPlaceholder.IsVisible = false;
        }
        catch (OperationCanceledException) { }
        catch { /* 加载失败保持占位色块 */ }
    }

    // ---------- 选集校验（红 P2-2：非法输入实时标红 + 禁用开始，禁止静默丢弃） ----------

    private void ValidatePageInput()
    {
        string t = PageBox.Text?.Trim() ?? "";
        // -info 勾选时选集本就不生效，不拦；其余情况非法即标红 + 禁用开始按钮
        _pageInvalid = t.Length > 0 && InfoBox.IsChecked != true && !CommandBuilder.IsValidSelectPage(t);
        PageErrorText.IsVisible = _pageInvalid;
        ApplyStartButton();   // v3.7（R1.2）：开始按钮状态统一走 StartButtonPolicy
    }

    // ---------- 选项互斥（规则在 Core.UiMutexPolicy，纯逻辑可单测；这里只落控件） ----------

    private void ApplyMutex()
    {
        bool info = InfoBox.IsChecked == true;
        bool contentLocked = CoverOnlyBox.IsChecked == true || SubOnlyBox.IsChecked == true || DOnly.IsChecked == true;
        bool audio = AudioOnlyBox.IsChecked == true;

        // 只解析优先级最高：勾选后下载相关控件全部禁用并亮说明行（对齐生成器 v5）
        var s = UiMutexPolicy.Compute(info, contentLocked, audio);

        var qualityAndEncoding = new Control[] { QAuto, QIa, Q1080, Q4k, Q480, EAuto, EHevc, EAvc };
        foreach (var c in qualityAndEncoding)
            c.IsEnabled = s.QualityEncodingEnabled;

        MtBox.IsEnabled = s.MultiThreadEnabled;
        AudioOnlyBox.IsEnabled = s.AudioOnlyEnabled;
        DNone.IsEnabled = DDd.IsEnabled = DOnly.IsEnabled = s.DanmakuEnabled;
        CoverOnlyBox.IsEnabled = SubOnlyBox.IsEnabled = s.CoverSubEnabled;
        PageBox.IsEnabled = s.SelectPageEnabled;
        DelayBox.IsEnabled = SaveArchivesBox.IsEnabled = s.ListAuxEnabled;
        BrowseBtn.IsEnabled = s.WorkDirEnabled;
        InfoHintText.IsVisible = s.InfoHintVisible;
        ValidatePageInput();   // -info 勾选状态变化会影响选集校验口径
    }

    // ---------- 下载 ----------

    private HelperOptions CollectOptions() => new()
    {
        MultiThread = MtBox.IsChecked == true,
        Quality = QIa.IsChecked == true ? "ia" : Q1080.IsChecked == true ? "q1080"
                : Q4k.IsChecked == true ? "q4k" : Q480.IsChecked == true ? "q480" : "auto",
        Encoding = EHevc.IsChecked == true ? "hevc" : EAvc.IsChecked == true ? "avc" : "auto",
        AudioOnly = AudioOnlyBox.IsChecked == true,
        Danmaku = DDd.IsChecked == true ? "dd" : DOnly.IsChecked == true ? "only" : "none",
        CoverOnly = CoverOnlyBox.IsChecked == true,
        SubOnly = SubOnlyBox.IsChecked == true,
        SelectPage = string.IsNullOrWhiteSpace(PageBox.Text) ? null : PageBox.Text.Trim(),
        WorkDir = DirText.Text,
        DelayPerPage = DelayBox.IsChecked == true,
        SaveArchives = SaveArchivesBox.IsChecked == true,
        InfoOnly = InfoBox.IsChecked == true
    };

    private string BbdownPath() => CoreLayout.BbdownPath(_baseDir);   // v3.3：core\BBDown.exe

    /// <summary>文件写入时间（UTC）；不存在或读取失败返回 null，绝不抛（回执判定用）。</summary>
    private static DateTime? TryGetFileTimeUtc(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.GetLastWriteTimeUtc(path);
        }
        catch { return null; }
    }

    private void ShowFailBanner(string text)
    {
        FailBannerText.Text = text;
        FailBanner.IsVisible = true;
    }

    private void HideFailBanner() => FailBanner.IsVisible = false;

    private async void OnStart(object? sender, RoutedEventArgs e)
    {
        // v3.7 真 bug ②（R1.2）双保险：识别中，或输入框文本与最近完成识别的文本不一致
        // （含从未识别过）→ 先触发识别并返回——杜绝窗口期点开始把上一个识别结果当本次输入下载（错视频）。
        if (_recognizing
            || (InputBox.Text ?? "").Trim() != (_recognizedText ?? "").Trim())
        {
            _ = RecognizeNowAsync(InputBox.Text ?? "");
            return;
        }

        if (_current is null || _current.Kind == InputKind.Unknown || _current.NeedsUserChoice
            || string.IsNullOrEmpty(_current.FinalInput))
        {
            StatusText.Text = "请先粘贴链接并完成识别（有多候选时要先选一个）。";
            return;
        }

        // 红 P2-2：选集非法时禁止开始（按钮已禁用，这里再兜一道，防止键盘/程序化触发）
        if (!string.IsNullOrWhiteSpace(PageBox.Text) && InfoBox.IsChecked != true
            && !CommandBuilder.IsValidSelectPage(PageBox.Text.Trim()))
        {
            StatusText.Text = "选集写法不对：只能填 8、1,2、3-5、ALL 或 LAST，请改正后再开始。";
            return;
        }

        // 蓝 P2-③：分P全不选时文本与全选同为 ""，静默回退=下载全部——有数据后果，必须阻断提示
        if (PageSelectRow.IsVisible && InfoBox.IsChecked != true)
        {
            var states = PageSelectPanel.Children
                .OfType<Avalonia.Controls.CheckBox>()
                .Select(c => c.IsChecked == true).ToArray();
            if (!PageSelectionPolicy.HasAnyChecked(states))
            {
                StatusText.Text = "分P一个都没勾：留空等同下载全部。请至少勾一个分P，或点「全选」。";
                return;
            }
        }

        // UP 主主页：强制二次确认（红队 P1-④）。
        // v3：确认文案如实说明 BBDown 的真实行为——只导出投稿清单 txt，红字是固定提示不是出错
        if (_current.RequiresConfirm)
        {
            bool ok = await ConfirmDialog.Show(this,
                "BBDown 不会直接下载 UP 主的投稿视频：点「确定继续」后，它会把该 UP 主的全部投稿地址导出为『UP昵称的投稿视频.txt』清单文件（保存在本程序所在文件夹），随后显示一段红字『暂不支持该功能』并退出——那段红字是 BBDown 的固定提示，不是出错。确定要开始导出吗？");
            if (!ok) { StatusText.Text = "已取消。"; return; }
        }

        if (!File.Exists(BbdownPath()))
        {
            StatusText.Text = "没找到 core\\BBDown.exe。请使用 v1.0.2 完整压缩包解压，不要只拷贝单个 exe；core 文件夹里应包含 BBDown.exe。";
            return;
        }

        HideFailBanner();
        CompletionActions.IsVisible = false;

        // v3.8（R2.4 高工#6）：4K/1080P + 未登录 → 一次性确认（可勾「本次不再提醒」持久化）
        bool wantsHighQ = Q4k.IsChecked == true || Q1080.IsChecked == true;
        bool loggedIn = File.Exists(CoreLayout.BbdownDataPath(_baseDir));
        if (wantsHighQ && !loggedIn && !_config.DontRemindLogin4K)
        {
            var (goLogin, dontRemind) = await ConfirmDialog.ShowWithOptOut(this,
                "当前未登录，1080P/4K 大概率下不了（B 站要求高清晰度必须登录）。要先扫码登录吗？",
                "先去扫码登录", "仍要下载试试", "本次不再提醒");
            if (dontRemind)
            {
                _config.DontRemindLogin4K = true;
                SaveConfig();
            }
            if (goLogin)
            {
                StatusText.Text = "已转为登录：扫码完成后回来再点「开始下载」。";
                _ = DoLoginAsync();
                return;
            }
        }

        // v3.8（R2.3 QC#5）：开始前预检——保存目录存在且可写（创建即删探针文件）；
        // 剩余空间低于阈值给黄条提醒（不阻塞）；目录不可用走三段式人话并中止。
        var storage = StorageGuard.Check(DirText.Text);
        if (!storage.Ok)
        {
            ShowFailBanner("无法开始下载：" + storage.Error + "。建议：点「浏览…」重新选择一个可用的保存文件夹。");
            StatusText.Text = "无法开始下载：" + storage.Error;
            return;
        }
        if (storage.LowSpace)
        {
            long freeGb = storage.FreeBytes / 1024 / 1024 / 1024;
            WarnBannerText.Text = $"提醒：保存盘剩余空间约 {freeGb}GB（低于建议的 5GB），大视频可能下不完整。";
            WarnBanner.IsVisible = true;
        }

        var cmd = CommandBuilder.Build(_current, CollectOptions());
        AppendLog("> BBDown " + string.Join(" ", cmd.Args.Select(a =>
            a.Contains(' ') || a.Contains('?') || a.Contains('&') ? $"\"{a}\"" : a)));

        // v3.8（R2.2 研发#2）：-ia（交互选画质）与正常下载统一登记 _proc——
        // 可见控制台进程同样获得：开始按钮禁用、取消确认链路、关窗拦截、完成态区分。
        bool interactive = cmd.Args.Contains("-ia");
        ProcessStartInfo psi;
        if (interactive)
        {
            // -ia 需要用户在控制台里输入序号：起可见控制台窗口，不重定向输出
            var sb = new System.Text.StringBuilder();
            foreach (var a in cmd.Args)
            {
                sb.Append(' ');
                sb.Append(QuoteArg(a));
            }
            psi = new ProcessStartInfo
            {
                FileName = BbdownPath(),
                Arguments = sb.ToString(),
                // v3.3：工作目录保持顶层（不指 core\）——UP 主投稿清单等相对路径产物落在顶层，
                // 与界面指引文案一致；aria2c/ffmpeg 由 BBDown 按 工作目录→自身exe目录(core\)→PATH 查找
                WorkingDirectory = _baseDir,
                UseShellExecute = true
            };
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = BbdownPath(),
                // v3.3：工作目录保持顶层，理由同 -ia 路径（UP 清单落顶层；aria2c/ffmpeg 走 BBDown 自身目录查找）
                WorkingDirectory = _baseDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // v3.2：BBDown 按系统 ANSI 代码页写 stdout（中文 Windows=GBK/936），
                // 硬按 UTF-8 解码会把中文全变成 U+FFFD——改用系统 ANSI 代码页（取不到回落 GB18030）
                StandardOutputEncoding = ConsoleEncoding.Current,
                StandardErrorEncoding = ConsoleEncoding.Current
            };
            foreach (var a in cmd.Args) psi.ArgumentList.Add(a);   // ArgumentList 传参，无 shell 注入面
        }

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc = proc;
        _procIsInteractive = interactive;
        // 本轮 P2 修复（红队#9）：_forceClosing 原来只置位、永不复位。
        // 只要有一次「确认关窗 → 关窗流程被打断」，标志就永久为真，
        // 之后每次进程结束都跳过 UI 收尾，界面永远停在「下载中」。
        // 新任务开始时复位，语义即「本次任务尚未进入强制退出」。
        _forceClosing = false;
        // 先挂 Exited 再 Start：杜绝「进程秒退、事件后挂」丢完成态的竞态
        proc.Exited += (_, _) => Dispatcher.UIThread.Post(OnProcExited);
        try
        {
            _procStartedAt = DateTime.Now;   // v3.7 真 bug ①：UP 清单验证的运行时间窗下限
            proc.Start();
        }
        catch (Exception ex)
        {
            // v3.8（P2-1 蓝队必修）：Process.Start() 抛异常时必须把 _proc 清回 null 并重算按钮——
            // 否则 StartButtonPolicy 看到非空 _proc 会把开始按钮永久禁用（假死），
            // 关窗也会反复弹「下载还在进行中」确认（假确认）。
            _proc = null;
            _procIsInteractive = false;
            try { proc.Dispose(); } catch { }
            var mapped = ErrorMapper.MapStartError(ex);
            ShowFailBanner("启动失败：" + mapped.ToText());
            StatusText.Text = "启动失败：" + mapped.What + "。" + mapped.Why + "。";
            AppendLog("——— 启动失败：" + mapped.ToText());
            ApplyStartButton();
            return;
        }

        ApplyStartButton();   // v3.7（R1.2）+ v3.8（R2.2）：进行中切红色「取消下载」（v4.0 R3.2 原地切换）
        SetSecondaryEnabled(false);   // v4.0（R3.2）：下载中其余操作降次级禁用
        InputBox.IsEnabled = false;
        DownloadProgress.IsIndeterminate = true;
        _progress.Reset();            // v4.0（R3.3）：新一轮下载复位批量进度
        BatchProgressText.IsVisible = false;
        BatchProgressText.Text = "";
        _presenter.Reset();   // v1.0.4 v2：新一轮回到不确定态、清空上次定值
        StatusText.Text = interactive
            ? "已打开 BBDown 控制台窗口，请在那个黑窗口里按提示输入画质序号；下载进度也在该窗口显示。"
            : "下载中…";

        if (!interactive)
        {
            _ = Task.Run(() => ReadPipeAsync(proc.StandardOutput));
            _ = Task.Run(() => ReadPipeAsync(proc.StandardError));
        }
    }

    /// <summary>
    /// v3.8（R2.1/R2.2/R2.5）：正常下载与 -ia 控制台共用的完成态出口。
    /// 判定全部走 Core.CompletionPolicy.Route（纯函数可单测）：取消 > UP 导出验证 > -ia > 常规。
    /// </summary>
    private void OnProcExited()
    {
        if (_forceClosing) return;   // 蓝 P2-3：确认关窗后不再碰 UI
        _log.Flush();   // 把管道里残余日志冲上屏/落盘，失败归因能拿到完整尾巴
        int exitCode;
        try { exitCode = _proc?.ExitCode ?? -1; } catch { exitCode = -1; }
        bool upSpace = _current?.Kind == InputKind.UpSpace;
        bool interactive = _procIsInteractive;
        bool wasCancelling = _cancelling;
        _proc = null;
        _procIsInteractive = false;
        _cancelling = false;
        InputBox.IsEnabled = true;
        DownloadProgress.IsIndeterminate = false;
        SetSecondaryEnabled(true);   // v4.0（R3.2）：恢复次级操作（按互斥策略）
        BatchProgressText.IsVisible = false;   // v4.0（R3.3）：默认撤下，成功态批量时再亮

        string? listPath = null;
        bool listFound = upSpace && exitCode == 0
            && UpSpaceExportVerifier.TryFindList(_baseDir, _procStartedAt, out listPath);
        string logTail = string.Join("\n", _log.Recent(60));
        // P1 修复：内核合并/解析失败只打红字后 return，退出码仍是 0；
        // 失败信号必须参与路由，否则「一个文件都没产出」会被报成下载完成。
        var outcome = CompletionPolicy.Route(wasCancelling, upSpace, interactive, exitCode, listFound,
            CompletionPolicy.HasFailureSignal(logTail));

        switch (outcome)
        {
            case CompletionOutcome.Cancelled:
                // v3.8（R2.1）：取消后如实告知临时文件残留处理办法
                HideFailBanner();
                DownloadProgress.Value = 0;
                StatusText.Text = "已取消，未完成的临时文件可在保存文件夹删除。";
                AppendLog("——— 已取消，未完成的临时文件可在保存文件夹删除。");
                break;

            case CompletionOutcome.DownloadSuccess:
                // v3.8（R2.5）：成功闭环——明示保存位置 + 打开文件夹 + 再下载一个 + 完成提示音
                HideFailBanner();
                DownloadProgress.Value = 100;
                // v4.0（R3.3）：批量/多P 成功态亮「已完成 N/N」
                // P2-1 修复：分子用真实落定数 Done（-p 子集时 Total 已被 of-count 修正，但
                // 异常退出/早停场景 Done 才是如实口径），不再自报 Total/Total。
                if (_progress.Total > 1)
                {
                    BatchProgressText.Text = $"已完成 {_progress.Done}/{_progress.Total}";
                    BatchProgressText.IsVisible = true;
                }
                StatusText.Text = "已完成，保存到 " + (DirText.Text ?? "");
                AppendLog("——— 下载完成！保存到 " + (DirText.Text ?? ""));
                CompletionActions.IsVisible = true;
                CompletionSound.Play(true);
                break;

            case CompletionOutcome.DownloadFailed:
            {
                // v3.8（R2.5 + 高工#9/QC#6）：ErrorMapper 归因 + 醒目三段式输出
                var mapped = ErrorMapper.MapExit(exitCode, logTail);
                // v4.0（R3.12）：下载失败信号联动登录态——登录相关失败 → 下次刷新标记为失效
                if (ErrorMapper.IsLoginRelated(logTail))
                    _loginProbeResult = false;
                ShowFailBanner($"下载失败。最可能的原因：{mapped.Why}。建议：{mapped.Action}");
                DownloadProgress.Value = 0;
                StatusText.Text = $"下载失败（退出码 {exitCode}）";
                AppendLog($"——— 下载失败（退出码 {exitCode}）。最可能的原因：{mapped.Why}。建议：{mapped.Action}");
                break;
            }

            case CompletionOutcome.UpExportSuccess:
                // v3.7 真 bug ①（R1.1）：退出码 0 且时间窗内真实生成非空清单，才报成功
                HideFailBanner();
                DownloadProgress.Value = 100;
                AppendUpSpaceGuidance();
                AppendLog("已生成投稿清单：" + listPath);
                StatusText.Text = "导出完成：投稿清单已保存为本程序同目录下的『"
                    + Path.GetFileName(listPath!) + "』。";
                // v4.0（R3.13）：闭环入口——打开清单所在文件夹 / 打开批量生成器（预填本次 UP 链接）
                _listPath = listPath;
                _lastUpUrl = _current?.FinalInput;
                UpSpaceActions.IsVisible = true;
                CompletionSound.Play(true);
                break;

            case CompletionOutcome.UpExportFailed:
            {
                // UP 导出沿用 v3.7 的 GuessCause 归因（含「未找到本次清单」语义），醒目条呈现
                string cause = UpSpaceExportVerifier.GuessCause(exitCode, logTail);
                ShowFailBanner("导出失败：" + cause + "。");
                DownloadProgress.Value = 0;
                StatusText.Text = "导出失败：" + cause + "。";
                AppendLog("——— 导出失败（退出码 " + exitCode + "，未找到本次生成的投稿清单）：" + cause);
                if (interactive)
                    AppendLog("如黑窗口里有更多信息，请回看 BBDown 控制台输出。");
                break;
            }

            case CompletionOutcome.IaEndedOk:
                // v3.8（R2.2）：-ia 完成态区分「正常结束 / 被关闭」
                HideFailBanner();
                DownloadProgress.Value = 100;
                StatusText.Text = "BBDown 控制台已正常结束，结果请看它自己的控制台窗口。";
                AppendLog("——— BBDown 控制台已正常结束（退出码 0）。");
                CompletionSound.Play(true);
                break;

            case CompletionOutcome.IaEndedAbnormal:
                DownloadProgress.Value = 0;
                StatusText.Text = $"BBDown 控制台窗口被关闭或异常退出（代码 {exitCode}），未完成的临时文件可在保存文件夹删除。";
                AppendLog($"——— BBDown 控制台窗口被关闭或异常退出（代码 {exitCode}）。");
                break;
        }

        ValidatePageInput();   // 恢复开始按钮（选集仍非法则保持禁用）
        UpdateLoginStatus();
    }

    /// <summary>UP 主主页运行结束后的指引：清单文件位置 + 批量下载走 bat 生成器。</summary>
    private void AppendUpSpaceGuidance()
    {
        AppendLog("———");
        AppendLog("已结束：BBDown 不支持一键下载 UP 主全部投稿，本次运行只是把全部投稿地址导出为清单文件。");
        AppendLog("清单位置：本程序所在文件夹（" + _baseDir.TrimEnd(Path.DirectorySeparatorChar) + "）下，文件名形如『UP昵称的投稿视频.txt』。");
        AppendLog("想批量下载清单里的视频：打开 bat下载命令生成器.html，粘贴同一个 UP 主主页链接，按提示生成两步 bat 后运行即可。");
    }

    // ---------- v3.8（R2.5）完成闭环操作 ----------

    /// <summary>「打开保存文件夹」：系统文件管理器打开下载目录；任何失败静默兜底。</summary>
    private void OnOpenDir(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = DirText.Text;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { /* 打不开文件夹就算了 */ }
    }

    /// <summary>「再下载一个」：复位完成态 UI 并清空输入（触发识别复位），回到初始可下载状态。</summary>
    private void OnAgain(object? sender, RoutedEventArgs e)
    {
        CompletionActions.IsVisible = false;
        UpSpaceActions.IsVisible = false;   // v4.0（R3.13）
        HideFailBanner();
        DownloadProgress.Value = 0;
        InputBox.Text = "";   // 经 DebouncedRecognize 复位识别结果与开始按钮
        StatusText.Text = "就绪";
    }

    // ---------- 蓝 P2-3：下载中关窗必须先确认并杀掉子进程 ----------

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // v3.8（R2.2）：_proc 现已同时覆盖 -ia 可见控制台进程——关窗确认对两条路径同标准
        if (_proc is not null && !_forceClosing)
        {
            e.Cancel = true;   // 先拦下关窗，等用户确认
            bool ok = await ConfirmDialog.Show(this, _procIsInteractive
                ? "BBDown 控制台窗口还在运行，确定要关闭吗？关闭会中断当前任务。"
                : "下载还在进行中，确定要关闭吗？关闭会中断当前下载。");
            if (!ok) return;
            _forceClosing = true;
            try { _proc.Kill(entireProcessTree: true); } catch { }
            Close();           // 再次触发关窗，这次放行
            return;
        }

        // 本轮 P2 修复（红队#8）：原来只拦「下载进程」，登录扫码窗口没被拦——
        // 关掉助手后，那个独立的 BBDown login 控制台还挂在后台等扫码，
        // 用户以为程序已经退出，实际还有个进程在跑；它写出的凭据也没人接管。
        if (_loginProc is not null && !_forceClosing)
        {
            e.Cancel = true;
            bool ok = await ConfirmDialog.Show(this,
                "扫码登录窗口还开着，确定要退出吗？退出会取消本次登录。");
            if (!ok) return;
            _forceClosing = true;
            try { _loginProc.Kill(entireProcessTree: true); } catch { }
            Close();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 退出前把残余日志冲上盘，供崩溃/求助导出取证
        try { _log.Flush(); _log.Dispose(); } catch { }
        base.OnClosed(e);
    }

    /// <summary>
    /// v3.8（地基）：读管线只负责把原始行推进 LogStore——进度解析、红字说明、上屏
    /// 全部移到 LogStore.LinesAdded 的合帧批处理（RenderLogLines），消除每行一次 UI Post。
    /// </summary>
    private async Task ReadPipeAsync(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
            _log.Append(line);
    }

    /// <summary>
    /// v3.8（高工#3）：LogStore 合帧后的批量上屏（80ms 一批）。
    /// 进度解析、UP 红字就地说明、简洁模式折叠、关键行高亮都在这里统一处理。
    /// </summary>
    private void RenderLogLines(IReadOnlyList<string> batch)
    {
        foreach (var l in batch)
        {
            bool fed = _proc is not null && _progress.Feed(l);

            // v1.0.4 v2：百分比行是否真的收到——PercentRx 命中且可解析即视为“有可见百分比行”
            //（重定向下 fork 不输出该行，故正常路径恒为 false）。
            int percentValue = 0;
            bool percentLineVisible = false;
            var pm = PercentRx.Match(l);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, out double pv))
            {
                percentLineVisible = true;
                percentValue = (int)Math.Clamp(pv, 0, 100);
            }

            // v4.0（R3.3）+ v1.0.4 v2：进度落值统一由 Core.ProgressBarPolicy 决定，界面不再自行判分支、
            // 也不再直接读 OverallPercent。非批量 ⇒ 不确定态（含 -p 只选一个分P：IsBatch 由真转假后
            // 必须回到不确定态，不得保留先前落下的 0% 定值）。
            if (fed || percentLineVisible)
            {
                if (_progress.IsBatch)
                {
                    BatchProgressText.Text = _progress.CurrentTitle is { Length: > 0 } t
                        ? $"已完成 {_progress.Done}/{_progress.Total} · 正在下载：{t}"
                        : $"已完成 {_progress.Done}/{_progress.Total}";
                    BatchProgressText.IsVisible = true;
                }
                else
                {
                    // 非批量没有 x/N 可报，撤下批量文案（否则 -p 单分P 会残留“已完成 0/12”）
                    BatchProgressText.IsVisible = false;
                }

                var decision = ProgressBarPolicy.Decide(
                    _progress.IsBatch, _progress.Done, _progress.Total, percentLineVisible, percentValue);
                var paint = _presenter.Apply(decision);
                if (paint.Changed)
                {
                    DownloadProgress.IsIndeterminate = paint.IsIndeterminate;
                    if (!paint.IsIndeterminate) DownloadProgress.Value = paint.Percent;
                }
            }
            // UP 主主页运行时 BBDown 会打红字「暂不支持该功能」：就地说明，避免误以为出错
            if (l.Contains("暂不支持") && _current?.Kind == InputKind.UpSpace)
                _log.Append("（上面这段红字是 BBDown 的固定提示：UP 主主页只会导出投稿清单 txt，不是出错，无需处理）");
            AddLogLineToView(l);
        }
        LogScroll.ScrollToEnd();
    }

    /// <summary>v3.8（R2.7 PM#11/UI#8）：简洁模式只上屏「进度行+关键结果行+我们的提示行」；
    /// 关键行（完成/失败/错误/成功/已取消）加粗高亮；可视行数封顶 600 防内存膨胀。</summary>
    private void AddLogLineToView(string line)
    {
        if (!_config.VerboseLog && !LogDisplayPolicy.IsSimpleVisible(line)) return;
        var tb = new TextBlock { Text = line, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        if (LogDisplayPolicy.IsKeyLine(line))
        {
            tb.FontWeight = Avalonia.Media.FontWeight.SemiBold;
            tb.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1FCC8800"));
        }
        LogList.Items.Add(tb);
        while (LogList.Items.Count > 600) LogList.Items.RemoveAt(0);
    }

    /// <summary>简洁/详细开关切换：按当前模式重渲整个缓冲区（折叠不销毁，LogStore 全量留存）。</summary>
    private void RerenderAllLogs()
    {
        LogList.Items.Clear();
        foreach (var l in _log.Recent(_log.Count))
            AddLogLineToView(l);
        LogScroll.ScrollToEnd();
    }

    /// <summary>
    /// v3.8（R2.7）：复制日志——取 LogStore 全量缓冲（不受简洁模式折叠影响）。
    /// 【本轮 P2 修复】原实现直接把缓冲区原文塞进剪贴板，而剪贴板的用途就是往外粘
    /// （发给维护者/贴到反馈里），凭据值会跟着一起出去。改为统一过 DiagnosticExport.Scrub，
    /// 与「导出求助文件」同一套打码口径；用户仍能在窗口里看到原始行，只是外发的那份被脱敏。
    /// </summary>
    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        try
        {
            var raw = _log.Recent(_log.Count);
            var text = string.Join("\n", raw.Select(DiagnosticExport.Scrub));
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null && text.Length > 0)
            {
                await clipboard.SetTextAsync(text);
                StatusText.Text = "已复制全部日志（凭据值已打码，可放心粘贴外发）。";
            }
            else
            {
                StatusText.Text = "暂无日志可复制。";
            }
        }
        catch { StatusText.Text = "复制失败，可改用「导出求助文件」。"; }
    }

    /// <summary>v3.8（R2.8 PM#5）：导出求助诊断文件到桌面（版本+系统信息+日志尾部，敏感键名的值已按键名打码）。</summary>
    private void OnExportHelp(object? sender, RoutedEventArgs e)
    {
        try
        {
            var desktop = DiagnosticExport.DesktopDir() ?? _baseDir;
            var path = Path.Combine(desktop, $"BBDownHelper-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var content = DiagnosticExport.BuildContent(
                AppVersion.WindowTitle,
                DiagnosticExport.TryGetBbdownVersion(BbdownPath()),
                _log.Recent(300));
            File.WriteAllText(path, content);
            StatusText.Text = "诊断文件已导出：" + path;
            AppendLog("——— 已导出诊断文件：" + path + "（敏感键值已打码，发出前建议再过目一遍）");
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    /// <summary>Windows 命令行引号规则（-ia 可见窗口路径用；参数均经白名单校验）。</summary>
    private static string QuoteArg(string a)
    {
        if (!a.Any(c => char.IsWhiteSpace(c) || c == '\"'))
            return a;
        return "\"" + a.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// v3.8（R2.1 QC#4）：取消先弹确认（与关窗同一标准的 ConfirmDialog）；
    /// 确认后置显式 _cancelling 标志再杀进程——Exited 完成态不再靠 StatusText 字符串比较判定取消。
    /// </summary>
    private async void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_proc is null) return;
        bool ok = await ConfirmDialog.Show(this, _procIsInteractive
            ? "BBDown 控制台窗口还在运行，确定要中断吗？未完成的临时文件可以在保存文件夹里删除。"
            : "下载还在进行中，确定要取消吗？未完成的临时文件可以在保存文件夹里删除。");
        if (!ok || _proc is null) return;   // 确认框打开期间进程可能已自然结束
        // 修复（红队#7）：原来先置 _cancelling=true 再杀进程，Kill 抛异常被吞也照样置位——
        // 如果这期间进程已自然跑完（大文件刚好收尾），完成态会被报成「已取消」，
        // 用户以为没下完、去删临时文件，其实文件已完整。
        // 现在：先杀进程并确认它真的没了，再置取消标志；杀不掉就不置位，保持真实退出码。
        // 关键一步：先确认进程是不是已经自己结束了。若已结束，绝不置取消标志，
        // 让它按真实退出码走完成态路由（否则「刚好下完了」会被谎报成「已取消」）。
        bool alive = true;
        try { alive = !_proc.HasExited; } catch { }
        if (!alive)
        {
            StatusText.Text = "任务已自行结束，按实际结果记录。";
            return;
        }

        // 进程还活着：先置标志再杀（置位必须在 Kill 之前，否则 Exited 回调抢跑会把取消记成正常结束）。
        _cancelling = true;
        StatusText.Text = "正在取消…";
        try { _proc.Kill(entireProcessTree: true); } catch { }

        // 杀完再校验：若仍存活说明取消失败，复位标志，避免后续完成态被谎报为已取消。
        bool stillAlive = false;
        try { stillAlive = !_proc.HasExited; } catch { }
        if (stillAlive)
        {
            _cancelling = false;
            StatusText.Text = "取消失败：BBDown 进程仍在运行，请稍后再试，或在任务管理器里结束它。";
        }
    }

    // ---------- 登录 ----------

    /// <summary>
    /// v3.8（R2.6 研发#5+QC#9）：登录闭环——进行中禁用登录按钮防重入；
    /// 进程退出后按凭据文件存在性给明确回执（成功 / 未完成）。
    /// </summary>
    private async void OnLogin(object? sender, RoutedEventArgs e) => await DoLoginAsync();

    private async Task DoLoginAsync()
    {
        if (_loginProc is not null) return;   // 防重入：扫码窗口已开着
        if (!File.Exists(BbdownPath()))
        {
            StatusText.Text = "没找到 core\\BBDown.exe。请使用 v1.0.2 完整压缩包解压，不要只拷贝单个 exe；core 文件夹里应包含 BBDown.exe。";
            return;
        }
        // 扫码需要人看控制台：用 UseShellExecute 起可见控制台窗口
        var psi = new ProcessStartInfo
        {
            FileName = BbdownPath(),
            Arguments = "login",
            // v3.3：工作目录保持顶层；登录凭据由 BBDown 写进自身 exe 目录(core\)的 BBDown.data，
            // 我们侧的登录状态检测同步读 core\BBDown.data
            WorkingDirectory = _baseDir,
            UseShellExecute = true
        };
        // 本轮 P2 修复：先记下登录前的凭据时间，退出后据此判断「这次」有没有真的写进去，
        // 而不是只看文件在不在（已登录状态重登失败会拿到上一次的旧凭据，误报成功）。
        DateTime? credBefore = TryGetFileTimeUtc(CoreLayout.BbdownDataPath(_baseDir));
        try
        {
            var p = Process.Start(psi);
            if (p is null)
            {
                StatusText.Text = "启动登录失败：系统没有返回进程句柄，请重试。";
                return;
            }
            _loginProc = p;
            LoginBtn.IsEnabled = false;
            StatusText.Text = "已弹出扫码窗口，请用手机 B 站 App 扫码（需要手机已安装 B 站 App）。";
            AppendLog("> BBDown login（扫码窗口已弹出）");
            await p.WaitForExitAsync();
            _loginProc = null;
            // 红 P3-2：恢复登录按钮须看下载态——下载中 SetSecondaryEnabled 已压下它，
            // 这里无条件亮起会导致下载中完成扫码后登录按钮被重新点亮（可再点、再弹窗）。
            LoginBtn.IsEnabled = _proc is null;
            // 登录回执：必须「存在 且 本次被改写」才算成功——
            // 已登录时重登失败会留下上次的旧凭据，只看存在性会误报成功。
            var credPath = CoreLayout.BbdownDataPath(_baseDir);
            DateTime? credAfter = TryGetFileTimeUtc(credPath);
            bool updated = LoginReceiptPolicy.IsRewritten(credBefore, credAfter);
            string receipt = LoginReceiptPolicy.Receipt(File.Exists(credPath), updated);
            bool ok = receipt == LoginReceiptPolicy.Success;
            // v4.0（R3.12）：登录回执联动三态——成功即已登录，失败交给后续探测/失败信号判定
            _loginProbeResult = ok ? true : null;
            UpdateLoginStatus();
            StatusText.Text = receipt;
            AppendLog("——— " + receipt);
        }
        catch (Exception ex)
        {
            _loginProc = null;
            LoginBtn.IsEnabled = _proc is null;   // 红 P3-2：同上，catch 路径也不得在下载中点亮登录按钮
            var mapped = ErrorMapper.MapStartError(ex);
            ShowFailBanner("启动登录失败：" + mapped.ToText());
            StatusText.Text = "启动登录失败：" + mapped.What + "。" + mapped.Why + "。";
            AppendLog("——— 启动登录失败：" + mapped.ToText());
        }
    }

    /// <summary>
    /// v4.0（R3.12）：登录态三态可视化——未登录(灰) / 已登录(绿) / 登录失效(橙)。
    /// 归并规则在 Core.LoginStatePolicy（纯函数可单测）：凭据文件 + 探测结果 + 下载失败信号。
    /// </summary>
    private void UpdateLoginStatus()
    {
        bool hasCred = File.Exists(CoreLayout.BbdownDataPath(_baseDir));   // v3.3：core\BBDown.data
        var state = LoginStatePolicy.Resolve(hasCred, _loginProbeResult);
        string color = LoginStatePolicy.ColorKey(state) switch
        {
            "green" => "#107C10",   // 对比度 5.37
            "orange" => "#B54708",  // 对比度 5.43
            _ => "#606060",         // gray，对比度 6.29
        };
        var brush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color));
        LoginStatus.Text = LoginStatePolicy.Label(state);
        LoginStatus.Foreground = brush;
        LoginStatusDot.Foreground = brush;
        LoginBtn.Content = LoginStatePolicy.ButtonLabel(state);   // 失效态 → 「重新登录」
    }

    /// <summary>
    /// v4.0（R3.12）：启动后台轻量探测凭据有效性（nav 接口一次，经 NetFacade 共享冷却）。
    /// 不阻塞启动；无凭据不发请求；失败（断网/超时/风控）静默保持二态，不误报失效。
    /// </summary>
    private async Task ProbeLoginStateAsync()
    {
        try
        {
            var r = await _probe.ProbeLoginStateAsync(ReadCookie(), CancellationToken.None);
            if (r is null) return;   // 探测失败/无凭据：保持二态
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _loginProbeResult = r;
                UpdateLoginStatus();
                if (r == false)
                    AppendLog("——— 登录凭据可能已失效（B 站返回未登录），需要时点底部「重新登录」。");
            });
        }
        catch { /* 探测是纯增强：吞掉一切异常 */ }
    }

    private string? ReadCookie()
    {
        try
        {
            string path = CoreLayout.BbdownDataPath(_baseDir);   // v3.3：core\BBDown.data
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>v3.8（地基）：所有日志统一走 LogStore（环形缓冲+合帧上屏+滚动落盘），不再直接拼 TextBox。</summary>
    private void AppendLog(string line) => _log.Append(line);

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择下载保存位置",
            AllowMultiple = false
        });
        if (folders.Count > 0)
        {
            var f = folders[0];
            DirText.Text = f.TryGetLocalPath() is string p ? p : f.Path.ToString();
            // v4.0（R3.11）：保存目录持久化 + 撤下首启引导条
            _config.SaveDir = DirText.Text;
            SaveConfig();
            FirstRunDirBanner.IsVisible = false;
        }
    }
}

/// <summary>简单确认弹窗。</summary>
public static class ConfirmDialog
{
    public static async Task<bool> Show(Window owner, string message)
    {
        var (ok, _) = await ShowWithOptOut(owner, message, "确定继续", "取消", null);
        return ok;
    }

    /// <summary>
    /// v3.8（R2.4）：带「免打扰勾选项」的确认弹窗。checkText 为 null 时不显示勾选框。
    /// 返回 (是否点了确定, 免打扰是否勾选)。
    /// </summary>
    public static async Task<(bool Ok, bool OptOut)> ShowWithOptOut(
        Window owner, string message, string okText, string cancelText, string? checkText)
    {
        var tcs = new TaskCompletionSource<bool>();
        var win = new Window
        {
            Title = "请确认",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var ok = new Button { Content = okText, Margin = new Avalonia.Thickness(0, 0, 10, 0), Padding = new Avalonia.Thickness(16, 6) };
        var no = new Button { Content = cancelText, Padding = new Avalonia.Thickness(16, 6) };
        ok.Click += (_, _) => { tcs.TrySetResult(true); win.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); win.Close(); };
        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 16
        };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        CheckBox? optOut = null;
        if (checkText is not null)
        {
            optOut = new CheckBox { Content = checkText, FontSize = 12 };
            panel.Children.Add(optOut);
        }
        panel.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { ok, no } });
        win.Content = panel;
        win.Closed += (_, _) => tcs.TrySetResult(false);
        await win.ShowDialog(owner);
        bool result = await tcs.Task;
        return (result, optOut?.IsChecked == true);
    }
}
