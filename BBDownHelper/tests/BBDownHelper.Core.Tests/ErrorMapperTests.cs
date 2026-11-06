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

    // ===== 本轮回归用例（错误归因三处误判）=====

    /// <summary>
    /// 内核只要有 BBDown.data，每次启动必打「加载本地cookie...」（Program.Methods.cs:227）。
    /// 这行天然坐在日志尾部，原来的裸 Contains("cookie") 让**任何一次下载失败**
    /// 都命中登录分支 → 报「需要登录」+ 登录徽标打失效。此例锁住：陈述句不算登录信号。
    /// </summary>
    [Fact]
    public void MapExit_LoadLocalCookieNotice_NotLoginRelated()
    {
        string tail = "[INFO] 加载本地cookie...\n[ERROR] 合并失败：无法写入输出文件";
        Assert.False(ErrorMapper.IsLoginRelated(tail));
        var m = ErrorMapper.MapExit(3, tail);
        // 关键断言：不得落到「这个内容需要登录或付费才能下载」这条硬登录分支。
        // 兜底文案里也会顺带提「先点扫码登录」，所以这里只锁分支名，不锁关键词。
        Assert.DoesNotContain("这个内容需要登录", m.What);
        Assert.DoesNotContain("风控", m.Why);
    }

    /// <summary>412 裸数字会撞 aria2 分段字节数 / 文件大小 / cid 片段，不能当风控信号。</summary>
    [Fact]
    public void MapExit_Bare412Number_NotRiskControl()
    {
        var m = ErrorMapper.MapExit(1, "[INFO] 下载完成 412 MiB，用时 6s\n[ERROR] 合并失败");
        Assert.DoesNotContain("412", m.What);
        Assert.DoesNotContain("风控", m.Why);
    }

    /// <summary>
    /// 内核缺 ffmpeg 抛「找不到可执行的ffmpeg文件」（Program.Methods.cs:148）。
    /// 原裸 Contains("找不到") 把它归因成「U 盘已拔出」，用户在正常目录上反复点「浏览…」也修不好。
    /// </summary>
    [Fact]
    public void MapExit_MissingFfmpeg_NotPathProblem()
    {
        var m = ErrorMapper.MapExit(1, "系统找不到可执行的ffmpeg文件，请确认 core 目录完整");
        Assert.Contains("内核组件缺失", m.What);
        Assert.DoesNotContain("浏览", m.Action);
    }

    [Fact]
    public void IsLoginRelated_CookieRefreshStatement_NotLoginRelated()
    {
        Assert.False(ErrorMapper.IsLoginRelated("Cookie 已自动刷新"));
        Assert.True(ErrorMapper.IsLoginRelated("检测到 cookie 失效，需要登录"));
    }
}
