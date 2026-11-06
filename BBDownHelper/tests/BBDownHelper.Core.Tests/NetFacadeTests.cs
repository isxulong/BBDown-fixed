using System.Net;
using BBDownHelper.Core;
using Xunit;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v3.7 NetFacade 薄层（架构 §1）单测：
///  - 共享 HttpClient + 统一 UA / 超时常量（全组件唯一来源）；
///  - 412 全局冷却：任一组件收 412 → 冷却置位（60s 窗口），冷却期内各组件降级；
///  - 降级形态：探测跳过全头首试（极简头起手）、封面/检查更新直接不发请求；
///  - 冷却到期后恢复全头首试；跨组件冷却共享（同一 facade 实例）。
/// </summary>
public class NetFacadeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage Resp(int status, string json = "{}") =>
        new((HttpStatusCode)status) { Content = new StringContent(json) };

    private static NetFacade FacadeAt(DateTimeOffset now) => new() { NowUtc = () => now };

    // ---------- 常量口径：UA / 超时全组件唯一来源 ----------

    [Fact]
    public void UA_SingleSource_ProbeAliasEqualsFacade()
    {
        Assert.Equal(NetFacade.UserAgent, BiliApiProbe.UserAgent);
        Assert.Contains("Chrome/", NetFacade.UserAgent);   // Chrome 桌面端口径
    }

    [Fact]
    public void TimeoutConstants_Consistent()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), NetFacade.DefaultTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), NetFacade.RiskCooldownDuration);
        Assert.Equal(NetFacade.DefaultTimeout, NetPolicy.Default.Timeout);
        Assert.True(NetPolicy.Default.AllowRedirect);
        Assert.Equal(NetFacade.DefaultTimeout, NetPolicy.NoRedirect.Timeout);
        Assert.False(NetPolicy.NoRedirect.AllowRedirect);
        // v3.5 规格例外：检查更新 5s，经 NetPolicy 显式传入
        Assert.Equal(5, UpdateChecker.TimeoutSeconds);
    }

    // ---------- 412 → 全局冷却 ----------

    [Fact]
    public void CooldownWindow_59sIn_60sBoundaryOut_61sOut()
    {
        var f = FacadeAt(T0);
        Assert.False(f.InRiskCooldown);
        f.Report412();
        Assert.True(f.InRiskCooldown);                       // t0：冷却开始
        f.NowUtc = () => T0.AddSeconds(59);
        Assert.True(f.InRiskCooldown);                       // 59s：仍在窗口内
        f.NowUtc = () => T0.AddSeconds(60);
        Assert.False(f.InRiskCooldown);                      // 60s：到达边界即出窗
        f.NowUtc = () => T0.AddSeconds(61);
        Assert.False(f.InRiskCooldown);                       // 61s：出窗
    }

    [Fact]
    public async Task SendAsync_412Response_AutoSetsCooldown()
    {
        var f = FacadeAt(T0);
        f.SendOverride = (_, _, _) => Task.FromResult(Resp(412));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x");
        var resp = await f.SendAsync(req, NetPolicy.Default, CancellationToken.None);
        Assert.NotNull(resp);
        Assert.Equal(412, (int)resp!.StatusCode);
        Assert.True(f.InRiskCooldown);                        // 412 响应自动触发全局冷却
    }

    [Fact]
    public async Task SendAsync_200_DoesNotSetCooldown()
    {
        var f = FacadeAt(T0);
        f.SendOverride = (_, _, _) => Task.FromResult(Resp(200));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x");
        await f.SendAsync(req, NetPolicy.Default, CancellationToken.None);
        Assert.False(f.InRiskCooldown);
    }

    [Fact]
    public async Task SendAsync_PreCancelled_ReturnsNull_NoSend()
    {
        var f = FacadeAt(T0);
        int calls = 0;
        f.SendOverride = (_, _, _) => { calls++; return Task.FromResult(Resp(200)); };
        var cts = new CancellationTokenSource();
        cts.Cancel();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x");
        Assert.Null(await f.SendAsync(req, NetPolicy.Default, cts.Token));
        Assert.Equal(0, calls);                              // 已取消：一个请求都不发
    }

    // ---------- 冷却期内：探测降级（极简头起手，不再全头触线） ----------

    [Fact]
    public async Task Probe_DuringCooldown_SingleAttempt_MinimalHeaders()
    {
        var f = FacadeAt(T0);
        f.Report412();
        int calls = 0;
        HttpRequestMessage? seen = null;
        var probe = new BiliApiProbe(f)
        {
            SendOverride = (req, _) =>
            {
                calls++;
                seen = req;
                return Task.FromResult(Resp(200,
                    "{\"code\":0,\"data\":{\"title\":\"t\",\"pic\":\"\",\"pages\":[],\"owner\":{\"name\":\"up\"}}}"));
            }
        };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal(1, calls);                              // 降级档起手成功：只发一次
        Assert.NotNull(seen);
        Assert.False(seen!.Headers.Contains("User-Agent"), "冷却期降级档不带 UA（风控期内不再全头触线）");
        Assert.Equal("https://www.bilibili.com/", seen.Headers.GetValues("Referer").First());
    }

    [Fact]
    public async Task Probe_DuringCooldown_412Again_NoRetry_SilentNull()
    {
        var f = FacadeAt(T0);
        f.Report412();
        int calls = 0;
        var probe = new BiliApiProbe(f)
        {
            SendOverride = (_, _) => { calls++; return Task.FromResult(Resp(412)); }
        };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None);
        Assert.Null(pv);
        Assert.Equal(1, calls);                              // 降级档仍被拦：不再重试（冷却期不反复触线）
    }

    // ---------- 冷却到期：恢复全头首试 ----------

    [Fact]
    public async Task Probe_AfterCooldownExpiry_ResumesFullHeaders()
    {
        var f = FacadeAt(T0);
        f.Report412();
        f.NowUtc = () => T0.AddSeconds(61);                   // 冷却出窗
        int calls = 0;
        HttpRequestMessage? seen = null;
        var probe = new BiliApiProbe(f)
        {
            SendOverride = (req, _) =>
            {
                calls++;
                seen = req;
                return Task.FromResult(Resp(200,
                    "{\"code\":0,\"data\":{\"title\":\"t\",\"pic\":\"\",\"pages\":[],\"owner\":{\"name\":\"up\"}}}"));
            }
        };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal(1, calls);
        Assert.NotNull(seen);
        Assert.True(seen!.Headers.Contains("User-Agent"), "冷却出窗后恢复全头首试");
        Assert.Equal(NetFacade.UserAgent, string.Join(" ", seen.Headers.GetValues("User-Agent"))); // TryAddWithoutValidation 会按空格拆段，拼回原串断言
    }

    // ---------- 跨组件冷却：探测 412 → 封面不发请求 → 检查更新不发请求 ----------

    [Fact]
    public async Task CrossComponent_Probe412_SetsCooldown_CoverLoaderSkipsRequest()
    {
        var f = FacadeAt(T0);
        var probe = new BiliApiProbe(f)
        {
            SendOverride = (_, _) => Task.FromResult(Resp(412))
        };
        Assert.Null(await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None));
        Assert.True(f.InRiskCooldown);                        // 探测组件的 412 已置全局冷却

        int coverCalls = 0;
        var loader = new CoverLoader(f)
        {
            SendOverride = (_, _) => { coverCalls++; return Task.FromResult(Resp(200)); }
        };
        Assert.Null(await loader.GetCoverBytesAsync("https://i0.hdslb.com/bfs/archive/a.jpg", CancellationToken.None));
        Assert.Equal(0, coverCalls);                          // 冷却期内封面组件直接不发请求
    }

    [Fact]
    public async Task CoverLoader_InCooldown_ReturnsNull_NoFacadeSend()
    {
        var f = FacadeAt(T0);
        f.Report412();
        int calls = 0;
        f.SendOverride = (_, _, _) => { calls++; return Task.FromResult(Resp(200)); };
        var loader = new CoverLoader(f);
        Assert.Null(await loader.GetCoverBytesAsync("https://i0.hdslb.com/bfs/archive/a.jpg", CancellationToken.None));
        Assert.Equal(0, calls);                               // 冷却降级在组件层，根本不进 SendAsync
    }

    [Fact]
    public async Task UpdateChecker_InCooldown_NoRequest_NoCacheWrite()
    {
        var f = FacadeAt(T0);
        f.Report412();
        int gets = 0, writes = 0;
        var checker = new UpdateChecker(f)
        {
            NowUtc = () => T0,
            GetStringOverride = _ => { gets++; return Task.FromResult<string?>("{}"); },
            ReadTextOverride = _ => null,
            WriteTextOverride = (_, _) => writes++
        };
        Assert.Null(await checker.CheckAsync("1.0.3", "core/.update-check.json"));
        Assert.Equal(0, gets);                                // 冷却期内检查更新静默降级：不发请求
        Assert.Equal(0, writes);                               // 不写缓存（下次启动再试）
    }

    [Fact]
    public async Task UpdateChecker_CooldownExpiry_ResumesRequest()
    {
        var f = FacadeAt(T0);
        f.Report412();
        f.NowUtc = () => T0.AddSeconds(61);                   // 出窗
        int gets = 0;
        var checker = new UpdateChecker(f)
        {
            NowUtc = () => T0.AddSeconds(61),
            GetStringOverride = _ => { gets++; return Task.FromResult<string?>(null); },
            ReadTextOverride = _ => null,
            WriteTextOverride = (_, _) => { }
        };
        Assert.Null(await checker.CheckAsync("1.0.3", "core/.update-check.json"));
        Assert.Equal(1, gets);                                // 出窗后恢复请求（null 响应静默、不写缓存）
    }

    // ---------- UpdateChecker 真实 HTTP 路径走门面（UA 口径一致） ----------

    [Fact]
    public async Task UpdateChecker_FetchPath_GoesThroughFacade_WithFacadeUa()
    {
        var f = FacadeAt(T0);
        HttpRequestMessage? seen = null;
        f.SendOverride = (req, _, _) =>
        {
            seen = req;
            return Task.FromResult(Resp(200,
                "{\"tag_name\":\"v9.9.9\",\"html_url\":\"https://github.com/isxulong/BBDown-fixed/releases/tag/v9.9.9\"}"));
        };
        int writes = 0;
        var checker = new UpdateChecker(f)
        {
            NowUtc = () => T0,
            ReadTextOverride = _ => null,
            WriteTextOverride = (_, _) => writes++
        };
        var info = await checker.CheckAsync("1.0.3", "core/.update-check.json");
        Assert.NotNull(info);                                 // v9.9.9 > 1.0.3：应提示
        Assert.Equal("v9.9.9", info!.LatestTag);
        Assert.NotNull(seen);
        Assert.Equal(UpdateChecker.LatestReleaseApi, seen!.RequestUri!.ToString());
        Assert.Equal(NetFacade.UserAgent, string.Join(" ", seen.Headers.GetValues("User-Agent"))); // TryAddWithoutValidation 会按空格拆段，拼回原串断言 // UA 唯一来源口径
        Assert.Equal(1, writes);                              // 成功后写缓存
    }
}
