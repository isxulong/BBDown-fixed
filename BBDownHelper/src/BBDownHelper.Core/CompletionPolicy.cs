namespace BBDownHelper.Core;

/// <summary>下载/导出进程结束的归一结论（UI 状态机只做路由展示，判定全部在这里，可单测）。</summary>
public enum CompletionOutcome
{
    /// <summary>用户确认取消（含关窗确认中断）。</summary>
    Cancelled,
    /// <summary>常规下载成功（退出码 0）。</summary>
    DownloadSuccess,
    /// <summary>常规下载失败（非零退出码）。</summary>
    DownloadFailed,
    /// <summary>UP 主导出成功（退出码 0 且时间窗内生成非空清单）。</summary>
    UpExportSuccess,
    /// <summary>UP 主导出失败（其余一切情况，不再有无条件成功）。</summary>
    UpExportFailed,
    /// <summary>-ia 可见控制台正常结束（退出码 0，非 UP 导出）。</summary>
    IaEndedOk,
    /// <summary>-ia 可见控制台被关闭/异常退出（非零退出码，非 UP 导出）。</summary>
    IaEndedAbnormal
}

/// <summary>
/// v3.8 完成态路由（需求 R2.1/R2.2/R2.5 的状态机核心，纯函数可单测）。
/// 取消优先级最高；UP 主导出两条进程路径（隐藏/-ia）同标准；其余按退出码分派。
/// </summary>
public static class CompletionPolicy
{
    /// <summary>
    /// 本轮 P1 修复：内核「失败但退出码仍为 0」的路径。
    /// 内核在合并失败 / 解析分P失败时只打一行红色日志然后 return
    /// （Program.cs:664、:753 两处「合并失败」，:767「解析此分P失败」），
    /// 进程随后照常走完循环并以 0 退出（DoWorkAsync 只在异常时才 Environment.Exit(1)）。
    /// 助手若只看退出码，就会把「一个文件都没产出」报成「下载完成、保存到 …」，
    /// 还播放完成提示音——用户以为下好了，去文件夹里找不到东西。
    ///
    /// 判据只认内核原文的失败措辞（带时间戳前缀的整行日志仍包含这些子串），
    /// 不用裸「失败」二字——那会命中「重试失败次数」等无害文本。
    /// </summary>
    public static bool HasFailureSignal(string? logTail)
    {
        string t = logTail ?? "";
        return t.Contains("合并失败", StringComparison.Ordinal)
            || t.Contains("解析此分P失败", StringComparison.Ordinal);
    }

    public static CompletionOutcome Route(
        bool cancelling, bool upSpace, bool interactive, int exitCode, bool listFound,
        bool failureSignaled = false)
    {
        if (cancelling) return CompletionOutcome.Cancelled;
        if (upSpace)
            return exitCode == 0 && listFound
                ? CompletionOutcome.UpExportSuccess
                : CompletionOutcome.UpExportFailed;
        if (interactive)
            return exitCode == 0 ? CompletionOutcome.IaEndedOk : CompletionOutcome.IaEndedAbnormal;
        // 退出码 0 但有明确失败日志 → 仍判失败（-ia 分支保持原语义：那是用户可见的控制台，按退出码）
        if (exitCode == 0 && failureSignaled) return CompletionOutcome.DownloadFailed;
        return exitCode == 0 ? CompletionOutcome.DownloadSuccess : CompletionOutcome.DownloadFailed;
    }
}
