using Xunit;
using System.ComponentModel;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>v3.8 ErrorMapper（R2.5 失败归因 / 高工#9 / 质检#6）：每个特征至少一个用例 + 兜底。</summary>
public class ErrorMapperTests
{
    [Fact]
    public void MapExit_412_RiskControl()
    {
        var m = ErrorMapper.MapExit(1, "[2026-11-05] - 请求失败，HTTP 412");
        Assert.Contains("412", m.What);
        Assert.Contains("风控", m.Why);
        Assert.Contains("稍等", m.Action);
    }

    [Fact]
    public void MapExit_403_LoginOrPaid()
    {
        var m = ErrorMapper.MapExit(1, "ERROR: HTTP 403 Forbidden");
        Assert.Contains("登录", m.What);
        Assert.Contains("扫码登录", m.Action);
    }

    [Fact]
    public void MapExit_CookieInvalid_MapsToLogin()   // 收编在册 P3「-403 文案口径」同族特征
    {
        var m = ErrorMapper.MapExit(1, "检测到 cookie 失效，需要登录");
        Assert.Contains("登录", m.What);
        Assert.Contains("扫码登录", m.Action);
    }

    [Fact]
    public void MapExit_PaidVideo_PurchaseHint()
    {
        var m = ErrorMapper.MapExit(1, "该视频为付费视频");
        Assert.Contains("付费", m.Why);
        Assert.Contains("购买", m.Action);
    }

    [Fact]
    public void MapExit_DiskFull()
    {
        var m = ErrorMapper.MapExit(1, "System.IO.IOException: No space left on device");
        Assert.Contains("空间不足", m.What);
        Assert.Contains("清理", m.Action);
    }

    [Fact]
    public void MapExit_PathMissing()
    {
        var m = ErrorMapper.MapExit(1, "Could not find a part of the path");
        Assert.Contains("保存位置不可用", m.What);
        Assert.Contains("浏览", m.Action);
    }

    [Fact]
    public void MapExit_Timeout()
    {
        var m = ErrorMapper.MapExit(1, "The request timed out");
        Assert.Contains("网络", m.What);
        Assert.Contains("重试", m.Action);
    }

    [Fact]
    public void MapExit_AccessDenied_AntivirusGuidance()
    {
        var m = ErrorMapper.MapExit(1, "Access is denied");
        Assert.Contains("杀毒软件", m.Why);
        Assert.Contains("白名单", m.Action);
    }

    [Fact]
    public void MapExit_Fallback_ThreeFactors_AndExitCode()
    {
        var m = ErrorMapper.MapExit(42, "some unknown output");
        Assert.Contains("42", m.Why);
        Assert.Contains("未登录", m.Action);   // 兜底覆盖三因
        Assert.Contains("导出求助文件", m.Action);
    }

    [Fact]
    public void MapExit_NullLogTail_NoThrow()
    {
        var m = ErrorMapper.MapExit(1, null!);
        Assert.False(string.IsNullOrEmpty(m.What));
    }

    [Fact]
    public void MapStartError_FileNotFound_RepackageGuidance()
    {
        var m = ErrorMapper.MapStartError(new Win32Exception(2));
        Assert.Contains("core\\BBDown.exe", m.What);
        Assert.Contains("完整压缩包", m.Action);
    }

    [Fact]
    public void MapStartError_AccessDenied_AntivirusGuidance()
    {
        var m = ErrorMapper.MapStartError(new Win32Exception(5));
        Assert.Contains("杀毒软件", m.Why);
        Assert.Contains("白名单", m.Action);
    }

    [Fact]
    public void MapStartError_GenericException_UsesMessage()
    {
        var m = ErrorMapper.MapStartError(new InvalidOperationException("boom-xyz"));
        Assert.Contains("boom-xyz", m.Why);
        Assert.Contains("重试", m.Action);
    }

    [Fact]
    public void ThreePartFormat_ToText()
    {
        var m = new MappedError("出了什么事", "为什么", "怎么做");
        Assert.Equal("出了什么事：为什么。建议：怎么做", m.ToText());
    }
}
