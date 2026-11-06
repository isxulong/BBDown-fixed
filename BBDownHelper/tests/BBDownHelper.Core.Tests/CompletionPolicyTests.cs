using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>v3.8 CompletionPolicy（R2.1 取消 / R2.2 -ia 登记 / R2.5 完成闭环 的状态机核心）。</summary>
public class CompletionPolicyTests
{
    [Fact]
    public void Cancelling_AlwaysCancelled_TakesPrecedenceOverSuccess()
    {
        Assert.Equal(CompletionOutcome.Cancelled,
            CompletionPolicy.Route(cancelling: true, upSpace: false, interactive: false, exitCode: 0, listFound: true));
    }

    [Fact]
    public void Cancelling_OverridesUpSpace()   // v3.7 既有约定：cancelled 分支不被并入导出判定
    {
        Assert.Equal(CompletionOutcome.Cancelled,
            CompletionPolicy.Route(cancelling: true, upSpace: true, interactive: false, exitCode: 0, listFound: true));
    }

    [Fact]
    public void Download_ExitZero_Success()
    {
        Assert.Equal(CompletionOutcome.DownloadSuccess,
            CompletionPolicy.Route(false, upSpace: false, interactive: false, exitCode: 0, listFound: false));
    }

    [Fact]
    public void Download_NonZero_Failed()
    {
        Assert.Equal(CompletionOutcome.DownloadFailed,
            CompletionPolicy.Route(false, upSpace: false, interactive: false, exitCode: 1, listFound: false));
    }

    [Fact]
    public void UpExport_ExitZeroAndListFound_Success()
    {
        Assert.Equal(CompletionOutcome.UpExportSuccess,
            CompletionPolicy.Route(false, upSpace: true, interactive: false, exitCode: 0, listFound: true));
    }

    [Fact]
    public void UpExport_ExitZeroButNoList_Failed()   // v3.7 真 bug ①：退出码 0 无清单也不算成功
    {
        Assert.Equal(CompletionOutcome.UpExportFailed,
            CompletionPolicy.Route(false, upSpace: true, interactive: false, exitCode: 0, listFound: false));
    }

    [Fact]
    public void UpExport_NonZero_Failed()
    {
        Assert.Equal(CompletionOutcome.UpExportFailed,
            CompletionPolicy.Route(false, upSpace: true, interactive: false, exitCode: 2, listFound: true));
    }

    [Fact]
    public void Ia_ExitZero_EndedOk()
    {
        Assert.Equal(CompletionOutcome.IaEndedOk,
            CompletionPolicy.Route(false, upSpace: false, interactive: true, exitCode: 0, listFound: false));
    }

    [Fact]
    public void Ia_WindowClosed_Abnormal()   // R2.2：完成态区分「控制台正常结束/被关闭」
    {
        Assert.Equal(CompletionOutcome.IaEndedAbnormal,
            CompletionPolicy.Route(false, upSpace: false, interactive: true, exitCode: -1073741510, listFound: false));
    }

    [Fact]
    public void IaUpExport_SameVerifierStandard()   // R2.2：-ia 的 UP 导出走同一 TryFindList 标准
    {
        Assert.Equal(CompletionOutcome.UpExportSuccess,
            CompletionPolicy.Route(false, upSpace: true, interactive: true, exitCode: 0, listFound: true));
        Assert.Equal(CompletionOutcome.UpExportFailed,
            CompletionPolicy.Route(false, upSpace: true, interactive: true, exitCode: 0, listFound: false));
    }

    // ===== 本轮回归（内核失败但退出码 0）=====

    /// <summary>
    /// 内核 Program.cs:664/753 合并失败、:767 解析失败都只打红字后 return，
    /// 进程照常以 0 退出。只看退出码会把「零产出」报成「下载完成」。
    /// </summary>
    [Fact]
    public void Route_ExitZero_WithMergeFailureSignal_MapsToDownloadFailed()
    {
        string tail = "[2026-11-06 10:00:00.000] - 开始合并音视频...\n"
                    + "[2026-11-06 10:00:01.000] - 合并失败";
        Assert.True(CompletionPolicy.HasFailureSignal(tail));
        Assert.Equal(CompletionOutcome.DownloadFailed,
            CompletionPolicy.Route(false, upSpace: false, interactive: false, exitCode: 0, listFound: false,
                failureSignaled: true));
    }

    [Fact]
    public void Route_ExitZero_NoFailureSignal_StillSuccess()
    {
        string tail = "[2026-11-06 10:00:00.000] - 下载P1完毕\n[2026-11-06 10:00:00.000] - 任务完成";
        Assert.False(CompletionPolicy.HasFailureSignal(tail));
        Assert.Equal(CompletionOutcome.DownloadSuccess,
            CompletionPolicy.Route(false, upSpace: false, interactive: false, exitCode: 0, listFound: false,
                failureSignaled: false));
    }

    /// <summary>裸「失败」会命中「重试失败次数」这类无害文本，判据不能放宽到那个粒度。</summary>
    [Fact]
    public void HasFailureSignal_BareFailWord_NotMatched()
    {
        Assert.False(CompletionPolicy.HasFailureSignal("重试失败次数: 1（已自动恢复）"));
        Assert.False(CompletionPolicy.HasFailureSignal("封面下载失败，已跳过"));
        Assert.True(CompletionPolicy.HasFailureSignal("解析此分P失败(建议--debug查看详细信息)"));
    }

    /// <summary>-ia 是用户可见控制台，语义保持只看退出码（避免把用户自己关窗判成下载失败）。</summary>
    [Fact]
    public void Route_Interactive_KeepsExitCodeSemantics()
    {
        Assert.Equal(CompletionOutcome.IaEndedOk,
            CompletionPolicy.Route(false, upSpace: false, interactive: true, exitCode: 0, listFound: false,
                failureSignaled: true));
    }
}
