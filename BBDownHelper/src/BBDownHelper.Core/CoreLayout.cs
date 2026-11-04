namespace BBDownHelper.Core;

/// <summary>
/// v3.3：core 子文件夹布局。BBDown.exe / aria2c.exe / ffmpeg.exe 收进 core\，
/// 顶层只留 BBDownHelper.exe 等入口文件，防零基础用户误双击黑窗一闪的 BBDown.exe。
/// 本类集中两件事：core\ 下各文件的路径解析（口径唯一）、首次启动迁移的计划（纯函数）。
/// 迁移的 IO 执行器在 <see cref="CoreDataMigrator"/>（失败只记录、不抛出）。
/// </summary>
public static class CoreLayout
{
    public const string CoreDirName = "core";
    public const string BbdownExeName = "BBDown.exe";
    public const string BbdownDataName = "BBDown.data";

    /// <summary>v3.5：检查更新的间隔缓存文件（core\.update-check.json）。</summary>
    public const string UpdateCheckCacheName = ".update-check.json";

    /// <summary>
    /// BBDown（1.6.3 fixed）按 APP_DIR = 自身 exe 目录读写的凭据/状态数据文件。
    /// 老版本（v1.0.0/v1.0.1）布局里这些文件与 BBDown.exe 同在顶层，
    /// 升级到 core\ 布局后必须搬进 core\，否则 BBDown 找不到登录态。
    /// v3.4（质检 P3）：补 BBDown.archives——「已下载跳过」记录同样读写于 APP_DIR
    /// （BBDown Program.Methods.cs:252/262），不迁移会丢失「记住已下载」状态。
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyDataFiles =
        new[] { "BBDown.data", "BBDown.refresh_token", "BBDownTV.data", "BBDown.archives" };

    /// <summary>core\ 子目录完整路径。</summary>
    public static string CoreDir(string baseDir) => Path.Combine(baseDir, CoreDirName);

    /// <summary>core\BBDown.exe 完整路径——v3.3 起 BBDown.exe 唯一正确的位置。</summary>
    public static string BbdownPath(string baseDir) => Path.Combine(baseDir, CoreDirName, BbdownExeName);

    /// <summary>core\BBDown.data 完整路径——登录状态判定与 cookie 读取的统一口径。</summary>
    public static string BbdownDataPath(string baseDir) => Path.Combine(baseDir, CoreDirName, BbdownDataName);

    /// <summary>v3.5：core\.update-check.json 完整路径——检查更新的 24 小时间隔缓存。</summary>
    public static string UpdateCheckCachePath(string baseDir) =>
        Path.Combine(baseDir, CoreDirName, UpdateCheckCacheName);

    /// <summary>
    /// 迁移计划（纯函数，可单测）：顶层存在、且 core\ 里还没有的数据文件才搬；
    /// core\ 已有的不覆盖——不能拿顶层旧凭据盖掉 core\ 里的新凭据。
    /// 清单外的文件（用户自己的下载、清单 txt 等）一律不碰。
    /// </summary>
    public static IReadOnlyList<string> PlanMigration(IEnumerable<string> topFiles, IEnumerable<string> coreFiles)
    {
        var core = new HashSet<string>(coreFiles, StringComparer.OrdinalIgnoreCase);
        return LegacyDataFiles
            .Where(f => topFiles.Contains(f, StringComparer.OrdinalIgnoreCase) && !core.Contains(f))
            .ToList();
    }
}

/// <summary>一次迁移执行的结果。Failed 与 Errors 按下标一一对应。</summary>
public sealed record CoreDataMigrationResult(
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Errors)
{
    public static readonly CoreDataMigrationResult Empty =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// 首次启动迁移执行器：把顶层的旧 BBDown 数据文件移动进 core\。
/// 任何一步失败（文件被占用 / 权限不足等）只记入结果——调用方写日志、按未登录态继续，
/// 绝不抛异常导致启动失败。
/// </summary>
public static class CoreDataMigrator
{
    public static CoreDataMigrationResult Execute(string baseDir) =>
        Execute(baseDir, Directory.EnumerateFiles);

    /// <summary>
    /// 可注入列举器的重载（测试用：可注入抛异常的列举器验证「绝不崩」）。
    /// </summary>
    internal static CoreDataMigrationResult Execute(string baseDir, Func<string, IEnumerable<string>> listFiles)
    {
        string coreDir = CoreLayout.CoreDir(baseDir);
        // core\ 不存在 = 安装不完整（只拷了单个 exe 没解压完整包）：
        // 不创建 core\ 也不动用户数据，等用户按完整包解压后下次启动再迁
        if (!Directory.Exists(coreDir)) return CoreDataMigrationResult.Empty;

        var plan = CoreLayout.PlanMigration(FileNames(baseDir, listFiles), FileNames(coreDir, listFiles));
        var moved = new List<string>();
        var failed = new List<string>();
        var errors = new List<string>();
        foreach (var name in plan)
        {
            try
            {
                File.Move(Path.Combine(baseDir, name), Path.Combine(coreDir, name));
                moved.Add(name);
            }
            catch (Exception ex)   // 占用/权限：记录后继续搬下一个，不中断也不崩
            {
                failed.Add(name);
                errors.Add(name + ": " + ex.Message);
            }
        }
        return new CoreDataMigrationResult(moved, failed, errors);
    }

    /// <summary>
    /// 目录文件名列举。v3.4（质检 P3）：EnumerateFiles 包 try/catch，
    /// 异常（权限/占用/句柄失效）返回空数组，守住「绝不崩」承诺——
    /// 顶层列举失败=无事可搬；core\ 列举失败=视为空（core 已有文件不存在的口径下全量搬入）。
    /// </summary>
    private static string[] FileNames(string dir, Func<string, IEnumerable<string>> listFiles)
    {
        try
        {
            return Directory.Exists(dir)
                ? listFiles(dir)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null)
                    .Select(n => n!)
                    .ToArray()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
