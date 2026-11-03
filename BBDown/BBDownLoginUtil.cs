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
using BBDown.Core.Util;

namespace BBDown
{
    internal class BBDownLoginUtil
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
                                LogError("换票失败: ticket 可能已过期(分钟级), 请重新扫码");
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
                        Log("登录成功: SESSDATA=" + sessdata);
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
        /// 用扫码返回的 crossDomain url (ticket) 换取 Cookie。
        /// 2026 起该 url 不再直接包含 SESSDATA, 需要请求该 url 并逐跳跟随 302, 从每跳响应头 Set-Cookie 收集凭据。
        /// 使用专用 HttpClient(禁止自动重定向), 禁止复用 HTTPUtil.GetWebSourceAsync(自动重定向会吞掉 Set-Cookie)。
        /// 参考: https://blog.margrop.net/post/bbdown-qr-login-ticket-cookie-fix/
        /// </summary>
        /// <param name="url">poll 返回的 crossDomain url</param>
        /// <returns>收集到的 Cookie (可能不含 SESSDATA, 由调用方判断)</returns>
        internal static async Task<Dictionary<string, string>> ExchangeTicketAsync(string url)
        {
            var cookies = new Dictionary<string, string>();
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            string? current = url;
            for (int hop = 0; hop < 5 && !string.IsNullOrEmpty(current); hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                req.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.UserAgent);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                //从本跳响应头收集 Set-Cookie (同键以先出现者为准)
                if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var sc in setCookies)
                    {
                        var main = sc.Split(';')[0];
                        int eq = main.IndexOf('=');
                        if (eq > 0)
                        {
                            string name = main[..eq].Trim();
                            if (!cookies.ContainsKey(name))
                                cookies[name] = main[(eq + 1)..].Trim();
                        }
                    }
                }
                LogDebug("换票第 {0} 跳: {1} => {2}", hop + 1, current, (int)resp.StatusCode);
                if (resp.StatusCode is System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.Moved
                    or System.Net.HttpStatusCode.SeeOther or System.Net.HttpStatusCode.TemporaryRedirect
                    or System.Net.HttpStatusCode.RedirectKeepVerb)
                {
                    var location = resp.Headers.Location;
                    if (location == null) break;
                    current = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(current), location).ToString();
                    continue;
                }
                break;
            }
            return cookies;
        }

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
                        Log("登录成功: AccessToken=" + cc);
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
