namespace BBDownHelper.Core;

/// <summary>开始前预检结果：Ok=目录存在且可写；LowSpace=可用空间低于阈值（黄条提醒、不阻塞）。</summary>
public sealed record StorageCheck(bool Ok, string? Error, bool LowSpace, long FreeBytes);

/// <summary>
/// v3.8 开始前保存位置预检（质检#5 / 需求 R2.3）：
/// ① 目录存在（被删/U 盘拔出 → 不启动，报「保存位置不可用」）；
/// ② 可写（创建即删一个临时文件验证）；
/// ③ 剩余空间低于 5GB → LowSpace=true（提醒不阻塞）。
/// 设计目标：快检（&lt;1s），IO 只做「列一次元数据 + 建删一个小文件」。
/// </summary>
public static class StorageGuard
{
    /// <summary>低空间提醒阈值：5GB。</summary>
    public const long LowSpaceThresholdBytes = 5L * 1024 * 1024 * 1024;

    public static StorageCheck Check(string? dir) =>
        Check(dir, Directory.Exists, TryWriteProbe, GetFreeBytes);

    /// <summary>可注入重载（测试用）：目录存在性、写探针、可用空间三个探针全部可替换。</summary>
    internal static StorageCheck Check(
        string? dir,
        Func<string, bool> dirExists,
        Func<string, bool> writeProbe,
        Func<string, long> freeBytesOf)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return new(false, "保存文件夹为空，请点「浏览…」选一个", false, -1);
        if (!Safe(() => dirExists(dir), false))
            return new(false, "保存文件夹不存在（可能被删除，或 U 盘/移动硬盘已拔出）", false, -1);
        if (!Safe(() => writeProbe(dir), false))
            return new(false, "保存文件夹不可写（没有写入权限，或被杀软/只读属性拦截）", false, -1);
        long free = Safe(() => freeBytesOf(dir), -1);
        return new(true, null, free >= 0 && free < LowSpaceThresholdBytes, free);
    }

    /// <summary>写探针：创建即删一个临时文件。任何异常返回 false。</summary>
    private static bool TryWriteProbe(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".bbdownhelper-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>可用空间：DriveInfo 取所在盘剩余字节；取不到返回 -1（不阻塞、不提醒）。</summary>
    private static long GetFreeBytes(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }
}
