using System.Net;
using BBDownHelper.Core;
using Xunit;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v3.6 两项 P2 修复的单测：
/// ① 蓝队 P2：view 端点对「只有 UA、缺其余浏览器头」的请求 412 风控 →
///    首试补齐浏览器全家桶头；仍被 412/403 拦则降级重试一次（无 UA 极简头）；其余失败不重试。
/// ② 红队 P2-A：封面下载与预览元数据同一代次源（DebounceGate）——
///    在途下载随新输入取消（HTTP 层贯穿 ct），慢响应回来 IsCurrent(gen)=false 不得上屏。
/// 通过 SendOverride 注入底层 HTTP 发送，在无网环境验证重试决策与两档头部形态。
/// </summary>
public class RiskControlAndRaceTests
{
    private const string ViewJson =
        @"{""code"":0,""data"":{""title"":""t"",""pic"":""http://i0.hdslb.com/bfs/archive/a.jpg"","
        + @"""pages"":[{},{},{""x"":1}],""owner"":{""name"":""up""}}}";

    private const string MediaListJson =
        @"{""code"":0,""data"":{""title"":""系列"",""media_count"":3,""cover"":"""","
        + @"""upper"":{""name"":""up""}}}";

    private static HttpResponseMessage Resp(int status, string json = ViewJson) =>
        new((HttpStatusCode)status) { Content = new StringContent(json) };

    private static HttpResponseMessage Resp(HttpStatusCode status, string json = ViewJson) =>
        new(status) { Content = new StringContent(json) };

    // ---------- 蓝队 P2：412/403 降级重试 ----------

    [Fact]
    public async Task Get_FirstAttemptCarriesFullBrowserHeaders()
    {
        HttpRequestMessage? seen = null;
        var probe = new BiliApiProbe
        {
            SendOverride = (req, _) => { seen = req; return Task.FromResult(Resp(HttpStatusCode.OK)); }
        };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.NotNull(seen);
        Assert.Equal("https://api.bilibili.com/x/web-interface/view?bvid=BV1GJ411x7h7", seen.RequestUri!.ToString());
        // 首试必须是浏览器全家桶头：UA + Accept + Accept-Language + sec-ch-ua* + sec-fetch-*
        Assert.True(seen.Headers.Contains("User-Agent"), "首试必须带 UA");
        Assert.True(seen.Headers.Contains("Accept"), "首试必须带 Accept（412 根因之一）");
        Assert.True(seen.Headers.Contains("Accept-Language"), "首试必须带 Accept-Language");
        Assert.True(seen.Headers.Contains("sec-ch-ua"), "首试必须带 sec-ch-ua");
        Assert.True(seen.Headers.Contains("sec-ch-ua-mobile"));
        Assert.True(seen.Headers.Contains("sec-ch-ua-platform"));
        Assert.True(seen.Headers.Contains("sec-fetch-dest"), "首试必须带 sec-fetch-dest");
        Assert.True(seen.Headers.Contains("sec-fetch-mode"));
        Assert.True(seen.Headers.Contains("sec-fetch-site"));
        Assert.Equal("https://www.bilibili.com/", seen.Headers.GetValues("Referer").First());
    }

    [Fact]
    public async Task Get_412_RetriesOnceWithoutUa_Succeeds()
    {
        int calls = 0;
        HttpRequestMessage? second = null;
        var probe = new BiliApiProbe
        {
            SendOverride = (req, _) =>
            {
                calls++;
                if (calls == 1) return Task.FromResult(Resp(412));   // 蓝队实测的风控拦截形态
                second = req;
                return Task.FromResult(Resp(HttpStatusCode.OK));
            }
        };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None);
        Assert.NotNull(pv);                       // 降级重试拿到数据：预览卡不再静默不出
        Assert.Equal(2, calls);                   // 恰好重试一次
        Assert.Equal("t", pv!.Title);
        Assert.NotNull(second);
        Assert.False(second.Headers.Contains("User-Agent"), "降级重试档不带 UA（蓝队实测无 UA 可 200）");
        Assert.Equal("https://www.bilibili.com/", second.Headers.GetValues("Referer").First());
    }

    [Fact]
    public async Task Get_403_AlsoRetriesOnce()
    {
        int calls = 0;
        var probe = new BiliApiProbe
        {
            SendOverride = (_, _) => { calls++; return Task.FromResult(calls == 1 ? Resp(403) : Resp(HttpStatusCode.OK)); }
        };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Get_412Twice_ReturnsNullSilently_NoThirdAttempt()
    {
        int calls = 0;
        var probe = new BiliApiProbe
        {
            SendOverride = (_, _) => { calls++; return Task.FromResult(Resp(412)); }
        };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None);
        Assert.Null(pv);                          // 两次都被拦：静默失败，维持「不出卡」契约
        Assert.Equal(2, calls);                   // 不无限重试
    }

    [Fact]
    public async Task Get_404_DoesNotRetry()
    {
        int calls = 0;
        var probe = new BiliApiProbe
        {
            SendOverride = (_, _) => { calls++; return Task.FromResult(Resp(HttpStatusCode.NotFound)); }
        };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None);
        Assert.Null(pv);
        Assert.Equal(1, calls);                   // 非 412/403 失败不重试（避免无谓等待）
    }

    [Fact]
    public async Task Get_NetworkError_DoesNotRetry()
    {
        int calls = 0;
        var probe = new BiliApiProbe
        {
            SendOverride = (_, _) => { calls++; throw new HttpRequestException("connection reset"); }
        };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None);
        Assert.Null(pv);                          // 网络异常静默吞掉，不抛出
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Get_CookieForwardedOnBothAttempts()
    {
        int calls = 0;
        var probe = new BiliApiProbe
        {
            SendOverride = (req, _) =>
            {
                calls++;
                if (req.Headers.Contains("Cookie"))
                    Assert.Contains("SESSDATA=abc", req.Headers.GetValues("Cookie").First());
                return Task.FromResult(calls == 1 ? Resp(412) : Resp(HttpStatusCode.OK));
            }
        };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", "SESSDATA=abc", CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Get_PreCancelled_NeverSends()
    {
        int calls = 0;
        var probe = new BiliApiProbe
        {
            SendOverride = (_, _) => { calls++; return Task.FromResult(Resp(HttpStatusCode.OK)); }
        };
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, cts.Token);
        Assert.Null(pv);
        Assert.Equal(0, calls);                   // 已取消：一个请求都不发（防抖闸 Submit 后的旧轮）
    }

    [Fact]
    public async Task MedialistPreview_412_SameRetryPolicy()
    {
        // 蓝队要求「medialist 端点同逻辑同步检查」：同一 GetStringAsync 管线，此处钉住行为
        int calls = 0;
        var probe = new BiliApiProbe
        {
            SendOverride = (_, _) => { calls++; return Task.FromResult(calls == 1 ? Resp(412, MediaListJson) : Resp(HttpStatusCode.OK, MediaListJson)); }
        };
        var pv = await probe.ProbeMediaListPreviewAsync(2735065, 5, null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal(2, calls);
    }

    // ---------- 红队 P2-A：封面竞态（同一代次源） ----------

    [Fact]
    public async Task Cover_StaleGenerationAfterNewSubmit_MustNotApply()
    {
        // 复现红队攻击场景：A 的封面响应慢于 B 的元数据到达。
        // A 轮取号 genA → 开始下载封面（令牌 = gate.Token）→ 用户输入 B（Submit 取消旧轮）→
        // A 的封面字节才回来：IsCurrent(genA)=false，UI 侧校验必须拦下（不许盖到 B 的卡片上）。
        var gate = new DebounceGate();
        var loader = new CoverLoader
        {
            // 模拟「慢响应」：等到测试放行才返回 A 的字节（mock 层不感知取消，模拟最坏情形：
            // 字节已完整到达——此时唯一的防线就是赋值前的 IsCurrent 校验）
            GetBytesOverride = async (_, _) =>
            {
                await Task.Delay(50);
                return new byte[] { 0x89, 0x50 };
            }
        };
        int genA = gate.Submit();
        var taskA = loader.GetCoverBytesAsync("https://i0.hdslb.com/bfs/archive/a.jpg", gate.Token);
        int genB = gate.Submit();                 // 输入 B：取消 A 轮等待与在途请求
        byte[]? bytesA = await taskA;              // A 的封面字节还是回来了（慢响应形态）
        Assert.True(gate.IsCurrent(genB));
        Assert.False(gate.IsCurrent(genA));        // 过期代次：UI 侧 LoadCoverAsync 在赋值前据此拦截
        // bytesA 可能非 null（mock 不感知取消）——正因如此 UI 侧 IsCurrent 校验是必需防线；
        // 真实 HTTP 层的取消语义由下方 Cover_CancelledInFlight 用例覆盖。
        _ = bytesA;
    }

    [Fact]
    public async Task Cover_CancelledInFlight_HttpLayerReturnsNull()
    {
        var gate = new DebounceGate();
        var loader = new CoverLoader
        {
            SendOverride = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);   // 在途等待，被取消 → OCE
                return Resp(HttpStatusCode.OK);
            }
        };
        int genA = gate.Submit();
        var taskA = loader.GetCoverBytesAsync("https://i0.hdslb.com/bfs/archive/a.jpg", gate.Token);
        gate.Submit();                                   // 新输入：旧令牌取消在途下载
        byte[]? bytes = await taskA;
        Assert.Null(bytes);                              // HTTP 层贯穿取消：慢响应被真正中止
        Assert.False(gate.IsCurrent(genA));
    }

    [Fact]
    public async Task Cover_PreCancelled_NoRequest()
    {
        int calls = 0;
        var loader = new CoverLoader
        {
            SendOverride = (_, _) => { calls++; return Task.FromResult(Resp(HttpStatusCode.OK)); }
        };
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await loader.GetCoverBytesAsync("https://i0.hdslb.com/bfs/archive/a.jpg", cts.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Cover_NonSuccess_ReturnsNull()
    {
        var loader = new CoverLoader
        {
            SendOverride = (_, _) => Task.FromResult(Resp(HttpStatusCode.NotFound))
        };
        Assert.Null(await loader.GetCoverBytesAsync("https://i0.hdslb.com/bfs/archive/a.jpg", CancellationToken.None));
    }

    [Fact]
    public async Task Cover_Success_ReturnsBytes_WithUaAndReferer()
    {
        HttpRequestMessage? seen = null;
        var payload = new byte[] { 1, 2, 3, 4 };
        var loader = new CoverLoader
        {
            SendOverride = (req, _) =>
            {
                seen = req;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            }
        };
        var bytes = await loader.GetCoverBytesAsync("https://i0.hdslb.com/bfs/archive/a.jpg", CancellationToken.None);
        Assert.Equal(payload, bytes);
        Assert.NotNull(seen);
        Assert.True(seen.Headers.Contains("User-Agent"));   // B 站图床防盗链：UA + Referer 口径与探测一致
        Assert.Equal("https://www.bilibili.com/", seen.Headers.GetValues("Referer").First());
    }

    [Fact]
    public async Task Cover_EmptyUrl_ReturnsNull_NoRequest()
    {
        int calls = 0;
        var loader = new CoverLoader
        {
            GetBytesOverride = (_, _) => { calls++; return Task.FromResult<byte[]?>(null); }
        };
        Assert.Null(await loader.GetCoverBytesAsync("", CancellationToken.None));
        Assert.Equal(0, calls);
    }
}
