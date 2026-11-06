namespace BBDownHelper.Core;

/// <summary>
/// 开始按钮的应然状态：是否可点 + 按钮文案 + 禁用原因（展示用，可为空）。
/// v4.0（R3.2）：新增 Cancel——true 表示按钮当前是「取消」形态（红色、点击走取消链路），
/// 主按钮原地两态切换（开始 ⇄ 取消），不再单独摆一个取消按钮。
/// </summary>
public sealed record StartButtonState(bool Enabled, string Label, string? Hint, bool Cancel = false);

/// <summary>
/// v3.7 开始按钮状态策略（真 bug ②：识别窗口期下错视频修复的纯逻辑部分，可单测）。
/// 规则优先级自上而下：
///  1. 识别中（输入刚变化、识别未完成）→ 禁用 + 「识别中…」——窗口期内绝不能开始；
///  2. 下载进行中 → v4.0（R3.2）：主按钮原地变红色「取消下载」，可点（点击走取消确认链路）；
///  3. 选集非法 → 禁用（红 P2-2 既有规则）；
///  4. 无识别结果 / 识别失败（Unknown）/ 多候选未选定 / 无最终输入 → 禁用；
///  5. 其余 → 可用 + 「开始下载」。
/// UI 层只负责把各标志位喂进来并落到控件，不再各自为政地改 IsEnabled。
/// </summary>
public static class StartButtonPolicy
{
    public const string LabelReady = "开始下载";
    public const string LabelRecognizing = "识别中…";
    public const string LabelCancel = "取消下载";

    public static StartButtonState Compute(
        bool recognizing, bool procRunning, bool pageInvalid, RecognitionResult? current)
    {
        if (recognizing)
            return new(false, LabelRecognizing, "正在识别输入内容，完成后才能开始");
        if (procRunning)
            // v4.0（R3.2）：下载中主按钮原地切换为红色「取消下载」——同位同尺寸、可点。
            // 注意：UI 层点击处理按「_proc 是否存在」路由（有进程=取消链路），不以本标志为唯一判据。
            return new(true, LabelCancel, "点击取消当前下载", Cancel: true);
        if (pageInvalid)
            return new(false, LabelReady, "选集写法不对，请改正后再开始");
        if (current is null)
            return new(false, LabelReady, "请先粘贴链接并完成识别");
        if (current.Kind == InputKind.Unknown)
            return new(false, LabelReady,
                string.IsNullOrEmpty(current.Message) ? "识别未成功" : current.Message);
        if (current.NeedsUserChoice)
            return new(false, LabelReady, "请在候选里选一个内容后再开始");
        if (string.IsNullOrEmpty(current.FinalInput))
            return new(false, LabelReady, "请先完成识别");
        return new(true, LabelReady, null);
    }
}
