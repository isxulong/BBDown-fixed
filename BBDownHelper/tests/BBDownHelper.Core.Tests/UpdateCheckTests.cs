using BBDownHelper.Core;
using Xunit;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v3.5 检查更新 + 版本单一来源。
/// 覆盖：语义化版本比较、是否提示判定、24h 缓存间隔、缓存读写失败静默降级、
/// GitHub release JSON 解析、HTTP mock 注入下 CheckAsync 的完整决策树。
/// </summary>
public class UpdateCheckTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    // ---------- 版本号单一来源 ----------

    [Fact]
    public void AppVersion_SingleSource_Pinned()
    {
        Assert.Equal("1.0.3", AppVersion.Release);
        Assert.Equal("v1.0.3", AppVersion.ReleaseTag);
        Assert.Equal("BBDown-fixed 下载助手 v1.0.3", AppVersion.WindowTitle);
        Assert.Equal(AppVersion.ReleaseTag, AppVersion.CornerLabel);
    }

    [Fact]
    public void CoreLayout_UpdateCheckCachePath_UnderCoreDir()
    {
        // 平台无关断言：baseDir / core / .update-check.json
        string baseDir = Path.Combine(Path.GetTempPath(), "app");
        var p = CoreLayout.UpdateCheckCachePath(baseDir);
        Assert.Equal(
            baseDir + Path.DirectorySeparatorChar + CoreLayout.CoreDirName
                + Path.DirectorySeparatorChar + CoreLayout.UpdateCheckCacheName,
            p);
    }

    // ---------- 语义化版本解析与比较 ----------

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V2.0", 2, 0, 0)]
    [InlineData("1", 1, 0, 0)]
    [InlineData("v1.2.3-beta1", 1, 2, 3)]   // 预发布后缀忽略
    [InlineData(" v1.2.3 ", 1, 2, 3)]       // 前后空白容忍
    public void TryParseSemVer_Valid(string tag, int major, int minor, int patch)
    {
        Assert.True(UpdateCheckLogic.TryParseSemVer(tag, out int ma, out int mi, out int pa));
        Assert.Equal((major, minor, patch), (ma, mi, pa));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]     // 4 段不支持
    [InlineData("1..2")]       // 空段
    [InlineData("1.x.2")]
    [InlineData("-1.2.3")]      // 负号不是合法段
    public void TryParseSemVer_Invalid(string? tag)
    {
        Assert.False(UpdateCheckLogic.TryParseSemVer(tag, out _, out _, out _));
    }

    [Theory]
    [InlineData("1.0.3", "1.0.3", 0)]
    [InlineData("1.0.3", "1.0.4", -1)]
    [InlineData("1.1.0", "1.0.9", 1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("v1.0.3", "1.0.3", 0)]
    [InlineData("1.0.3", "abc", 1)]     // 不可解析按 0.0.0
    [InlineData("abc", "abc", 0)]
    public void CompareSemVer(string a, string b, int sign)
    {
        int r = UpdateCheckLogic.CompareSemVer(a, b);
        Assert.Equal(sign, Math.Sign(r));
    }

    [Theory]
    [InlineData("v1.0.3", "v1.0.4", true)]
    [InlineData("v1.0.3", "v2.0", true)]
    [InlineData("v1.0.3", "v1.0.3", false)]      // 同版本不提示
    [InlineData("v1.0.4", "v1.0.3", false)]      // 比当前旧不提示
    [InlineData("v1.0.3", "not-a-version", false)] // 非法 latest 不提示
    [InlineData("v1.0.3", "v1.0.3-beta", false)]   // 同版本预发布不提示
    public void ShouldPrompt_Decision(string current, string? latest, bool expected)
    {
        Assert.Equal(expected, UpdateCheckLogic.ShouldPrompt(current, latest));
    }

    // ---------- 缓存新鲜度（24 小时间隔） ----------

    [Theory]
    [InlineData(0, true)]        // 刚检查过
    [InlineData(23 * 3600 + 59 * 60, true)]   // 23h59m 内
    [InlineData(24 * 3600, false)]            // 恰满 24h：过期
    [InlineData(24 * 3600 + 60, false)]       // 24h01m：过期
    [InlineData(-3600, true)]                 // 时钟回拨（负差值）：按新鲜处理
    public void IsCacheFresh_24hInterval(int offsetSeconds, bool expected)
    {
        Assert.Equal(expected, UpdateCheckLogic.IsCacheFresh(T0, T0.AddSeconds(offsetSeconds)));
    }

    // ---------- GitHub latest release JSON 解析 ----------

    private const string ReleaseJson = """
        {"url":"https://api.github.com/repos/isxulong/BBDown-fixed/releases/99",
         "tag_name":"v1.0.4",
         "name":"v1.0.4",
         "html_url":"https://github.com/isxulong/BBDown-fixed/releases/tag/v1.0.4"}
        """;

    [Fact]
    public void ParseReleaseJson_FullFields()
    {
        var info = UpdateCheckLogic.ParseReleaseJson(ReleaseJson);
        Assert.NotNull(info);
        Assert.Equal("v1.0.4", info.LatestTag);
        Assert.Equal("https://github.com/isxulong/BBDown-fixed/releases/tag/v1.0.4", info.ReleaseUrl);
    }

    [Theory]
    [InlineData("""{"html_url":"https://github.com/x"}""")]          // 缺 tag_name
    [InlineData("""{"tag_name":"v1.0.4"}""")]                          // 缺 html_url
    [InlineData("""{"tag_name":1,"html_url":"https://x"}""")]          // 类型不对
    [InlineData("""{"tag_name":"","html_url":"https://x"}""")]        // 空 tag
    [InlineData("""{"tag_name":"v1","html_url":""}""")]                // 空 url
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseReleaseJson_Invalid_ReturnsNull(string? json)
    {
        Assert.Null(UpdateCheckLogic.ParseReleaseJson(json));
    }

    // ---------- 缓存序列化往返 ----------

    [Fact]
    public void Cache_SerializeRoundtrip()
    {
        var cache = new UpdateCheckCache(T0, new UpdateInfo("v1.0.4", "https://github.com/x"));
        var back = UpdateCheckLogic.DeserializeCache(UpdateCheckLogic.SerializeCache(cache));
        Assert.NotNull(back);
        Assert.Equal(T0, back.CheckedAtUtc);
        Assert.Equal("v1.0.4", back.Info.LatestTag);
        Assert.Equal("https://github.com/x", back.Info.ReleaseUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"checkedAtUtc":"bad","latestTag":"v1","releaseUrl":"u"}""")]
    [InlineData("""{"latestTag":"v1","releaseUrl":"u"}""")]
    public void DeserializeCache_Invalid_ReturnsNull(string? json)
    {
        Assert.Null(UpdateCheckLogic.DeserializeCache(json));
    }

    // ---------- UpdateChecker.CheckAsync（mock 注入决策树） ----------

    private static UpdateChecker Checker(DateTimeOffset nowUtc) => new() { NowUtc = () => nowUtc };

    private static string CacheJson(DateTimeOffset checkedAt, string tag = "v1.0.4") =>
        UpdateCheckLogic.SerializeCache(new UpdateCheckCache(checkedAt, new UpdateInfo(tag, "https://github.com/x")));

    [Fact]
    public async Task CheckAsync_NoCache_FetchNewer_ReturnsInfoAndWritesCache()
    {
        var checker = Checker(T0);
        int fetches = 0;
        string? written = null;
        checker.GetStringOverride = _ => { fetches++; return Task.FromResult<string?>(ReleaseJson); };
        checker.WriteTextOverride = (_, c) => written = c;

        var info = await checker.CheckAsync("v1.0.3", "core\\.update-check.json");
        Assert.Equal(1, fetches);
        Assert.NotNull(info);
        Assert.Equal("v1.0.4", info.LatestTag);
        Assert.NotNull(written);
        // 写入的缓存反序列化后应是本次检查时间 + 最新版本
        var back = UpdateCheckLogic.DeserializeCache(written!);
        Assert.NotNull(back);
        Assert.Equal(T0, back!.CheckedAtUtc);
    }

    [Fact]
    public async Task CheckAsync_NoCache_FetchSameVersion_ReturnsNullButStillCaches()
    {
        // 当前已是最新：不提示，但检查结果照样写缓存（24h 内不再重复请求）
        var checker = Checker(T0);
        string? written = null;
        checker.GetStringOverride = _ => Task.FromResult<string?>(ReleaseJson);
        checker.WriteTextOverride = (_, c) => written = c;

        var info = await checker.CheckAsync("v1.0.4", "core\\.update-check.json");
        Assert.Null(info);
        Assert.NotNull(written);
    }

    [Fact]
    public async Task CheckAsync_FetchFails_SilentNull_NoCacheWritten()
    {
        var checker = Checker(T0);
        bool written = false;
        checker.GetStringOverride = _ => Task.FromResult<string?>(null);   // 网络失败/超时
        checker.WriteTextOverride = (_, _) => written = true;

        Assert.Null(await checker.CheckAsync("v1.0.3", "core\\.update-check.json"));
        Assert.False(written);   // 失败不写缓存：下次启动再试
    }

    [Fact]
    public async Task CheckAsync_HttpThrows_SilentNull()
    {
        var checker = Checker(T0);
        checker.GetStringOverride = _ => throw new HttpRequestException("被墙");
        checker.ReadTextOverride = _ => null;                 // 不落盘：全部走注入
        checker.WriteTextOverride = (_, _) => { };
        Assert.Null(await checker.CheckAsync("v1.0.3", "core\\.update-check.json"));
    }

    [Fact]
    public async Task CheckAsync_FreshCache_NoHttpRequest()
    {
        // 24h 内成功检查过：直接按缓存判定，不再发请求
        var checker = Checker(T0.AddHours(12));
        int fetches = 0;
        checker.GetStringOverride = _ => { fetches++; return Task.FromResult<string?>(ReleaseJson); };
        checker.ReadTextOverride = _ => CacheJson(T0);

        var info = await checker.CheckAsync("v1.0.3", "core\\.update-check.json");
        Assert.Equal(0, fetches);
        Assert.NotNull(info);
        Assert.Equal("v1.0.4", info.LatestTag);
    }

    [Fact]
    public async Task CheckAsync_FreshCache_CachedVersionNotNewer_ReturnsNullNoFetch()
    {
        var checker = Checker(T0.AddHours(12));
        int fetches = 0;
        checker.GetStringOverride = _ => { fetches++; return Task.FromResult<string?>(ReleaseJson); };
        checker.ReadTextOverride = _ => CacheJson(T0, tag: "v1.0.2");   // 缓存里是更旧版本

        Assert.Null(await checker.CheckAsync("v1.0.3", "core\\.update-check.json"));
        Assert.Equal(0, fetches);
    }

    [Fact]
    public async Task CheckAsync_StaleCache_RefetchesAndRewrites()
    {
        // 超过 24h：重新请求并刷新缓存时间戳
        var checker = Checker(T0.AddHours(25));
        int fetches = 0;
        DateTimeOffset? writtenAt = null;
        checker.GetStringOverride = _ => { fetches++; return Task.FromResult<string?>(ReleaseJson); };
        checker.ReadTextOverride = _ => CacheJson(T0);
        checker.WriteTextOverride = (_, c) =>
            writtenAt = UpdateCheckLogic.DeserializeCache(c)!.CheckedAtUtc;

        var info = await checker.CheckAsync("v1.0.3", "core\\.update-check.json");
        Assert.Equal(1, fetches);
        Assert.NotNull(info);
        Assert.Equal(T0.AddHours(25), writtenAt);
    }

    [Fact]
    public async Task CheckAsync_CacheReadThrows_DegradesToFetch()
    {
        // 缓存读失败（占用/权限）：静默按无缓存处理，照常请求
        var checker = Checker(T0);
        checker.ReadTextOverride = _ => throw new IOException("文件被占用");
        checker.GetStringOverride = _ => Task.FromResult<string?>(ReleaseJson);
        checker.WriteTextOverride = (_, _) => { };          // 不落盘：全部走注入

        var info = await checker.CheckAsync("v1.0.3", "core\\.update-check.json");
        Assert.NotNull(info);
        Assert.Equal("v1.0.4", info.LatestTag);
    }

    [Fact]
    public async Task CheckAsync_CacheWriteThrows_StillReturnsInfo()
    {
        // 缓存写失败：不影响本次结论，只是下次启动会再请求一次
        var checker = Checker(T0);
        checker.GetStringOverride = _ => Task.FromResult<string?>(ReleaseJson);
        checker.WriteTextOverride = (_, _) => throw new IOException("只读目录");

        var info = await checker.CheckAsync("v1.0.3", "core\\.update-check.json");
        Assert.NotNull(info);
        Assert.Equal("v1.0.4", info.LatestTag);
    }

    [Fact]
    public async Task CheckAsync_CorruptCache_TreatedAsMissing()
    {
        var checker = Checker(T0);
        int fetches = 0;
        checker.ReadTextOverride = _ => "垃圾内容{{ not json";
        checker.GetStringOverride = _ => { fetches++; return Task.FromResult<string?>(ReleaseJson); };
        checker.WriteTextOverride = (_, _) => { };          // 不落盘：全部走注入

        var info = await checker.CheckAsync("v1.0.3", "core\\.update-check.json");
        Assert.Equal(1, fetches);
        Assert.NotNull(info);
    }

    // ---------- 真实 IO（缓存文件落地） ----------

    [Fact]
    public async Task CheckAsync_RealFileCache_WrittenThenReadWithoutFetch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bbdh-test-" + Guid.NewGuid().ToString("N"));
        string path = CoreLayout.UpdateCheckCachePath(dir);
        try
        {
            var checker = Checker(T0);
            checker.GetStringOverride = _ => Task.FromResult<string?>(ReleaseJson);
            Assert.NotNull(await checker.CheckAsync("v1.0.3", path));   // 首次：请求 + 落盘
            Assert.True(File.Exists(path));

            var checker2 = Checker(T0.AddHours(1));   // 命中缓存的另一次启动
            int fetches = 0;
            checker2.GetStringOverride = _ => { fetches++; return Task.FromResult<string?>(ReleaseJson); };
            var info = await checker2.CheckAsync("v1.0.3", path);
            Assert.Equal(0, fetches);                 // 缓存新鲜：未发请求
            Assert.NotNull(info);                     // 缓存里是更新版本：照常提示
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
