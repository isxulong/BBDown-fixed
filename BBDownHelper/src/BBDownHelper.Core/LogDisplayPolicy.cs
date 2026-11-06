using System.Text.RegularExpressions;

namespace BBDownHelper.Core;

/// <summary>
/// v3.8 日志降噪判定（PM#11/UI#8 / 需求 R2.7，纯函数可单测）：
/// 简洁模式只显示「进度行 + 关键结果行 + 我们写的已知提示行」；BBDown 的逐行细节
/// （形如 [2026-11-05 12:00:00] - … 的时间戳行）默认折叠，经「显示详细输出」开关查看。
/// 简洁模式只折叠展示、不销毁信息（LogStore 全量留存，诊断包取完整日志）。
/// </summary>
public static partial class LogDisplayPolicy
{
    // BBDown 自己的时间戳行：[2026-11-05 12:00:00.123] - ……
    [GeneratedRegex(@"^\[\d{4}-\d{2}-\d{2} ")]
    private static partial Regex BbdTimestampRx();

    /// <summary>关键结果行（完成/失败/错误/成功）：简洁模式显示且底色高亮。</summary>
    public static bool IsKeyLine(string line) =>
        line.Contains("完成", StringComparison.Ordinal)
        || line.Contains("失败", StringComparison.Ordinal)
        || line.Contains("错误", StringComparison.Ordinal)
        || line.Contains("成功", StringComparison.Ordinal)
        || line.Contains("出错", StringComparison.Ordinal)
        || line.Contains("已取消", StringComparison.Ordinal);

    /// <summary>简洁模式下是否可见：非 BBDown 时间戳行（我们自己的提示/命令回显）全显示；
    /// BBDown 时间戳行里只放行进度行（含 %）与关键结果行。</summary>
    public static bool IsSimpleVisible(string line)
    {
        if (!BbdTimestampRx().IsMatch(line)) return true;
        if (line.Contains('%')) return true;          // 进度行
        return IsKeyLine(line);
    }
}
