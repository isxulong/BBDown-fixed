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

    /// <summary>GET 返回 body；可注入 mock。参数：url, cookie。</summary>
    public Func<string, string?, Task<string?>>? GetStringOverride { get; set; }

    /// <summary>取 302 跳转目标；可注入 mock。参数：url。返回最终 URL。</summary>
    public Func<string, Task<string?>>? GetRedirectOverride { get; set; }

    private async Task<string?> GetStringAsync(string url, string? cookie)
    {
        if (GetStringOverride is not null) return await GetStringOverride(url, cookie);
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UA);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            if (!string.IsNullOrEmpty(cookie)) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync();
        }
        catch { return null; }
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
