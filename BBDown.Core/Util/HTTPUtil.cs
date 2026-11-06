using System;
using System.Net;
using System.Net.Http.Headers;
using static BBDown.Core.Logger;

namespace BBDown.Core.Util
{
    public class HTTPUtil
    {

        public static readonly HttpClient AppHttpClient = new(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        })
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        //固定使用当代 Chrome UA, 不再随机生成 (随机老版本 UA 已成为风控特征)
        private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

        public static string UserAgent { get; set; } = DefaultUserAgent;

        public static async Task<string> GetWebSourceAsync(string url)
        {
            using var webRequest = new HttpRequestMessage(HttpMethod.Get, url);
            webRequest.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            webRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
            webRequest.Headers.TryAddWithoutValidation("Cookie", (url.Contains("/ep") || url.Contains("/ss")) ? Config.COOKIE + ";CURRENT_FNVAL=4048;" : Config.COOKIE);
            if (url.Contains("api.bilibili.com"))
                webRequest.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            webRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
            webRequest.Headers.Connection.Clear();

            // 修复（红队#12）：原来把请求头整份 dump 进 debug 日志，其中 Cookie 头含完整
            // SESSDATA / bili_jct；debug 一旦被 BBDown.config 打开，凭据就上进日志面板、
            // helper-*.log 与「导出求助文件」。改为只打 URL 与关键头是否存在，不打值。
            LogDebug("获取网页内容: Url: {0}, Cookie已附加: {1}", url, webRequest.Headers.Contains("Cookie"));
            var webResponse = (await AppHttpClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead)).EnsureSuccessStatusCode();

            string htmlCode = await webResponse.Content.ReadAsStringAsync();
            // 修复（红队#12）：响应体全文同样不再无条件转储（登录链路里可能带 ticket）。
            LogDebug("Response: 长度 {0}", htmlCode.Length);
            return htmlCode;
        }

        // 重写重定向处理, 自动跟随多次重定向
        public static async Task<string> GetWebLocationAsync(string url)
        {
            using var webRequest = new HttpRequestMessage(HttpMethod.Head, url);
            webRequest.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            webRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
            webRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
            webRequest.Headers.Connection.Clear();

            LogDebug("获取网页重定向地址: Url: {0}", url);
            var webResponse = (await AppHttpClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead)).EnsureSuccessStatusCode();
            string location = webResponse.RequestMessage.RequestUri.AbsoluteUri;
            LogDebug("Location: {0}", location);
            return location;
        }

        public static async Task<byte[]> GetPostResponseAsync(string Url, byte[] postData, Dictionary<string, string> headers = null)
        {
            // 修复（红队#12）：请求体 base64 不再转储（含用户会话相关字段）。
            LogDebug("Post to: {0}, data 长度: {1}", Url, postData.Length);

            ByteArrayContent content = new(postData);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/grpc");

            HttpRequestMessage request = new()
            {
                RequestUri = new Uri(Url),
                Method = HttpMethod.Post,
                Content = content,
                //Version = HttpVersion.Version20
            };

            if (headers != null)
            {
                foreach (KeyValuePair<string, string> header in headers)
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 6.0.1; oneplus a5010 Build/V417IR) 6.10.0 os/android model/oneplus a5010 mobi_app/android build/6100500 channel/bili innerVer/6100500 osVer/6.0.1 network/2");
                request.Headers.TryAddWithoutValidation("grpc-encoding", "gzip");
            }

            HttpResponseMessage response = await AppHttpClient.SendAsync(request);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();

            return bytes;
        }
    }
}
