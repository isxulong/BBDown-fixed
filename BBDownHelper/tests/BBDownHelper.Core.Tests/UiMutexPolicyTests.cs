using BBDownHelper.Core;
using Xunit;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// 「只解析，不下载」置灰联动（对齐生成器 v5）+ 与 contentLocked / audioOnly 的优先级。
/// UI 侧 MainWindow.ApplyMutex 只是把这些 bool 落到控件 IsEnabled，规则本身全部在这里可测。
/// </summary>
public class UiMutexPolicyTests
{
    [Fact]
    public void InfoOnly_DisablesAllDownloadControls_HintVisible()
    {
        // 勾选「只解析」：画质、编码、内容勾选、弹幕、多线程、选集、保存位置、间隔、断点跳过全部禁用
        var s = UiMutexPolicy.Compute(infoOnly: true, contentLocked: false, audioOnly: false);
        Assert.False(s.QualityEncodingEnabled);
        Assert.False(s.MultiThreadEnabled);
        Assert.False(s.AudioOnlyEnabled);
        Assert.False(s.DanmakuEnabled);
        Assert.False(s.CoverSubEnabled);
        Assert.False(s.SelectPageEnabled);
        Assert.False(s.ListAuxEnabled);
        Assert.False(s.WorkDirEnabled);
        Assert.True(s.InfoHintVisible);      // 「已开启只解析：下面的下载设置全部不生效」说明行
    }

    [Fact]
    public void InfoOnly_PriorityHighest_OverridesContentLockedAndAudio()
    {
        // 只解析优先级最高：与 contentLocked / audioOnly 任意组合，结果一致——全部禁用
        foreach (bool cl in new[] { false, true })
        foreach (bool ao in new[] { false, true })
        {
            var s = UiMutexPolicy.Compute(infoOnly: true, contentLocked: cl, audioOnly: ao);
            Assert.False(s.QualityEncodingEnabled);
            Assert.False(s.MultiThreadEnabled);
            Assert.False(s.AudioOnlyEnabled);
            Assert.False(s.DanmakuEnabled);
            Assert.False(s.CoverSubEnabled);
            Assert.False(s.SelectPageEnabled);
            Assert.False(s.ListAuxEnabled);
            Assert.False(s.WorkDirEnabled);
            Assert.True(s.InfoHintVisible);
        }
    }

    [Fact]
    public void NotInfoOnly_HintHidden()
    {
        // 取消勾选立即恢复：说明行必须随勾消隐
        Assert.False(UiMutexPolicy.Compute(false, false, false).InfoHintVisible);
        Assert.False(UiMutexPolicy.Compute(false, true, true).InfoHintVisible);
    }

    [Fact]
    public void ContentLocked_LocksQualityEncodingMtAudio_KeepsRest()
    {
        // 只要封面/字幕/弹幕：锁画质编码、多线程、「只要音频」；弹幕与内容勾选、选集、列表辅助、保存位置不锁
        var s = UiMutexPolicy.Compute(infoOnly: false, contentLocked: true, audioOnly: false);
        Assert.False(s.QualityEncodingEnabled);
        Assert.False(s.MultiThreadEnabled);
        Assert.False(s.AudioOnlyEnabled);
        Assert.True(s.DanmakuEnabled);
        Assert.True(s.CoverSubEnabled);
        Assert.True(s.SelectPageEnabled);
        Assert.True(s.ListAuxEnabled);
        Assert.True(s.WorkDirEnabled);
    }

    [Fact]
    public void AudioOnly_LocksQualityDanmakuCoverSub_KeepsMt()
    {
        // 只要音频：锁画质编码、弹幕三选一、「只要封面/字幕」；多线程与其余不锁
        var s = UiMutexPolicy.Compute(infoOnly: false, contentLocked: false, audioOnly: true);
        Assert.False(s.QualityEncodingEnabled);
        Assert.False(s.DanmakuEnabled);
        Assert.False(s.CoverSubEnabled);
        Assert.True(s.MultiThreadEnabled);
        Assert.True(s.AudioOnlyEnabled);     // 「只要音频」自己当然可用
        Assert.True(s.SelectPageEnabled);
        Assert.True(s.ListAuxEnabled);
        Assert.True(s.WorkDirEnabled);
    }

    [Fact]
    public void Default_AllEnabled_HintHidden()
    {
        var s = UiMutexPolicy.Compute(infoOnly: false, contentLocked: false, audioOnly: false);
        Assert.True(s.QualityEncodingEnabled);
        Assert.True(s.MultiThreadEnabled);
        Assert.True(s.AudioOnlyEnabled);
        Assert.True(s.DanmakuEnabled);
        Assert.True(s.CoverSubEnabled);
        Assert.True(s.SelectPageEnabled);
        Assert.True(s.ListAuxEnabled);
        Assert.True(s.WorkDirEnabled);
        Assert.False(s.InfoHintVisible);
    }

    // ---------- v3.1 P3：-info 自动勾选标记复位 ----------

    [Fact]
    public void InfoFlag_UserManualToggle_ResetsAutoFlag()
    {
        // v3.1 P3 回归：自动勾上后用户「手动取消再勾回」，
        // 标记必须复位为 false，否则换输入会误清用户手勾
        Assert.False(InfoFlagPolicy.AfterToggle(programmatic: false, currentAutoChecked: true));
        Assert.False(InfoFlagPolicy.AfterToggle(programmatic: false, currentAutoChecked: false));
    }

    [Fact]
    public void InfoFlag_ProgrammaticToggle_KeepsFlagForCaller()
    {
        // ShowResult 程序化设置勾选时保持现状（调用方随后显式设定标记）
        Assert.True(InfoFlagPolicy.AfterToggle(programmatic: true, currentAutoChecked: true));
        Assert.False(InfoFlagPolicy.AfterToggle(programmatic: true, currentAutoChecked: false));
    }
}
