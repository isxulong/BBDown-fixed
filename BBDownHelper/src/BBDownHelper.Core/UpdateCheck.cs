using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace BBDownHelper.Core;

/// <summary>一次「发现新版本」的结论：最新版本 tag 与发布页地址。</summary>
public sealed record UpdateInfo(string LatestTag, string ReleaseUrl);

/// <summary>检查更新的缓存记录：上次成功检查的时刻 + 当时的最新版本。</summary>
public sealed record UpdateCheckCache(DateTimeOffset CheckedAtUtc, UpdateInfo Info);

/// <summary>
/// v3.5 检查更新的纯逻辑：语义化版本比较、是否提示判定、缓存新鲜度、
/// GitHub latest release JSON 解析、缓存序列化。全部无副作用、可单测。
/// </summary>
public static class UpdateCheckLogic
{
    /// <summary>两次检查之间的最小间隔（间隔内不重复请求）。</summary>
    public static readonly TimeSpan CacheInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// 解析 "v1.2.3" / "1.2.3" / "v1.2" / "v1.2.3-beta1" 为 (major, minor, patch)。
    /// v 前缀可选；最多 3 段、缺段按 0；预发布后缀（-beta 等）忽略不参与比较。
    /// </summary>
    public static bool TryParseSemVer(string? tag, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var t = tag.Trim();
        if (t[0] is 'v' or 'V') t = t[1..];
        int dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash];
        var parts = t.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        var nums = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                return false;
            nums[i] = n;
        }
        (major, minor, patch) = (nums[0], nums[1], nums[2]);
        return true;
    }

    /// <summary>语义化版本比较：a&lt;b 负、相等 0、a&gt;b 正。不可解析按 0.0.0。</summary>
    public static int CompareSemVer(string? a, string? b)
    {
        TryParseSemVer(a, out int am, out int an, out int ap);
        TryParseSemVer(b, out int bm, out int bn, out int bp);
        if (am != bm) return am.CompareTo(bm);
        if (an != bn) return an.CompareTo(bn);
        return ap.CompareTo(bp);
    }

    /// <summary>是否提示更新：latest 必须可解析且严格大于当前版本（同版本/更旧/非法一律不提示）。</summary>
    public static bool ShouldPrompt(string? currentTag, string? latestTag) =>
        TryParseSemVer(latestTag, out _, out _, out _)
        && CompareSemVer(latestTag, currentTag) > 0;

    /// <summary>
    /// 缓存是否新鲜（间隔内不重复请求）。负差值（时钟回拨/系统时间被改）
    /// 视为新鲜——宁可少查一次，不因时钟问题多打请求。
    /// </summary>
    public static bool IsCacheFresh(DateTimeOffset checkedAtUtc, DateTimeOffset nowUtc,
        TimeSpan? interval = null) =>
        nowUtc - checkedAtUtc < (interval ?? CacheInterval);

    /// <summary>解析 GitHub latest release 响应：取 tag_name 与 html_url。字段缺失/非法返回 null。</summary>
    public static UpdateInfo? ParseReleaseJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var t) || t.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("html_url", out var u) || u.ValueKind != JsonValueKind.String) return null;
            var tag = t.GetString();
            var url = u.GetString();
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(url)) return null;
            return new UpdateInfo(tag, url);
        }
        catch { return null; }
    }

    /// <summary>缓存文件序列化。</summary>
    public static string SerializeCache(UpdateCheckCache cache) =>
        JsonSerializer.Serialize(new
        {
            checkedAtUtc = cache.CheckedAtUtc,
            latestTag = cache.Info.LatestTag,
            releaseUrl = cache.Info.ReleaseUrl
        });

    /// <summary>缓存文件解析。字段缺失/非法返回 null。</summary>
    public static UpdateCheckCache? DeserializeCache(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("checkedAtUtc", out var t)
                || !t.TryGetDateTimeOffset(out var checkedAt)) return null;
            if (!root.TryGetProperty("latestTag", out var tagEl) || tagEl.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("releaseUrl", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) return null;
            var tag = tagEl.GetString();
            var url = urlEl.GetString();
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(url)) return null;
            return new UpdateCheckCache(checkedAt, new UpdateInfo(tag, url));
        }
        catch { return null; }
    }
}

/// <summary>
/// v3.5 检查更新执行器。规则：
/// 1. 缓存新鲜（24 小时内成功检查过）→ 不发请求，直接按缓存里的最新版本判定；
/// 2. 缓存缺失/过期 → 请求 GitHub latest release（≤5s 超时），成功后把结果写入缓存；
/// 3. 网络失败/超时/解析失败/缓存读写失败 → 一律静默（返回 null、不抛、不写缓存），下次启动再试。
/// 任何异常都不得影响启动与主流程——本类是纯增量。
/// HTTP 与文件 IO 均可注入（mock / 抛异常），与 BiliApiProbe 同一模式，便于测试。
/// </summary>
public sealed class UpdateChecker
{
    public const string LatestReleaseApi = "https://api.github.com/repos/isxulong/BBDown-fixed/releases/latest";

    /// <summary>v3.5 规格明文：检查更新请求超时 5s（不经统一 15s 默认档）。internal 供测试断言口径一致。</summary>
    internal const int TimeoutSeconds = 5;

    private readonly NetFacade _facade;

    /// <summary>
    /// v3.7：经 NetFacade 注入（生产注入 NetFacade.Shared，412 冷却全局共享）。
    /// 默认新建独立实例，保持既有单测互不影响。
    /// </summary>
    public UpdateChecker(NetFacade? facade = null) => _facade = facade ?? new NetFacade();

    /// <summary>GET 返回 body；可注入 mock。参数：url。</summary>
    public Func<string, Task<string?>>? GetStringOverride { get; set; }

    /// <summary>当前时刻（UTC）；可注入（测 24h 间隔用）。</summary>
    public Func<DateTimeOffset> NowUtc { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>缓存文件读取；可注入 mock/异常。参数：path。</summary>
    internal Func<string, string?>? ReadTextOverride { get; set; }

    /// <summary>缓存文件写入；可注入 mock/异常。参数：path, content。</summary>
    internal Action<string, string>? WriteTextOverride { get; set; }

    /// <summary>
    /// 检查一次更新。返回非 null = 「应提示有新版本」。
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(string? currentTag, string cachePath)
    {
        try
        {
            // 1) 缓存新鲜 → 不重复请求，按缓存判定
            var cached = ReadCache(cachePath);
            if (cached is not null && UpdateCheckLogic.IsCacheFresh(cached.CheckedAtUtc, NowUtc()))
                return UpdateCheckLogic.ShouldPrompt(currentTag, cached.Info.LatestTag) ? cached.Info : null;

            // 2) 请求 latest release（v3.7：412 冷却期内静默降级，不发请求、不写缓存，下次启动再试）
            if (_facade.InRiskCooldown) return null;
            string? json = GetStringOverride is not null
                ? await GetStringOverride(LatestReleaseApi)
                : await FetchAsync();
            var info = UpdateCheckLogic.ParseReleaseJson(json);
            if (info is null) return null;   // 失败静默、不写缓存：下次启动再试

            WriteCache(cachePath, new UpdateCheckCache(NowUtc(), info));
            // 3) 是否提示
            return UpdateCheckLogic.ShouldPrompt(currentTag, info.LatestTag) ? info : null;
        }
        catch { return null; }   // 兜底：任何环节抛异常都按「无新版本」处理
    }

    private async Task<string?> FetchAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            // GitHub API 要求 UA；v3.7 起 UA 唯一来源为 NetFacade.UserAgent（BiliApiProbe.UserAgent 是其别名）
            req.Headers.TryAddWithoutValidation("User-Agent", NetFacade.UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            // v3.7：改经 NetFacade 共享 HttpClient；5s 超时按 v3.5 规格以 NetPolicy 显式传入
            using var resp = await _facade.SendAsync(
                req, new NetPolicy(TimeSpan.FromSeconds(TimeoutSeconds), true), CancellationToken.None);
            if (resp is null || !resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync();
        }
        catch { return null; }   // 超时/被墙/断网：静默
    }

    private UpdateCheckCache? ReadCache(string path)
    {
        try
        {
            string? json = ReadTextOverride is not null ? ReadTextOverride(path) : TryReadFile(path);
            return UpdateCheckLogic.DeserializeCache(json);
        }
        catch { return null; }   // 读失败 = 视为无缓存
    }

    private void WriteCache(string path, UpdateCheckCache cache)
    {
        try
        {
            if (WriteTextOverride is not null) WriteTextOverride(path, UpdateCheckLogic.SerializeCache(cache));
            else TryWriteFile(path, UpdateCheckLogic.SerializeCache(cache));
        }
        catch { /* 写失败静默：下次启动重新请求，无副作用 */ }
    }

    private static string? TryReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static void TryWriteFile(string path, string content)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
        }
        catch { /* 占用/权限/只读：静默跳过 */ }
    }
}
