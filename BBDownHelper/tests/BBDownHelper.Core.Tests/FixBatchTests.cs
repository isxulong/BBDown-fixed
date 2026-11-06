using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v1.0.4 修复批回归用例（先红后绿）：
///  - 红队 P1：分P输入超长数字溢出（PageSelectionPolicy.ToCheckedState / InputRecognizer.ExtractPageParam）；
///  - 红队 P2-1：-p 子集播报分母失实（ProgressTracker of-count 覆盖）；
///  - 红队 P2-2：403 子串误判登录失效（ErrorMapper.IsLoginRelated）；
///  - 红队 P2-3：无百分比行日志下批量进度自驱动（ProgressTracker.OverallPercent）；
///  - 蓝队 P2-③：分P全不选的 Core 侧判定辅助。
/// </summary>
public class FixBatchTests
{
    // ==================== 红队 P1：超长数字溢出回归 ====================

    [Fact]
    public void P1_ToCheckedState_SingleOverflowDigits_ReturnsNull()
    {
        // 10 位数字 > int.MaxValue（2147483647），旧实现 int.Parse 直接抛 OverflowException
        Assert.Null(PageSelectionPolicy.ToCheckedState("9999999999", 12));
    }

    [Fact]
    public void P1_ToCheckedState_RangeOverflowDigits_ReturnsNull()
    {
        Assert.Null(PageSelectionPolicy.ToCheckedState("1-9999999999", 12));
    }

    [Fact]
    public void P1_ExtractPageParam_OverflowDigits_NotWrittenToPageParam()
    {
        // ?p=9999999999 不得写入 -p 参数（10 位超出 int 量级，属非法输入）
        var (page, url) = InputRecognizer.ExtractPageParam("https://www.bilibili.com/video/BV1xx411c7mD?p=9999999999");
        Assert.Null(page);
        Assert.Equal("https://www.bilibili.com/video/BV1xx411c7mD?p=9999999999", url);
    }

    // ==================== 红队 P1 补充：位数上限边界 ====================

    [Fact]
    public void P1_ToCheckedState_LegalRangePage12_StillParsed()
    {
        var states = PageSelectionPolicy.ToCheckedState("1-9999999999".Replace("9999999999", "12"), 12);
        Assert.NotNull(states);
        Assert.Equal(12, states!.Length);
        Assert.True(states.All(s => s));
    }

    [Fact]
    public void P1_IsValidSelectPage_OverflowDigits_Rejected()
    {
        // 红队 P1 修复建议：IsValidSelectPage 与 ExtractPageParam 同步增加量级上限（≤6 位）
        Assert.False(CommandBuilder.IsValidSelectPage("9999999999"));
        Assert.False(CommandBuilder.IsValidSelectPage("1-9999999999"));
        // 合法写法不受影响
        Assert.True(CommandBuilder.IsValidSelectPage("8"));
        Assert.True(CommandBuilder.IsValidSelectPage("1,2"));
        Assert.True(CommandBuilder.IsValidSelectPage("3-5"));
        Assert.True(CommandBuilder.IsValidSelectPage("ALL"));
        Assert.True(CommandBuilder.IsValidSelectPage("LAST"));
    }

    // ==================== 红队 P2-1：-p 子集分母失实 ====================

    [Fact]
    public void P21_ParseStartOfCount_OverridesPreFilterTotal()
    {
        // -p 3,5：fork 先打全量「共计 12 个分P」，再打过滤后「(i of 2)」；
        // 旧实现 Total=Math.Max 钉住 12，进度显示 1/12、2/12，成功播报 12/12——均失实。
        var t = new ProgressTracker();
        Assert.True(t.Feed("共计 12 个分P"));
        Assert.Equal(12, t.Total);
        Assert.True(t.Feed("开始解析P3: 114514... (1 of 2)"));
        Assert.True(t.Feed("下载P3完毕"));
        Assert.True(t.Feed("开始解析P5: 114514... (2 of 2)"));
        Assert.True(t.Feed("下载P5完毕"));
        Assert.True(t.Feed("任务完成"));
        Assert.Equal(2, t.Total);   // 修复后：真实分母=过滤后条目数
        Assert.Equal(2, t.Done);
    }

    // ==================== 红队 P2-2：403 子串误判登录失效 ====================

    [Fact]
    public void P22_IsLoginRelated_Aria2TrafficText403MiB_False()
    {
        // aria2 流量文本「403MiB」不是 HTTP 状态码，不得翻登录徽标
        Assert.False(ErrorMapper.IsLoginRelated("[#x 403MiB/1.5GiB]"));
    }

    [Fact]
    public void P22_IsLoginRelated_EnsureSuccess403_False()
    {
        // EnsureSuccessStatusCode 抛的「...success: 403」是 CDN upos 链路过期/封禁，与登录态无关
        Assert.False(ErrorMapper.IsLoginRelated("Response status code does not indicate success: 403"));
    }

    [Fact]
    public void P22_IsLoginRelated_ExplicitHttpStatus403_StillTrue()
    {
        // 显式 HTTP 状态行（如「HTTP 403 Forbidden」）仍属登录/付费信号，不得丢检
        Assert.True(ErrorMapper.IsLoginRelated("ERROR: HTTP 403 Forbidden"));
        // 真实大会员视频的明确文本信号仍然有效（fork Parser.cs L95 原文）
        Assert.True(ErrorMapper.IsLoginRelated("此视频需要大会员，您大概率需要登录一个有大会员的账号才可以下载"));
    }

    // ==================== 红队 P2-3：重定向下进度条自驱动（数据源刻画） ====================

    [Fact]
    public void P23_OverallPercent_AdvancesWithoutPercentLines()
    {
        // helper 以隐藏子进程跑 BBDown，stdout 必然重定向；fork 的 ProgressBar 在
        // Console.IsOutputRedirected 为真时不输出任何百分比行——PercentRx 永不命中，
        // 进度条唯一可行数据源是 x/N。断言：无百分比行的日志下 OverallPercent 仍逐条推进。
        // （UI 消费点在 MainWindow.RenderLogLines 批量分支，本用例刻画其数据源行为。）
        var t = new ProgressTracker();
        t.Feed("共计 2 个分P");
        Assert.Equal(0, t.OverallPercent);
        t.Feed("开始解析P1: 114... (1 of 2)");
        Assert.Equal(0, t.OverallPercent);
        t.Feed("下载P1完毕");
        Assert.Equal(50, t.OverallPercent);
        t.Feed("开始解析P2: 114... (2 of 2)");
        Assert.Equal(50, t.OverallPercent);
        t.Feed("下载P2完毕");
        Assert.Equal(100, t.OverallPercent);
    }

    // ---------- 蓝队 P2-③：分P全不选 = 下载全部（数据后果），UI 必须可判定并阻断 ----------

    [Fact]
    public void BlueP23_ToSelectPageText_NoneSelected_SameAsAllSelected_ProofOfAmbiguity()
    {
        // 文档化现状：全不选与全选都产出 ""——仅凭文本无法区分，UI 必须基于勾选数组判定。
        // 这正是蓝 P2-③ 要求在开始前阻断、而不是靠文本回退的原因。
        Assert.Equal("", PageSelectionPolicy.ToSelectPageText(new bool[] { false, false, false }));
        Assert.Equal("", PageSelectionPolicy.ToSelectPageText(new bool[] { true, true, true }));
    }

    [Fact]
    public void BlueP23_HasAnyChecked_DistinguishesNoneSelected()
    {
        Assert.False(PageSelectionPolicy.HasAnyChecked(new bool[] { false, false, false }));
        Assert.True(PageSelectionPolicy.HasAnyChecked(new bool[] { false, true, false }));
        Assert.True(PageSelectionPolicy.HasAnyChecked(new bool[] { true, true, true }));
    }

    // ==================== 补修（红队 P2-3 闭合）：进度条落值决策 ====================
    // fork 证据（BBDown-1.6.3，2026-11-05 实读 + 逐行复刻渲染路径实测，见 artifacts/probe_ui_path.log）：
    //  L44 `if (!Console.IsOutputRedirected || RelatedTask is not null)` —— helper 以
    //  `RedirectStandardOutput = true` 的隐藏子进程运行 BBDown（MainWindow.axaml.cs L975），且非
    //  服务器下载任务，故 ResetTimer 不执行、定时器不启动；
    //  L115 `UpdateText` 首行 `if (Console.IsOutputRedirected) return;` —— 重定向下不写任何标签，
    //  L96-99 拼出的唯一进度百分比文本（format 里的 {2,3}%）永不落地。
    //  结论：重定向下没有任何进度百分比数据源，进度条不能自报百分比。

    [Fact]
    public void P23Fix_SingleVideo_NoVisiblePercentLine_IsIndeterminate()
    {
        // 单P 视频：fork 打「共计 1 个分P」⇒ Total=1 ⇒ IsBatch=false，全程无 x/N 也无百分比行。
        // 必须是不确定态；若落 0% 定值，用户会误读成「进度卡死在 0」（旧声明「单视频维持百分比」正是此形态）。
        var d = ProgressBarPolicy.Decide(isBatch: false, done: 0, total: 1, percentLineVisible: false);
        Assert.True(d.IsIndeterminate, "单视频无百分比行时必须是不确定态，不得落 0% 定值");
    }

    [Fact]
    public void P23Fix_HasNotSeenTotalYet_IsIndeterminate()
    {
        // 开跑初期还没拿到任何条目计数（Total=0）：同样只能是不确定态
        var d = ProgressBarPolicy.Decide(isBatch: false, done: 0, total: 0, percentLineVisible: false);
        Assert.True(d.IsIndeterminate, "尚未拿到条目计数时不得自报百分比");
    }

    [Fact]
    public void P23Fix_SinglePageSelectedViaMinusP_IsIndeterminate()
    {
        // 「-p 只选一个分P」的终态：fork 的 of-count 为 1 ⇒ Total=1 ⇒ IsBatch=false，
        // 此时既无 x/N 文案也无百分比行，落值决策必须是不确定态。
        var d = ProgressBarPolicy.Decide(isBatch: false, done: 0, total: 1, percentLineVisible: false);
        Assert.True(d.IsIndeterminate, "单选一个分P（Total=1，非批量）时必须是不确定态");
        // Done 已推进到 1、但任务未结束：仍不得自报 100%
        var d2 = ProgressBarPolicy.Decide(isBatch: false, done: 1, total: 1, percentLineVisible: false);
        Assert.True(d2.IsIndeterminate, "非批量模式不得用 Done/Total 自报百分比");
    }

    [Fact]
    public void P23Fix_MinusPSinglePage_TransientBatchZero_IsKnownResidual()
    {
        // 现状刻画（characterization，非期望行为）：-p 只选一个分P 时，fork 先打过滤前的
        // 「共计 12 个分P」（12>1 ⇒ IsBatch=true ⇒ 本次喂行把进度条置为定值 0%），随后打
        // 过滤后的「(1 of 1)」把 Total 覆盖为 1 ⇒ IsBatch=false ⇒ 批量分支不再进、单条分支
        // 也无百分比行 ⇒ 进度条停在 0% 直到结束时一跳 100%。
        // 这与「不定态」不符，属上一批已验收 UI 代码（RenderLogLines）的残留偏差；
        // 本补修按组长约束未改 UI，故在此如实刻画，供复核方据此决定是否开小口子修一行。
        var t = new ProgressTracker();
        Assert.False(t.IsBatch);
        Assert.True(t.Feed("共计 12 个分P, 已选择：3"));
        Assert.True(t.IsBatch);                              // 此刻会被落成 0% 定值
        Assert.Equal(0, t.OverallPercent);
        Assert.True(t.Feed("开始解析P3: 114514... (1 of 1)"));
        Assert.False(t.IsBatch);                             // Total 被覆盖为 1，后续不再更新
        Assert.Equal(1, t.Total);
    }

    [Fact]
    public void P23Fix_Batch_NoVisiblePercentLine_DrivesByDoneOverTotal()
    {
        // 多P/列表：x/N 是唯一可信数据源（本批已验收的 P2-3 修复路径，此处刻画决策边界）
        var d1 = ProgressBarPolicy.Decide(isBatch: true, done: 1, total: 3, percentLineVisible: false);
        Assert.False(d1.IsIndeterminate);
        Assert.Equal(33, d1.Percent);

        var d2 = ProgressBarPolicy.Decide(isBatch: true, done: 3, total: 3, percentLineVisible: false);
        Assert.False(d2.IsIndeterminate);
        Assert.Equal(100, d2.Percent);
    }

    [Fact]
    public void P23Fix_Batch_DoneBeyondTotal_ClampedTo100()
    {
        var d = ProgressBarPolicy.Decide(isBatch: true, done: 5, total: 3, percentLineVisible: false);
        Assert.False(d.IsIndeterminate);
        Assert.Equal(100, d.Percent);
    }

    [Fact]
    public void P23Fix_VisiblePercentLine_Single_UsesPercent()
    {
        // 兜底分支：仅当真有可见百分比行（非重定向来源）时才按百分比落值
        var d = ProgressBarPolicy.Decide(isBatch: false, done: 0, total: 1, percentLineVisible: true, percentValue: 42);
        Assert.False(d.IsIndeterminate);
        Assert.Equal(42, d.Percent);
    }

    [Fact]
    public void P23Fix_VisiblePercentLine_Batch_NoBackJump()
    {
        // 批量下百分比行只作兜底，取 x/N 与百分比较大者，保证进度条不回跳
        var d = ProgressBarPolicy.Decide(isBatch: true, done: 2, total: 3, percentLineVisible: true, percentValue: 10);
        Assert.False(d.IsIndeterminate);
        Assert.Equal(67, d.Percent);
    }

    // ==================== v1.0.4 修复批 v2：界面落值端（纯函数接入 + -p 单分P 回归） ====================
    // 上一批的缺口：Decide 只在测试里被调用（src/BBDownHelper 下引用数 0），界面仍走
    // `if (... && _progress.IsBatch) { bp = _progress.OverallPercent; DownloadProgress.Value = bp; }`。
    // IsBatch 由真转假（-p 只选一个分P：先「共计 12 个分P」→ 后「(1 of 1)」）后，该分支再也不进，
    // 界面保留先前落下的 0% 定值 ⇒ 用户看到「下载卡在 0%」。
    // 以下用例打在**界面该写入什么**这一层（ProgressBarPresenter.Apply），而不是再打一遍 Decide。

    [Fact]
    public void P23Fix2_UiPaint_BatchThenSingle_ReturnsToIndeterminate()
    {
        // 组长指名的序列用例：isBatch 由真转假，第二次决策必须让界面回到不确定态。
        var ui = new ProgressBarPresenter();

        // 第 1 步：过滤前的「共计 12 个分P」⇒ IsBatch=true、Done=0/Total=12 ⇒ 定值 0%
        var p1 = ui.Apply(ProgressBarPolicy.Decide(isBatch: true, done: 0, total: 12, percentLineVisible: false));
        Assert.True(p1.Changed, "批量首次落值必须产生一次界面写入");
        Assert.False(p1.IsIndeterminate, "批量 x/N 应落定值");
        Assert.Equal(0, p1.Percent);

        // 第 2 步：过滤后的「(1 of 1)」⇒ Total 归 1、IsBatch 转假 ⇒ 必须回到不确定态
        var p2 = ui.Apply(ProgressBarPolicy.Decide(isBatch: false, done: 0, total: 1, percentLineVisible: false));
        Assert.True(p2.Changed, "IsBatch 由真转假时必须产生一次界面写入，否则界面会停在旧定值");
        Assert.True(p2.IsIndeterminate, "-p 单分P：IsBatch 转假后进度条必须回到不确定态，不得停留 0% 定值");
    }

    [Fact]
    public void P23Fix2_UiPaint_SingleVideoFromStart_IndeterminateWithoutRedundantWrite()
    {
        // 单P 视频：全程非批量。初始态即不确定态，重复决策不应反复写 UI（防抖动）。
        var ui = new ProgressBarPresenter();
        var d = ProgressBarPolicy.Decide(isBatch: false, done: 0, total: 1, percentLineVisible: false);
        var p1 = ui.Apply(d);
        Assert.True(p1.IsIndeterminate);
        Assert.False(p1.Changed, "初始即为不确定态，无需再写一次界面");

        var p2 = ui.Apply(d);
        Assert.True(p2.IsIndeterminate);
        Assert.False(p2.Changed, "同状态重复决策不得重复写入");
    }

    [Fact]
    public void P23Fix2_UiPaint_BatchProgress_AdvancesAndDedupes()
    {
        // 批量正常推进 0→33→67→100，且同值重复决策不重写
        var ui = new ProgressBarPresenter();
        var p0 = ui.Apply(ProgressBarPolicy.Decide(true, 0, 3, false));
        Assert.False(p0.IsIndeterminate);
        Assert.Equal(0, p0.Percent);

        var p1 = ui.Apply(ProgressBarPolicy.Decide(true, 1, 3, false));
        Assert.True(p1.Changed);
        Assert.Equal(33, p1.Percent);

        var p1b = ui.Apply(ProgressBarPolicy.Decide(true, 1, 3, false));
        Assert.False(p1b.Changed, "同一百分比重复决策不得重复写入");

        var p3 = ui.Apply(ProgressBarPolicy.Decide(true, 3, 3, false));
        Assert.True(p3.Changed);
        Assert.Equal(100, p3.Percent);
    }

    [Fact]
    public void P23Fix2_UiPaint_IndeterminateAfterValue_DropsStalePercent()
    {
        // 从定值回到不定态后，再回定值时旧百分比不得复用（必须重新落一次）
        var ui = new ProgressBarPresenter();
        ui.Apply(ProgressBarPolicy.Decide(true, 1, 4, false));      // 25%
        var back = ui.Apply(ProgressBarPolicy.Decide(false, 0, 1, false));  // 回不定态
        Assert.True(back.IsIndeterminate);
        var again = ui.Apply(ProgressBarPolicy.Decide(true, 1, 4, false));  // 再回 25%
        Assert.True(again.Changed, "回到定值时必须重新写入，不得认为与上次同值而跳过");
        Assert.Equal(25, again.Percent);
    }

    [Fact]
    public void P23Fix2_EndToEnd_MinusPSinglePage_RealForkLines_EndsIndeterminate()
    {
        // 端到端复刻 RenderLogLines：真实 fork 日志行 → ProgressTracker → ProgressBarPolicy.Decide
        // → ProgressBarPresenter（界面除控件写入外的全部路径）。这是组长指明的用户可点路径：
        // 预览卡只勾 1 个分P → PageSelectionPolicy 输出 "3" → CommandBuilder 传 -p 3。
        var t = new ProgressTracker();
        var ui = new ProgressBarPresenter();
        t.Reset(); ui.Reset();

        // fork Program.cs L332：过滤前打全量「共计 12 个分P, 已选择：3」
        Assert.True(t.Feed("共计 12 个分P, 已选择：3"));
        Assert.True(t.IsBatch);                       // 此刻 12 > 1 ⇒ 批量
        var d1 = ProgressBarPolicy.Decide(t.IsBatch, t.Done, t.Total, false);
        var p1 = ui.Apply(d1);
        Assert.False(p1.IsIndeterminate);             // 旧实现在这里落 0%

        // fork L356：过滤后才打「开始解析P3: ... (1 of 1)」⇒ Total 覆盖为 1、IsBatch 转假
        Assert.True(t.Feed("开始解析P3: 114514... (1 of 1)"));
        Assert.False(t.IsBatch);
        var d2 = ProgressBarPolicy.Decide(t.IsBatch, t.Done, t.Total, false);
        var p2 = ui.Apply(d2);
        Assert.True(p2.Changed, "IsBatch 由真转假必须写一次界面，否则停在 0%");
        Assert.True(p2.IsIndeterminate, "界面最终必须是不确定态，不得保留 0% 定值");

        // 后续日志（下载完毕 / 任务完成）在界面完成态出口接管前，同样不得把进度条打回定值
        t.Feed("下载P3完毕");
        var d3 = ProgressBarPolicy.Decide(t.IsBatch, t.Done, t.Total, false);
        var p3 = ui.Apply(d3);
        Assert.True(p3.IsIndeterminate, "非批量下 Done=1 也不得自报 100%（结束态由完成出口置 100）");
    }

    [Fact]
    public void P23Fix2_EndToEnd_FullBatch_StillDrivesByCount()
    {
        // 回归保护：多P 全选时批量推进不能被本批改动削弱
        var t = new ProgressTracker();
        var ui = new ProgressBarPresenter();
        t.Reset(); ui.Reset();
        t.Feed("共计 3 个分P");
        t.Feed("开始解析P1: 1... (1 of 3)");
        var s1 = ui.Apply(ProgressBarPolicy.Decide(t.IsBatch, t.Done, t.Total, false));
        Assert.False(s1.IsIndeterminate);
        t.Feed("下载P1完毕");
        var s2 = ui.Apply(ProgressBarPolicy.Decide(t.IsBatch, t.Done, t.Total, false));
        Assert.False(s2.IsIndeterminate);
        Assert.True(s2.Percent > s1.Percent, "批量必须持续推进");
        t.Feed("下载P2完毕"); t.Feed("下载P3完毕");
        var s3 = ui.Apply(ProgressBarPolicy.Decide(t.IsBatch, t.Done, t.Total, false));
        Assert.Equal(100, s3.Percent);
    }
}
