using System.Text;
using System.Text.RegularExpressions;

namespace BBDownHelper.Core;

/// <summary>
/// 输入净化 + 分类（纯逻辑、无网络，可在 Linux 上直接测试）。
/// 逐条实现《BBDown 输入识别规则 —— 共享规格》：
///  - 净化优先级：b23.tv 短链 → 完整 URL → BV → av → 纯数字(>=4位)
///  - 11 类分类，命中即停
///  - 新版 space/lists 链接必须转换为 collectiondetail/seriesdetail 旧格式
///  - 安全红线：用户原始粘贴文本永不直接进命令行，只用提取 token 白名单重建
/// </summary>
public static partial class InputRecognizer
{
    // ---------- 第 0 步：输入净化 ----------

    /// <summary>按优先级从粘贴文本中提取第一个命中 token；全部不中返回 null。</summary>
    public static string? ExtractToken(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        var m = B23Regex().Match(rawText);            // 1. b23.tv 短链
        if (m.Success) return "https://" + m.Value;

        m = UrlRegex().Match(rawText);                // 2. 完整 URL
        if (m.Success) return m.Value;

        m = BvSearchRegex().Match(rawText);           // 3. BV 号
        if (m.Success) return m.Value;

        m = AvSearchRegex().Match(rawText);           // 4. av 号
        if (m.Success) return m.Value;

        m = BareNumberRegex().Match(rawText);         // 5. 纯数字 >=4 位
        if (m.Success) return m.Value;

        return null;
    }

    // ---------- 第 1 步：分类（命中即停） ----------

    /// <summary>对已提取的 token 分类。不做任何网络请求（b23.tv / 裸数字由上层探测后再走这里）。</summary>
    public static RecognitionResult Classify(string token)
    {
        token = token.Trim();
        var r = new RecognitionResult();

        // 规则 11：b23.tv 短链 → 待解析（由上层跟随 302 后重新分类）
        // v3.1 P3：锚定 ^https://b23.tv/，防止 https://b23.tv.evil.com/abc 被误判为短链
        if (B23AnchorRegex().IsMatch(token))
        {
            r.Kind = InputKind.ShortLink;
            r.KindName = "短链接（需解析）";
            r.Message = "短链接需要联网解析后才能识别。";
            return r;
        }

        // URL 类输入必须先通过白名单校验 + 重建（安全红线 P1-②），host 非法直接拒绝
        string? rebuiltUrl = null;
        if (token.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            rebuiltUrl = RebuildUrl(token, out _, out var rebuildError);
            if (rebuiltUrl is null)
            {
                // 域名确实不对：如实拒绝（文案只在域名场景提「域名」，不得误报 B 站链接）
                if (rebuildError == UrlRebuildError.InvalidHost)
                    return Unknown("这个链接不属于 bilibili.com 域名，请复制 B 站地址栏里的完整链接重试。");
                // 红 P2-1：整体校验失败（多为粘贴时混入中文/全角字符）时，在 token 内回退搜索 BV/av 号再试一次
                var bvRetry = BvSearchRegex().Match(token);
                if (bvRetry.Success) token = bvRetry.Value;
                else
                {
                    var avRetry = AvSearchRegex().Match(token);
                    if (!avRetry.Success)
                        return Unknown("没认出这个链接（可能混入了多余文字）。请复制浏览器地址栏里的完整链接重试。");
                    token = avRetry.Value;
                }
            }
        }

        // 规则 1：BV 号（裸 token 或合法 B 站 URL 里的 /video/BV）
        var bvMatch = BvInUrlOrBare().Match(token);
        if (BvFullRegex().IsMatch(token) || bvMatch.Success)
        {
            string bv = BvFullRegex().IsMatch(token) ? token : bvMatch.Groups[1].Value;
            if (!BvFullRegex().IsMatch(bv)) return Unknown("BV 号格式不对，请检查后重试。");
            r.Kind = InputKind.SingleVideo;
            r.KindName = "单个视频";
            r.FinalInput = bv;                       // BV 全串已白名单校验，直接用本体
            (r.SelectPageSuggestion, _) = ExtractPageParam(token);
            return r;
        }

        // 规则 2：av 号
        var avMatch = AvInUrlOrBare().Match(token);
        if (AvFullRegex().IsMatch(token) || avMatch.Success)
        {
            string digits = AvFullRegex().IsMatch(token)
                ? token[2..]
                : avMatch.Groups[1].Value;
            if (!DigitsRegex().IsMatch(digits)) return Unknown("av 号格式不对，请检查后重试。");
            r.Kind = InputKind.SingleVideo;
            r.KindName = "单个视频";
            r.FinalInput = "av" + digits;
            (r.SelectPageSuggestion, _) = ExtractPageParam(token);
            return r;
        }

        if (rebuiltUrl is not null)
        {
            string rebuilt = rebuiltUrl;

            // 规则 3：番剧/影视
            if (EpSsInPath().IsMatch(rebuilt) && rebuilt.Contains("/bangumi/play/"))
            {
                r.Kind = InputKind.Bangumi; r.KindName = "番剧/影视";
                r.FinalInput = rebuilt; (r.SelectPageSuggestion, _) = ExtractPageParam(token);
                return r;
            }
            // 规则 4：课程
            if (rebuilt.Contains("/cheese/play/"))
            {
                r.Kind = InputKind.Cheese; r.KindName = "课程";
                r.FinalInput = rebuilt; (r.SelectPageSuggestion, _) = ExtractPageParam(token);
                return r;
            }
            // 规则 5/6：新版 space lists 链接 → 必须转换为旧格式（直接喂会下载整个 UP 主页）
            var lists = NewListsRegex().Match(rebuilt);
            if (lists.Success)
            {
                string sid = lists.Groups[2].Value;
                string? type = GetQueryValue(rebuilt, "type");
                if (type == "season")
                {
                    r.Kind = InputKind.Collection; r.KindName = "合集";
                    r.FinalInput = $"https://www.bilibili.com/channel/collectiondetail?sid={sid}";
                    return r;
                }
                if (type == "series")
                {
                    r.Kind = InputKind.Series; r.KindName = "系列";
                    r.FinalInput = $"https://www.bilibili.com/channel/seriesdetail?sid={sid}";
                    return r;
                }
                return Unknown("这个新版列表链接缺少 type 参数，请回到 B 站重新复制完整链接（地址栏里应带 ?type=season 或 ?type=series）。");
            }
            // 规则 7：旧版合集
            if (rebuilt.Contains("/channel/collectiondetail?sid=") ||
                (rebuilt.Contains("/medialist/") && rebuilt.Contains("business=space_collection") && rebuilt.Contains("business_id=")))
            {
                r.Kind = InputKind.Collection; r.KindName = "合集";
                r.FinalInput = rebuilt; (r.SelectPageSuggestion, _) = ExtractPageParam(token);
                return r;
            }
            // 规则 8：旧版系列
            if (rebuilt.Contains("/channel/seriesdetail?sid=") ||
                (rebuilt.Contains("/medialist/") && rebuilt.Contains("business=space_series") && rebuilt.Contains("business_id=")))
            {
                r.Kind = InputKind.Series; r.KindName = "系列";
                r.FinalInput = rebuilt; (r.SelectPageSuggestion, _) = ExtractPageParam(token);
                return r;
            }
            // 规则 9：收藏夹
            if (SpaceMidRegex().IsMatch(rebuilt) && rebuilt.Contains("/favlist"))
            {
                r.Kind = InputKind.Favorites; r.KindName = "收藏夹";
                r.FinalInput = rebuilt;
                return r;
            }
            // 规则 10：UP 主主页（以上都不中）→ 强制二次确认 + 默认 -info
            // v3：BBDown 1.6.3 对 space 主页只会导出「UP昵称的投稿视频.txt」清单，
            // 然后打印红字「暂不支持该功能」退出（BBDown 源码 SpaceVideoFetcher.cs 实锤，
            // 该红字是固定提示、不是出错）——必须如实告诉用户，避免被红字吓到
            if (SpaceMidRegex().IsMatch(rebuilt))
            {
                r.Kind = InputKind.UpSpace; r.KindName = "UP 主主页（全部投稿）";
                r.FinalInput = rebuilt;
                r.RequiresConfirm = true;
                r.Message = "BBDown 不支持一键下载 UP 主全部投稿：运行后只会把该 UP 的全部投稿地址导出为工作目录下的『UP昵称的投稿视频.txt』清单文件；程序结尾的红字『暂不支持该功能』是 BBDown 的固定提示，不是出错。想批量下载清单里的视频，可用 bat下载命令生成器.html 粘贴同一个链接生成两步 bat。";
                return r;
            }
            return Unknown("没认出这个链接。目前支持：视频链接/BV号/av号、番剧、课程、合集、系列、收藏夹、UP主主页。");
        }

        // 裸数字 token（净化第 5 级命中）→ 待探测
        if (DigitsRegex().IsMatch(token))
        {
            r.Kind = InputKind.BareNumber;
            r.KindName = "纯数字（待核实）";
            r.FinalInput = "";                       // 多候选禁止静默采用，选定前不得有最终输入
            return r;
        }

        return Unknown("没认出这段内容。请粘贴 B 站链接、BV 号或 av 号。");
    }

    private static RecognitionResult Unknown(string msg) =>
        new() { Kind = InputKind.Unknown, KindName = "无法识别", Message = msg };

    // ---------- ?p=N 提取 ----------

    /// <summary>提取 URL 上的 p 参数（映射为 -p N，不留在 URL 里）。</summary>
    public static (string? page, string urlWithoutPage) ExtractPageParam(string url)
    {
        string? p = GetQueryValue(url, "p");
        if (p is null || !DigitsRegex().IsMatch(p)) return (null, url);
        return (p, url);
    }

    // ---------- 白名单 URL 重建（安全红线） ----------

    /// <summary>白名单重建失败原因（红 P2-1：用于给出不误导的提示，并决定是否回退搜 BV/av）。</summary>
    public enum UrlRebuildError { None, NotHttp, InvalidChar, InvalidHost }

    private static readonly char[] AllowedUrlChars =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._~!$&'()*+,;=:@%/?-#[]".ToCharArray();
    private static readonly HashSet<char> AllowedSet = new(AllowedUrlChars);

    /// <summary>
    /// 把提取出的 URL token 按白名单重建：协议头固定 https，host 限定 bilibili.com 域，
    /// 其余每个字符必须在白名单内。返回 null 表示校验失败。
    /// 注意：只使用 token 自身，绝不接触用户原始粘贴文本的其他部分。
    /// </summary>
    public static string? RebuildUrl(string token, out string? pageParam) =>
        RebuildUrl(token, out pageParam, out _);

    /// <summary>带失败原因的重建：调用方可据此区分「域名不对」（拒绝）与「混入杂字符」（回退搜 BV/av）。</summary>
    public static string? RebuildUrl(string token, out string? pageParam, out UrlRebuildError error)
    {
        pageParam = null;
        error = UrlRebuildError.None;
        if (!token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !token.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            error = UrlRebuildError.NotHttp;
            return null;
        }

        // host 校验优先于字符校验：非 bilibili 域名一律拒绝（InvalidHost），不给 BV/av 回退机会
        string rest = token[(token.IndexOf("://", StringComparison.Ordinal) + 3)..];
        int slash = rest.IndexOf('/');
        string host = slash >= 0 ? rest[..slash] : rest;
        host = host.ToLowerInvariant();
        // host 限定 bilibili.com 域（含子域）与 b23.tv（解析前）
        if (host != "bilibili.com" && !host.EndsWith(".bilibili.com", StringComparison.Ordinal) &&
            host != "b23.tv")
        {
            error = UrlRebuildError.InvalidHost;
            return null;
        }

        foreach (char c in token)
            if (!AllowedSet.Contains(c))
            {
                error = UrlRebuildError.InvalidChar;
                return null;
            }

        var sb = new StringBuilder("https://");
        sb.Append(host);
        if (slash >= 0)
        {
            string pathAndQuery = rest[slash..];
            // ?p=N 提取出来映射为 -p N，不留在 URL 里
            int q = pathAndQuery.IndexOf('?');
            if (q >= 0)
            {
                string path = pathAndQuery[..q];
                var kept = new List<string>();
                foreach (var pair in pathAndQuery[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    string k = eq > 0 ? pair[..eq] : pair;
                    if (k == "p")
                    {
                        string v = eq > 0 ? pair[(eq + 1)..] : "";
                        if (DigitsRegex().IsMatch(v)) { pageParam = v; continue; }
                    }
                    kept.Add(pair);
                }
                sb.Append(path);
                if (kept.Count > 0) sb.Append('?').Append(string.Join('&', kept));
            }
            else
            {
                sb.Append(pathAndQuery);
            }
        }
        return sb.ToString();
    }

    public static string? GetQueryValue(string url, string name)
    {
        int q = url.IndexOf('?');
        if (q < 0) return null;
        foreach (var pair in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == name) return pair[(eq + 1)..];
        }
        return null;
    }

    // ---------- 正则 ----------

    // 红 P2-1：\w 在 .NET 默认匹配 CJK，会把「https://b23.tv/abc123。快看」里的中文吞进 token，收紧为纯 ASCII
    [GeneratedRegex(@"b23\.tv/[A-Za-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex B23Regex();

    // 红 P2-1：字符集与白名单重建 AllowedUrlChars 保持一致，CJK 及全角标点天然在 token 边界处截断，
    // 「地址：https://...BV1xx411c7mD/，速来」「链接https://...BV.../真的好看」都能正确取出 URL
    [GeneratedRegex(@"https?://[A-Za-z0-9._~!$&'()*+,;=:@%/?\-#\[\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    // BV 字符集不含 0/O/I/l，按规格用 [A-Za-z0-9] 宽松匹配后由全串校验兜底
    [GeneratedRegex(@"BV1[A-Za-z0-9]{9}")]
    private static partial Regex BvSearchRegex();

    // v3.1 P2：.NET 的 \d 匹配 Unicode 全角数字（如「８」），中文输入法全角模式可穿透校验，
    // 本文件所有数字正则一律收紧为 [0-9]，杜绝全角入口（DigitsRegex / AvSearchRegex / BareNumberRegex 为必修三处，
    // 其余几处同根同修，确保不存在其他 Unicode 数字入口）
    [GeneratedRegex(@"av[0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex AvSearchRegex();

    [GeneratedRegex(@"[0-9]{4,}")]
    private static partial Regex BareNumberRegex();

    [GeneratedRegex(@"^BV1[A-Za-z0-9]{9}$")]
    public static partial Regex BvFullRegex();

    [GeneratedRegex(@"^av[0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex AvFullRegex();

    [GeneratedRegex(@"^[0-9]+$")]
    public static partial Regex DigitsRegex();

    [GeneratedRegex(@"/video/(BV1[A-Za-z0-9]{9})")]
    private static partial Regex BvInUrlOrBare();

    [GeneratedRegex(@"/video/av([0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AvInUrlOrBare();

    [GeneratedRegex(@"/(ep|ss)[0-9]+")]
    private static partial Regex EpSsInPath();

    [GeneratedRegex(@"space\.bilibili\.com/([0-9]+)/lists/([0-9]+)")]
    private static partial Regex NewListsRegex();

    [GeneratedRegex(@"space\.bilibili\.com/([0-9]+)")]
    private static partial Regex SpaceMidRegex();

    // v3.1 P3：短链判定锚定完整前缀，b23.tv.evil.com 之类仿冒域不再误判
    [GeneratedRegex(@"^https://b23\.tv/", RegexOptions.IgnoreCase)]
    private static partial Regex B23AnchorRegex();
}
