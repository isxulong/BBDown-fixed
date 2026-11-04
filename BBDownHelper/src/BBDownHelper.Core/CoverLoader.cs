namespace BBDownHelper.Core;

/// <summary>
/// v3.6 红队 P2-A：封面图下载移入 Core（UI 侧只负责解码与上屏）。
/// 取消令牌（DebounceGate.Token，与预览元数据同一代次源）贯穿 HTTP 层：
/// 输入再变化时 DebounceGate.Submit 取消旧令牌，在途封面下载随之中止。
/// UI 侧拿到字节后仍须再验 IsCurrent(gen) 才允许上屏——慢响应（A 的封面晚于 B 的元数据到达）
/// 在两层防线（取消 + 代次校验）下都不再可能错配到新卡片。
/// 失败/取消一律返回 null（沿用「静默失败保持占位色块」契约，不抛出）。
/// </summary>
public sealed class CoverLoader
{
    /// <summary>可注入 mock（单测用）。参数：url, ct。返回图片字节；失败/取消返回 null。</summary>
    public Func<string, CancellationToken, Task<byte[]?>>? GetBytesOverride { get; set; }

    /// <summary>底层 HTTP 发送可注入（单测取消/非成功路径用）；生产为 null 走真实 HttpClient。</summary>
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendOverride { get; set; }

    /// <summary>下载封面字节。空 URL / 已取消直接返回 null；非成功状态码返回 null；成功返回图片字节。</summary>
    public async Task<byte[]?> GetCoverBytesAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url) || ct.IsCancellationRequested) return null;
        if (GetBytesOverride is not null) return await GetBytesOverride(url, ct);
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // 与识别探测同一匿名请求口径：固定 UA + Referer（B 站图床 hdslb.com 校验 Referer 防盗链）
            req.Headers.TryAddWithoutValidation("User-Agent", BiliApiProbe.UserAgent);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            using var resp = SendOverride is not null
                ? await SendOverride(req, ct)
                : await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch { return null; }
    }
}
