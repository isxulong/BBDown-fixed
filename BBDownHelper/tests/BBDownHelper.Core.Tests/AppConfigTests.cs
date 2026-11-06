using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>v3.8 AppConfig（R2.4/R2.7 配置持久化）：损坏回退、缺字段回退、原子写、写失败静默。</summary>
public class AppConfigTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, ".config.json");
    }

    [Fact]
    public void Load_MissingFile_Defaults()
    {
        var cfg = AppConfig.Load(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"), "c.json"));
        Assert.False(cfg.VerboseLog);
        Assert.False(cfg.DontRemindLogin4K);
    }

    [Fact]
    public void Load_CorruptJson_Defaults_NoThrow()
    {
        var p = TempPath();
        File.WriteAllText(p, "{ this is not json !!!");
        var cfg = AppConfig.Load(p);
        Assert.False(cfg.VerboseLog);
        Assert.False(cfg.DontRemindLogin4K);
    }

    [Fact]
    public void Load_MissingFields_FallBackToDefaults()
    {
        var p = TempPath();
        File.WriteAllText(p, "{\"verboseLog\":true}");
        var cfg = AppConfig.Load(p);
        Assert.True(cfg.VerboseLog);
        Assert.False(cfg.DontRemindLogin4K);   // 缺字段回退默认
    }

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var p = TempPath();
        var cfg = new AppConfig { VerboseLog = true, DontRemindLogin4K = true };
        cfg.Save(p);
        var back = AppConfig.Load(p);
        Assert.True(back.VerboseLog);
        Assert.True(back.DontRemindLogin4K);
    }

    [Fact]
    public void Save_AtomicWrite_NoTmpLeftBehind()
    {
        var p = TempPath();
        new AppConfig { VerboseLog = true }.Save(p);
        Assert.True(File.Exists(p));
        Assert.False(File.Exists(p + ".tmp"));   // tmp + move 后无残留
    }

    [Fact]
    public void Save_OverwritesExisting()
    {
        var p = TempPath();
        new AppConfig { VerboseLog = true }.Save(p);
        new AppConfig { VerboseLog = false, DontRemindLogin4K = true }.Save(p);
        var back = AppConfig.Load(p);
        Assert.False(back.VerboseLog);
        Assert.True(back.DontRemindLogin4K);
    }

    [Fact]
    public void Save_BadDir_SilentNoThrow()
    {
        var cfg = new AppConfig { VerboseLog = true };
        cfg.Save(Path.Combine("Z:\\no-such-drive-" + Guid.NewGuid().ToString("N"), "c.json"));   // 不抛
    }

    // ---------- v4.0（R3.11 / PM R3.10）：下载目录记忆 + 全部选项持久化 ----------

    [Fact]
    public void Load_MissingFile_V40Defaults()
    {
        var cfg = AppConfig.Load(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"), "c.json"));
        Assert.Null(cfg.SaveDir);
        Assert.Equal("auto", cfg.Quality);
        Assert.Equal("auto", cfg.Encoding);
        Assert.Equal("none", cfg.Danmaku);
        Assert.True(cfg.MultiThread);
        Assert.False(cfg.AudioOnly);
        Assert.False(cfg.CoverOnly);
        Assert.False(cfg.SubOnly);
        Assert.True(cfg.DelayPerPage);
        Assert.True(cfg.SaveArchives);
    }

    [Fact]
    public void SaveLoad_V40Fields_RoundTrip()
    {
        var p = TempPath();
        var cfg = new AppConfig
        {
            SaveDir = "D:\\视频", Quality = "q1080", Encoding = "hevc", Danmaku = "dd",
            MultiThread = false, AudioOnly = true, CoverOnly = true, SubOnly = true,
            DelayPerPage = false, SaveArchives = false
        };
        cfg.Save(p);
        var back = AppConfig.Load(p);
        Assert.Equal("D:\\视频", back.SaveDir);
        Assert.Equal("q1080", back.Quality);
        Assert.Equal("hevc", back.Encoding);
        Assert.Equal("dd", back.Danmaku);
        Assert.False(back.MultiThread);
        Assert.True(back.AudioOnly);
        Assert.True(back.CoverOnly);
        Assert.True(back.SubOnly);
        Assert.False(back.DelayPerPage);
        Assert.False(back.SaveArchives);
    }

    [Fact]
    public void Load_InvalidEnumValues_FallBackToDefaults()
    {
        // 手改 config / 跨版本漂移：非法枚举值静默回退默认，不进命令组装
        var p = TempPath();
        File.WriteAllText(p, "{\"quality\":\"8k\",\"encoding\":\"vp9\",\"danmaku\":\"xml\"}");
        var cfg = AppConfig.Load(p);
        Assert.Equal("auto", cfg.Quality);
        Assert.Equal("auto", cfg.Encoding);
        Assert.Equal("none", cfg.Danmaku);
    }

    [Fact]
    public void Load_EmptySaveDir_NormalizedToNull()
    {
        var p = TempPath();
        File.WriteAllText(p, "{\"saveDir\":\"  \"}");
        Assert.Null(AppConfig.Load(p).SaveDir);
    }

    [Fact]
    public void Load_OldV38Config_NewFieldsGetDefaults()
    {
        // v3.8 的 config（只有两个字段）升级到 v4.0：新字段全部取默认，不丢旧字段
        var p = TempPath();
        File.WriteAllText(p, "{\"verboseLog\":true,\"dontRemindLogin4K\":true}");
        var cfg = AppConfig.Load(p);
        Assert.True(cfg.VerboseLog);
        Assert.True(cfg.DontRemindLogin4K);
        Assert.Null(cfg.SaveDir);
        Assert.Equal("auto", cfg.Quality);
        Assert.True(cfg.MultiThread);
    }
}
