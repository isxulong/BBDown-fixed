namespace BBDownHelper.Core;

/// <summary>
/// v3.8 崩溃兜底（质检#3 / 需求 R2.8②）：注册全局未处理异常钩子。
/// 崩溃时把异常全文 + 日志尾部写入 core\logs\crash-yyyyMMdd-HHmmss.log，
/// 并尽力通过 Notifier 弹一句「程序出错了，日志已存到 core\logs 文件夹」。
/// 全程 best-effort：任何一步失败都不允许二次崩溃。
/// </summary>
public static class CrashHandler
{
    private static string _logDir = "";
    /// <summary>
    /// 本轮 P2 修复：原先只有一把「已处理」闸门，被两条完全不同性质的事件共用。
    /// 被丢弃的异步任务异常（fire-and-forget）也会走到这里，它通常**不致命**，
    /// 却把闸门用掉——之后真正让进程死掉的崩溃反而不再留现场，用户拿不到任何日志。
    /// 拆成两把闸门：致命崩溃一把、任务异常一把。
    /// </summary>
    private static int _fatalHandled;   // AppDomain 未处理异常（进程即将终止）
    private static int _taskHandled;    // TaskScheduler 未观察异常（进程继续运行）

    /// <summary>日志尾部提供器（MainWindow 启动后接上 LogStore.Recent；未接则只写异常本身）。</summary>
    public static Func<IReadOnlyList<string>>? TailProvider { get; set; }

    /// <summary>用户可见通知（尽力弹窗；由 UI 层接线，可为 null）。</summary>
    public static Action<string>? Notifier { get; set; }

    /// <summary>注册 AppDomain + TaskScheduler 两级钩子。重复注册安全（幂等）。</summary>
    public static void Register(string logDir)
    {
        _logDir = logDir;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            OnCrash(e.ExceptionObject as Exception, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // 非致命：进程还会继续跑。留现场但不抢占「致命崩溃」的闸门，
            // 也不弹「程序出错了」——用户眼前一切正常，弹窗只会造成恐慌。
            OnCrash(e.Exception, fatal: false);
            e.SetObserved();   // 已留现场，不再 escalate 成进程崩溃
        };
    }

    private static void OnCrash(Exception? ex, bool fatal)
    {
        // 两类事件各自只处理第一次；致命崩溃不会因为先前的任务异常而被跳过。
        if (!TryBegin(fatal)) return;
        string? path = null;
        try { path = WriteCrashFile(_logDir, ex, TailProvider?.Invoke(), fatal); } catch { }
        if (!fatal) return;   // 非致命不弹窗：用户界面上一切正常，弹了反而吓人
        try
        {
            Notifier?.Invoke(path is not null
                ? $"程序出错了，日志已存到：{path}。请把它发给维护者。"
                : "程序出错了，日志保存在 core\\logs 文件夹，请把它发给维护者。");
        }
        catch { /* 崩溃期弹窗失败就算了 */ }
    }

    /// <summary>
    /// 领取「本次事件要不要处理」的名额（可单测）。
    /// fatal=true 用致命闸门，false 用任务异常闸门；同一种事件只处理第一次。
    /// 关键性质：先来一个任务异常，不影响之后致命崩溃照样留现场。
    /// </summary>
    public static bool TryBegin(bool fatal)
    {
        if (fatal) return Interlocked.Exchange(ref _fatalHandled, 1) == 0;
        return Interlocked.Exchange(ref _taskHandled, 1) == 0;
    }

    /// <summary>仅测试用：复位两把闸门（生产代码不调用）。</summary>
    public static void ResetGatesForTest()
    {
        Interlocked.Exchange(ref _fatalHandled, 0);
        Interlocked.Exchange(ref _taskHandled, 0);
    }

    /// <summary>
    /// 写崩溃现场文件（纯 IO，可单测）：异常全文 + 日志尾部。
    /// 返回文件完整路径；任何失败返回 null，绝不抛。
    /// </summary>
    public static string? WriteCrashFile(string logDir, Exception? ex, IReadOnlyList<string>? tail, bool fatal = true)
    {
        try
        {
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            using var w = new StreamWriter(path);
            w.WriteLine($"BBDownHelper {(fatal ? "崩溃现场" : "后台任务异常（程序仍在运行）")} "
                        + $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            w.WriteLine($"版本：{AppVersion.ReleaseTag}（内部 {typeof(CrashHandler).Assembly.GetName().Version}）");
            w.WriteLine($"系统：{Environment.OSVersion}");
            w.WriteLine("——— 异常 ———");
            // 本轮 P2 修复：异常 Message 会带完整请求 URL（B 站 412 的 URL 含 ticket），
            // 同崩溃现场要发给维护者，必须与日志尾部同一套打码口径，否则等于绕开脱敏。
            w.WriteLine(ex is null ? "（无异常对象）" : DiagnosticExport.Scrub(ex.ToString()));
            if (tail is { Count: > 0 })
            {
                w.WriteLine("——— 崩溃前日志尾部 ———");
                foreach (var line in tail) w.WriteLine(DiagnosticExport.Scrub(line));   // 本轮 P2：崩溃文件也是外发材料，逐行打码
            }
            return path;
        }
        catch { return null; }
    }
}
