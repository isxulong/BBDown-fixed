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
    private static int _handled;   // 防重入（崩溃处理里再崩只写第一次）

    /// <summary>日志尾部提供器（MainWindow 启动后接上 LogStore.Recent；未接则只写异常本身）。</summary>
    public static Func<IReadOnlyList<string>>? TailProvider { get; set; }

    /// <summary>用户可见通知（尽力弹窗；由 UI 层接线，可为 null）。</summary>
    public static Action<string>? Notifier { get; set; }

    /// <summary>注册 AppDomain + TaskScheduler 两级钩子。重复注册安全（幂等）。</summary>
    public static void Register(string logDir)
    {
        _logDir = logDir;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            OnCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            OnCrash(e.Exception);
            e.SetObserved();   // 已留现场，不再 escalate 成进程崩溃
        };
    }

    private static void OnCrash(Exception? ex)
    {
        if (Interlocked.Exchange(ref _handled, 1) != 0) return;   // 只处理第一次
        string? path = null;
        try { path = WriteCrashFile(_logDir, ex, TailProvider?.Invoke()); } catch { }
        try
        {
            Notifier?.Invoke(path is not null
                ? $"程序出错了，日志已存到：{path}。请把它发给维护者。"
                : "程序出错了，日志保存在 core\\logs 文件夹，请把它发给维护者。");
        }
        catch { /* 崩溃期弹窗失败就算了 */ }
    }

    /// <summary>
    /// 写崩溃现场文件（纯 IO，可单测）：异常全文 + 日志尾部。
    /// 返回文件完整路径；任何失败返回 null，绝不抛。
    /// </summary>
    public static string? WriteCrashFile(string logDir, Exception? ex, IReadOnlyList<string>? tail)
    {
        try
        {
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            using var w = new StreamWriter(path);
            w.WriteLine($"BBDownHelper 崩溃现场 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            w.WriteLine($"版本：{AppVersion.ReleaseTag}（内部 {typeof(CrashHandler).Assembly.GetName().Version}）");
            w.WriteLine($"系统：{Environment.OSVersion}");
            w.WriteLine("——— 异常 ———");
            w.WriteLine(ex?.ToString() ?? "（无异常对象）");
            if (tail is { Count: > 0 })
            {
                w.WriteLine("——— 崩溃前日志尾部 ———");
                foreach (var line in tail) w.WriteLine(line);
            }
            return path;
        }
        catch { return null; }
    }
}
