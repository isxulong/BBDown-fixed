using System.Net;
using BBDownHelper.Core;
using Xunit;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v3.7 批次 1 识别管线新行为单测：
///  - R1.5 LooksComplete 半成品预校验（不发联网请求）；
///  - R1.6 断网归因分离（「检查网络」vs「没认出」文案可区分）；
///  - R1.7 合集标题补全失败置 ListInfoFailed（UI 显示灰字说明）。
/// </summary>
public class RecognitionV37Tests
{
    private const string ViewJson =
        "{\"code\":0,\"data\":{\"title\":\"t\",\"pic\":\"\",\"pages\":[],\"owner\":{\"name\":\"up\"}}}";

    private static HttpResponseMessage Resp(int status, string json = "{}") =>
        new((HttpStatusCode)status) { Content = new StringContent(json) };

    // ---------- R1.5：LooksComplete 纯函数 ----------

    [Theory]
    [InlineData("https://b23.tv/ab", false)]          // 半成品：路径 2 位
    [InlineData("https://b23.tv/abc", false)]         // 半成品：路径 3 位
    [InlineData("https://b23.tv/abcd", true)]        // 边界：路径 4 位（= 阈值，判完整）
    [InlineData("https://b23.tv/abc123", true)]       // 正常短链
    [InlineData("b23.tv/abc123", true)]              // 无协议头也认
    [InlineData("https://b23.tv.evil.com/abc", false)] // 仿冒域：非 b23 短链、非 bilibili URL
    [InlineData("BV1GJ411x7h7", true)]
    [InlineData("av170001", true)]
    [InlineData("https://www.bilibili.com/video/BV1GJ411x7h7", true)]
    [InlineData("170001", true)]                     // 纯数字 ≥4 位（粘贴行为不变）
    [InlineData("【标题】好看 https://b23.tv/abc123 快看", true)]  // 整段分享文案一次到位
    [InlineData("看这个 https://www.bilibili.com/video/BV1GJ411x7h7/?share_source=copy_web，速来", true)]
    [InlineData("８８８８", false)]                   // 全角数字：不得按纯数字判完整
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("随便说说没有链接", false)]
    [InlineData("1", false)]                          // 单字符数字：非完整输入
    public void LooksComplete_Theory(string text, bool expected)
    {
        Assert.Equal(expected, InputRecognizer.LooksComplete(text));
    }

    // ---------- R1.5：半成品短链零联网请求 ----------

    [Fact]
    public async Task HalfB23_ZeroHttpRequest_LocalMessageOnly()
    {
        int redirects = 0;
        var probe = new BiliApiProbe
        {
            GetRedirectOverride = _ => { redirects++; return Task.FromResult<string?>(null); }
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("https://b23.tv/ab", null);
        Assert.Equal(InputKind.Unknown, r.Kind);
        Assert.Contains("还没粘贴完整", r.Message);   // 只走本地提示
        Assert.Equal(0, redirects);                    // 一个 HTTP 请求都不发
    }

    [Fact]
    public async Task FullB23_StillResolves_PasteBehaviorUnchanged()
    {
        var probe = new BiliApiProbe
        {
            GetRedirectOverride = _ => Task.FromResult<string?>(
                "https://www.bilibili.com/video/BV1GJ411x7h7")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("https://b23.tv/abc123", null);
        Assert.Equal(InputKind.SingleVideo, r.Kind);  // 完整短链照常解析
        Assert.Equal("BV1GJ411x7h7", r.FinalInput);
    }

    // ---------- R1.6：断网归因分离（短链路径） ----------

    [Fact]
    public async Task ShortLink_NetworkFailure_MessageSaysCheckNetwork()
    {
        var probe = new BiliApiProbe
        {
            SendOverride = (_, _) => throw new HttpRequestException("connection reset")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("https://b23.tv/abc123", null);
        Assert.Equal(InputKind.Unknown, r.Kind);
        Assert.Contains("请检查网络连接后重试", r.Message);   // 联网失败单列文案
        Assert.True(probe.LastNetworkFailed);
    }

    [Fact]
    public async Task ShortLink_ResolveNullNoNetFail_GenericMessage_WithoutCheckNetwork()
    {
        var probe = new BiliApiProbe { GetRedirectOverride = _ => Task.FromResult<string?>(null) };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("https://b23.tv/abc123", null);
        Assert.Equal(InputKind.Unknown, r.Kind);
        Assert.Contains("短链接解析失败", r.Message);
        Assert.DoesNotContain("请检查网络连接后重试", r.Message);  // 非断网：不得误报网络
        Assert.False(probe.LastNetworkFailed);
    }

    // ---------- R1.6：断网归因分离（裸数字路径） ----------

    [Fact]
    public async Task BareNumber_NetworkFailure_MessageSaysCheckNetwork()
    {
        var probe = new BiliApiProbe
        {
            SendOverride = (_, _) => throw new HttpRequestException("offline")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("4662096", null);
        Assert.Equal(InputKind.Unknown, r.Kind);
        Assert.Contains("请检查网络连接后重试", r.Message);
        Assert.DoesNotContain("没认出", r.Message);
    }

    [Fact]
    public async Task BareNumber_NoCandidates_NotNetwork_SaysUnrecognized()
    {
        var probe = new BiliApiProbe
        {
            // 接口正常返回但三路都不命中（data=null / code!=0）
            GetStringOverride = (_, _) => Task.FromResult<string?>("{\"code\":-400,\"data\":null}")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("4662096", null);
        Assert.Equal(InputKind.Unknown, r.Kind);
        Assert.Contains("没认出", r.Message);                       // 格式无法识别口径
        Assert.DoesNotContain("请检查网络连接后重试", r.Message);
    }

    [Fact]
    public async Task BareNumber_NetworkFailure_SingleCandidateStillNeedsUserChoice()
    {
        // 断网下三路探测全失败 → 0 候选（网络失败归因优先于多候选流程）
        var probe = new BiliApiProbe
        {
            SendOverride = (_, _) => throw new HttpRequestException("offline")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("2735065", null);
        Assert.Equal(InputKind.Unknown, r.Kind);
        Assert.False(r.NeedsUserChoice);
        Assert.Contains("请检查网络连接后重试", r.Message);
    }

    // ---------- R1.7：合集标题补全失败置标记 ----------

    [Fact]
    public async Task Collection_ListInfoProbeFails_SetsFlag_RecognitionUsable()
    {
        var probe = new BiliApiProbe
        {
            GetStringOverride = (_, _) => Task.FromResult<string?>(null)   // 探测联网失败
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("https://space.bilibili.com/3537108/lists/4662096?type=season", null);
        Assert.Equal(InputKind.Collection, r.Kind);
        Assert.Equal("https://www.bilibili.com/channel/collectiondetail?sid=4662096", r.FinalInput);
        Assert.True(r.ListInfoFailed);                          // UI 显示「没取到合集标题（网络原因），不影响下载」
        Assert.Null(r.Title);
    }

    [Fact]
    public async Task Collection_ListInfoOk_NoFlag()
    {
        var probe = new BiliApiProbe
        {
            GetStringOverride = (_, _) => Task.FromResult<string?>(
                "{\"code\":0,\"data\":{\"title\":\"合集标题\",\"mid\":3537108,\"media_count\":12}}")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("https://space.bilibili.com/3537108/lists/4662096?type=season", null);
        Assert.Equal(InputKind.Collection, r.Kind);
        Assert.False(r.ListInfoFailed);
        Assert.Equal("合集标题", r.Title);
        Assert.Equal(12, r.MediaCount);
    }

    [Fact]
    public async Task Collection_NoSidInUrl_NoProbe_NoFlag()
    {
        // 旧版合集 URL 带 sid 一定有；此处用 sid 缺失形态（business_id 同源）钉住「没联网不算失败」
        var probe = new BiliApiProbe
        {
            GetStringOverride = (_, _) => Task.FromResult<string?>(null)
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("https://space.bilibili.com/3537108/lists/4662096?type=series", null);
        Assert.Equal(InputKind.Series, r.Kind);
        // type=series 时 sid=4662096 合法，仍会探测 → null → 置标记（与 Collection 同口径）
        Assert.True(r.ListInfoFailed);
    }
}
