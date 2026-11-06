namespace BBDownHelper.Core;

/// <summary>
/// v3.8 登录回执判定（研发#5 / 需求 R2.6，纯函数可单测）：
/// login 进程退出后按凭据文件存在性给一句明确回执。
/// </summary>
public static class LoginReceiptPolicy
{
    public const string Success = "登录成功，以后不用再扫。";
    public const string NotCompleted = "未完成登录：扫码窗口被关闭了，再点一次登录重试。";

    /// <summary>
    /// 本轮 P2 修复：已登录状态下重复点「登录」、中途关掉扫码窗口，
    /// 旧判据只问「凭据文件在不在」——文件本来就在（上次登录留下的），
    /// 于是照样回「登录成功，以后不用再扫」，用户以为这次也扫上了。
    /// 必须问「这次有没有真的重新写进去」：文件不存在 → 未完成；
    /// 文件存在但本次没被改写 → 也不能算成功（那只是旧凭据）。
    /// </summary>
    public static string Receipt(bool credentialExists, bool credentialUpdated) =>
        credentialExists && credentialUpdated ? Success : NotCompleted;

    /// <summary>兼容保留：只知存在性时（如首次登录前文件必不存在）的简化口径。</summary>
    public static string Receipt(bool credentialExists) => Receipt(credentialExists, credentialExists);

    /// <summary>
    /// 凭据文件是否在本次登录期间被改写（纯函数，可单测）：
    /// 登录前不存在、登录后存在 → 改写了；两次存在但写入时间没变 → 没改写（旧凭据）。
    /// </summary>
    public static bool IsRewritten(DateTime? beforeUtc, DateTime? afterUtc) =>
        afterUtc is not null && (beforeUtc is null || afterUtc > beforeUtc);
}
