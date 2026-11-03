namespace BBDownHelper.Core;

/// <summary>
/// 界面置灰联动的一次性判定结果（纯逻辑、无 UI 依赖，Linux 可单测）。
/// 由 MainWindow.ApplyMutex 调用并逐项落到控件 IsEnabled。
/// </summary>
public sealed record MutexState
{
    public bool QualityEncodingEnabled { get; init; }   // 画质 5 项 + 编码 3 项
    public bool MultiThreadEnabled { get; init; }       // -mt
    public bool AudioOnlyEnabled { get; init; }         // 只要音频
    public bool DanmakuEnabled { get; init; }           // 弹幕三选一
    public bool CoverSubEnabled { get; init; }          // 只要封面 / 只要字幕
    public bool SelectPageEnabled { get; init; }        // 选集
    public bool ListAuxEnabled { get; init; }           // 间隔 5 秒 / 断点跳过
    public bool WorkDirEnabled { get; init; }           // 保存位置「浏览…」
    public bool InfoHintVisible { get; init; }          // 「已开启只解析…」说明行
}

/// <summary>
/// 界面互斥/置灰规则（对齐 bat 下载命令生成器 v5 行为）：
/// 优先级从高到低——
/// 1) 只解析（-info）：勾选后画质、编码、内容勾选、弹幕、多线程、选集、保存位置、
///    间隔、断点跳过等下载相关控件全部禁用，并显示说明行；取消勾选立即恢复；
/// 2) contentLocked（只要封面/只要字幕/只要弹幕）：锁画质编码、多线程与「只要音频」；
/// 3) audioOnly（只要音频）：锁画质编码、弹幕三选一与「只要封面/字幕」。
/// </summary>
public static class UiMutexPolicy
{
    public static MutexState Compute(bool infoOnly, bool contentLocked, bool audioOnly)
    {
        if (infoOnly)
        {
            // 只解析优先级最高：下载设置全部禁用（record 默认全 false），只亮说明行
            return new MutexState { InfoHintVisible = true };
        }
        return new MutexState
        {
            QualityEncodingEnabled = !contentLocked && !audioOnly,
            MultiThreadEnabled = !contentLocked,
            AudioOnlyEnabled = !contentLocked,
            DanmakuEnabled = !audioOnly,
            CoverSubEnabled = !audioOnly,
            SelectPageEnabled = true,
            ListAuxEnabled = true,
            WorkDirEnabled = true,
            InfoHintVisible = false
        };
    }
}
