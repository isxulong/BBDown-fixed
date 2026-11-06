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
}
