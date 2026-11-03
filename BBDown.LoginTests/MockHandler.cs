using System.Net;

namespace BBDown.LoginTests;

/// <summary>
/// 按 host+path 路由的 mock HttpMessageHandler, 记录全部请求供断言。
/// </summary>
internal sealed class MockHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new();
    public List<(string Host, string Path)> Requests { get; } = new();
    public List<HttpRequestMessage> RawRequests { get; } = new();

    public void AddRoute(string host, string path, Func<HttpResponseMessage> factory)
        => _routes[$"{host}{path}"] = factory;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("no uri");
        Requests.Add((uri.Host, uri.AbsolutePath));
        RawRequests.Add(request);
        if (_routes.TryGetValue($"{uri.Host}{uri.AbsolutePath}", out var factory))
            return Task.FromResult(factory());
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal static class TestResp
{
    /// <summary>
    /// 构造 mock 响应: 状态码 + 可选 Location(302) + 可选正文 + 任意多个 Set-Cookie。
    /// </summary>
    public static HttpResponseMessage Resp(int status, string? location = null, string? body = null, params string[] setCookies)
    {
        var resp = new HttpResponseMessage((HttpStatusCode)status);
        if (location != null) resp.Headers.Location = new Uri(location);
        foreach (var sc in setCookies) resp.Headers.Add("Set-Cookie", sc);
        if (body != null) resp.Content = new StringContent(body);
        return resp;
    }
}
