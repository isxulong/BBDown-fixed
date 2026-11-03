using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BBDownHelper.Core;

namespace BBDownHelper;

public partial class MainWindow : Window
{
    private readonly string _baseDir = AppContext.BaseDirectory;
    private readonly RecognitionService _recognizer = new();
    private readonly GenerationGate _recogGate = new();   // 红蓝共识 P2：识别竞态代际闸
    private RecognitionResult? _current;
    private Process? _proc;
    private CancellationTokenSource? _debounce;
    private int _lastPercent = -1;
    private bool _pageInvalid;      // 红 P2-2：选集非法时禁用开始按钮
    private bool _infoAutoChecked;  // 红 P3-1：-info 是否由我们自动勾上（换输入时只复位自动勾的）
    private bool _infoSettingProgrammatically;  // v3.1 P3：区分程序化设置与用户手动改动 InfoBox
    private bool _forceClosing;     // 蓝 P2-3：确认关窗后跳过 Exited 的 UI 更新

    public MainWindow()
    {
        InitializeComponent();
        DirText.Text = _baseDir.TrimEnd(Path.DirectorySeparatorChar);
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) DebouncedRecognize();
        };
        PageBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) ValidatePageInput();
        };
        BrowseBtn.Click += OnBrowse;
        StartBtn.Click += OnStart;
        CancelBtn.Click += (_, _) => KillRunning();
        LoginBtn.Click += OnLogin;
        CandidateOkBtn.Click += OnCandidateOk;
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
        UpdateLoginStatus();
    }

    // ---------- 识别 ----------

    private void DebouncedRecognize()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        string text = InputBox.Text ?? "";
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
        if (string.IsNullOrWhiteSpace(text)) { ResultCard.IsVisible = false; _current = null; return; }
        StatusText.Text = "正在识别…";
        var r = await _recognizer.RecognizeAsync(text, ReadCookie());
        if (!_recogGate.IsCurrent(gen)) return;   // 过期结果不得覆盖新卡片
        ShowResult(r);
        StatusText.Text = "就绪";
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
    }

    private async void OnCandidateOk(object? sender, RoutedEventArgs e)
    {
        var chosen = CandidateList.Children.OfType<RadioButton>()
            .FirstOrDefault(rb => rb.IsChecked == true)?.Tag as NumberCandidate;
        if (chosen is null) { StatusText.Text = "请先在上面选一个内容。"; return; }
        CandidateCard.IsVisible = false;
        ShowResult(RecognitionService.FromCandidate(chosen));
        StatusText.Text = "就绪";
        await Task.CompletedTask;
    }

    // ---------- 选集校验（红 P2-2：非法输入实时标红 + 禁用开始，禁止静默丢弃） ----------

    private void ValidatePageInput()
    {
        string t = PageBox.Text?.Trim() ?? "";
        // -info 勾选时选集本就不生效，不拦；其余情况非法即标红 + 禁用开始按钮
        _pageInvalid = t.Length > 0 && InfoBox.IsChecked != true && !CommandBuilder.IsValidSelectPage(t);
        PageErrorText.IsVisible = _pageInvalid;
        StartBtn.IsEnabled = !_pageInvalid && _proc is null;
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

    private string BbdownPath() => Path.Combine(_baseDir, "BBDown.exe");

    private async void OnStart(object? sender, RoutedEventArgs e)
    {
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
            StatusText.Text = "没找到 BBDown.exe。请把本程序和 BBDown.exe 放在同一个文件夹里再试。";
            return;
        }

        var cmd = CommandBuilder.Build(_current, CollectOptions());
        AppendLog("> BBDown " + string.Join(" ", cmd.Args.Select(a =>
            a.Contains(' ') || a.Contains('?') || a.Contains('&') ? $"\"{a}\"" : a)));

        // -ia（交互选画质）需要用户在控制台里输入序号：起可见控制台窗口，不走重定向
        if (cmd.Args.Contains("-ia"))
        {
            var sb = new System.Text.StringBuilder();
            foreach (var a in cmd.Args)
            {
                sb.Append(' ');
                sb.Append(QuoteArg(a));
            }
            var ppsi = new ProcessStartInfo
            {
                FileName = BbdownPath(),
                Arguments = sb.ToString(),
                WorkingDirectory = _baseDir,
                UseShellExecute = true
            };
            try
            {
                var p2 = Process.Start(ppsi);
                StatusText.Text = "已打开 BBDown 控制台窗口，请在那个黑窗口里按提示输入画质序号；下载进度也在该窗口显示。";
                if (p2 is not null) await p2.WaitForExitAsync();
                if (_current?.Kind == InputKind.UpSpace)
                {
                    // UP 主主页：红字是 BBDown 固定提示，退出码不代表出错——指引清单文件位置
                    AppendUpSpaceGuidance();
                    StatusText.Text = "导出完成：投稿清单已保存为本程序同目录下的『UP昵称的投稿视频.txt』。";
                }
                else
                {
                    StatusText.Text = p2 is not null && p2.ExitCode == 0 ? "完成！（详见 BBDown 控制台窗口）" : "BBDown 已退出，结果请看它自己的控制台窗口。";
                }
                UpdateLoginStatus();
            }
            catch (Exception ex) { StatusText.Text = "启动失败：" + ex.Message; }
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = BbdownPath(),
            WorkingDirectory = _baseDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var a in cmd.Args) psi.ArgumentList.Add(a);   // ArgumentList 传参，无 shell 注入面

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try { _proc.Start(); }
        catch (Exception ex) { StatusText.Text = "启动失败：" + ex.Message; return; }

        StartBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        InputBox.IsEnabled = false;
        DownloadProgress.IsIndeterminate = true;
        StatusText.Text = "下载中…";
        _lastPercent = -1;

        _ = Task.Run(() => ReadPipeAsync(_proc.StandardOutput));
        _ = Task.Run(() => ReadPipeAsync(_proc.StandardError));

        _proc.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_forceClosing) return;   // 蓝 P2-3：确认关窗后不再碰 UI
            bool cancelled = StatusText.Text == "正在取消…";
            int exitCode = _proc?.ExitCode ?? -1;
            bool upSpace = _current?.Kind == InputKind.UpSpace;
            _proc = null;
            CancelBtn.IsEnabled = false;
            InputBox.IsEnabled = true;
            DownloadProgress.IsIndeterminate = false;
            ValidatePageInput();   // 恢复开始按钮（选集仍非法则保持禁用）
            if (cancelled) { StatusText.Text = "已取消。"; DownloadProgress.Value = 0; }
            else if (upSpace)
            {
                // UP 主主页：BBDown 导出清单后打印红字「暂不支持该功能」退出——
                // 红字与退出码都不代表出错，不得标「失败/出错」；指引清单文件位置
                AppendUpSpaceGuidance();
                StatusText.Text = "导出完成：投稿清单已保存为本程序同目录下的『UP昵称的投稿视频.txt』。";
                DownloadProgress.Value = 100;
            }
            else if (exitCode == 0) { StatusText.Text = "完成！"; DownloadProgress.Value = 100; }
            else StatusText.Text = $"BBDown 退出，代码 {exitCode}（详见上方日志）";
        });
    }

    /// <summary>UP 主主页运行结束后的指引：清单文件位置 + 批量下载走 bat 生成器。</summary>
    private void AppendUpSpaceGuidance()
    {
        AppendLog("———");
        AppendLog("已结束：BBDown 不支持一键下载 UP 主全部投稿，本次运行只是把全部投稿地址导出为清单文件。");
        AppendLog("清单位置：本程序所在文件夹（" + _baseDir.TrimEnd(Path.DirectorySeparatorChar) + "）下，文件名形如『UP昵称的投稿视频.txt』。");
        AppendLog("想批量下载清单里的视频：打开 bat下载命令生成器.html，粘贴同一个 UP 主主页链接，按提示生成两步 bat 后运行即可。");
    }

    // ---------- 蓝 P2-3：下载中关窗必须先确认并杀掉子进程 ----------

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_proc is not null && !_forceClosing)
        {
            e.Cancel = true;   // 先拦下关窗，等用户确认
            bool ok = await ConfirmDialog.Show(this, "下载还在进行中，确定要关闭吗？关闭会中断当前下载。");
            if (!ok) return;
            _forceClosing = true;
            try { _proc.Kill(entireProcessTree: true); } catch { }
            Close();           // 再次触发关窗，这次放行
            return;
        }
        base.OnClosing(e);
    }

    private async Task ReadPipeAsync(StreamReader reader)
    {
        var percentRx = new Regex(@"(\d+(?:\.\d+)?)\s*%");
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            string l = line;
            Dispatcher.UIThread.Post(() =>
            {
                AppendLog(l);
                // UP 主主页运行时 BBDown 会打红字「暂不支持该功能」：就地说明，避免误以为出错
                if (l.Contains("暂不支持") && _current?.Kind == InputKind.UpSpace)
                    AppendLog("（上面这段红字是 BBDown 的固定提示：UP 主主页只会导出投稿清单 txt，不是出错，无需处理）");
                var m = percentRx.Match(l);
                if (m.Success && double.TryParse(m.Groups[1].Value, out double p))
                {
                    int ip = (int)Math.Clamp(p, 0, 100);
                    if (ip != _lastPercent)
                    {
                        _lastPercent = ip;
                        DownloadProgress.IsIndeterminate = false;
                        DownloadProgress.Value = ip;
                    }
                }
            });
        }
    }

    /// <summary>Windows 命令行引号规则（-ia 可见窗口路径用；参数均经白名单校验）。</summary>
    private static string QuoteArg(string a)
    {
        if (!a.Any(c => char.IsWhiteSpace(c) || c == '\"'))
            return a;
        return "\"" + a.Replace("\"", "\"\"") + "\"";
    }

    private void KillRunning()
    {
        if (_proc is null) return;
        StatusText.Text = "正在取消…";
        try { _proc.Kill(entireProcessTree: true); } catch { }
    }

    // ---------- 登录 ----------

    private async void OnLogin(object? sender, RoutedEventArgs e)
    {
        if (!File.Exists(BbdownPath()))
        {
            StatusText.Text = "没找到 BBDown.exe。请把本程序和 BBDown.exe 放在同一个文件夹里再试。";
            return;
        }
        // 扫码需要人看控制台：用 UseShellExecute 起可见控制台窗口
        var psi = new ProcessStartInfo
        {
            FileName = BbdownPath(),
            Arguments = "login",
            WorkingDirectory = _baseDir,
            UseShellExecute = true
        };
        try
        {
            var p = Process.Start(psi);
            StatusText.Text = "已弹出扫码窗口，请用手机 B 站 App 扫码。";
            if (p is not null) await p.WaitForExitAsync();
            UpdateLoginStatus();
        }
        catch (Exception ex) { StatusText.Text = "启动登录失败：" + ex.Message; }
    }

    private void UpdateLoginStatus()
    {
        bool logged = File.Exists(Path.Combine(_baseDir, "BBDown.data"));
        LoginStatus.Text = logged ? "已登录" : "未登录";
    }

    private string? ReadCookie()
    {
        try
        {
            string path = Path.Combine(_baseDir, "BBDown.data");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch { return null; }
    }

    private void AppendLog(string line)
    {
        LogBox.Text = (LogBox.Text ?? "") + line + "\n";
        LogScroll.ScrollToEnd();
    }

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
        }
    }
}

/// <summary>简单确认弹窗。</summary>
public static class ConfirmDialog
{
    public static async Task<bool> Show(Window owner, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var win = new Window
        {
            Title = "请确认",
            Width = 420, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var ok = new Button { Content = "确定继续", Margin = new Avalonia.Thickness(0, 0, 10, 0), Padding = new Avalonia.Thickness(16, 6) };
        var no = new Button { Content = "取消", Padding = new Avalonia.Thickness(16, 6) };
        ok.Click += (_, _) => { tcs.TrySetResult(true); win.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); win.Close(); };
        win.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { ok, no } }
            }
        };
        win.Closed += (_, _) => tcs.TrySetResult(false);
        await win.ShowDialog(owner);
        return await tcs.Task;
    }
}
