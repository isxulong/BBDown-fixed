namespace BBDownHelper.Core;

/// <summary>
/// v3.8 登录回执判定（研发#5 / 需求 R2.6，纯函数可单测）：
/// login 进程退出后按凭据文件存在性给一句明确回执。
/// </summary>
public static class LoginReceiptPolicy
{
    public const string Success = "登录成功，以后不用再扫。";
    public const string NotCompleted = "未完成登录：扫码窗口被关闭了，再点一次登录重试。";

    /// <summary>credentialExists = core\BBDown.data 是否存在（登录写凭据成功的判据）。</summary>
    public static string Receipt(bool credentialExists) =>
        credentialExists ? Success : NotCompleted;
}
