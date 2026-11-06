using System;
using System.IO;
using BBDownHelper.Core;
using Xunit;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v1.0.6（真机反馈 #1）回归：用户改过系统「下载」文件夹位置后，
/// 「用『下载』文件夹」按钮必须指向真实位置，而不是硬编码的 C:\Users\xx\Downloads。
///
/// 判别力说明：这些用例注入「已知文件夹探测」返回值，
/// 走 <see cref="DownloadsFolderLocator.Resolve"/> 的同一段决策代码；
/// 若有人把实现改回硬拼 %USERPROFILE%\Downloads，KnownFolder_* 用例会立刻报红。
/// </summary>
public class DownloadsFolderLocatorTests
{
    private static string TempDirWithUniqueName()
    {
        var d = Path.Combine(Path.GetTempPath(), "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ---------- 核心回归：已知文件夹优先 ----------

    [Fact]
    public void KnownFolder_Wins_OverUserProfileDownloads()
    {
        // 用户把「下载」搬到了 E:\Downloads，同时 C:\Users\x\Downloads 仍然存在（旧位置常被系统留空壳）
        var realDownloads = TempDirWithUniqueName();   // 用临时目录冒充「E:\Downloads」
        var profileDownloads = TempDirWithUniqueName(); // 冒充 C:\Users\x\Downloads
        try
        {
            var actual = Path.Combine(profileDownloads, "Downloads");
            Directory.CreateDirectory(actual);

            var r = DownloadsFolderLocator.Resolve(
                userProfile: profileDownloads,
                knownFolderProbe: () => realDownloads);

            Assert.Equal(realDownloads, r.Path);
            Assert.Equal(DownloadsFolderLocator.Source.KnownFolder, r.From);
        }
        finally
        {
            try { Directory.Delete(realDownloads, true); } catch { }
            try { Directory.Delete(profileDownloads, true); } catch { }
        }
    }

    [Fact]
    public void KnownFolder_Used_EvenWhenFallbackPathMissing()
    {
        // 极端情况：%USERPROFILE%\Downloads 根本不存在（用户搬走后删掉了旧目录）——
        // 修复前这里会 Directory.CreateDirectory 出一个假的 C 盘目录，用户在资源管理器「下载」里找不到文件。
        var realDownloads = TempDirWithUniqueName();
        var profile = TempDirWithUniqueName();
        try
        {
            var r = DownloadsFolderLocator.Resolve(
                userProfile: profile,
                knownFolderProbe: () => realDownloads);

            Assert.Equal(realDownloads, r.Path);
        }
        finally
        {
            try { Directory.Delete(realDownloads, true); } catch { }
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    // ---------- 退化保护：探不到时行为不得变差 ----------

    [Fact]
    public void ProbeReturnsNull_FallsBackToUserProfileDownloads()
    {
        var profile = TempDirWithUniqueName();
        try
        {
            var r = DownloadsFolderLocator.Resolve(
                userProfile: profile,
                knownFolderProbe: () => null);

            Assert.Equal(Path.Combine(profile, "Downloads"), r.Path);
            Assert.Equal(DownloadsFolderLocator.Source.Fallback, r.From);
        }
        finally { try { Directory.Delete(profile, true); } catch { } }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ProbeReturnsBlank_FallsBack(string probe)
    {
        var profile = TempDirWithUniqueName();
        try
        {
            var r = DownloadsFolderLocator.Resolve(userProfile: profile, knownFolderProbe: () => probe);
            Assert.Equal(DownloadsFolderLocator.Source.Fallback, r.From);
            Assert.Equal(Path.Combine(profile, "Downloads"), r.Path);
        }
        finally { try { Directory.Delete(profile, true); } catch { } }
    }

    [Fact]
    public void ProbeReturnsNonexistentPath_FallsBack()
    {
        // 指向已卸载的盘（如 U 盘拔了）→ 不能当成功，否则按钮会把保存目录设到一个死路径
        var profile = TempDirWithUniqueName();
        try
        {
            var ghost = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"));
            var r = DownloadsFolderLocator.Resolve(userProfile: profile, knownFolderProbe: () => ghost);

            Assert.Equal(DownloadsFolderLocator.Source.Fallback, r.From);
            Assert.False(r.Path == ghost);
        }
        finally { try { Directory.Delete(profile, true); } catch { } }
    }

    [Fact]
    public void CombineDownloads_KeepsLegacyBehaviour()
    {
        // 兜底路径必须与修复前逐字一致，确保「探不到」时用户感知不变
        Assert.Equal(Path.Combine(@"C:\Users\x", "Downloads"), DownloadsFolderLocator.CombineDownloads(@"C:\Users\x"));
        Assert.Equal("Downloads", DownloadsFolderLocator.CombineDownloads(null));
        Assert.Equal("Downloads", DownloadsFolderLocator.CombineDownloads(""));
    }

    [Fact]
    public void DefaultProbe_OnLinux_DoesNotThrow()
    {
        // 非 Windows 平台 must not throw（否则按钮点击会被 catch 成「没能定位」而丢掉兜底）
        if (OperatingSystem.IsWindows()) return;
        var r = DownloadsFolderLocator.Resolve();
        Assert.Equal(DownloadsFolderLocator.Source.Fallback, r.From);
        Assert.False(string.IsNullOrWhiteSpace(r.Path));
    }
}
