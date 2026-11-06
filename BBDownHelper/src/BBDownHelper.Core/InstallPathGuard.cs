namespace BBDownHelper.Core;

/// <summary>
/// v3.8 安放路径自检（架构#1 / 需求 R2.11）：程序放在 Temp（压缩软件直跑）/
/// 常见同步盘目录时，启动后给一次性黄条提醒。纯函数：命中返回提醒文案，正常位置返回 null。
/// </summary>
public static class InstallPathGuard
{
    /// <summary>命中时的提醒文案。</summary>
    public const string Warning =
        "程序放在了容易丢数据的位置（临时目录或网盘同步文件夹），建议移到桌面或普通文件夹，否则下载的文件和登录状态可能丢失。";

    // 临时落点路径段（精确匹配）：Temp、tmp 等
    private static readonly string[] TempExact = { "temp", "tmp", "temporaryinternetfiles" };

    // 压缩软件直跑的随机临时目录特征（前缀匹配）：7zO123、Rar$DIa0.123 等
    private static readonly string[] TempPrefix = { "7zo", "rar$" };

    // 常见同步盘目录名特征（段内包含比较）
    private static readonly string[] SyncSegments =
    {
        "onedrive", "dropbox", "googledrive", "nutstore", "iclouddrive",
        "百度网盘", "坚果云", "腾讯微云", "微云", "天翼云盘", "阿里云盘", "夸克网盘"
    };

    /// <summary>
    /// 检查程序安放路径。命中 Temp/压缩软件缓存/同步盘特征返回 <see cref="Warning"/>，否则 null。
    /// 逐路径段比较（不整串 Contains，避免「D:\Template\」这类误命中）。
    /// </summary>
    public static string? Check(string? baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir)) return null;
        // 显式按两种分隔符切段：Windows 路径用 \，Linux 上 Path.DirectorySeparatorChar 是 /，
        // 若只按系统分隔符切，在 Linux 上跑测试时整条 Windows 路径会被当成一段而漏判。
        foreach (var rawSeg in baseDir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = rawSeg.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
            if (seg.Length == 0) continue;
            foreach (var t in TempExact)
                if (seg == t) return Warning;
            foreach (var p in TempPrefix)
                if (seg.StartsWith(p, StringComparison.Ordinal)) return Warning;
            foreach (var s in SyncSegments)
                if (seg.Contains(s, StringComparison.Ordinal)) return Warning;
        }
        return null;
    }
}
