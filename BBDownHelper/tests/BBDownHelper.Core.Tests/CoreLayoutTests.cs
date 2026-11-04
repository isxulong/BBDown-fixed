using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v3.3：core 子文件夹布局的路径解析与首次启动迁移。
/// 老版本（v1.0.0/v1.0.1）把 BBDown.data 等写在顶层，BBDown 1.6.3 按 APP_DIR
/// （自身 exe 目录）读写数据文件——升级到 core\ 布局后必须把这些文件搬进 core\。
/// </summary>
public class CoreLayoutTests
{
    // ---------- 路径解析 ----------

    [Fact]
    public void BbdownPath_ResolvesIntoCoreSubfolder()
    {
        string p = CoreLayout.BbdownPath(Path.Combine("root", "BBDownHelper"));
        Assert.EndsWith(Path.Combine("core", "BBDown.exe"), p);
    }

    [Fact]
    public void BbdownDataPath_ResolvesIntoCoreSubfolder()
    {
        string p = CoreLayout.BbdownDataPath(Path.Combine("root", "BBDownHelper"));
        Assert.EndsWith(Path.Combine("core", "BBDown.data"), p);
    }

    // ---------- 迁移计划（纯函数） ----------

    [Fact]
    public void Plan_TopHasAllCoreEmpty_PlansAllThree()
    {
        // 老用户 v1.0.1 完整升级场景：三个数据文件都在顶层、core\ 全新
        var plan = CoreLayout.PlanMigration(
            new[] { "BBDown.data", "BBDown.refresh_token", "BBDownTV.data", "BBDownHelper.exe" },
            new[] { "BBDown.exe", "aria2c.exe", "ffmpeg.exe" });
        Assert.Equal(new[] { "BBDown.data", "BBDown.refresh_token", "BBDownTV.data" }, plan);
    }

    [Fact]
    public void Plan_CoreAlreadyHasDataFile_SkipsIt_PlansOthers()
    {
        // core\ 已有 BBDown.data（新包自带或已迁移过）：不覆盖，只搬剩下的
        var plan = CoreLayout.PlanMigration(
            new[] { "BBDown.data", "BBDown.refresh_token" },
            new[] { "BBDown.data", "BBDown.exe" });
        Assert.Equal(new[] { "BBDown.refresh_token" }, plan);
    }

    [Fact]
    public void Plan_NoTopDataFiles_EmptyPlan()
    {
        // 新装用户：顶层本来就没有数据文件，无事可做
        var plan = CoreLayout.PlanMigration(
            new[] { "BBDownHelper.exe", "使用说明.txt" },
            new[] { "BBDown.exe" });
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_IgnoresFilesOutsideKnownList()
    {
        // 清单外的文件（用户下载、UP 投稿清单 txt 等）一律不碰
        // v3.4（质检 P3）：BBDown.archives 已进白名单，从「清单外」例子里移除
        var plan = CoreLayout.PlanMigration(
            new[] { "某UP的投稿视频.txt", "video.mp4", "BBDown.log" },
            Array.Empty<string>());
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_ArchivesFile_IsKnownAndPlanned()
    {
        // v3.4（质检 P3）：BBDown.archives（「已下载跳过」记录，BBDown 写在 APP_DIR）
        // 必须与其他数据文件一样进迁移清单
        Assert.Contains("BBDown.archives", CoreLayout.LegacyDataFiles);
        var plan = CoreLayout.PlanMigration(
            new[] { "BBDown.archives", "BBDownHelper.exe" },
            new[] { "BBDown.exe" });
        Assert.Equal(new[] { "BBDown.archives" }, plan);
    }

    [Fact]
    public void Plan_FileNameMatchIsCaseInsensitive()
    {
        // Windows 文件系统大小写不敏感：bbdown.data 也要识别为数据文件
        var plan = CoreLayout.PlanMigration(new[] { "bbdown.data" }, Array.Empty<string>());
        Assert.Equal(new[] { "BBDown.data" }, plan);
    }

    // ---------- 迁移执行器（临时目录实跑 IO） ----------

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bbdh_v33_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "core"));
        return dir;
    }

    [Fact]
    public void Execute_MovesLegacyFilesIntoCore()
    {
        string top = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(top, "core", "BBDown.exe"), "bin");
            File.WriteAllText(Path.Combine(top, "BBDown.data"), "cookie");
            File.WriteAllText(Path.Combine(top, "BBDown.refresh_token"), "rt");
            File.WriteAllText(Path.Combine(top, "BBDownTV.data"), "token");
            File.WriteAllText(Path.Combine(top, "用户下载.txt"), "keep");

            var r = CoreDataMigrator.Execute(top);

            Assert.Equal(new[] { "BBDown.data", "BBDown.refresh_token", "BBDownTV.data" }, r.Moved);
            Assert.Empty(r.Failed);
            Assert.True(File.Exists(Path.Combine(top, "core", "BBDown.data")));
            Assert.True(File.Exists(Path.Combine(top, "core", "BBDown.refresh_token")));
            Assert.True(File.Exists(Path.Combine(top, "core", "BBDownTV.data")));
            // 顶层只搬清单内文件：用户自己的文件原样保留
            Assert.False(File.Exists(Path.Combine(top, "BBDown.data")));
            Assert.True(File.Exists(Path.Combine(top, "用户下载.txt")));
        }
        finally { Directory.Delete(top, true); }
    }

    [Fact]
    public void Execute_NoCoreDir_IsNoOp_AndDoesNotCreateCore()
    {
        // 只拷了单个 exe 的残缺安装：不动用户数据、不创建 core\
        string top = Path.Combine(Path.GetTempPath(), "bbdh_v33_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(top);
        try
        {
            File.WriteAllText(Path.Combine(top, "BBDown.data"), "cookie");

            var r = CoreDataMigrator.Execute(top);

            Assert.Same(CoreDataMigrationResult.Empty, r);
            Assert.False(Directory.Exists(Path.Combine(top, "core")));
            Assert.True(File.Exists(Path.Combine(top, "BBDown.data")));   // 原地未动
        }
        finally { Directory.Delete(top, true); }
    }

    [Fact]
    public void Execute_CoreAlreadyHasDataFile_TopFileKept()
    {
        // core\ 已有凭据时不覆盖：顶层旧文件留在原地，其余照搬
        string top = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(top, "core", "BBDown.data"), "new");
            File.WriteAllText(Path.Combine(top, "BBDown.data"), "old");
            File.WriteAllText(Path.Combine(top, "BBDown.refresh_token"), "rt");

            var r = CoreDataMigrator.Execute(top);

            Assert.Equal(new[] { "BBDown.refresh_token" }, r.Moved);
            Assert.Equal("new", File.ReadAllText(Path.Combine(top, "core", "BBDown.data")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(top, "BBDown.data")));   // 未被覆盖/删除
            Assert.True(File.Exists(Path.Combine(top, "core", "BBDown.refresh_token")));
        }
        finally { Directory.Delete(top, true); }
    }

    [Fact]
    public void Execute_FailureRecorded_NotThrown_OtherFilesStillMoved()
    {
        // core\BBDown.data 处同名条目是目录（模拟目标被占用/异常）：
        // File.Move 抛 IOException——必须记录为失败并继续搬其余文件，绝不抛出
        string top = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(top, "BBDown.data"), "cookie");
            Directory.CreateDirectory(Path.Combine(top, "core", "BBDown.data"));
            File.WriteAllText(Path.Combine(top, "BBDown.refresh_token"), "rt");

            var r = CoreDataMigrator.Execute(top);

            Assert.Contains("BBDown.data", r.Failed);
            Assert.Single(r.Errors);
            Assert.Equal(new[] { "BBDown.refresh_token" }, r.Moved);
            Assert.True(File.Exists(Path.Combine(top, "BBDown.data")));   // 原样保留
            Assert.True(File.Exists(Path.Combine(top, "core", "BBDown.refresh_token")));
        }
        finally { Directory.Delete(top, true); }
    }

    // ---------- v3.4（质检 P3）：目录列举异常兜底 ----------

    [Fact]
    public void Execute_ListerThrows_NoCrash_EmptyPlan()
    {
        // EnumerateFiles 抛异常（权限/句柄失效）：不得崩、不得抛——返回空计划
        string top = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(top, "BBDown.data"), "cookie");

            var r = CoreDataMigrator.Execute(top, _ => throw new IOException("句柄失效"));

            Assert.Empty(r.Moved);
            Assert.Empty(r.Failed);
            Assert.True(File.Exists(Path.Combine(top, "BBDown.data")));   // 原地未动
        }
        finally { Directory.Delete(top, true); }
    }

    [Fact]
    public void Execute_CoreListerThrows_TreatedAsEmpty_MigratesAll()
    {
        // core\ 列举失败 = 视 core 为空：顶层数据文件全量搬入（core\ 本身存在，File.Move 能落）
        string top = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(top, "BBDown.data"), "cookie");
            File.WriteAllText(Path.Combine(top, "BBDown.archives"), "a1\na2\n");

            var r = CoreDataMigrator.Execute(top, d =>
                d.EndsWith("core", StringComparison.OrdinalIgnoreCase)
                    ? throw new IOException("core 句柄失效")
                    : Directory.EnumerateFiles(d));

            Assert.Equal(new[] { "BBDown.data", "BBDown.archives" }, r.Moved);
            Assert.True(File.Exists(Path.Combine(top, "core", "BBDown.archives")));
        }
        finally { Directory.Delete(top, true); }
    }

    [Fact]
    public void Execute_MovesArchivesIntoCore()
    {
        // BBDown.archives 与其他数据文件一样：顶层有、core 没有时搬入 core\
        string top = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(top, "core", "BBDown.exe"), "bin");
            File.WriteAllText(Path.Combine(top, "BBDown.archives"), "BV1xx411c7mD\n");

            var r = CoreDataMigrator.Execute(top);

            Assert.Equal(new[] { "BBDown.archives" }, r.Moved);
            Assert.True(File.Exists(Path.Combine(top, "core", "BBDown.archives")));
            Assert.False(File.Exists(Path.Combine(top, "BBDown.archives")));
        }
        finally { Directory.Delete(top, true); }
    }
}