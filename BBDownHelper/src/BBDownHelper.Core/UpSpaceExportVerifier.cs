namespace BBDownHelper.Core;

/// <summary>
/// v3.7 UP 主导出验证器（真 bug ①：导出假成功修复）。
/// BBDown 对 UP 主主页的真实行为：把全部投稿地址导出为工作目录下『UP昵称的投稿视频.txt』，
/// 随后打印红字「暂不支持该功能」退出——退出码与红字都不可靠，
/// 唯一可信的判据是：本次运行时间窗内，工作目录下真实生成了非空的清单 txt。
/// 断网/凭据失效/风控下清单根本不会生成，旧版却无条件报「导出完成」（全程序唯一不看退出码的成功路径）。
/// </summary>
public static class UpSpaceExportVerifier
{
    /// <summary>清单文件名特征：『UP昵称的投稿视频.txt』。</summary>
    public const string ListFileSuffix = "的投稿视频.txt";

    /// <summary>
    /// 在 baseDir 下找本次运行（since 之后写入）生成且非空的投稿清单 txt，取最新一个。
    /// 命中返回 true 且 path 为完整路径；未命中/IO 异常返回 false（path=null）。
    /// 纯文件系统判定，可单测。
    /// </summary>
    public static bool TryFindList(string baseDir, DateTime since, out string? path)
    {
        path = null;
        try
        {
            string? best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var f in Directory.EnumerateFiles(baseDir, "*" + ListFileSuffix))
            {
                var fi = new FileInfo(f);
                if (fi.Length <= 0) continue;            // 空清单 = 没导出来，不算成功
                if (fi.LastWriteTime < since) continue;  // 上次运行留下的旧清单，不算本次产物
                if (fi.LastWriteTime >= bestTime) { best = f; bestTime = fi.LastWriteTime; }
            }
            path = best;
            return best is not null;
        }
        catch { return false; }   // 目录不可读/被删：按未导出处理，不抛
    }

    /// <summary>
    /// 导出失败的最可能原因（纯函数，可单测）：按日志尾部特征归因，三段式给行动建议。
    /// 特征不足时给覆盖三种常见原因的兜底句（未登录/网络/风控）。
    /// </summary>
    public static string GuessCause(int exitCode, string logTail)
    {
        if (logTail.Contains("412", StringComparison.Ordinal))
            return "最可能原因：被 B 站风控拦截（412）——稍等几分钟、减少操作频率后重试";
        // 登录/凭据判定复用 ErrorMapper.IsLoginRelated，不在此另抄一份特征集（那会与 MapExit
        // 形成第三处分叉）。红队 P2-2 已把裸子串换成锚定正则 Http403Rx：裸 Contains("403")
        // 会误吃 aria2 流量文本「403MiB」与 EnsureSuccessStatusCode 的「...success: 403」，
        // 从而把网络/路径原因的导出失败归因成「凭据失效」，用户重扫码也修不好。
        // 两处取舍说明：IsLoginRelated 比本场景所需多收了「付费」信号。UP 投稿清单导出本身
        // 不涉及付费内容，但「付费」在 B 站同样由登录态决定（未登录访问付费资源即返回付费提示），
        // 归入登录分支不会指向错误操作；若在本地剔除该分支，就等于把特征集复制成第二份，
        // 故此处整体沿用同一集合，差异只保留在本注释中。
        if (ErrorMapper.IsLoginRelated(logTail))
            return "最可能原因：未登录或登录凭据已失效——点「扫码登录」完成登录后重试";
        if (logTail.Contains("网络", StringComparison.Ordinal)
            || logTail.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || logTail.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || logTail.Contains("连接", StringComparison.Ordinal)
            || logTail.Contains("resolve", StringComparison.OrdinalIgnoreCase))
            return "最可能原因：网络连接问题——检查网络后重试";
        return "最可能原因：未登录、网络不通或被 B 站风控——先点「扫码登录」再重试；仍失败请换个网络环境稍后重试";
    }
}
