namespace BBDownHelper.Core;

/// <summary>v4.0（R3.12）：登录态三态——未登录 / 已登录 / 登录失效。</summary>
public enum LoginState { LoggedOut, LoggedIn, Expired }

/// <summary>
/// v4.0（批次3 R3.12 / 架构 §13）：登录态三态呈现与归并（纯逻辑可单测）。
/// 归并规则：
///  - 无凭据文件 → 未登录（探测都不发）；
///  - 有凭据 + 探测确认无效（false）→ 失效；
///  - 有凭据 + 探测失败（null：断网/超时/风控）→ 保持已登录（静默，不误报失效）；
///  - 有凭据 + 探测有效（true）→ 已登录。
/// 颜色键供 UI 映射画刷（画刷资源在 UI 层，这里不出颜色值，保持 Core 无 UI 依赖）。
/// </summary>
public static class LoginStatePolicy
{
    /// <summary>归并：凭据文件存在性 + 探测结果（null=探测失败/未探测）→ 展示态。</summary>
    public static LoginState Resolve(bool hasCredential, bool? probeLoggedIn)
    {
        if (!hasCredential) return LoginState.LoggedOut;
        if (probeLoggedIn == false) return LoginState.Expired;
        return LoginState.LoggedIn;
    }

    /// <summary>展示文案（状态标识行用）。</summary>
    public static string Label(LoginState s) => s switch
    {
        LoginState.LoggedIn => "已登录",
        LoginState.Expired => "登录可能已失效（下载失败/凭据过期时会出现），请重新登录",
        _ => "未登录（1080P/4K 与会员内容需要登录）"
    };

    /// <summary>登录按钮文案：失效态引导重新登录。</summary>
    public static string ButtonLabel(LoginState s) =>
        s == LoginState.Expired ? "重新登录" : "扫码登录";

    /// <summary>颜色键：gray / green / orange（UI 层映射到达标画刷）。</summary>
    public static string ColorKey(LoginState s) => s switch
    {
        LoginState.LoggedIn => "green",
        LoginState.Expired => "orange",
        _ => "gray"
    };
}
