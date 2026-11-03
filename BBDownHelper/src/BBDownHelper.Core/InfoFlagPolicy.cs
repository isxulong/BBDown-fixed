namespace BBDownHelper.Core;

/// <summary>
/// v3.1 P3：-info 自动勾选标记（_infoAutoChecked）的复位策略（纯函数，可测）。
/// 背景：UP 主主页识别成功时 UI 会自动勾上 -info 并记标记，换输入时只复位「自动勾的」、
/// 不动用户手勾的。但旧实现里用户「自动勾上 → 手动取消 → 再手动勾回」后标记仍是 true，
/// 再换普通输入会把用户最后这次手动勾选误清掉。
/// 规则：用户手动改动勾选（programmatic=false）时，标记必须立即复位为 false。
/// </summary>
public static class InfoFlagPolicy
{
    /// <summary>
    /// 勾选变化后 _infoAutoChecked 的新值。
    /// programmatic=true（ShowResult 程序化设置）时保持现状（调用方随后会显式设定标记）；
    /// programmatic=false（用户手动改动）时一律复位为 false。
    /// </summary>
    public static bool AfterToggle(bool programmatic, bool currentAutoChecked)
        => programmatic ? currentAutoChecked : false;
}
