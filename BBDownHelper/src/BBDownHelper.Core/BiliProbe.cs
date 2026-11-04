using System.Net;
using System.Text.Json;

namespace BBDownHelper.Core;

/// <summary>
/// B 站接口探测（裸数字决策树 + b23.tv 短链解析）。
/// 判据严格来自 2026-11-03 实测：code 永远为 0，判据是 data != null && media_count > 0；
/// 合集(type=8)与系列(type=5)是两套命名空间、号码可碰撞，多候选必须交给用户选。
/// HTTP 访问可通过委托注入，便于在无网/风控环境下跑测试。
/// </summary>
public sealed class BiliApiProbe
{
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>匿名请求 UA（GUI 侧下载封面图沿用同一口径）。</summary>
    public const string UserAgent = UA;

    /// <summary>GET 返回 body；可注入 mock。参数：url, cookie。</summary>
    public Func<string, string?, Task<string?>>? GetStringOverride { get; set; }

    /// <summary>取 302 跳转目标；可注入 mock。参数：url。返回最终 URL。</summary>
    public Func<string, Task<string?>>? GetRedirectOverride { get; set; }

    /// <summary>
    /// v3.6 蓝队 P2：底层 HTTP 发送可注入（测试 412/403 降级重试矩阵用）。
    /// 生产为 null 走真实 HttpClient；注入时请求头已按 ApplyBrowserHeaders 组装完毕，可直接断言。
    /// </summary>
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendOverride { get; set; }

    private async Task<string?> GetStringAsync(string url, string? cookie) =>
        await GetStringAsync(url, cookie, CancellationToken.None);

    /// <summary>
    /// v3.4 预览卡用：带取消令牌的 GET。取消（防抖窗口内输入又变了）返回 null 静默。
    /// v3.6 蓝队 P2：api.bilibili.com 的 view 端点对「只有 UA、缺其余浏览器头」的请求返回 412 风控
    /// （2026-11-04 蓝队实测：带 UA 缺 Accept/sec-ch-ua/sec-fetch-* → 412；同 URL 不带 UA → 200）。
    /// 修法：首试补齐常见浏览器请求头；仍被 412/403 拦则降级重试一次（极简头不带 UA——蓝队实测可 200）；
    /// 其余失败（超时/404/网络异常）不重试，维持「静默失败返回 null」总体契约。
    /// </summary>
    private async Task<string?> GetStringAsync(string url, string? cookie, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;
        if (GetStringOverride is not null) return await GetStringOverride(url, cookie);
        var (body, blocked) = await TryGetAsync(url, cookie, ct, fullHeaders: true);
        if (body is not null || !blocked || ct.IsCancellationRequested) return body;
        (body, _) = await TryGetAsync(url, cookie, ct, fullHeaders: false);
        return body;
    }

    /// <summary>单次 GET：成功返回 (body,false)；412/403 返回 (null,true) 允许上层降级重试；其余失败 (null,false)。</summary>
    private async Task<(string? Body, bool Blocked)> TryGetAsync(
        string url, string? cookie, CancellationToken ct, bool fullHeaders)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyBrowserHeaders(req, fullHeaders);
            if (!string.IsNullOrEmpty(cookie)) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var resp = SendOverride is not null
                ? await SendOverride(req, ct)
                : await client.SendAsync(req, ct);
            int status = (int)resp.StatusCode;
            if (status is 412 or 403) return (null, true);   // 风控拦截 → 上层降级重试一次
            if (!resp.IsSuccessStatusCode) return (null, false);
            return (await resp.Content.ReadAsStringAsync(ct), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (null, false); }
        catch { return (null, false); }
    }

    /// <summary>
    /// v3.6 蓝队 P2：B 站 API 请求头两档。
    /// fullHeaders=true：Chrome 全家桶（UA + Accept + Accept-Language + sec-ch-ua* + sec-fetch-*），
    ///                   对齐浏览器从 www.bilibili.com 页面发 fetch 的真实头形态；
    /// fullHeaders=false：降级重试档，只留 Referer、不带 UA（蓝队实测同 URL 无 UA 可 200）。
    /// internal 供测试断言首试/重试两档头部形态。
    /// </summary>
    internal static void ApplyBrowserHeaders(HttpRequestMessage req, bool fullHeaders)
    {
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        if (!fullHeaders) return;
        req.Headers.TryAddWithoutValidation("User-Agent", UA);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        req.Headers.TryAddWithoutValidation("sec-ch-ua",
            "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-site");
    }

    /// <summary>b23.tv 短链：跟随 302 拿最终 URL。失败返回 null。</summary>
    public async Task<string?> ResolveShortLinkAsync(string shortUrl)
    {
        if (GetRedirectOverride is not null) return await GetRedirectOverride(shortUrl);
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            string current = shortUrl;
            for (int i = 0; i < 6; i++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                req.Headers.TryAddWithoutValidation("User-Agent", UA);
                using var resp = await client.SendAsync(req);
                if (resp.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect)
                {
                    var loc = resp.Headers.Location;
                    if (loc is null) return null;
                    current = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(current), loc).ToString();
                    if (!current.Contains("b23.tv")) return current;   // 已跳出短链域
                    continue;
                }
                // 有些短链直接 200（极少）：视为解析终点
                return current.Contains("b23.tv") ? null : current;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>探测 medialist（type=5 系列 / type=8 合集）。判据：data != null && media_count > 0。</summary>
    public async Task<NumberCandidate?> ProbeMediaListAsync(long bizId, int type, string? cookie)
    {
        string url = $"https://api.bilibili.com/x/v1/medialist/info?type={type}&biz_id={bizId}";
        string? json = await GetStringAsync(url, cookie);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            int count = data.TryGetProperty("media_count", out var mc) && mc.ValueKind == JsonValueKind.Number
                ? mc.GetInt32() : 0;
            if (count <= 0) return null;               // 误配 ≠ 报错：media_count=0 不采用
            string title = data.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            long mid = data.TryGetProperty("mid", out var m) && m.ValueKind == JsonValueKind.Number
                ? m.GetInt64() : 0;
            string upName = await ProbeUpNameAsync(mid, cookie);
            var kind = type == 5 ? InputKind.Series : InputKind.Collection;
            string final = kind == InputKind.Series
                ? $"https://www.bilibili.com/channel/seriesdetail?sid={bizId}"
                : $"https://www.bilibili.com/channel/collectiondetail?sid={bizId}";
            return new NumberCandidate
            {
                Kind = kind, Title = title, UpName = upName,
                MediaCount = count, FinalInput = final
            };
        }
        catch { return null; }
    }

    /// <summary>探测 av 号：x/web-interface/view?aid=N，判据 code==0。</summary>
    public async Task<NumberCandidate?> ProbeVideoAsync(long aid, string? cookie)
    {
        string url = $"https://api.bilibili.com/x/web-interface/view?aid={aid}";
        string? json = await GetStringAsync(url, cookie);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 0) return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            string title = data.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string upName = "";
            if (data.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object
                && owner.TryGetProperty("name", out var n))
                upName = n.GetString() ?? "";
            return new NumberCandidate
            {
                Kind = InputKind.SingleVideo, Title = title,
                UpName = string.IsNullOrEmpty(upName) ? "未知 UP 主" : upName,
                MediaCount = 0, FinalInput = $"av{aid}"
            };
        }
        catch { return null; }
    }

    private async Task<string> ProbeUpNameAsync(long mid, string? cookie)
    {
        if (mid <= 0) return "未知 UP 主";
        string? json = await GetStringAsync($"https://api.bilibili.com/x/space/acc/info?mid={mid}", cookie);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("name", out var n))
                {
                    string? name = n.GetString();
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { }
        }
        return $"UP主ID: {mid}";
    }

    /// <summary>
    /// v3.4 预览卡：单视频预览（支持 BV 号与 av 号——旧探测只支持 view?aid=，BV 走 view?bvid=）。
    /// 网络失败/取消返回 null（静默不显示）；接口明确 code != 0 返回 NotFound=true（温和提示）。
    /// </summary>
    public async Task<VideoPreview?> ProbeViewPreviewAsync(string finalInput, string? cookie, CancellationToken ct)
    {
        string? url = ViewUrlFor(finalInput);
        if (url is null) return null;
        string? json = await GetStringAsync(url, cookie, ct);
        return ParseViewPreview(json);
    }

    /// <summary>识别结果的最终输入（BV 号 / av 号）→ view 接口 URL；不是单视频形态返回 null。</summary>
    public static string? ViewUrlFor(string finalInput)
    {
        if (string.IsNullOrEmpty(finalInput)) return null;
        finalInput = finalInput.Trim();
        if (InputRecognizer.BvFullRegex().IsMatch(finalInput))
            return $"https://api.bilibili.com/x/web-interface/view?bvid={finalInput}";
        if (InputRecognizer.AvFullRegex().IsMatch(finalInput)
            && long.TryParse(finalInput[2..], out long aid))
            return $"https://api.bilibili.com/x/web-interface/view?aid={aid}";
        return null;
    }

    /// <summary>
    /// 解析 view 响应为预览数据。null/JSON 损坏返回 null（静默）；code != 0 返回 NotFound。
    /// 2026-11-04 实测口径：data.pic 为 http:// 前缀（规范成 https）、data.pages 数组长度=分P数。
    /// </summary>
    internal static VideoPreview? ParseViewPreview(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number)
                return null;
            if (code.GetInt32() != 0)
                return new VideoPreview { NotFound = true };   // -400 请求错误 / -403 权限不足：可能不存在或已删除
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            string title = data.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string pic = data.TryGetProperty("pic", out var p) ? p.GetString() ?? "" : "";
            int pages = data.TryGetProperty("pages", out var ps) && ps.ValueKind == JsonValueKind.Array
                ? ps.GetArrayLength() : 0;
            string upName = "";
            if (data.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object
                && owner.TryGetProperty("name", out var n))
                upName = n.GetString() ?? "";
            return new VideoPreview
            {
                Kind = InputKind.SingleVideo,
                Title = title,
                CoverUrl = NormalizeCoverUrl(pic),
                PageCount = pages,
                UpName = upName
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// v3.4 预览卡：合集/系列预览（medialist 接口）。
    /// 2026-11-04 实测口径：data.cover 可能是空串（该系列没有封面）、data.upper.name 为创建者昵称。
    /// 失败一律返回 null 静默（合集/系列不存在时该接口 data=null，不作文案提示）。
    /// </summary>
    public async Task<VideoPreview?> ProbeMediaListPreviewAsync(long bizId, int type, string? cookie, CancellationToken ct)
    {
        string url = $"https://api.bilibili.com/x/v1/medialist/info?type={type}&biz_id={bizId}";
        string? json = await GetStringAsync(url, cookie, ct);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            int count = data.TryGetProperty("media_count", out var mc) && mc.ValueKind == JsonValueKind.Number
                ? mc.GetInt32() : 0;
            if (count <= 0) return null;
            string title = data.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string cover = data.TryGetProperty("cover", out var c) ? NormalizeCoverUrl(c.GetString() ?? "") : "";
            string upName = "";
            if (data.TryGetProperty("upper", out var up) && up.ValueKind == JsonValueKind.Object
                && up.TryGetProperty("name", out var un))
                upName = un.GetString() ?? "";
            return new VideoPreview
            {
                Kind = type == 5 ? InputKind.Series : InputKind.Collection,
                Title = title,
                CoverUrl = cover,
                MediaCount = count,
                UpName = upName
            };
        }
        catch { return null; }
    }

    /// <summary>封面 URL 规范：B 站 view 接口的 pic 返回 http:// 前缀，统一升 https。</summary>
    internal static string NormalizeCoverUrl(string pic)
    {
        if (string.IsNullOrEmpty(pic)) return "";
        return pic.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + pic["http://".Length..]
            : pic;
    }

    /// <summary>
    /// 裸数字三路探测决策树：并行 type=5 / type=8 / view?aid=N。
    /// 0 候选 → 空列表；>=2 → 调用方必须弹候选列表让用户单选（禁止静默选第一个）。
    /// </summary>
    public async Task<List<NumberCandidate>> ProbeBareNumberAsync(string digits, string? cookie)
    {
        if (!InputRecognizer.DigitsRegex().IsMatch(digits)) return new List<NumberCandidate>();
        // 红 P3-5：>=19 位的裸数字会让 long.Parse 抛 OverflowException 导致识别静默卡死，改 TryParse 兜底
        if (!long.TryParse(digits, out long n)) return new List<NumberCandidate>();
        var t5 = ProbeMediaListAsync(n, 5, cookie);
        var t8 = ProbeMediaListAsync(n, 8, cookie);
        var tv = ProbeVideoAsync(n, cookie);
        await Task.WhenAll(t5, t8, tv);
        var list = new List<NumberCandidate>();
        if (t5.Result is not null) list.Add(t5.Result);
        if (t8.Result is not null) list.Add(t8.Result);
        if (tv.Result is not null) list.Add(tv.Result);
        return list;
    }
}
