using BBDownHelper.Core;
using Xunit;

namespace BBDownHelper.Core.Tests;

public class ProbeAndCommandTests
{
    private static string MediaListJson(string title, long mid, int count) =>
        "{\"code\":0,\"data\":{\"title\":\"" + title + "\",\"mid\":" + mid + ",\"media_count\":" + count + "}}";

    // ---------- 裸数字决策树 ----------

    [Fact]
    public async Task BareNumber_SingleHit_AutoAdopted_WithRegretHint()
    {
        var probe = new BiliApiProbe
        {
            GetStringOverride = (url, _) => Task.FromResult<string?>(
                url.Contains("type=5") ? MediaListJson("睡前消息", 316568752, 857) :
                url.Contains("type=8") ? """{"code":0,"data":null}""" :
                url.Contains("view?aid") ? """{"code":-404,"data":null}""" :
                """{"code":0,"data":{"name":"马督工"}}""")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("2735065", null);
        Assert.Equal(InputKind.Series, r.Kind);
        Assert.False(r.NeedsUserChoice);
        Assert.Equal("https://www.bilibili.com/channel/seriesdetail?sid=2735065", r.FinalInput);
        Assert.Equal(857, r.MediaCount);
        Assert.Contains("如果不对", r.Message);        // 单命中也要允许反悔
    }

    [Fact]
    public async Task BareNumber_MultiHit_RequiresUserChoice_NeverSilentlyFirst()
    {
        // 关键断言②：实测 2735065 同时是系列(type=5)与无关合集(type=8)，多候选必须弹窗
        var probe = new BiliApiProbe
        {
            GetStringOverride = (url, _) => Task.FromResult<string?>(
                url.Contains("type=5") ? MediaListJson("睡前消息", 316568752, 857) :
                url.Contains("type=8") ? MediaListJson("带你读懂民国", 2001450391, 7) :
                url.Contains("view?aid") ? """{"code":-404,"data":null}""" :
                """{"code":0,"data":{"name":"某某"}}""")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("2735065", null);
        Assert.True(r.NeedsUserChoice);
        Assert.Equal(2, r.Candidates.Count);
        Assert.Equal("", r.FinalInput);              // 未选定前不得有最终输入
        Assert.Contains(r.Candidates, c => c.Kind == InputKind.Series && c.MediaCount == 857);
        Assert.Contains(r.Candidates, c => c.Kind == InputKind.Collection && c.MediaCount == 7);
    }

    [Fact]
    public async Task BareNumber_ZeroHit_ClearMessage()
    {
        var probe = new BiliApiProbe
        {
            GetStringOverride = (_, _) => Task.FromResult<string?>(
                """{"code":0,"data":null}""")        // 不存在的 sid 也不报错，data=null
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("987654321", null);
        Assert.Equal(InputKind.Unknown, r.Kind);
        Assert.Contains("没认出数字", r.Message);
    }

    [Fact]
    public async Task BareNumber_MediaCountZero_NotAdopted()
    {
        // 误配 ≠ 报错：media_count=0 不算命中
        var probe = new BiliApiProbe
        {
            GetStringOverride = (url, _) => Task.FromResult<string?>(
                url.Contains("medialist") ? MediaListJson("某个空列表", 111, 0) :
                url.Contains("view?aid") ? """{"code":0,"data":{"title":"测试视频","owner":{"name":"UP主甲"}}}""" :
                """{"code":0,"data":{"name":"UP主乙"}}""")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("170001", null);
        Assert.Equal(InputKind.SingleVideo, r.Kind);
        Assert.Equal("av170001", r.FinalInput);
    }

    // ---------- b23.tv 解析 ----------

    [Fact]
    public async Task ShortLink_Resolved_ThenReclassified()
    {
        var probe = new BiliApiProbe
        {
            GetRedirectOverride = _ => Task.FromResult<string?>(
                "https://space.bilibili.com/3537108/lists/4662096?type=season")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("https://b23.tv/abc123", null);
        Assert.Equal(InputKind.Collection, r.Kind);
        Assert.Equal("https://www.bilibili.com/channel/collectiondetail?sid=4662096", r.FinalInput);
    }

    [Fact]
    public async Task ShortLink_ResolveFail_FriendlyMessage()
    {
        var probe = new BiliApiProbe { GetRedirectOverride = _ => Task.FromResult<string?>(null) };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("https://b23.tv/dead", null);
        Assert.Contains("短链接解析失败", r.Message);
    }

    // ---------- 命令组装 ----------

    private static RecognitionResult Video() =>
        new() { Kind = InputKind.SingleVideo, KindName = "单个视频", FinalInput = "BV1xx411c7mD" };

    private static RecognitionResult Collection() =>
        new()
        {
            Kind = InputKind.Collection, KindName = "合集",
            FinalInput = "https://www.bilibili.com/channel/collectiondetail?sid=4405454"
        };

    [Fact]
    public void Build_Default_HasMt_NoQualityFlag()
    {
        var cmd = CommandBuilder.Build(Video(), new HelperOptions());
        Assert.Contains("-mt", cmd.Args);
        Assert.DoesNotContain("-q", cmd.Args);
        Assert.Equal("BV1xx411c7mD", cmd.Args[^1]);
    }

    [Fact]
    public void Build_Quality1080_UsesExactDfnString()
    {
        var cmd = CommandBuilder.Build(Video(), new HelperOptions { Quality = "q1080" });
        int i = cmd.Args.IndexOf("-q");
        Assert.True(i >= 0);
        Assert.Equal("1080P 高码率,1080P 高清,720P 高清", cmd.Args[i + 1]);
    }

    [Fact]
    public void Build_InfoOnly_IgnoresEverythingElse()
    {
        var cmd = CommandBuilder.Build(Collection(), new HelperOptions
        {
            InfoOnly = true, Quality = "q4k", AudioOnly = true, SaveArchives = true, MultiThread = true
        });
        Assert.Equal(new List<string> { "-info", Collection().FinalInput }, cmd.Args);
    }

    [Fact]
    public void Build_ListKind_AddsDelayAndArchives()
    {
        var cmd = CommandBuilder.Build(Collection(), new HelperOptions
        {
            DelayPerPage = true, SaveArchives = true
        });
        Assert.Contains("--delay-per-page", cmd.Args);
        Assert.Contains("5", cmd.Args);
        Assert.Contains("--save-archives-to-file", cmd.Args);
        // 展示串中链接含 ? 必须加引号
        Assert.Contains("\"https://www.bilibili.com/channel/collectiondetail?sid=4405454\"", cmd.Display);
    }

    [Fact]
    public void Build_NonListKind_NoDelayEvenIfChecked()
    {
        var cmd = CommandBuilder.Build(Video(), new HelperOptions { DelayPerPage = true, SaveArchives = true });
        Assert.DoesNotContain("--delay-per-page", cmd.Args);
        Assert.DoesNotContain("--save-archives-to-file", cmd.Args);
    }

    [Fact]
    public void Build_CoverOnly_LocksAudioAndDanmaku()
    {
        var cmd = CommandBuilder.Build(Video(), new HelperOptions { CoverOnly = true, AudioOnly = true, Danmaku = "dd" });
        Assert.Contains("--cover-only", cmd.Args);
        Assert.DoesNotContain("--audio-only", cmd.Args);
        Assert.DoesNotContain("-dd", cmd.Args);
    }

    [Fact]
    public void SelectPage_Whitelist()
    {
        Assert.True(CommandBuilder.IsValidSelectPage("8"));
        Assert.True(CommandBuilder.IsValidSelectPage("1,2"));
        Assert.True(CommandBuilder.IsValidSelectPage("3-5"));
        Assert.True(CommandBuilder.IsValidSelectPage("ALL"));
        Assert.True(CommandBuilder.IsValidSelectPage("LAST"));
        Assert.False(CommandBuilder.IsValidSelectPage("8; rm -rf /"));
        Assert.False(CommandBuilder.IsValidSelectPage("abc"));
        // v3.1 P2 回归（质检官点名）：.NET \d 匹配 Unicode 全角数字，
        // 中文输入法全角模式的「８」曾穿透 -p 校验原样传给 BBDown，收紧 [0-9] 后必须拒绝
        Assert.False(CommandBuilder.IsValidSelectPage("８"));
        Assert.False(CommandBuilder.IsValidSelectPage("１,２"));
        Assert.False(CommandBuilder.IsValidSelectPage("３-５"));
    }

    [Fact]
    public void Build_WorkDir()
    {
        var cmd = CommandBuilder.Build(Video(), new HelperOptions { WorkDir = @"D:\B站视频" });
        int i = cmd.Args.IndexOf("--work-dir");
        Assert.True(i >= 0);
        Assert.Equal(@"D:\B站视频", cmd.Args[i + 1]);
    }

    // ---------- 蓝 P2-1：只要音频 / 只要XX 时画质与编码分支必须整体跳过 ----------

    [Fact]
    public void Build_AudioOnly_SkipsQualityAndEncoding()
    {
        // 现状会生成 -q "1080P…" --audio-only（-q 静默失效、违反规格互斥），修复后不得再出现
        var cmd = CommandBuilder.Build(Video(), new HelperOptions
        {
            AudioOnly = true, Quality = "q1080", Encoding = "hevc"
        });
        Assert.Contains("--audio-only", cmd.Args);
        Assert.DoesNotContain("-q", cmd.Args);
        Assert.DoesNotContain("--encoding-priority", cmd.Args);
    }

    [Fact]
    public void Build_ContentLocked_SkipsQualityAndEncoding()
    {
        var cmd = CommandBuilder.Build(Video(), new HelperOptions
        {
            CoverOnly = true, Quality = "q4k", Encoding = "avc"
        });
        Assert.Contains("--cover-only", cmd.Args);
        Assert.DoesNotContain("-q", cmd.Args);
        Assert.DoesNotContain("--encoding-priority", cmd.Args);
    }

    // ---------- 红蓝共识 P2：识别竞态代际闸 ----------

    [Fact]
    public void GenerationGate_StaleGeneration_Discarded()
    {
        // 慢的旧识别（如 b23 解析中）返回时，代号已过期，结果必须被丢弃
        var gate = new GenerationGate();
        int stale = gate.Next();
        int current = gate.Next();
        Assert.False(gate.IsCurrent(stale));
        Assert.True(gate.IsCurrent(current));
    }

    // ---------- 红 P3-5：>=19 位裸数字 long 溢出不得卡死识别 ----------

    [Fact]
    public async Task BareNumber_OverflowDigits_NoCrash_Unknown()
    {
        var probe = new BiliApiProbe
        {
            GetStringOverride = (_, _) => throw new InvalidOperationException("溢出输入不应发起任何网络请求")
        };
        var svc = new RecognitionService(probe);
        var r = await svc.RecognizeAsync("12345678901234567890123456", null);
        Assert.Equal(InputKind.Unknown, r.Kind);
        Assert.Contains("没认出数字", r.Message);
    }
}
