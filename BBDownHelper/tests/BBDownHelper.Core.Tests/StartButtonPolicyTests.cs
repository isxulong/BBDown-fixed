using BBDownHelper.Core;
using Xunit;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v3.7 开始按钮状态策略（真 bug ②：识别窗口期下错视频的纯逻辑投影）单测。
/// 优先级：识别中 &gt; 下载中 &gt; 选集非法 &gt; 无结果/识别失败/多候选未选定/无最终输入 &gt; 可用。
/// 「快速改输入+立即点开始」的状态序列在本文件按时间轴逐步走查。
/// </summary>
public class StartButtonPolicyTests
{
    private static RecognitionResult Video() =>
        new() { Kind = InputKind.SingleVideo, KindName = "单个视频", FinalInput = "BV1GJ411x7h7" };

    // ---------- 优先级矩阵 ----------

    [Fact]
    public void Recognizing_TakesTopPriority_EvenIfEverythingElseReady()
    {
        var s = StartButtonPolicy.Compute(recognizing: true, procRunning: false, pageInvalid: false, current: Video());
        Assert.False(s.Enabled);
        Assert.Equal("识别中…", s.Label);
        Assert.NotNull(s.Hint);
    }

    [Fact]
    public void Recognizing_WinsOver_ProcRunning_And_PageInvalid()
    {
        var s = StartButtonPolicy.Compute(true, procRunning: true, pageInvalid: true, current: Video());
        Assert.False(s.Enabled);
        Assert.Equal(StartButtonPolicy.LabelRecognizing, s.Label);   // 识别中优先于下载中
    }

    [Fact]
    public void ProcRunning_CancelMode_Enabled_Red()
    {
        // v4.0（R3.2）：下载中主按钮原地变红色「取消下载」——可点、Cancel 形态
        var s = StartButtonPolicy.Compute(false, true, false, Video());
        Assert.True(s.Enabled);
        Assert.Equal(StartButtonPolicy.LabelCancel, s.Label);
        Assert.True(s.Cancel);
    }

    [Fact]
    public void NotRunning_NotCancelMode()
    {
        Assert.False(StartButtonPolicy.Compute(false, false, false, Video()).Cancel);
        Assert.False(StartButtonPolicy.Compute(true, false, false, Video()).Cancel);
    }

    [Fact]
    public void PageInvalid_Disabled_WithHint()
    {
        var s = StartButtonPolicy.Compute(false, false, true, Video());
        Assert.False(s.Enabled);
        Assert.Contains("选集", s.Hint);
    }

    [Fact]
    public void CurrentNull_Disabled_PromptPaste()
    {
        var s = StartButtonPolicy.Compute(false, false, false, null);
        Assert.False(s.Enabled);
        Assert.Contains("粘贴", s.Hint);
    }

    [Fact]
    public void CurrentUnknown_Disabled_UsesMessageAsHint()
    {
        var r = new RecognitionResult { Kind = InputKind.Unknown, Message = "短链接解析失败：请检查网络连接后重试。" };
        var s = StartButtonPolicy.Compute(false, false, false, r);
        Assert.False(s.Enabled);
        Assert.Equal("短链接解析失败：请检查网络连接后重试。", s.Hint);   // 识别失败：显式原因直出按钮旁
    }

    [Fact]
    public void CurrentUnknown_EmptyMessage_FallbackHint()
    {
        var r = new RecognitionResult { Kind = InputKind.Unknown, Message = "" };
        var s = StartButtonPolicy.Compute(false, false, false, r);
        Assert.False(s.Enabled);
        Assert.Equal("识别未成功", s.Hint);
    }

    [Fact]
    public void NeedsUserChoice_Disabled_ChooseFirst()
    {
        var r = new RecognitionResult
        {
            Kind = InputKind.BareNumber, NeedsUserChoice = true,
            Candidates = new List<NumberCandidate> { new() { Kind = InputKind.Series, FinalInput = "x" } }
        };
        var s = StartButtonPolicy.Compute(false, false, false, r);
        Assert.False(s.Enabled);
        Assert.Contains("选一个", s.Hint);
    }

    [Fact]
    public void EmptyFinalInput_Disabled()
    {
        var r = new RecognitionResult { Kind = InputKind.SingleVideo, KindName = "单个视频", FinalInput = "" };
        var s = StartButtonPolicy.Compute(false, false, false, r);
        Assert.False(s.Enabled);
        Assert.Equal("请先完成识别", s.Hint);
    }

    [Fact]
    public void Ready_Enabled_NoHint()
    {
        var s = StartButtonPolicy.Compute(false, false, false, Video());
        Assert.True(s.Enabled);
        Assert.Equal(StartButtonPolicy.LabelReady, s.Label);
        Assert.Null(s.Hint);
    }

    // ---------- 「快速改输入 + 立即点开始」时间轴走查（纯逻辑投影） ----------
    // UI 实际接线：输入变化 → _recognizing=true → ApplyStartButton → 防抖到点 →
    // RecognizeNowAsync finally（代次+文本双验）→ _recognizing=false → 重算按钮。

    [Fact]
    public void QuickEditSequence_StateWalkthrough()
    {
        // t0 空输入：无识别结果 → 禁用，提示粘贴
        var s0 = StartButtonPolicy.Compute(false, false, false, null);
        Assert.False(s0.Enabled);

        // t1 粘贴 BV 号：输入变化瞬间 _recognizing=true → 禁用 + 「识别中…」
        var s1 = StartButtonPolicy.Compute(true, false, false, null);
        Assert.False(s1.Enabled);
        Assert.Equal("识别中…", s1.Label);

        // t2 识别完成（finally 双验通过，_recognizing=false，_current=单视频）→ 可用
        var s2 = StartButtonPolicy.Compute(false, false, false, Video());
        Assert.True(s2.Enabled);

        // t3 用户快速敲一个空格：输入再变 → _recognizing 又置 true → 立即回到禁用
        //    （此时 _current 仍是旧结果：绝不允许拿旧结果点开始）
        var s3 = StartButtonPolicy.Compute(true, false, false, Video());
        Assert.False(s3.Enabled);
        Assert.Equal("识别中…", s3.Label);

        // t4 用户在 t3 窗口内点开始：按钮已禁用点不动；即使绕过（回车等），
        //    OnStart 双保险（文本 != _current 来源 token 或 _recognizing）→ 触发重识别并 return
        //    ——本策略层以「t3 时刻 Enabled=false」钉住该窗口期不可开始。
        Assert.False(s3.Enabled);

        // t5 重识别完成、文本与 _current 重新一致 → 恢复可用
        var s5 = StartButtonPolicy.Compute(false, false, false, Video());
        Assert.True(s5.Enabled);

        // t6 识别失败态（Unknown + 原因）：保持禁用 + 显式原因（R1.4 的「重新识别」按钮挂在这张卡上）
        var rFail = new RecognitionResult { Kind = InputKind.Unknown, Message = "识别失败，请检查链接或网络后重试" };
        var s6 = StartButtonPolicy.Compute(false, false, false, rFail);
        Assert.False(s6.Enabled);
        Assert.Equal("识别失败，请检查链接或网络后重试", s6.Hint);
    }
}
