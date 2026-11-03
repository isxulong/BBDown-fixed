using BBDownHelper.Core;
using Xunit;

namespace BBDownHelper.Core.Tests;

public class RecognitionTests
{
    private const string BV = "BV1xx411c7mD";   // 12 字符，符合 ^BV1[A-Za-z0-9]{9}$

    // ---------- 净化 ----------

    [Fact]
    public void Purify_ShareText_PicksFullUrl()
    {
        // 分享话术文本：整段话里挑链接
        string raw = "【睡前消息1126】xxxx-哔哩哔哩】 https://www.bilibili.com/video/" + BV + "/ 快来围观";
        Assert.Equal("https://www.bilibili.com/video/" + BV + "/", InputRecognizer.ExtractToken(raw));
    }

    [Fact]
    public void Purify_ShareText_PicksB23First()
    {
        string raw = "【标题】好看 https://b23.tv/abc123 快看";
        Assert.Equal("https://b23.tv/abc123", InputRecognizer.ExtractToken(raw));
    }

    [Fact]
    public void Purify_BareBv_Extracted()
    {
        Assert.Equal(BV, InputRecognizer.ExtractToken("看这个 " + BV));
    }

    [Fact]
    public void Purify_AvCaseInsensitive()
    {
        Assert.Equal("av170001", InputRecognizer.ExtractToken("av170001"));
        Assert.Equal("AV170001", InputRecognizer.ExtractToken("AV170001"));
    }

    [Fact]
    public void Purify_ShortDigitsIgnored()
    {
        Assert.Null(InputRecognizer.ExtractToken("abc 123"));   // 少于 4 位视为误粘贴
    }

    // ---------- 分类 11 类 ----------

    [Fact]
    public void Classify_BvToken()
    {
        var r = InputRecognizer.Classify(BV);
        Assert.Equal(InputKind.SingleVideo, r.Kind);
        Assert.Equal(BV, r.FinalInput);
    }

    [Fact]
    public void Classify_VideoUrl_WithPageParam()
    {
        var r = InputRecognizer.Classify("https://www.bilibili.com/video/" + BV + "/?p=3&vd_source=abc");
        Assert.Equal(InputKind.SingleVideo, r.Kind);
        Assert.Equal(BV, r.FinalInput);                 // BV 号本体，不带 URL
        Assert.Equal("3", r.SelectPageSuggestion);      // ?p=N 映射为 -p 建议
    }

    [Fact]
    public void Classify_AvUrl()
    {
        var r = InputRecognizer.Classify("https://www.bilibili.com/video/av170001/");
        Assert.Equal(InputKind.SingleVideo, r.Kind);
        Assert.Equal("av170001", r.FinalInput);
    }

    [Fact]
    public void Classify_BangumiEpAndSs()
    {
        var ep = InputRecognizer.Classify("https://www.bilibili.com/bangumi/play/ep12345");
        Assert.Equal(InputKind.Bangumi, ep.Kind);
        Assert.Equal("https://www.bilibili.com/bangumi/play/ep12345", ep.FinalInput);

        var ss = InputRecognizer.Classify("https://www.bilibili.com/bangumi/play/ss6789?from=search");
        Assert.Equal(InputKind.Bangumi, ss.Kind);
    }

    [Fact]
    public void Classify_Cheese()
    {
        var r = InputRecognizer.Classify("https://www.bilibili.com/cheese/play/ep12345");
        Assert.Equal(InputKind.Cheese, r.Kind);
    }

    [Fact]
    public void Classify_NewListsSeason_ConvertsToCollectiondetail()
    {
        // 关键断言①：新版 lists 链接必须转换为旧格式（直接喂会下载整个 UP 主页）
        var r = InputRecognizer.Classify("https://space.bilibili.com/3537108/lists/4662096?type=season");
        Assert.Equal(InputKind.Collection, r.Kind);
        Assert.Equal("https://www.bilibili.com/channel/collectiondetail?sid=4662096", r.FinalInput);
    }

    [Fact]
    public void Classify_NewListsSeries_ConvertsToSeriesdetail()
    {
        var r = InputRecognizer.Classify("https://space.bilibili.com/3537108/lists/2735065?type=series");
        Assert.Equal(InputKind.Series, r.Kind);
        Assert.Equal("https://www.bilibili.com/channel/seriesdetail?sid=2735065", r.FinalInput);
    }

    [Fact]
    public void Classify_OldCollection()
    {
        var r = InputRecognizer.Classify("https://www.bilibili.com/channel/collectiondetail?sid=4405454");
        Assert.Equal(InputKind.Collection, r.Kind);
        Assert.Contains("collectiondetail?sid=4405454", r.FinalInput);
    }

    [Fact]
    public void Classify_MedialistCollection()
    {
        var r = InputRecognizer.Classify("https://www.bilibili.com/medialist/detail/ml1234?business=space_collection&business_id=4405454");
        Assert.Equal(InputKind.Collection, r.Kind);
    }

    [Fact]
    public void Classify_OldSeries()
    {
        var r = InputRecognizer.Classify("https://www.bilibili.com/channel/seriesdetail?sid=2735065");
        Assert.Equal(InputKind.Series, r.Kind);
        Assert.Contains("seriesdetail?sid=2735065", r.FinalInput);
    }

    [Fact]
    public void Classify_Favlist()
    {
        var r = InputRecognizer.Classify("https://space.bilibili.com/3537108/favlist?fid=123456");
        Assert.Equal(InputKind.Favorites, r.Kind);
    }

    [Fact]
    public void Classify_UpSpace_RequiresConfirm()
    {
        // 红队 P1-④：UP 主主页必须强制二次确认
        var r = InputRecognizer.Classify("https://space.bilibili.com/3537108");
        Assert.Equal(InputKind.UpSpace, r.Kind);
        Assert.True(r.RequiresConfirm);
        Assert.Contains("全部投稿", r.Message);
    }

    [Fact]
    public void Classify_UpSpace_MessageExplainsExportOnly_RedTextIsNotError()
    {
        // v3：UP 主主页说明文案必须讲清三件事——只导出清单 txt、红字是固定提示不是出错、批量走 bat 生成器
        var r = InputRecognizer.Classify("https://space.bilibili.com/3537108");
        Assert.Equal(InputKind.UpSpace, r.Kind);
        Assert.True(r.RequiresConfirm);                       // 确认门槛保留
        Assert.Contains("导出", r.Message);
        Assert.Contains("投稿视频.txt", r.Message);
        Assert.Contains("红字", r.Message);
        Assert.Contains("不是出错", r.Message);
        Assert.Contains("bat下载命令生成器.html", r.Message);
        // 不得再沿用 v2 的误导性说法（暗示会真的下载几百个视频）
        Assert.DoesNotContain("耗时很久", r.Message);
    }

    [Fact]
    public void Classify_ShortLink_FlaggedForResolve()
    {
        var r = InputRecognizer.Classify("https://b23.tv/abc123");
        Assert.Equal(InputKind.ShortLink, r.Kind);
    }

    // ---------- v3.1 P2/P3 回归 ----------

    [Fact]
    public void Classify_B23EvilSubdomain_NotShortLink()
    {
        // v3.1 P3：b23 判定锚定 ^https://b23.tv/，
        // b23.tv.evil.com 不再被误判为短链（不会向攻击者域名发起解析请求）
        var r = InputRecognizer.Classify("https://b23.tv.evil.com/abc");
        Assert.NotEqual(InputKind.ShortLink, r.Kind);
        Assert.Equal(InputKind.Unknown, r.Kind);   // host 白名单拒绝
    }

    [Fact]
    public void ExtractToken_FullWidthAv_NotExtracted()
    {
        // v3.1 P2 同源次生：av+全角数字曾被 AvSearchRegex(\d) 接受进 FinalInput
        Assert.Null(InputRecognizer.ExtractToken("av１７０００１"));
        Assert.Null(InputRecognizer.ExtractToken("看看 av１２３４ 这个"));
    }

    [Fact]
    public void Classify_FullWidthAv_Unknown_NoFinalInput()
    {
        // 即使 token 直接进 Classify，av 全串校验收紧后也不得产出 FinalInput
        var r = InputRecognizer.Classify("av１７０００１");
        Assert.Equal(InputKind.Unknown, r.Kind);
        Assert.True(string.IsNullOrEmpty(r.FinalInput));
    }

    [Fact]
    public void ExtractToken_FullWidthBareNumber_NotExtracted()
    {
        // v3.1 P2：裸全角数字不得被 BareNumberRegex 提取
        Assert.Null(InputRecognizer.ExtractToken("１２３４"));
        Assert.Null(InputRecognizer.ExtractToken("第１２３４个视频"));
    }

    [Fact]
    public void Classify_EvilHost_Rejected()
    {
        var r = InputRecognizer.Classify("https://evil.com/video/" + BV);
        Assert.Equal(InputKind.Unknown, r.Kind);   // host 限定 bilibili.com 域
    }

    // ---------- 关键断言③：白名单重建不含原始输入 ----------

    [Fact]
    public async Task Safety_RawTextNeverReachesCommand()
    {
        // 用户粘贴了一段带注入意图的文字
        string evil = "看这个视频 https://b23.tv/abc123 & del /q C:\\ & echo pwned";
        var probe = new BiliApiProbe
        {
            GetRedirectOverride = _ => Task.FromResult<string?>("https://www.bilibili.com/video/" + BV + "/")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync(evil, null);
        Assert.Equal(InputKind.SingleVideo, r.Kind);

        var cmd = CommandBuilder.Build(r, new HelperOptions());
        foreach (var a in cmd.Args)
        {
            Assert.DoesNotContain("del", a);
            Assert.DoesNotContain("echo pwned", a);
            Assert.DoesNotContain("C:\\", a);
        }
        Assert.Equal(BV, cmd.Args[^1]);   // 末参只含重建后的 BV
    }

    [Fact]
    public void RebuildUrl_StripsPageParam()
    {
        var rebuilt = InputRecognizer.RebuildUrl("https://www.bilibili.com/bangumi/play/ss123?p=2&from=search", out var p);
        Assert.Equal("2", p);
        Assert.DoesNotContain("p=2", rebuilt);
        Assert.Contains("from=search", rebuilt);
    }

    // ---------- 红 P2-1：分享话术容错（CJK 标点 / 中文紧贴 URL） ----------

    [Fact]
    public void Purify_CjkPunctuationAfterUrl_Excluded()
    {
        // 中文逗号紧跟 URL：token 必须在 CJK 标点处截断
        string raw = "地址：https://www.bilibili.com/video/" + BV + "/，速来";
        Assert.Equal("https://www.bilibili.com/video/" + BV + "/", InputRecognizer.ExtractToken(raw));
    }

    [Fact]
    public void Purify_CjkTextTouchingUrl_Excluded()
    {
        // 中文字符紧贴 URL 首尾：两端都要截干净
        string raw = "链接https://www.bilibili.com/video/" + BV + "/真的好看";
        Assert.Equal("https://www.bilibili.com/video/" + BV + "/", InputRecognizer.ExtractToken(raw));
    }

    [Fact]
    public void Purify_B23WithCjkPunctuation_Excluded()
    {
        // b23.tv 短链后跟中文句号：\w 不得吞 CJK
        Assert.Equal("https://b23.tv/abc123", InputRecognizer.ExtractToken("快看 https://b23.tv/abc123。真的好"));
    }

    [Fact]
    public async Task ShareText_CjkWrapped_BvVideoRecognized()
    {
        // 端到端：CJK 包裹的分享话术应识别为单视频，而不是「无法识别」
        var svc = new RecognitionService(new BiliApiProbe());
        var r = await svc.RecognizeAsync("地址：https://www.bilibili.com/video/" + BV + "/，速来", null);
        Assert.Equal(InputKind.SingleVideo, r.Kind);
        Assert.Equal(BV, r.FinalInput);
    }

    [Fact]
    public void Classify_UrlTokenWithCjkTail_FallsBackToBv()
    {
        // 白名单整体校验失败时，在 token 内回退搜索 BV 号再试一次
        var r = InputRecognizer.Classify("https://www.bilibili.com/video/" + BV + "/，速来");
        Assert.Equal(InputKind.SingleVideo, r.Kind);
        Assert.Equal(BV, r.FinalInput);
    }

    [Fact]
    public void Classify_InvalidCharFailure_MessageNotMisleading()
    {
        // 失败文案不得把 B 站链接误报为「不属于 bilibili.com 域名」
        var r = InputRecognizer.Classify("https://www.bilibili.com/video/，");
        Assert.Equal(InputKind.Unknown, r.Kind);
        Assert.DoesNotContain("域名", r.Message);
    }

    [Fact]
    public void Classify_EvilHost_StillRejected_NoBvFallback()
    {
        // 域名不对时不做 BV 回退，保持安全红线（与红 P2-1 的回退只针对混入杂字符的场景）
        var r = InputRecognizer.Classify("https://evil.com/video/" + BV + "/，");
        Assert.Equal(InputKind.Unknown, r.Kind);
        Assert.Contains("域名", r.Message);
    }
}
