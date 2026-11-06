namespace BBDownHelper.Core;

/// <summary>
/// v4.0（批次3 R3.13 / 架构 §12）：UP 主链路 → bat 生成器入口（纯逻辑可单测）。
/// EXE 导出 UP 投稿清单成功后，提供「打开清单所在文件夹」「打开批量生成器」两个入口；
/// 生成器经 file URL 的 hash 参数预填链接：#up=&lt;url-encoded UP 主主页链接&gt;，
/// 生成器 html onload 读取 location.hash 预填并触发识别。
/// 架构 §12 边界：「界面内一键生成 bat」不进 v1.0.4，本类只做打开+预填。
/// </summary>
public static class GeneratorLink
{
    /// <summary>生成器 html 文件名（与解压包内文件名一致）。</summary>
    public const string GeneratorFileName = "bat下载命令生成器.html";

    /// <summary>生成器完整路径（程序根目录下）。</summary>
    public static string GeneratorPath(string baseDir) =>
        Path.Combine(baseDir, GeneratorFileName);

    /// <summary>生成器是否随包就位。</summary>
    public static bool Exists(string baseDir) => File.Exists(GeneratorPath(baseDir));

    /// <summary>
    /// 生成带预填参数的 file URI。upUrl 为空时退化为纯 file URI（不带 hash）。
    /// 空格/中文等经 Uri.EscapeDataString 编码；浏览器打开 file URI 时 hash 原样带到 location.hash。
    /// </summary>
    public static string BuildUri(string baseDir, string? upUrl)
    {
        string fileUri = new Uri(GeneratorPath(baseDir)).AbsoluteUri;
        if (string.IsNullOrEmpty(upUrl)) return fileUri;
        return fileUri + "#up=" + Uri.EscapeDataString(upUrl);
    }
}
