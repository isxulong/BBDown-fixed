// BBDown 换票 v4 mock 测试
// 在 v3 三情形基础上, 覆盖 v4 六项修复的回归断言:
//  ① 首个候选 302 链直接给全 Cookie (含 SESSDATA), 应立即停止、不再请求剩余候选
//  ② 前两个候选无 SESSDATA、第三个候选 200 HTML 且 body 含 sso 链接、sso 链接响应给 SESSDATA (实网失败复现场景)
//     —— v4 追加: Cookie 按域重放断言 (第二跳 / sso 请求的 Cookie 请求头可见)
//  ③ 全部候选失败 (无任何 SESSDATA) —— LoginWEB 中触发「换票失败」报错的条件路径
//  ④ 候选地址构造 (ticket/gourl 解析、gourl 重编码、无参数退化) 与 ExtractSsoLinks 单元断言
//     —— v4 追加: 非法 host (sso.bilibili.com.evil.com 等伪装域) 的 sso 链接被拒绝
//  ⑤ 同键后到覆盖 —— 失败候选的凭据不固化, 最终 SESSDATA/bili_jct 必须来自同一候选 (凭据配对一致)
//  ⑥ sso 链接异常隔离 —— 首条 sso 链接抛异常后, 剩余链接仍被请求并可取得 SESSDATA;
//     畸形候选 URL (降级路径) 不抛异常、返回空结果
// 退出码: 0 = 全部通过, 1 = 存在失败。
// 运行: dotnet build BBDown.LoginTests 后执行其输出 dll。
using System.Net;
using BBDown;
using BBDown.Core.Util;
using BBDown.LoginTests;
using static BBDown.LoginTests.TestResp;

int failures = 0;

void Check(bool cond, string name)
{
    Console.WriteLine(cond ? $"[PASS] {name}" : $"[FAIL] {name}");
    if (!cond) failures++;
}

string enc = Uri.EscapeDataString("https://www.bilibili.com/");
// poll 返回的原始 URL (模拟真实返回: crossDomain?ticket=...&gourl=...)
string pollUrl = "https://passport.bilibili.com/x/passport-login/web/crossDomain?ticket=TICKET123&gourl=" + enc;

// 取指定 host+path 的第 1 个请求的 Cookie 请求头 (无则 null)
string? GetCookieHeader(MockHandler handler, string host, string path)
{
    var req = handler.RawRequests.FirstOrDefault(r => r.RequestUri!.Host == host && r.RequestUri.AbsolutePath == path);
    if (req == null) return null;
    return req.Headers.TryGetValues("Cookie", out var vals) ? string.Join("; ", vals) : null;
}

// ---------- 测试 ④: 候选地址构造与 sso 链接提取 ----------

Console.WriteLine("== 测试④: BuildCrossDomainCandidates / ExtractSsoLinks ==");
{
    var c = BBDownLoginUtil.BuildCrossDomainCandidates(pollUrl);
    Check(c.Count == 3, "解析出 ticket/gourl 时应产出 3 个候选地址");
    Check(c[0] == $"https://passport.biligame.com/x/passport-login/web/crossDomain?ticket=TICKET123&gourl={enc}", "候选1 = biligame x/passport-login/web/crossDomain, gourl 重编码");
    Check(c[1] == $"https://passport.bilibili.com/x/passport-login/web/crossDomain?ticket=TICKET123&gourl={enc}", "候选2 = bilibili x/passport-login/web/crossDomain");
    Check(c[2] == $"https://passport.biligame.com/crossDomain?ticket=TICKET123&gourl={enc}", "候选3 = biligame /crossDomain");

    var fallback = BBDownLoginUtil.BuildCrossDomainCandidates("https://example.com/only?url=1");
    Check(fallback.Count == 1 && fallback[0] == "https://example.com/only?url=1", "解析不出 ticket/gourl 时退化为仅原始 URL");

    // gourl 双重编码场景: quote(unquote(gourl)) 语义 —— 解一层再重编码, 结果与输入一致
    string enc2 = Uri.EscapeDataString(enc);
    var c2 = BBDownLoginUtil.BuildCrossDomainCandidates("https://passport.bilibili.com/crossDomain?ticket=T1&gourl=" + enc2);
    Check(c2.Count == 3 && c2[0].EndsWith("&gourl=" + enc2), "gourl 双重编码: 解一层再重编码后与原输入一致");

    var links = BBDownLoginUtil.ExtractSsoLinks("<html>\"https://sso.bilibili.com/api/sso?act=fetch\\u0026t=1\" \"https://passport.biligame.com/x/sso/check?y=2&amp;z=3\" \"https://evil.com/sso?x=1\" \"https://passport.bilibili.com/plain-path\"</html>");
    Check(links.Count == 2, "ExtractSsoLinks 应提取 2 个 sso 链接 (排除非 bilibili 域与不含 sso 的链接)");
    Check(links[0] == "https://sso.bilibili.com/api/sso?act=fetch&t=1", "\\u0026 应还原为 &");
    Check(links[1] == "https://passport.biligame.com/x/sso/check?y=2&z=3", "&amp; 应还原为 &");

    // v4: host 后缀白名单——整串子串伪装一律拒绝
    var evil = BBDownLoginUtil.ExtractSsoLinks("<html>\"https://sso.bilibili.com.evil.com/x?sso=1\" \"https://evil.com/?q=sso.bilibili.com\" \"https://bilibili.evil.com/sso\" \"https://SSO.BILIBILI.COM/api/sso\"</html>");
    Check(evil.Count == 1, "非法 host 的 sso 链接应被拒绝 (sso.bilibili.com.evil.com / query 带 bilibili 的外域 / bilibili.evil.com)");
    Check(evil[0] == "https://SSO.BILIBILI.COM/api/sso", "合法的 sso.bilibili.com (含大写 host) 应保留");
}

// ---------- 测试 ①: 首个候选 302 链直接给全 Cookie ----------

Console.WriteLine();
Console.WriteLine("== 测试①: 首个候选 302 链直接给全 Cookie ==");
{
    var handler = new MockHandler();
    handler.AddRoute("passport.biligame.com", "/x/passport-login/web/crossDomain",
        () => Resp(302, "https://passport.biligame.com/hop2?x=1", null,
            "SESSDATA=first%2Csess; Path=/; Domain=.bilibili.com", "buvid3=BA1; Path=/"));
    handler.AddRoute("passport.biligame.com", "/hop2",
        () => Resp(200, null, null, "bili_jct=jct1; Path=/"));

    var cookies = await BBDownLoginUtil.ExchangeTicketAsync(pollUrl, handler);

    Check(cookies.TryGetValue("SESSDATA", out var sd1) && sd1 == "first%2Csess", "SESSDATA 应为第一跳的值 (链内无同键冲突)");
    Check(cookies.TryGetValue("bili_jct", out var jct) && jct == "jct1", "第二跳的 bili_jct 应被收集");
    Check(cookies.TryGetValue("buvid3", out var bv) && bv == "BA1", "第一跳的 buvid3 应被收集");
    Check(handler.Requests.Count == 2, "拿到 SESSDATA 后应停止, 总请求数 = 2 (仅候选1 两跳)");
    Check(!handler.Requests.Any(r => r.Host == "passport.bilibili.com" && r.Path == "/x/passport-login/web/crossDomain"), "不应再请求候选2");
    Check(!handler.Requests.Any(r => r.Host == "passport.biligame.com" && r.Path == "/crossDomain"), "不应再请求候选3");
    Check(handler.RawRequests.All(r => r.Headers.TryGetValues("Referer", out var refs) && refs.First() == BBDownLoginUtil.ExchangeReferer), "所有请求都应带 Referer: https://passport.bilibili.com/");
    string uaDebug = handler.RawRequests.Count == 0 ? "<no request>"
        : (handler.RawRequests[0].Headers.TryGetValues("User-Agent", out var dbg) ? string.Join(" | ", dbg) : "<missing>");
    Check(handler.RawRequests.All(r => r.Headers.TryGetValues("User-Agent", out var uas) && string.Join(" ", uas) == HTTPUtil.UserAgent),
        "所有请求都应带固定 Chrome UA (实际首个请求UA: " + uaDebug + ")");
    // v4: Cookie 按域重放——第一跳收集的 Cookie 应出现在第二跳请求头
    string? ck = GetCookieHeader(handler, "passport.biligame.com", "/hop2");
    Check(ck != null && ck.Contains("buvid3=BA1"), "第二跳请求应重放第一跳收集的 buvid3 (Cookie 按域回传)");
}

// ---------- 测试 ②: 实网失败复现——第三候选 200 HTML + sso 链接 ----------

Console.WriteLine();
Console.WriteLine("== 测试②: 前两候选无 SESSDATA, 第三候选 200 HTML 含 sso 链接给 SESSDATA ==");
{
    var handler = new MockHandler();
    // 候选1: 302 -> 200, 只有 buvid3, 无 SESSDATA
    handler.AddRoute("passport.biligame.com", "/x/passport-login/web/crossDomain",
        () => Resp(302, "https://passport.biligame.com/x1?from=c1", setCookies: "buvid3=B1; Path=/"));
    handler.AddRoute("passport.biligame.com", "/x1", () => Resp(200, body: "<html>ok</html>"));
    // 候选2: 302 -> 200, 只有 bili_jct (Domain=.bilibili.com, 供 sso 域重放断言), 无 SESSDATA
    handler.AddRoute("passport.bilibili.com", "/x/passport-login/web/crossDomain",
        () => Resp(302, "https://passport.bilibili.com/x2?from=c2", setCookies: "bili_jct=J2; Path=/; Domain=.bilibili.com"));
    handler.AddRoute("passport.bilibili.com", "/x2", () => Resp(200, body: "<html>ok</html>"));
    // 候选3: 200 HTML, 正文含 sso 链接 (覆盖 \u0026 与 &amp; 两种转义)
    handler.AddRoute("passport.biligame.com", "/crossDomain",
        () => Resp(200, body: "<html><script>jump(\"https://sso.bilibili.com/api/sso?act=fetch\\u0026t=9\",\"https://passport.biligame.com/x/sso/check?y=2&amp;z=3\");</script></html>"));
    // sso 链接1: 302 -> 200, SESSDATA 在末跳才出现
    handler.AddRoute("sso.bilibili.com", "/api/sso",
        () => Resp(302, "https://sso.bilibili.com/api/sso/done?k=1", setCookies: "DedeUserID=42; Path=/"));
    handler.AddRoute("sso.bilibili.com", "/api/sso/done",
        () => Resp(200, setCookies: "SESSDATA=sso%2Cfinal; Path=/; Domain=.bilibili.com"));
    // sso 链接2: 200, 同键 bili_jct 后到覆盖候选2 的值 (对齐参考实现 dict 赋值语义)
    handler.AddRoute("passport.biligame.com", "/x/sso/check",
        () => Resp(200, setCookies: "bili_jct=LATE9; Path=/"));

    var cookies = await BBDownLoginUtil.ExchangeTicketAsync(pollUrl, handler);

    Check(cookies.TryGetValue("SESSDATA", out var sd2) && sd2 == "sso%2Cfinal", "SESSDATA 应来自 sso 链接末跳 (实网失败场景已复现修复)");
    Check(cookies.TryGetValue("buvid3", out var b3) && b3 == "B1", "候选1 的 buvid3 应跨候选累积保留");
    Check(cookies.TryGetValue("bili_jct", out var j) && j == "LATE9", "v4 后到覆盖: bili_jct 应为 sso check 的 LATE9, 与 SESSDATA 同来自候选3 的 sso 扫描");
    Check(cookies.TryGetValue("DedeUserID", out var d) && d == "42", "sso 302 中间跳的 DedeUserID 应被收集");
    var expectedOrder = new (string Host, string Path)[]
    {
        ("passport.biligame.com", "/x/passport-login/web/crossDomain"),
        ("passport.biligame.com", "/x1"),
        ("passport.bilibili.com", "/x/passport-login/web/crossDomain"),
        ("passport.bilibili.com", "/x2"),
        ("passport.biligame.com", "/crossDomain"),
        ("sso.bilibili.com", "/api/sso"),
        ("sso.bilibili.com", "/api/sso/done"),
        ("passport.biligame.com", "/x/sso/check"),
    };
    Check(handler.Requests.SequenceEqual(expectedOrder), "请求顺序 = 候选1两跳→候选2两跳→候选3→sso链1两跳→sso链2, 且拿到SESSDATA后停止剩余候选");
    Check(handler.RawRequests.All(r => r.Headers.TryGetValues("Referer", out var refs) && refs.First() == "https://passport.bilibili.com/"), "所有请求(含 sso 链接)都应带 Referer");

    // v4: Cookie 按域重放断言 (蓝队 P2 / 红队 P3 共识——对齐参考实现 requests.Session 语义)
    string? ck1 = GetCookieHeader(handler, "passport.biligame.com", "/x/passport-login/web/crossDomain");
    Check(ck1 == null, "首个请求 Cookie 头应为空 (尚无已收集 Cookie)");
    string? ck2 = GetCookieHeader(handler, "passport.biligame.com", "/x1");
    Check(ck2 != null && ck2.Contains("buvid3=B1"), "候选1 第二跳应重放第一跳收集的 buvid3 (缺省域=响应 host)");
    string? ck3 = GetCookieHeader(handler, "sso.bilibili.com", "/api/sso");
    Check(ck3 != null && ck3.Contains("bili_jct=J2"), "sso 链1 请求应重放 Domain=.bilibili.com 的 bili_jct (跨子域重放)");
    string? ck4 = GetCookieHeader(handler, "sso.bilibili.com", "/api/sso/done");
    Check(ck4 != null && ck4.Contains("bili_jct=J2") && ck4.Contains("DedeUserID=42"), "sso 链1 第二跳应重放 bili_jct 与首跳收集的 DedeUserID");
    string? ck5 = GetCookieHeader(handler, "passport.biligame.com", "/x/sso/check");
    Check(ck5 != null && ck5.Contains("buvid3=B1") && !ck5.Contains("SESSDATA") && !ck5.Contains("bili_jct"),
        "sso 链2 (biligame 域) 应重放 buvid3, 不重放 .bilibili.com 域的 SESSDATA/bili_jct (域后缀匹配边界)");
}

// ---------- 测试 ③: 全部候选失败 ----------

Console.WriteLine();
Console.WriteLine("== 测试③: 全部候选均未返回 SESSDATA (报错触发条件) ==");
{
    var handler = new MockHandler();
    // 候选1: 500, 无 Cookie
    handler.AddRoute("passport.biligame.com", "/x/passport-login/web/crossDomain", () => Resp(500));
    // 候选2: 200 无 Cookie 无 sso 链接
    handler.AddRoute("passport.bilibili.com", "/x/passport-login/web/crossDomain", () => Resp(200, body: "<html>empty</html>"));
    // 候选3: 302 -> 404
    handler.AddRoute("passport.biligame.com", "/crossDomain", () => Resp(302, "https://passport.biligame.com/gone"));
    handler.AddRoute("passport.biligame.com", "/gone", () => Resp(404));

    var cookies = await BBDownLoginUtil.ExchangeTicketAsync(pollUrl, handler);

    // LoginWEB 中 string.IsNullOrEmpty(sessdata) 为真即走 LogError("换票失败：所有跨域地址均未返回 SESSDATA（详见上方日志），请重新扫码")
    Check(!cookies.ContainsKey("SESSDATA"), "无任何 SESSDATA —— 即 LoginWEB 换票失败报错分支的触发条件");
    Check(cookies.Count == 0, "全部候选失败时应未收集到任何 Cookie");
    Check(handler.Requests.Any(r => r.Host == "passport.biligame.com" && r.Path == "/x/passport-login/web/crossDomain")
        && handler.Requests.Any(r => r.Host == "passport.bilibili.com" && r.Path == "/x/passport-login/web/crossDomain")
        && handler.Requests.Any(r => r.Host == "passport.biligame.com" && r.Path == "/crossDomain"),
        "三个候选地址应全部被尝试过");
}

// ---------- 测试 ⑤: 同键后到覆盖——凭据配对一致 (红队 P2) ----------

Console.WriteLine();
Console.WriteLine("== 测试⑤: 同键后到覆盖, 失败候选的凭据不固化 ==");
{
    var handler = new MockHandler();
    // 候选1 (失败): 设 bili_jct=STALE, 无 SESSDATA
    handler.AddRoute("passport.biligame.com", "/x/passport-login/web/crossDomain",
        () => Resp(302, "https://passport.biligame.com/hopA1", setCookies: "bili_jct=STALE; Path=/"));
    handler.AddRoute("passport.biligame.com", "/hopA1",
        () => Resp(200, setCookies: "buvid3=OLD; Path=/"));
    // 候选2 (成功): 设 SESSDATA + bili_jct=FRESH, 第二跳再设 buvid3=NEW (链内后到覆盖)
    handler.AddRoute("passport.bilibili.com", "/x/passport-login/web/crossDomain",
        () => Resp(302, "https://passport.bilibili.com/hopB1", null,
            "SESSDATA=fresh%2Csess; Path=/; Domain=.bilibili.com", "bili_jct=FRESH; Path=/; Domain=.bilibili.com"));
    handler.AddRoute("passport.bilibili.com", "/hopB1",
        () => Resp(200, setCookies: "buvid3=NEW; Path=/"));

    var cookies = await BBDownLoginUtil.ExchangeTicketAsync(pollUrl, handler);

    Check(cookies.TryGetValue("SESSDATA", out var sd5) && sd5 == "fresh%2Csess", "SESSDATA 来自候选2 (成功链)");
    Check(cookies.TryGetValue("bili_jct", out var j5) && j5 == "FRESH",
        "bili_jct 后到覆盖为候选2 的 FRESH (失败候选的 STALE 不固化), 与 SESSDATA 同候选配对——防 csrf 凭据错乱");
    Check(cookies.TryGetValue("buvid3", out var b5) && b5 == "NEW", "buvid3 后到覆盖: 候选2 第二跳的 NEW 压过候选1 的 OLD");
    Check(handler.Requests.Count == 4, "拿到 SESSDATA 后应停止, 总请求数 = 4 (候选1 两跳 + 候选2 两跳)");
    Check(!handler.Requests.Any(r => r.Host == "passport.biligame.com" && r.Path == "/crossDomain"), "候选3 不应再被请求");
}

// ---------- 测试 ⑥: sso 链接异常隔离 + 畸形候选 URL 降级 (红队 P2必修 / P3) ----------

Console.WriteLine();
Console.WriteLine("== 测试⑥: sso 首链接异常不吞掉剩余链接; 畸形候选 URL 不抛出 ==");
{
    var handler = new MockHandler();
    // 候选1/2: 500 失败
    handler.AddRoute("passport.biligame.com", "/x/passport-login/web/crossDomain", () => Resp(500));
    handler.AddRoute("passport.bilibili.com", "/x/passport-login/web/crossDomain", () => Resp(500));
    // 候选3: 200 HTML, 正文含两条 sso 链接
    handler.AddRoute("passport.biligame.com", "/crossDomain",
        () => Resp(200, body: "<html>\"https://sso.bilibili.com/api/sso?k=1\" \"https://passport.biligame.com/x/sso/check?k=2\"</html>"));
    // sso 链接1: 路由直接抛异常 (模拟网络异常)
    handler.AddRoute("sso.bilibili.com", "/api/sso",
        () => throw new HttpRequestException("mock: connection reset"));
    // sso 链接2: 200 给 SESSDATA
    handler.AddRoute("passport.biligame.com", "/x/sso/check",
        () => Resp(200, setCookies: "SESSDATA=sso2%2Cwin; Path=/; Domain=.bilibili.com"));

    var cookies = await BBDownLoginUtil.ExchangeTicketAsync(pollUrl, handler);

    Check(cookies.TryGetValue("SESSDATA", out var sd6) && sd6 == "sso2%2Cwin", "sso 链接1 抛异常后, 链接2 仍被请求并取得 SESSDATA (异常隔离生效)");
    Check(handler.Requests.Any(r => r.Host == "sso.bilibili.com" && r.Path == "/api/sso")
        && handler.Requests.Any(r => r.Host == "passport.biligame.com" && r.Path == "/x/sso/check"),
        "两条 sso 链接都应被请求过 (首个链接失败不影响剩余链接)");
    Check(handler.Requests.Count == 5, "请求数 = 3 候选 + 2 条 sso 链接");

    // 畸形候选 URL (解析不出 ticket/gourl 降级为仅原始 URL): 应被候选级 catch 吞掉, 不外抛
    var bad = await BBDownLoginUtil.ExchangeTicketAsync(":::: not-a-url ::::");
    Check(bad.Count == 0, "畸形候选 URL 应不抛异常, 返回空 Cookie (降级路径 TryCreate 修复)");
    Check(BBDownLoginUtil.SafeHost(":::: not-a-url ::::") != null, "SafeHost 对畸形 URL 不抛异常");
}

// ===== 修复（红队#3）归档与退出码判定：纯函数行为验证 =====
// 背景：DownloadPageAsync 在「合并失败」「解析此分P失败」时只打红字 return（不抛异常），
// 旧代码调用方无条件 SaveAidToFile，失败分P被记入归档 → 下次直接跳过，不可逆。
// 这些断言锁住「失败不归档、失败置非零退出码、成功仍为 0」三条语义。

Check(BBDown.ArchivePolicy.ShouldArchive(true),
    "分P成功 → 应写入已下载归档");
Check(!BBDown.ArchivePolicy.ShouldArchive(false),
    "分P失败 → 不得写入已下载归档 (缺陷①: 否则下次永久跳过)");

Check(BBDown.ArchivePolicy.ExitCode(0) == 0,
    "无失败分P → 退出码 0 (成功路径不得改动既有脚本行为)");
Check(BBDown.ArchivePolicy.ExitCode(1) == 1,
    "1 个失败分P → 退出码 1");
Check(BBDown.ArchivePolicy.ExitCode(5) == 1,
    "多个失败分P → 退出码仍为 1 (非失败数量)");

Check(BBDown.ArchivePolicy.ShouldReportTaskDone(0),
    "无失败 → 应报「任务完成」");
Check(!BBDown.ArchivePolicy.ShouldReportTaskDone(3),
    "有失败 → 不得报「任务完成」 (缺陷②: 否则用户以为下好了)");

// ===== 探针：InvokeAsync 是否会吞掉 Environment.ExitCode =====
var probe = await BBDown.LoginTests.ExitCodeProbe.RunAsync();
Console.WriteLine($"[PROBE] InvokeAsync 返回={probe.invokeResult}, 处理器设置的 Environment.ExitCode={probe.envExitCode}");
Check(probe.invokeResult == 0,
    "探针确认: 处理器成功时 InvokeAsync 返回 0 (与 Environment.ExitCode 无关)");
Check(probe.envExitCode == 1,
    "探针确认: 处理器内 Environment.ExitCode=1 已被设置但会被 Main 的返回值覆盖");
Check(BBDown.ArchivePolicy.CombineExitCode(probe.invokeResult, probe.envExitCode) == 1,
    "合并规则: InvokeAsync=0 时必须以 Environment.ExitCode 为准 (否则失败码丢失)");
Check(BBDown.ArchivePolicy.CombineExitCode(1, 0) == 1,
    "合并规则: InvokeAsync 非 0 (如参数错误) 时优先保留它");
Check(BBDown.ArchivePolicy.CombineExitCode(0, 0) == 0,
    "合并规则: 两者均为 0 → 成功");

// ===== 端到端：失败退出码能否真正传出进程 =====
int oldMain = await BBDown.LoginTests.ExitCodeProbe.RunOldMainAsync();
Check(oldMain == 0,
    "缺陷复现: 旧写法 (直接 return InvokeAsync) 在有失败分P时仍返回 0 —— 修复前失败码是丢的");
int fixedMain = await BBDown.LoginTests.ExitCodeProbe.RunFixedMainAsync();
Check(fixedMain == 1,
    "修复验证: 合并后 Main 返回 1 —— 失败码真正传出进程 (助手可据此判失败)");

// ===== 第三轮：清理阶段异常不得判为下载失败 =====
Check(!BBDown.ArchivePolicy.CleanupFailureCountsAsDownloadFailure(true),
    "成品已校验通过后清理失败 → 不得判为下载失败 (否则误重试+整批中断+已成功分P被归档永久跳过)");
Check(!BBDown.ArchivePolicy.CleanupFailureCountsAsDownloadFailure(false),
    "成品无效时也不靠清理异常来判失败 (失败由合并校验负责, 职责不重叠)");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部测试通过" : $"{failures} 个断言失败");
return failures == 0 ? 0 : 1;
