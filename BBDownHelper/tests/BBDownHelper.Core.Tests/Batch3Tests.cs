using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v4.0 批次3 纯逻辑测试：ProgressTracker（R3.3）、PageSelectionPolicy（R3.6）、
/// 番剧/课程/收藏夹预览解析（R3.7-9）、登录态三态（R3.12）、GeneratorLink（R3.13）。
/// 日志样本口径锚定 BBDown-fixed Program.cs（2026-11-05 实读）；
/// 接口 JSON 样本锚定 2026-11-05 沙箱实测字段形态。
/// </summary>
public class Batch3Tests
{
    // ==================== R3.3 ProgressTracker ====================

    [Fact]
    public void Tracker_SinglePage_NotBatch_KeepsPercentMode()
    {
        var t = new ProgressTracker();
        t.Feed("开始解析P1: 12345... (1 of 1)");
        Assert.False(t.IsBatch);
        Assert.Equal(1, t.Total);
    }

    [Fact]
    public void Tracker_ParseStartSequence_AdvancesDone()
    {
        var t = new ProgressTracker();
        Assert.True(t.Feed("开始解析P1: 111... (1 of 3)"));
        Assert.Equal(3, t.Total);
        Assert.Equal(0, t.Done);
        t.Feed("下载P1完毕");
        t.Feed("开始解析P2: 222... (2 of 3)");
        Assert.Equal(1, t.Done);   // 第 2 条开始 ⇒ 前 1 条已落定
        t.Feed("开始解析P3: 333... (3 of 3)");
        Assert.Equal(2, t.Done);
        t.Feed("任务完成");
        Assert.Equal(3, t.Done);
        Assert.Equal(100, t.OverallPercent);
    }

    [Fact]
    public void Tracker_SkippedItems_CountedViaParseOrder()
    {
        // 「已下载过, 跳过下载」的条目没有「完毕」行——Done 靠解析序号推进兜住
        var t = new ProgressTracker();
        t.Feed("开始解析P1: 111... (1 of 3)");
        t.Feed("下载P1完毕");
        t.Feed("开始解析P2: 222... (2 of 3)");
        t.Feed("aid: 222已下载过, 跳过下载...");   // 跳过：无完毕行
        t.Feed("开始解析P3: 333... (3 of 3)");
        Assert.Equal(2, t.Done);   // P1 完毕 + P2 跳过都被第 3 条的解析序号兜住
        t.Feed("任务完成");
        Assert.Equal(3, t.Done);
    }

    [Fact]
    public void Tracker_MultiPageVideo_TotalFromFenPLine()
    {
        // 多P视频：先有「共计 N 个分P」，再逐条解析
        var t = new ProgressTracker();
        t.Feed("共计 12 个分P");
        Assert.Equal(12, t.Total);
        Assert.True(t.IsBatch);
        t.Feed("开始解析P1: 999... (1 of 12)");
        Assert.Equal(0, t.Done);
        Assert.True(t.IsBatch);
    }

    [Fact]
    public void Tracker_OverallPercent_Monotonic_NoJumpBack()
    {
        // 进度条不 0→100 回跳：OverallPercent 单调不减
        var t = new ProgressTracker();
        int last = 0;
        for (int i = 1; i <= 5; i++)
        {
            t.Feed($"开始解析P{i}: {1000 + i}... ({i} of 5)");
            Assert.True(t.OverallPercent >= last);
            last = t.OverallPercent;
            t.Feed($"下载P{i}完毕");
            Assert.True(t.OverallPercent >= last);
            last = t.OverallPercent;
        }
        t.Feed("任务完成");
        Assert.Equal(100, t.OverallPercent);
    }

    [Fact]
    public void Tracker_PageTitles_MapAndFallback()
    {
        var t = new ProgressTracker();
        t.Feed("P1: [1001] [第一话 开场] [10:00]");
        t.Feed("开始解析P1: 111... (1 of 2)");
        Assert.Equal("第一话 开场", t.CurrentTitle);
        // >5 页时只打前 5+最后 1 条标题：缺号回退「P{序号}」
        t.Feed("开始解析P2: 222... (2 of 2)");
        Assert.Equal("P2", t.CurrentTitle);
    }

    [Fact]
    public void Tracker_LargeList_50Items()
    {
        // 模拟 50 条列表下载完整日志流
        var t = new ProgressTracker();
        for (int i = 1; i <= 50; i++)
        {
            t.Feed($"开始解析P{i}: {2000 + i}... ({i} of 50)");
            if (i % 7 != 0) t.Feed($"下载P{i}完毕");   // 每 7 条跳过一条（已下载过）
            else t.Feed($"aid: {2000 + i}已下载过, 跳过下载...");
        }
        t.Feed("任务完成");
        Assert.Equal(50, t.Total);
        Assert.Equal(50, t.Done);
        Assert.Equal(100, t.OverallPercent);
    }

    [Fact]
    public void Tracker_Reset_ClearsState()
    {
        var t = new ProgressTracker();
        t.Feed("开始解析P1: 111... (1 of 5)");
        t.Reset();
        Assert.Equal(0, t.Total);
        Assert.Equal(0, t.Done);
        Assert.False(t.IsBatch);
        Assert.Null(t.CurrentTitle);
        Assert.Equal(0, t.OverallPercent);
    }

    [Fact]
    public void Tracker_UnrelatedLines_Ignored()
    {
        var t = new ProgressTracker();
        Assert.False(t.Feed("视频标题: 某个视频"));
        Assert.False(t.Feed("20.5%"));
        Assert.False(t.Feed(""));
        Assert.Equal(0, t.Total);
    }

    // ==================== R3.6 PageSelectionPolicy ====================

    [Fact]
    public void ToSelectPageText_AllChecked_Empty()
    {
        Assert.Equal("", PageSelectionPolicy.ToSelectPageText(new[] { true, true, true }));
    }

    [Fact]
    public void ToSelectPageText_NoneChecked_Empty()
    {
        // 文档化边角：全不选 = 留空 = 全部（勾选行无「全不选」入口，手动全取消视为重置）
        Assert.Equal("", PageSelectionPolicy.ToSelectPageText(new[] { false, false }));
    }

    [Fact]
    public void ToSelectPageText_Partial_CompressesRanges()
    {
        // 1,2,3,5,7,8,9 → "1-3,5,7-9"
        var states = new[] { true, true, true, false, true, false, true, true, true };
        Assert.Equal("1-3,5,7-9", PageSelectionPolicy.ToSelectPageText(states));
    }

    [Fact]
    public void ToSelectPageText_SinglePick()
    {
        Assert.Equal("3", PageSelectionPolicy.ToSelectPageText(new[] { false, false, true, false }));
    }

    [Fact]
    public void ToCheckedState_Empty_AllTrue()
    {
        var s = PageSelectionPolicy.ToCheckedState("", 4);
        Assert.NotNull(s);
        Assert.All(s!, Assert.True);
    }

    [Fact]
    public void ToCheckedState_AllAndLast()
    {
        var all = PageSelectionPolicy.ToCheckedState("ALL", 3);
        Assert.Equal(new[] { true, true, true }, all);
        var last = PageSelectionPolicy.ToCheckedState("LAST", 3);
        Assert.Equal(new[] { false, false, true }, last);
    }

    [Fact]
    public void ToCheckedState_RangeSyntax()
    {
        var s = PageSelectionPolicy.ToCheckedState("1-2,4", 5);
        Assert.Equal(new[] { true, true, false, true, false }, s);
    }

    [Fact]
    public void ToCheckedState_InvalidSyntax_Null()
    {
        Assert.Null(PageSelectionPolicy.ToCheckedState("abc", 5));
        Assert.Null(PageSelectionPolicy.ToCheckedState("1,abc", 5));
    }

    [Fact]
    public void ToCheckedState_OutOfRange_Null()
    {
        // 越界（页数对不上当前预览）→ null：不得拿它覆盖勾选行
        Assert.Null(PageSelectionPolicy.ToCheckedState("1-9", 5));
        Assert.Null(PageSelectionPolicy.ToCheckedState("6", 5));
        Assert.Null(PageSelectionPolicy.ToCheckedState("0", 5));
    }

    [Fact]
    public void ToCheckedState_ZeroPages_Null()
    {
        Assert.Null(PageSelectionPolicy.ToCheckedState("", 0));
    }

    // ==================== R3.7-9 番剧/课程/收藏夹预览解析 ====================

    // 2026-11-05 实测 pgc：数据在根级 result；episodes[].title + long_title
    private const string PgcJson = @"{""code"":0,""message"":""success"",""result"":{
        ""title"":""测试番剧"",""cover"":""http://i0.hdslb.com/bfs/bangumi/xxx.jpg"",
        ""episodes"":[{""title"":""1"",""long_title"":""启程""},{""title"":""2"",""long_title"":""转折""}]}}";

    [Fact]
    public void ParseBangumi_Ok()
    {
        var pv = BiliApiProbe.ParseBangumiPreview(PgcJson);
        Assert.NotNull(pv);
        Assert.Equal(InputKind.Bangumi, pv!.Kind);
        Assert.Equal("测试番剧", pv.Title);
        Assert.StartsWith("https://", pv.CoverUrl);   // http→https 规范
        Assert.Equal(2, pv.PageCount);
        Assert.Equal(new[] { "1 启程", "2 转折" }, pv.PageTitles);
        Assert.False(pv.NotFound);
    }

    [Fact]
    public void ParseBangumi_BadId_NotFound()
    {
        var pv = BiliApiProbe.ParseBangumiPreview(@"{""code"":-404,""message"":""啥都木有""}");
        Assert.NotNull(pv);
        Assert.True(pv!.NotFound);
    }

    [Fact]
    public void ParseBangumi_NullOrBroken_Null()
    {
        Assert.Null(BiliApiProbe.ParseBangumiPreview(null));
        Assert.Null(BiliApiProbe.ParseBangumiPreview("{broken"));
    }

    // 2026-11-05 实测 pugv：数据在根级 data；up_info.uname；episodes[].title
    private const string PugvJson = @"{""code"":0,""data"":{
        ""title"":""测试课程"",""cover"":""https://i0.hdslb.com/bfs/cheese/yyy.jpg"",
        ""up_info"":{""uname"":""某老师""},""ep_count"":2,
        ""episodes"":[{""title"":""第一节""},{""title"":""第二节""}]}}";

    [Fact]
    public void ParseCheese_Ok()
    {
        var pv = BiliApiProbe.ParseCheesePreview(PugvJson);
        Assert.NotNull(pv);
        Assert.Equal(InputKind.Cheese, pv!.Kind);
        Assert.Equal("测试课程", pv.Title);
        Assert.Equal("某老师", pv.UpName);
        Assert.Equal(2, pv.PageCount);
        Assert.Equal(new[] { "第一节", "第二节" }, pv.PageTitles);
    }

    [Fact]
    public void ParseCheese_BadId_NotFound()
    {
        var pv = BiliApiProbe.ParseCheesePreview(@"{""code"":-404}");
        Assert.True(pv!.NotFound);
    }

    // 2026-11-05 实测 fav：data.cover 可能空串；坏 fid 时 code=0 但 data=null
    private const string FavJson = @"{""code"":0,""data"":{
        ""title"":""我的收藏"",""cover"":"""",""media_count"":42,""upper"":{""name"":""收藏者甲""}}}";

    [Fact]
    public void ParseFavorites_Ok()
    {
        var pv = BiliApiProbe.ParseFavoritesPreview(FavJson);
        Assert.NotNull(pv);
        Assert.Equal(InputKind.Favorites, pv!.Kind);
        Assert.Equal("我的收藏", pv.Title);
        Assert.Equal(42, pv.MediaCount);
        Assert.Equal("收藏者甲", pv.UpName);
        Assert.Equal("", pv.CoverUrl);   // 空封面保持空（UI 出占位色块）
    }

    [Fact]
    public void ParseFavorites_NullData_Silent()
    {
        // 坏 fid：code=0 但 data=null → 静默不出卡（不当 NotFound）
        Assert.Null(BiliApiProbe.ParseFavoritesPreview(@"{""code"":0,""data"":null}"));
    }

    [Fact]
    public void ParseView_PageTitles_Extracted()
    {
        var json = @"{""code"":0,""data"":{""title"":""多P"",""pic"":""http://i0.hdslb.com/x.jpg"",
            ""pages"":[{""cid"":1,""part"":""P1标题""},{""cid"":2,""part"":""P2标题""}],
            ""owner"":{""name"":""UP""}}}";
        var pv = BiliApiProbe.ParseViewPreview(json);
        Assert.NotNull(pv);
        Assert.Equal(2, pv!.PageCount);
        Assert.Equal(new[] { "P1标题", "P2标题" }, pv.PageTitles);
    }

    // ==================== R3.7-9 PreviewService 路由 + R3.10 SupportsPreview ====================

    private static RecognitionResult R(InputKind kind, string final) =>
        new() { Kind = kind, FinalInput = final, KindName = kind.ToString() };

    [Fact]
    public async Task GetPreview_Bangumi_RoutesToPgc()
    {
        string? called = null;
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (url, _) => { called = url; return Task.FromResult<string?>(PgcJson); }
        });
        var pv = await svc.GetPreviewAsync(
            R(InputKind.Bangumi, "https://www.bilibili.com/bangumi/play/ss12345"), null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal("测试番剧", pv!.Title);
        Assert.Contains("pgc/view/web/season?season_id=12345", called);
    }

    [Fact]
    public async Task GetPreview_Bangumi_EpLink_UsesEpId()
    {
        string? called = null;
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (url, _) => { called = url; return Task.FromResult<string?>(PgcJson); }
        });
        var pv = await svc.GetPreviewAsync(
            R(InputKind.Bangumi, "https://www.bilibili.com/bangumi/play/ep678"), null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Contains("pgc/view/web/season?ep_id=678", called);
    }

    [Fact]
    public async Task GetPreview_Cheese_RoutesToPugv()
    {
        string? called = null;
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (url, _) => { called = url; return Task.FromResult<string?>(PugvJson); }
        });
        var pv = await svc.GetPreviewAsync(
            R(InputKind.Cheese, "https://www.bilibili.com/cheese/play/ss5462"), null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal("测试课程", pv!.Title);
        Assert.Contains("pugv/view/web/season?season_id=5462", called);
    }

    [Fact]
    public async Task GetPreview_Cheese_EpLink_SilentNull()
    {
        // ep 单节链接取不到整课信息：静默不出卡（不发请求）
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (_, _) => throw new InvalidOperationException("ep 课程链接不得请求")
        });
        Assert.Null(await svc.GetPreviewAsync(
            R(InputKind.Cheese, "https://www.bilibili.com/cheese/play/ep123"), null, CancellationToken.None));
    }

    [Fact]
    public async Task GetPreview_Favorites_RoutesToFolderInfo()
    {
        string? called = null;
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (url, _) => { called = url; return Task.FromResult<string?>(FavJson); }
        });
        var pv = await svc.GetPreviewAsync(
            R(InputKind.Favorites, "https://space.bilibili.com/123/favlist?fid=999"), null, CancellationToken.None);
        Assert.NotNull(pv);
        Assert.Equal("我的收藏", pv!.Title);
        Assert.Contains("fav/folder/info?media_id=999", called);
    }

    [Fact]
    public async Task GetPreview_Favorites_NoFid_SilentNull()
    {
        var svc = new PreviewService(new BiliApiProbe
        {
            GetStringOverride = (_, _) => throw new InvalidOperationException("无 fid 不得请求")
        });
        Assert.Null(await svc.GetPreviewAsync(
            R(InputKind.Favorites, "https://space.bilibili.com/123/favlist"), null, CancellationToken.None));
    }

    [Theory]
    [InlineData(InputKind.SingleVideo, true)]
    [InlineData(InputKind.Collection, true)]
    [InlineData(InputKind.Series, true)]
    [InlineData(InputKind.Bangumi, true)]
    [InlineData(InputKind.Cheese, true)]
    [InlineData(InputKind.Favorites, true)]
    [InlineData(InputKind.UpSpace, false)]
    [InlineData(InputKind.Unknown, false)]
    [InlineData(InputKind.ShortLink, false)]
    public void SupportsPreview_Matrix(InputKind kind, bool expected)
    {
        Assert.Equal(expected, PreviewService.SupportsPreview(R(kind, "x")));
    }

    [Fact]
    public void SupportsPreview_NeedsUserChoice_False()
    {
        var r = R(InputKind.SingleVideo, "BV1GJ411x7h7");
        r.NeedsUserChoice = true;
        Assert.False(PreviewService.SupportsPreview(r));
    }

    // ==================== R3.12 登录态三态 ====================

    [Theory]
    [InlineData(false, null, LoginState.LoggedOut)]    // 无凭据文件：未登录（不发探测）
    [InlineData(false, true, LoginState.LoggedOut)]    // 无凭据即使探测结果存在也不认（不可能场景，防御）
    [InlineData(true, true, LoginState.LoggedIn)]      // 有凭据 + 服务端确认
    [InlineData(true, false, LoginState.Expired)]      // 有凭据 + 服务端不认 → 失效
    [InlineData(true, null, LoginState.LoggedIn)]      // 有凭据 + 探测失败 → 保持二态（静默）
    public void LoginState_Resolve_Matrix(bool hasCred, bool? probe, LoginState expected)
    {
        Assert.Equal(expected, LoginStatePolicy.Resolve(hasCred, probe));
    }

    [Fact]
    public void LoginState_Presentation()
    {
        Assert.Equal("重新登录", LoginStatePolicy.ButtonLabel(LoginState.Expired));
        Assert.Equal("扫码登录", LoginStatePolicy.ButtonLabel(LoginState.LoggedOut));
        Assert.Equal("扫码登录", LoginStatePolicy.ButtonLabel(LoginState.LoggedIn));
        Assert.Equal("green", LoginStatePolicy.ColorKey(LoginState.LoggedIn));
        Assert.Equal("orange", LoginStatePolicy.ColorKey(LoginState.Expired));
        Assert.Equal("gray", LoginStatePolicy.ColorKey(LoginState.LoggedOut));
        Assert.Contains("失效", LoginStatePolicy.Label(LoginState.Expired));
    }

    [Fact]
    public void ParseLoginState_Variants()
    {
        // 2026-11-05 实测：坏 cookie → code=-101；有效 cookie → code=0, data.isLogin=true
        Assert.False(BiliApiProbe.ParseLoginState(@"{""code"":-101,""message"":""账号未登录""}"));
        Assert.False(BiliApiProbe.ParseLoginState(@"{""code"":0,""data"":{""isLogin"":false}}"));
        Assert.True(BiliApiProbe.ParseLoginState(@"{""code"":0,""data"":{""isLogin"":true,""mid"":123}}"));
        Assert.Null(BiliApiProbe.ParseLoginState(null));              // 网络失败
        Assert.Null(BiliApiProbe.ParseLoginState("{broken"));         // JSON 损坏
    }

    [Fact]
    public async Task ProbeLoginState_NoCookie_NullNoRequest()
    {
        var probe = new BiliApiProbe
        {
            GetStringOverride = (_, _) => throw new InvalidOperationException("无凭据不得发探测")
        };
        Assert.Null(await probe.ProbeLoginStateAsync(null, CancellationToken.None));
    }

    [Fact]
    public void IsLoginRelated_Features()
    {
        Assert.True(ErrorMapper.IsLoginRelated("HTTP 403"));
        Assert.True(ErrorMapper.IsLoginRelated("需要登录才能下载"));
        Assert.True(ErrorMapper.IsLoginRelated("cookie 已失效"));
        Assert.True(ErrorMapper.IsLoginRelated("付费内容"));
        Assert.False(ErrorMapper.IsLoginRelated("HTTP 412"));
        Assert.False(ErrorMapper.IsLoginRelated("磁盘空间不足"));
    }

    // ==================== R3.13 GeneratorLink ====================

    [Fact]
    public void GeneratorLink_BuildUri_EncodesUpUrl()
    {
        var uri = GeneratorLink.BuildUri("/tmp/base", "https://space.bilibili.com/123?x=1&y=2");
        Assert.StartsWith("file://", uri);
        Assert.Contains("bat", uri);
        // ? & 必须编码，不能与 file URI 自身的查询语法混淆
        Assert.Contains("#up=", uri);
        var up = uri[(uri.IndexOf("#up=", StringComparison.Ordinal) + 4)..];
        Assert.Equal("https://space.bilibili.com/123?x=1&y=2", Uri.UnescapeDataString(up));
        Assert.DoesNotContain("?x=1", uri[..uri.IndexOf("#up=", StringComparison.Ordinal)]);
    }

    [Fact]
    public void GeneratorLink_BuildUri_NoUpUrl_PlainFileUri()
    {
        var uri = GeneratorLink.BuildUri("/tmp/base", null);
        Assert.StartsWith("file://", uri);
        Assert.DoesNotContain("#up=", uri);
    }

    [Fact]
    public void GeneratorLink_PathAndExists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.False(GeneratorLink.Exists(dir));
        File.WriteAllText(GeneratorLink.GeneratorPath(dir), "<html></html>");
        Assert.True(GeneratorLink.Exists(dir));
        Assert.EndsWith("bat下载命令生成器.html", GeneratorLink.GeneratorPath(dir));
    }

    // ==================== R3.4 拖拽 / ep-ss 提取 ====================

    [Fact]
    public void TryExtractEpSsId_Matrix()
    {
        Assert.True(InputRecognizer.TryExtractEpSsId("https://www.bilibili.com/bangumi/play/ep123", out bool isEp, out long id));
        Assert.True(isEp); Assert.Equal(123, id);
        Assert.True(InputRecognizer.TryExtractEpSsId("https://www.bilibili.com/bangumi/play/ss456?x=1", out isEp, out id));
        Assert.False(isEp); Assert.Equal(456, id);
        Assert.True(InputRecognizer.TryExtractEpSsId("https://www.bilibili.com/cheese/play/ss5462", out isEp, out id));
        Assert.False(isEp); Assert.Equal(5462, id);
        Assert.False(InputRecognizer.TryExtractEpSsId("https://www.bilibili.com/video/BV1GJ411x7h7", out _, out _));
        Assert.False(InputRecognizer.TryExtractEpSsId("", out _, out _));
    }
}
