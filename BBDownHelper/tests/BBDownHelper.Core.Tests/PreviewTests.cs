using BBDownHelper.Core;
using Xunit;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v3.4 预览卡：输入识别后自动联网取标题+封面。
/// 覆盖：view 接口 bvid/aid 双形态、pic/pages 字段解析、http→https 规范、
/// code != 0 温和提示、网络失败静默、medialist 合集/系列预览、取消语义、防抖闸。
/// </summary>
public class PreviewTests
{
    // ---------- view URL 形态（bvid 支持） ----------

    [Theory]
    [InlineData("BV1GJ411x7h7", "https://api.bilibili.com/x/web-interface/view?bvid=BV1GJ411x7h7")]
    [InlineData("av170001", "https://api.bilibili.com/x/web-interface/view?aid=170001")]
    [InlineData("", null)]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", null)]   // 不是裸 BV/av 形态：不探测
    [InlineData("av", null)]
    public void ViewUrlFor_BvAndAvForms(string input, string? expected)
    {
        Assert.Equal(expected, BiliApiProbe.ViewUrlFor(input));
    }

    // ---------- view 响应解析 ----------

    private const string ViewJson = """
        {"code":0,"data":{"title":"测试视频标题","pic":"http://i1.hdslb.com/bfs/archive/abc.jpg",
        "pages":[{"cid":1},{"cid":2},{"cid":3}],"owner":{"name":"测试UP"}}}
        """;

    [Fact]
    public void ParseView_FullFields_TitleCoverPagesOwner()
    {
        var pv = BiliApiProbe.ParseViewPreview(ViewJson);
        Assert.NotNull(pv);
        Assert.False(pv.NotFound);
        Assert.Equal(InputKind.SingleVideo, pv.Kind);
        Assert.Equal("测试视频标题", pv.Title);
        Assert.Equal("https://i1.hdslb.com/bfs/archive/abc.jpg", pv.CoverUrl);   // http→https
        Assert.Equal(3, pv.PageCount);
        Assert.Equal("测试UP", pv.UpName);
    }

    [Fact]
    public void ParseView_HttpsPic_KeptAsIs()
    {
        string json = """{"code":0,"data":{"title":"t","pic":"https://i0.hdslb.com/x.jpg","owner":{}}}""";
        var pv = BiliApiProbe.ParseViewPreview(json);
        Assert.NotNull(pv);
        Assert.Equal("https://i0.hdslb.com/x.jpg", pv.CoverUrl);
    }

    [Fact]
    public void ParseView_MissingPicAndPages_EmptyCoverZeroPages()
    {
        // 接口没给 pic / pages：封面空串（UI 显示占位色块）、分P 数 0
        string json = """{"code":0,"data":{"title":"t","owner":{"name":"u"}}}""";
        var pv = BiliApiProbe.ParseViewPreview(json);
        Assert.NotNull(pv);
        Assert.Equal("", pv.CoverUrl);
        Assert.Equal(0, pv.PageCount);
    }

    [Theory]
    [InlineData("""{"code":-400,"message":"请求错误"}""")]       // 不存在的 BV（2026-11-04 实测）
    [InlineData("""{"code":-403,"message":"访问权限不足"}""")]    // 权限不足同样视为「可能不存在」
    public void ParseView_NonZeroCode_NotFound(string json)
    {
        var pv = BiliApiProbe.ParseViewPreview(json);
        Assert.NotNull(pv);
        Assert.True(pv.NotFound);
    }

    [Theory]
    [InlineData(null)]                     // 网络失败：静默
    [InlineData("")]
    [InlineData("not json")]               // JSON 损坏：静默
    [InlineData("""{"code":0,"data":null}""")]   // data null：静默
    public void ParseView_BadPayload_NullSilent(string? json)
    {
        Assert.Null(BiliApiProbe.ParseViewPreview(json));
    }

    // ---------- 联网探测（mock 注入） ----------

    [Fact]
    public async Task ProbeViewPreview_BvInput_UsesBvidEndpoint()
    {
        string? calledUrl = null;
        var probe = new BiliApiProbe
        {
            GetStringOverride = (url, _) => { calledUrl = url; return Task.FromResult<string?>(ViewJson); }
        };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Contains("view?bvid=BV1GJ411x7h7", calledUrl);
        Assert.Equal("测试视频标题", pv.Title);
    }

    [Fact]
    public async Task ProbeViewPreview_AvInput_UsesAidEndpoint()
    {
        string? calledUrl = null;
        var probe = new BiliApiProbe
        {
            GetStringOverride = (url, _) => { calledUrl = url; return Task.FromResult<string?>(ViewJson); }
        };
        var pv = await probe.ProbeViewPreviewAsync("av80433022", null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Contains("view?aid=80433022", calledUrl);
    }

    [Fact]
    public async Task ProbeViewPreview_Cancelled_NetworkNullSilent()
    {
        var probe = new BiliApiProbe
        {
            GetStringOverride = (_, _) => throw new InvalidOperationException("取消后不得发起网络请求")
        };
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, cts.Token);
        Assert.Null(pv);   // 静默不显示
    }

    [Fact]
    public async Task ProbeViewPreview_NetworkFail_NullSilent()
    {
        var probe = new BiliApiProbe { GetStringOverride = (_, _) => Task.FromResult<string?>(null) };
        var pv = await probe.ProbeViewPreviewAsync("BV1GJ411x7h7", null, CancellationToken.None);
        Assert.Null(pv);
    }

    // ---------- medialist 合集/系列预览 ----------

    [Fact]
    public async Task ProbeMediaListPreview_CoverAndUpperName()
    {
        // 2026-11-04 实测口径：data.cover（可能空串）、data.upper.name、data.media_count
        string json = """
            {"code":0,"data":{"title":"睡前消息","media_count":857,
            "cover":"http://i0.hdslb.com/bfs/archive/ml.jpg","upper":{"name":"马督工"}}}
            """;
        var probe = new BiliApiProbe
        {
            GetStringOverride = (url, _) => url.Contains("medialist")
                ? Task.FromResult<string?>(json)
                : Task.FromResult<string?>(null)
        };
        var pv = await probe.ProbeMediaListPreviewAsync(2735065, 5, null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal(InputKind.Series, pv.Kind);
        Assert.Equal("睡前消息", pv.Title);
        Assert.Equal(857, pv.MediaCount);
        Assert.Equal("https://i0.hdslb.com/bfs/archive/ml.jpg", pv.CoverUrl);
        Assert.Equal("马督工", pv.UpName);
    }

    [Fact]
    public async Task ProbeMediaListPreview_EmptyCover_NoCrash()
    {
        // 实测部分系列 cover 为空串：CoverUrl 空即可（UI 占位色块）
        string json = """{"code":0,"data":{"title":"某系列","media_count":10,"cover":"","upper":{}}}""";
        var probe = new BiliApiProbe { GetStringOverride = (_, _) => Task.FromResult<string?>(json) };
        var pv = await probe.ProbeMediaListPreviewAsync(123, 5, null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal("", pv.CoverUrl);
        Assert.Equal("", pv.UpName);
    }

    [Fact]
    public async Task ProbeMediaListPreview_DataNull_FailSilent()
    {
        var probe = new BiliApiProbe { GetStringOverride = (_, _) => Task.FromResult<string?>(@"{""code"":0,""data"":null}") };
        var pv = await probe.ProbeMediaListPreviewAsync(987654321, 8, null, CancellationToken.None);
        Assert.Null(pv);
    }

    // ---------- PreviewService 门面：哪些识别结果出预览 ----------

    private static RecognitionResult R(InputKind kind, string final) =>
        new() { Kind = kind, FinalInput = final };

    [Fact]
    public async Task GetPreview_BvVideo_UsesBvidView()
    {
        string? called = null;
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (url, _) => { called = url; return Task.FromResult<string?>(ViewJson); }
        });
        var pv = await svc.GetPreviewAsync(R(InputKind.SingleVideo, "BV1GJ411x7h7"), null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Contains("view?bvid=", called);
    }

    [Fact]
    public async Task GetPreview_Collection_SidFromUrl()
    {
        string? called = null;
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (url, _) =>
            {
                called = url;
                return Task.FromResult<string?>(@"{""code"":0,""data"":{""title"":""合集X"",""media_count"":7,""cover"":"""",""upper"":{""name"":""UP""}}}");
            }
        });
        var pv = await svc.GetPreviewAsync(
            R(InputKind.Collection, "https://www.bilibili.com/channel/collectiondetail?sid=4405454"),
            null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal(InputKind.Collection, pv.Kind);
        Assert.Equal(7, pv.MediaCount);
        Assert.Contains("type=8&biz_id=4405454", called);
    }

    [Fact]
    public async Task GetPreview_Series_UsesType5()
    {
        string? called = null;
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (url, _) =>
            {
                called = url;
                return Task.FromResult<string?>(@"{""code"":0,""data"":{""title"":""系列Y"",""media_count"":857,""upper"":{}}}");
            }
        });
        var pv = await svc.GetPreviewAsync(
            R(InputKind.Series, "https://www.bilibili.com/channel/seriesdetail?sid=2735065"),
            null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal(InputKind.Series, pv.Kind);
        Assert.Contains("type=5&biz_id=2735065", called);
    }

    [Theory]
    [InlineData(InputKind.UpSpace, "https://space.bilibili.com/123")]        // UP 主主页：沿用现有引导，不出卡
    [InlineData(InputKind.Unknown, "")]
    public async Task GetPreview_OtherKinds_NullNoProbe(InputKind kind, string final)
    {
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (_, _) => throw new InvalidOperationException("该类型不得发起预览请求")
        });
        Assert.Null(await svc.GetPreviewAsync(R(kind, final), null, CancellationToken.None));
    }

    [Fact]
    public async Task GetPreview_NeedsUserChoice_NoPreviewBeforeSelection()
    {
        // 裸数字多候选：用户选定前不出预览卡（红线）
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (_, _) => throw new InvalidOperationException("多候选未选定不得预览")
        });
        var r = R(InputKind.BareNumber, "");
        r.NeedsUserChoice = true;
        r.Candidates.Add(new NumberCandidate { Kind = InputKind.Series, FinalInput = "https://www.bilibili.com/channel/seriesdetail?sid=1" });
        Assert.Null(await svc.GetPreviewAsync(r, null, CancellationToken.None));
    }

    [Fact]
    public async Task GetPreview_CancelledToken_NullWithoutProbe()
    {
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (_, _) => throw new InvalidOperationException("取消后不得发起预览请求")
        });
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await svc.GetPreviewAsync(R(InputKind.SingleVideo, "BV1GJ411x7h7"), null, cts.Token));
    }

    [Fact]
    public async Task GetPreview_CollectionWithoutSid_NullSilent()
    {
        // 链接里取不到 sid：静默不出卡
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (_, _) => throw new InvalidOperationException("无 sid 不得请求")
        });
        Assert.Null(await svc.GetPreviewAsync(R(InputKind.Collection, "https://www.bilibili.com/"), null, CancellationToken.None));
    }

    [Fact]
    public async Task GetPreview_ProbeThrows_SwallowsException()
    {
        // 纯增强红线：探测内部任何异常必须吞掉，不得影响主流程
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (_, _) => throw new HttpRequestException("网络崩了")
        });
        Assert.Null(await svc.GetPreviewAsync(R(InputKind.SingleVideo, "BV1GJ411x7h7"), null, CancellationToken.None));
    }

    // ---------- 防抖闸（DebounceGate） ----------

    [Fact]
    public async Task Debounce_QuietWindow_ElapsesAndCurrent()
    {
        var gate = new DebounceGate((ms, ct) => Task.Delay(ms, ct));
        int g = gate.Submit();
        Assert.True(await gate.ElapsedAsync(g, 30));
        Assert.True(gate.IsCurrent(g));
    }

    [Fact]
    public async Task Debounce_NewInputWithinWindow_OldRoundInvalidated()
    {
        var release = new TaskCompletionSource();
        var gate = new DebounceGate((_, ct) => release.Task.WaitAsync(ct));
        int g1 = gate.Submit();
        var elapsed1 = gate.ElapsedAsync(g1);
        int g2 = gate.Submit();          // 防抖窗口内又变了输入：g1 作废
        release.SetResult();
        Assert.False(await elapsed1);   // 旧轮不得放行
        Assert.False(gate.IsCurrent(g1));
        Assert.True(gate.IsCurrent(g2));
    }

    [Fact]
    public void Debounce_SubmitCancelsPreviousToken()
    {
        var gate = new DebounceGate();
        var t1 = gate.Token;
        Assert.False(t1.IsCancellationRequested);
        gate.Submit();
        Assert.True(t1.IsCancellationRequested);   // 旧请求的取消令牌被触发
    }
}
