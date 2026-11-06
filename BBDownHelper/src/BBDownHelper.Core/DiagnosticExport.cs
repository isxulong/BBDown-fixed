using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BBDownHelper.Core;

/// <summary>
/// v3.8 求助导出（PM#5 / 需求 R2.8）：一键生成诊断文件——套件版本 + 系统信息 + 日志尾部。
/// v3.9（红队 P2-1 修复）：命中敏感键名（SESSDATA 等）后其后的值一律打码，覆盖
/// = / : 键值、JSON 引号键值（"SESSDATA":"..."）、tab/多空白分隔、引号包裹的带空格值；
/// 打码无法保证覆盖一切书写形态，导出文案按实际能力如实描述。纯逻辑可单测。
/// </summary>
public static partial class DiagnosticExport
{
    // 敏感键名后的值一律打码（v3.9 加宽）：
    //   形态1 键值对：SESSDATA=xxx / SESSDATA: xxx（等号/冒号两侧可带空白，键后可带 JSON 引号）
    //   形态2 分隔符：tab 或 ≥2 个空格分隔（如 SESSDATA\t<值>）
    //   值：引号包裹（可含空格）整体打码，或未引号的一段非空白 token
    [GeneratedRegex(@"(?i)(SESSDATA|bili_jct|DedeUserID|refresh_token|access_key|cookie)(""?\s*[=:]\s*|(?:[\t ]*\t[\t ]*| {2,}))(""[^""]*""|[^\s;,}""]+)")]
    private static partial Regex SecretRx();

    /// <summary>打码一行日志里的凭据值（保留字段名与分隔符，值替换为 ***；引号包裹的值保留引号）。</summary>
    public static string Scrub(string line) => SecretRx().Replace(line, m =>
    {
        var v = m.Groups[3].Value;
        var masked = v.Length >= 2 && v[0] == '"' ? "\"***\"" : "***";
        return m.Groups[1].Value + m.Groups[2].Value + masked;
    });

    /// <summary>组装诊断文件内容。</summary>
    public static string BuildContent(string suiteVersion, string? bbdownVersion, IReadOnlyList<string> logTail)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BBDown-fixed 下载助手 · 求助诊断文件");
        sb.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"套件版本：{suiteVersion}");
        sb.AppendLine($"BBDown 内核：{(string.IsNullOrEmpty(bbdownVersion) ? "未知" : bbdownVersion)}");
        sb.AppendLine($"系统：{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）");
        sb.AppendLine($"运行时：{RuntimeInformation.FrameworkDescription}");
        sb.AppendLine("说明：日志中命中敏感键名（SESSDATA 等）的值已打码，覆盖 = / : 键值、JSON 引号键值、tab/多空白分隔等常见形态；打码不保证覆盖所有书写形式，发出前请自行过目一遍。");
        sb.AppendLine("——— 最近日志 ———");
        foreach (var line in logTail)
            sb.AppendLine(Scrub(line));
        return sb.ToString();
    }

    /// <summary>求助文件建议落点：桌面；取不到桌面（极少见）返回 null，调用方回退程序目录。</summary>
    public static string? DesktopDir()
    {
        try
        {
            var d = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return string.IsNullOrEmpty(d) ? null : d;
        }
        catch { return null; }
    }

    /// <summary>尽力读 core\BBDown.exe 的文件版本；读不到返回 null（不影响导出）。</summary>
    public static string? TryGetBbdownVersion(string bbdownPath)
    {
        try
        {
            if (!File.Exists(bbdownPath)) return null;
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(bbdownPath);
            return v.FileVersion ?? v.ProductVersion;
        }
        catch { return null; }
    }
}
