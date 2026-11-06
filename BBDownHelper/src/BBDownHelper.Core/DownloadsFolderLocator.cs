using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BBDownHelper.Core;

/// <summary>
/// v1.0.6（真机反馈 #1）：「用『下载』文件夹」按钮的真实路径解析。
///
/// 缺陷原状：界面直接拼 <c>Environment.GetFolderPath(UserProfile) + "Downloads"</c>，
/// 等于硬编码 %USERPROFILE%\Downloads。只要用户改过系统「下载」文件夹位置
/// （Windows 允许把该文件夹搬到别的盘，例如 E:\Downloads），
/// 这条路径就指向 C 盘那个已废弃的空目录。真机诊断文件里实证：
/// 「保存位置已设为：C:\Users\Administrator\Downloads」，而用户实际目录在 E:\Downloads。
///
/// 正确做法：优先读系统的「下载」已知文件夹（即资源管理器「下载」指向的那个目录），
/// 拿不到时才退回 %USERPROFILE%\Downloads（老行为，保证不退化）。
/// 探测委托可注入，便于在非 Windows 沙箱上单测这段决策逻辑。
/// </summary>
public static class DownloadsFolderLocator
{
    /// <summary>「下载」已知文件夹的 FOLDERID（{374DE290-123F-4565-9164-39C4925E467B}）。</summary>
    public const string DownloadsFolderId = "{374DE290-123F-4565-9164-39C4925E467B}";

    /// <summary>路径来源，供日志说明「这个路径是怎么来的」。</summary>
    public enum Source
    {
        /// <summary>系统已知文件夹（SHGetKnownFolderPath）——权威来源。</summary>
        KnownFolder,
        /// <summary>兜底：%USERPROFILE%\Downloads（修复前的老行为）。</summary>
        Fallback
    }

    /// <summary>解析结果：路径 + 来源。</summary>
    public sealed record Resolution(string Path, Source From);

    /// <summary>
    /// 解析真实「下载」文件夹。
    /// </summary>
    /// <param name="userProfile">用户配置目录（兜底用）。null 时取 SpecialFolder.UserProfile。</param>
    /// <param name="knownFolderProbe">已知文件夹探测（可注入；返回 null/空白/不存在视为取不到）。</param>
    public static Resolution Resolve(
        string? userProfile = null,
        Func<string?>? knownFolderProbe = null)
    {
        userProfile ??= SafeUserProfile();

        var known = Normalize((knownFolderProbe ?? ProbeKnownFolder)());
        if (known is not null) return new Resolution(known, Source.KnownFolder);

        return new Resolution(CombineDownloads(userProfile), Source.Fallback);
    }

    /// <summary>
    /// 兜底路径：&lt;用户配置目录&gt;\Downloads。公开供测试断言「兜底不变」。
    /// </summary>
    public static string CombineDownloads(string? userProfile)
    {
        if (string.IsNullOrWhiteSpace(userProfile)) return "Downloads";
        return Path.Combine(userProfile, "Downloads");
    }

    /// <summary>空白 / 指向不存在的目录 → 视为「取不到」，返回 null。</summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        // 指向已卸载的盘或已删目录时也算取不到，避免把一条死路径当成功
        if (!Directory.Exists(p)) return null;
        return p;
    }

    private static string? SafeUserProfile()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        catch { return null; }
    }

    /// <summary>非 Windows 平台直接返回 null（沙箱/Linux 自然走兜底）。</summary>
    private static string? ProbeKnownFolder()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return WindowsKnownFolder.GetDownloadsPath(); }
        catch { return null; }
    }
}

/// <summary>
/// Windows 已知文件夹原生调用（与 locator 分开，让决策逻辑保持纯函数可测）。
/// </summary>
internal static class WindowsKnownFolder
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr pv);

    /// <summary>取「下载」已知文件夹；失败返回 null，由上层转兜底。</summary>
    public static string? GetDownloadsPath()
    {
        var guid = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        int hr = SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var ptr);
        if (hr != 0 || ptr == IntPtr.Zero) return null;
        try
        {
            // 该 API 返回 unicode 指针，内存由 CoTaskMemAlloc 分配，必须 CoTaskMemFree
            return Marshal.PtrToStringUni(ptr);
        }
        finally { CoTaskMemFree(ptr); }
    }
}
