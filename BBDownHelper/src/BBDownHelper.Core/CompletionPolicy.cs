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
    public static CompletionOutcome Route(
        bool cancelling, bool upSpace, bool interactive, int exitCode, bool listFound)
    {
        if (cancelling) return CompletionOutcome.Cancelled;
        if (upSpace)
            return exitCode == 0 && listFound
                ? CompletionOutcome.UpExportSuccess
                : CompletionOutcome.UpExportFailed;
        if (interactive)
            return exitCode == 0 ? CompletionOutcome.IaEndedOk : CompletionOutcome.IaEndedAbnormal;
        return exitCode == 0 ? CompletionOutcome.DownloadSuccess : CompletionOutcome.DownloadFailed;
    }
}
