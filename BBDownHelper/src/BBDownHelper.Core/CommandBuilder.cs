using System.Text;

namespace BBDownHelper.Core;

/// <summary>用户在界面上勾选的需求（与 bbdown_web_spec.md 选项区逐条对应）。</summary>
public sealed class HelperOptions
{
    public bool MultiThread { get; set; } = true;          // -mt 默认勾
    public string Quality { get; set; } = "auto";          // auto / ia / q1080 / q4k / q480
    public string Encoding { get; set; } = "auto";         // auto / hevc / avc
    public bool AudioOnly { get; set; }
    public string Danmaku { get; set; } = "none";          // none / dd / only
    public bool CoverOnly { get; set; }
    public bool SubOnly { get; set; }
    public string? SelectPage { get; set; }                // -p 原文，校验后使用
    public string? WorkDir { get; set; }                   // --work-dir
    public bool DelayPerPage { get; set; }                 // --delay-per-page 5（列表类显示）
    public bool SaveArchives { get; set; }                 // --save-archives-to-file（列表类显示）
    public bool InfoOnly { get; set; }                     // -info
}

/// <summary>
/// 命令组装。参数名/取值全部出自 BBDown 源码 CommandLineInvoker.cs，禁止凭印象造 flag。
/// 产出 args 数组（供 ProcessStartInfo.ArgumentList，无 shell 注入面）与展示用命令字符串。
/// </summary>
public static class CommandBuilder
{
    public sealed record BuiltCommand(List<string> Args, string Display);

    public static BuiltCommand Build(RecognitionResult rec, HelperOptions opt)
    {
        var args = new List<string>();

        if (opt.InfoOnly)
        {
            args.Add("-info");                              // -info 勾选后其他全部忽略
        }
        else
        {
            // 「只要 XX」与内容类选项互斥（UI 已灰掉，这里再兜一道）
            bool contentLocked = opt.CoverOnly || opt.SubOnly || opt.Danmaku == "only";
            // 蓝 P2-1：只要音频 / 只要XX 生效时，画质与编码无意义（-q 会静默失效、违反规格互斥），直接跳过不生成
            bool skipQualityEncoding = contentLocked || opt.AudioOnly;

            if (opt.MultiThread) args.Add("-mt");

            if (!skipQualityEncoding)
            {
                // 清晰度单选组（-ia 与 -q 天然互斥）
                switch (opt.Quality)
                {
                    case "ia": args.Add("-ia"); break;
                    case "q1080": args.Add("-q"); args.Add("1080P 高码率,1080P 高清,720P 高清"); break;
                    case "q4k": args.Add("-q"); args.Add("4K 超清,1080P 高码率,1080P 高清"); break;
                    case "q480": args.Add("-q"); args.Add("480P 清晰,360P 流畅"); break;
                }

                // 编码单选组
                switch (opt.Encoding)
                {
                    case "hevc": args.Add("--encoding-priority"); args.Add("hevc,av1,avc"); break;
                    case "avc": args.Add("--encoding-priority"); args.Add("avc,av1,hevc"); break;
                }
            }

            if (!contentLocked)
            {
                if (opt.AudioOnly) args.Add("--audio-only");
                if (opt.Danmaku == "dd") args.Add("-dd");
            }
            if (opt.Danmaku == "only") args.Add("--danmaku-only");
            if (opt.CoverOnly) args.Add("--cover-only");
            if (opt.SubOnly) args.Add("--sub-only");

            // 选集（白名单校验写法：数字、逗号、连字符、ALL/LAST）
            if (!string.IsNullOrWhiteSpace(opt.SelectPage) && IsValidSelectPage(opt.SelectPage))
            {
                args.Add("-p"); args.Add(opt.SelectPage.Trim());
            }

            // 列表类辅助
            if (rec.IsListKind)
            {
                if (opt.DelayPerPage) { args.Add("--delay-per-page"); args.Add("5"); }
                if (opt.SaveArchives) args.Add("--save-archives-to-file");
            }
        }

        if (!string.IsNullOrWhiteSpace(opt.WorkDir))
        {
            args.Add("--work-dir"); args.Add(opt.WorkDir);
        }

        args.Add(rec.FinalInput);

        return new BuiltCommand(args, ToDisplay(args));
    }

    /// <summary>-p 写法白名单：8 / 1,2 / 3-5 / ALL / LAST（规格指定）。</summary>
    public static bool IsValidSelectPage(string s)
    {
        s = s.Trim();
        if (s.Equals("ALL", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("LAST", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (InputRecognizer.DigitsRegex().IsMatch(part)) continue;
            var seg = part.Split('-', StringSplitOptions.TrimEntries);
            if (seg.Length == 2 && InputRecognizer.DigitsRegex().IsMatch(seg[0]) && InputRecognizer.DigitsRegex().IsMatch(seg[1]))
                continue;
            return false;
        }
        return s.Length > 0;
    }

    /// <summary>展示用命令字符串；链接含 ? 或 & 时加英文双引号。</summary>
    private static string ToDisplay(List<string> args)
    {
        var sb = new StringBuilder("BBDown");
        foreach (var a in args)
        {
            sb.Append(' ');
            if (a.Contains('?') || a.Contains('&') || a.Contains(' ') || a.Contains('"'))
                sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
            else
                sb.Append(a);
        }
        return sb.ToString();
    }
}
