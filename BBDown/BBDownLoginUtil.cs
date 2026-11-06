using QRCoder;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static BBDown.BBDownUtil;
using static BBDown.Core.Logger;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Text.RegularExpressions;
using BBDown.Core.Util;

namespace BBDown
{
    internal partial class BBDownLoginUtil
    {
        public static async Task<string> GetLoginStatusAsync(string qrcodeKey)
        {
            string queryUrl = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}&source=main-fe-header";
            return await HTTPUtil.GetWebSourceAsync(queryUrl);
        }

        public static async Task LoginWEB()
        {
            try
            {
                Log("获取登录地址...");
                string loginUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header";
                string url = JsonDocument.Parse(await HTTPUtil.GetWebSourceAsync(loginUrl)).RootElement.GetProperty("data").GetProperty("url").ToString();
                string qrcodeKey = GetQueryString("qrcode_key", url);
                //Log(oauthKey);
                //Log(url);
                bool flag = false;
                Log("生成二维码...");
                QRCodeGenerator qrGenerator = new();
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
                PngByteQRCode pngByteCode = new(qrCodeData);
                File.WriteAllBytes("qrcode.png", pngByteCode.GetGraphic(7));
                Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
                var consoleQRCode = new ConsoleQRCode(qrCodeData);
                consoleQRCode.GetGraphic();

                while (true)
                {
                    await Task.Delay(1000);
                    string w = await GetLoginStatusAsync(qrcodeKey);
                    int code = JsonDocument.Parse(w).RootElement.GetProperty("data").GetProperty("code").GetInt32();
                    if (code == 86038)
                    {
                        LogColor("二维码已过期, 请重新执行登录指令.");
                        break;
                    }
                    else if (code == 86101) //等待扫码
                    {
                        continue;
                    }
                    else if (code == 86090) //等待确认
                    {
                        if (!flag)
                        {
                            Log("扫码成功, 请确认...");
                            flag = !flag;
                        }
                    }
                    else
                    {
                        var dataNode = JsonDocument.Parse(w).RootElement.GetProperty("data");
                        string cc = dataNode.GetProperty("url").ToString();
                        string? refreshToken = dataNode.TryGetProperty("refresh_token", out var rtNode) ? rtNode.ToString() : null;

                        string sessdata = GetQueryString("SESSDATA", cc);
                        string cookieString;
                        if (!string.IsNullOrEmpty(sessdata))
                        {
                            //旧版返回: query 中直接带 SESSDATA, 保持原逻辑
                            cookieString = cc[(cc.IndexOf('?') + 1)..].Replace("&", ";").Replace(",", "%2C");
                        }
                        else
                        {
                            //2026 起返回 crossDomain?ticket=...&gourl=..., query 不含 SESSDATA, 需要换票
                            Log("登录URL不含SESSDATA, 正在换取Cookie...");
                            var cookies = await ExchangeTicketAsync(cc);
                            sessdata = cookies.TryGetValue("SESSDATA", out var sd) ? sd : "";
                            if (string.IsNullOrEmpty(sessdata))
                            {
                                LogError("换票失败：所有跨域地址均未返回 SESSDATA（详见上方日志），请重新扫码");
                                break;
                            }
                            //导出cookie, 转义英文逗号 否则部分场景会出问题
                            cookieString = string.Join(";", cookies.Select(c => $"{c.Key}={c.Value}")).Replace(",", "%2C");
                        }

                        //写盘前备份旧凭据
                        string dataPath = Path.Combine(Program.APP_DIR, "BBDown.data");
                        if (File.Exists(dataPath)) File.Copy(dataPath, dataPath + ".bak", true);
                        File.WriteAllText(dataPath, cookieString);
                        //refresh_token 单独存一行, 供自动刷新使用
                        if (!string.IsNullOrWhiteSpace(refreshToken))
                            File.WriteAllText(Path.Combine(Program.APP_DIR, "BBDown.refresh_token"), refreshToken);
                        // P1 安全修复：不再打印凭据值本身。
                        // 原句 `Log("登录成功: SESSDATA=" + sessdata)` 把 SESSDATA 明文打到控制台，
                        // 而扫码登录是必经路径（每次都打印），bat 版/EXE 版都是用户可见的窗口，
                        // 说明书又教用户「复制这段发给维护者」——等于教用户外发凭据。
                        // 保留「登录成功」可诊断语义 + 凭据长度指纹（足以判断有没有拿到、有没有串号），
                        // 不含可用于冒充登录的明文。
                        Log("登录成功: 已写入 BBDown.data（SESSDATA 长度 " + (sessdata?.Length ?? 0) + "，内容不打印）");
                        File.Delete("qrcode.png");
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                //G3: HttpRequestException 的 Message 会携带完整请求 URL (含 ticket), 不打印 query 部分防泄露
                if (e is HttpRequestException)
                {
                    LogError("换票请求失败: " + e.GetType().Name);
                    LogDebug("换票失败详情: {0}", e.Message);
                }
                else
                {
                    LogError(e.Message);
                }
            }
        }

        /// <summary>
        /// 换票请求统一携带的 Referer (对齐实测成功方案 session 全局头)。
        /// </summary>
        internal const string ExchangeReferer = "https://passport.bilibili.com/";

        /// <summary>
        /// 用扫码返回的 crossDomain url (ticket) 换取 Cookie。
        /// v4: 对齐实测成功方案——依次尝试多个 crossDomain 地址变体 (拿到 SESSDATA 即停),
        /// 每跳手动跟随 302 (上限 5 跳), Set-Cookie 跨候选地址累积收集、同键后到覆盖,
        /// 已收集的 Cookie 按域匹配回传到后续请求 (对齐参考实现 requests.Session 的 Cookie 重放语义),
        /// 最终响应可能是 200 HTML, 部分凭据要靠扫描正文中的 sso 链接再请求一次才拿得到
        /// (单条 sso 链接失败只记日志, 不影响其余链接)。
        /// 请求头带 Referer: https://passport.bilibili.com/ 与固定 Chrome UA。
        /// 参考: https://blog.margrop.net/post/bbdown-qr-login-ticket-cookie-fix/
        /// </summary>
        /// <param name="url">poll 返回的 crossDomain url</param>
        /// <param name="handler">可选 HttpMessageHandler, 供 mock 测试注入; 默认禁止自动重定向</param>
        /// <returns>收集到的 Cookie (可能不含 SESSDATA, 由调用方判断)</returns>
        internal static async Task<Dictionary<string, string>> ExchangeTicketAsync(string url, HttpMessageHandler? handler = null)
        {
            var jar = new CookieJar();
            using var h = handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) };
            foreach (var candidate in BuildCrossDomainCandidates(url))
            {
                try
                {
                    await FetchChainAsync(client, candidate, jar, scanSso: true);
                }
                catch (Exception e)
                {
                    //单个候选失败不中断, 继续尝试剩余候选
                    //v4: host 提取改用 TryCreate, 畸形候选 URL 不再二次抛异常;
                    //候选异常升级为普通日志 (正常模式可见), 但只输出 host 与异常类型名,
                    //不输出 URL / ticket / 异常 Message (HttpRequestException 的 Message 携带完整请求 URL)
                    Log($"换票候选 {SafeHost(candidate)} 请求异常: {e.GetType().Name}");
                }
                if (jar.Contains("SESSDATA"))
                {
                    LogDebug("换票成功: 已从 {0} 拿到 SESSDATA, 停止剩余候选", SafeHost(candidate));
                    break;
                }
            }
            return jar.ToFlat();
        }

        /// <summary>
        /// 提取 URL 的 host; 解析失败返回占位符而不抛异常 (降级路径遇畸形 URL 时防二次异常)。
        /// </summary>
        internal static string SafeHost(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "<无效地址>";
        }

        /// <summary>
        /// 从 poll 返回的 URL 解析 ticket/gourl, 构造要依次尝试的 crossDomain 地址变体;
        /// 解析不出参数时退化为仅尝试原始 URL。
        /// </summary>
        internal static List<string> BuildCrossDomainCandidates(string url)
        {
            var candidates = new List<string>();
            string ticket = GetQueryString("ticket", url);
            string gourl = GetQueryString("gourl", url);
            if (string.IsNullOrEmpty(ticket) || string.IsNullOrEmpty(gourl))
            {
                candidates.Add(url);
                return candidates;
            }
            //gourl 先 URL-decode 再重新 quote 编码
            string encoded = Uri.EscapeDataString(Uri.UnescapeDataString(gourl));
            candidates.Add($"https://passport.biligame.com/x/passport-login/web/crossDomain?ticket={ticket}&gourl={encoded}");
            candidates.Add($"https://passport.bilibili.com/x/passport-login/web/crossDomain?ticket={ticket}&gourl={encoded}");
            candidates.Add($"https://passport.biligame.com/crossDomain?ticket={ticket}&gourl={encoded}");
            return candidates;
        }

        /// <summary>
        /// 请求起始地址并逐跳手动跟随 302 (上限 5 跳), 每跳收集 Set-Cookie;
        /// scanSso=true 时, 终态响应(如 200 HTML)的正文里若含 sso 链接, 逐个请求一次收集 Cookie。
        /// </summary>
        private static async Task FetchChainAsync(HttpClient client, string startUrl, CookieJar jar, bool scanSso)
        {
            string current = startUrl;
            for (int hop = 0; hop < 5 && !string.IsNullOrEmpty(current); hop++)
            {
                using var resp = await SendExchangeRequestAsync(client, current, jar);
                var received = CollectCookies(resp, jar, current);
                string names = string.Join(", ", received);
                Log($"换票尝试: {SafeHost(current)} => HTTP {(int)resp.StatusCode}, 收到 {received.Count} 个Cookie: {names}");
                if (resp.StatusCode is System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.Moved
                    or System.Net.HttpStatusCode.SeeOther or System.Net.HttpStatusCode.TemporaryRedirect
                    or System.Net.HttpStatusCode.RedirectKeepVerb)
                {
                    var location = resp.Headers.Location;
                    if (location == null) break;
                    current = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(current), location).ToString();
                    continue;
                }
                //终态: 200 HTML 场景下部分凭据藏在正文 sso 链接里, 需再请求一次
                if (scanSso && resp.Content != null)
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    foreach (var sso in ExtractSsoLinks(body))
                    {
                        //v4: 单条 sso 链接请求包异常隔离, 单个失败只记日志继续下一条——
                        //首个链接网络异常不能导致剩余 sso 链接 (SESSDATA 可能藏在其中) 被跳过
                        try
                        {
                            await FetchChainAsync(client, sso, jar, scanSso: false);
                        }
                        catch (Exception e)
                        {
                            //日志只出 host 与异常类型名, 不出 URL / ticket / 异常 Message
                            LogDebug("sso链接失败: {0} => {1}", SafeHost(sso), e.GetType().Name);
                        }
                    }
                }
                break;
            }
        }

        private static async Task<HttpResponseMessage> SendExchangeRequestAsync(HttpClient client, string url, CookieJar jar)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.UserAgent);
            req.Headers.TryAddWithoutValidation("Referer", ExchangeReferer);
            //v4: 把目标 host 后缀匹配的已收集 Cookie 组装进 Cookie 请求头 (对齐参考实现 Session 的重放语义)
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                string? cookieHeader = jar.BuildCookieHeader(u.Host);
                if (!string.IsNullOrEmpty(cookieHeader))
                    req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
            return await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        }

        /// <summary>
        /// 从响应头收集 Set-Cookie 到 CookieJar (Domain 属性缺省取请求 host, 统一去前导点;
        /// 同键后到覆盖, 对齐参考实现的 dict 赋值语义), 返回本次响应收到的 Cookie 名字列表(不含值)。
        /// </summary>
        private static List<string> CollectCookies(HttpResponseMessage resp, CookieJar jar, string requestUrl)
        {
            var names = new List<string>();
            if (!resp.Headers.TryGetValues("Set-Cookie", out var setCookies)) return names;
            string defaultDomain = SafeHost(requestUrl);
            foreach (var sc in setCookies)
            {
                var parts = sc.Split(';');
                var main = parts[0];
                int eq = main.IndexOf('=');
                if (eq <= 0) continue;
                string name = main[..eq].Trim();
                if (!names.Contains(name)) names.Add(name);
                string domain = defaultDomain;
                foreach (var attr in parts.Skip(1))
                {
                    string kv = attr.Trim();
                    if (kv.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase))
                    {
                        domain = kv["Domain=".Length..].Trim();
                        break;
                    }
                }
                jar.Set(domain, name, main[(eq + 1)..].Trim());
            }
            return names;
        }

        /// <summary>
        /// 换票过程收集的 Cookie 容器: 按域名分组存储 (Domain 属性缺省取响应 host, 统一去前导点、小写),
        /// 发请求时把目标 host 后缀匹配的已收集 Cookie 组装进 Cookie 请求头
        /// (对齐参考实现 requests.Session 的跨请求 Cookie 重放语义); 同键冲突后到覆盖。
        /// 注意: 依赖 HttpClientHandler.UseCookies=false——该开关为 true 时 Set-Cookie 会被 handler 吞掉, 无法收集。
        /// </summary>
        internal sealed class CookieJar
        {
            //域名(去前导点、小写) -> 该域下按插入序收集的 Cookie 记录
            private readonly Dictionary<string, List<Entry>> byDomain = new(StringComparer.OrdinalIgnoreCase);
            //全局收集时序号: 「后到覆盖」以实际收集顺序为准, 而不是域名分组顺序
            private long seq;

            private sealed record Entry(string Name, string Value, long Seq);

            public void Set(string domain, string name, string value)
            {
                string d = (domain ?? "").Trim().TrimStart('.').ToLowerInvariant();
                if (d.Length == 0) return;
                if (!byDomain.TryGetValue(d, out var entries))
                {
                    entries = new List<Entry>();
                    byDomain[d] = entries;
                }
                entries.RemoveAll(e => e.Name == name); //移除旧记录, 同域同键后到覆盖
                entries.Add(new Entry(name, value, ++seq));
            }

            public bool Contains(string name) => Get(name) != null;

            /// <summary>取某 Cookie 的当前值 (后到覆盖: 全局最后收集的值生效)。</summary>
            public string? Get(string name)
            {
                string? v = null;
                long max = -1;
                foreach (var entries in byDomain.Values)
                    foreach (var e in entries)
                        if (e.Name == name && e.Seq > max) { max = e.Seq; v = e.Value; }
                return v;
            }

            /// <summary>
            /// 组装发送给 targetHost 的 Cookie 请求头: 收集时 Domain 等于目标 host 或为其后缀
            /// (host == 域 或 host.EndsWith("." + 域)) 的 Cookie 全部带上, 同名 Cookie 以最后收集的为准。
            /// </summary>
            public string? BuildCookieHeader(string targetHost)
            {
                string host = (targetHost ?? "").Trim().TrimStart('.').ToLowerInvariant();
                if (host.Length == 0) return null;
                var matched = new List<Entry>();
                foreach (var (domain, entries) in byDomain)
                {
                    if (host != domain && !host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) continue;
                    matched.AddRange(entries);
                }
                if (matched.Count == 0) return null;
                matched.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                var pairs = new Dictionary<string, string>();
                foreach (var e in matched) pairs[e.Name] = e.Value;
                return string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"));
            }

            /// <summary>扁平化输出 (供写盘): 跨域同键按收集时序后到覆盖, 名字保持首次出现顺序。</summary>
            public Dictionary<string, string> ToFlat()
            {
                var all = new List<Entry>();
                foreach (var entries in byDomain.Values) all.AddRange(entries);
                all.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                var flat = new Dictionary<string, string>();
                foreach (var e in all) flat[e.Name] = e.Value;
                return flat;
            }
        }

        /// <summary>
        /// 从响应正文提取所有含 sso (不区分大小写) 且属 bilibili.com / biligame.com 或其子域的 http(s) 链接;
        /// 正文里的 \u0026 与 &amp; 先还原成 & 再匹配。
        /// v4: 域校验改为 Uri 解析 + host 后缀白名单 (host == 域 或 host 以 ".域" 结尾),
        /// 杜绝 sso.bilibili.com.evil.com 这类整串子串伪装通过后被真实请求。
        /// </summary>
        internal static List<string> ExtractSsoLinks(string body)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(body)) return result;
            string normalized = body.Replace("\\u0026", "&").Replace("&amp;", "&");
            foreach (Match m in HttpUrlRegex().Matches(normalized))
            {
                string u = m.Value;
                if (u.IndexOf("sso", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) continue;
                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;
                string host = uri.Host.ToLowerInvariant();
                bool domainOk = host == "bilibili.com" || host == "biligame.com"
                    || host.EndsWith(".bilibili.com") || host.EndsWith(".biligame.com");
                if (!domainOk) continue;
                if (!result.Contains(u)) result.Add(u);
            }
            return result;
        }

        [GeneratedRegex("https?://[^\\s\"'<>(){}\\\\]+")]
        private static partial Regex HttpUrlRegex();

        public static async Task LoginTV()
        {
            try
            {
                string loginUrl = "https://passport.snm0516.aisee.tv/x/passport-tv-login/qrcode/auth_code";
                string pollUrl = "https://passport.bilibili.com/x/passport-tv-login/qrcode/poll";
                var parms = GetTVLoginParms();
                Log("获取登录地址...");
                byte[] responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(loginUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
                string web = Encoding.UTF8.GetString(responseArray);
                string url = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("url").ToString();
                string authCode = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("auth_code").ToString();
                Log("生成二维码...");
                QRCodeGenerator qrGenerator = new();
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
                PngByteQRCode pngByteCode = new(qrCodeData);
                File.WriteAllBytes("qrcode.png", pngByteCode.GetGraphic(7));
                Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
                var consoleQRCode = new ConsoleQRCode(qrCodeData);
                consoleQRCode.GetGraphic();
                parms.Set("auth_code", authCode);
                parms.Set("ts", GetTimeStamp(true));
                parms.Remove("sign");
                parms.Add("sign", GetSign(ToQueryString(parms)));
                bool flag = false;
                while (true)
                {
                    await Task.Delay(1000);
                    responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(pollUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
                    web = Encoding.UTF8.GetString(responseArray);
                    string code = JsonDocument.Parse(web).RootElement.GetProperty("code").ToString();
                    if (code == "86038")
                    {
                        LogColor("二维码已过期, 请重新执行登录指令.");
                        break;
                    }
                    else if (code == "86039") //等待扫码
                    {
                        continue;
                    }
                    else if (code == "86090") //已扫码待确认, 正常中间态, 继续轮询
                    {
                        if (!flag)
                        {
                            Log("扫码成功, 请确认...");
                            flag = !flag;
                        }
                        continue;
                    }
                    else if (code == "0")
                    {
                        string cc = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("access_token").ToString();
                        // P1 安全修复：同网页通道，不打印 access_token 明文（TV 通道凭据同样可冒充登录）。
                        Log("登录成功: 已写入 BBDownTV.data（access_token 长度 " + (cc?.Length ?? 0) + "，内容不打印）");
                        //导出cookie
                        File.WriteAllText(Path.Combine(Program.APP_DIR, "BBDownTV.data"), "access_token=" + cc);
                        File.Delete("qrcode.png");
                        break;
                    }
                    else
                    {
                        //未知 code 一律按失败处理, 打印原始返回便于排查
                        LogError($"登录失败, 原始返回: {web}");
                        break;
                    }
                }
            }
            catch (Exception e) { LogError(e.Message); }
        }
    }
}
