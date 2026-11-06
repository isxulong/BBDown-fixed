using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>v3.8 登录回执（R2.6）+ 诊断导出（R2.8）+ 崩溃现场（R2.8②）。</summary>
public class LoginReceiptPolicyTests
{
    [Fact]
    public void CredentialExists_SuccessReceipt()
    {
        Assert.Equal("登录成功，以后不用再扫。", LoginReceiptPolicy.Receipt(true));
    }

    [Fact]
    public void CredentialMissing_NotCompletedReceipt()
    {
        var s = LoginReceiptPolicy.Receipt(false);
        Assert.Contains("未完成登录", s);
        Assert.Contains("再点一次登录重试", s);
    }

    // ===== 本轮回归（已登录状态重登失败被误报成功）=====

    /// <summary>
    /// 已登录时重复点「登录」、扫码窗口被关掉：凭据文件本来就在（上次留下的），
    /// 旧判据只看存在性 → 报「登录成功」。必须要求本次真的被改写。
    /// </summary>
    [Fact]
    public void CredentialExistsButNotRewritten_NotCompleted()
    {
        Assert.Contains("未完成登录", LoginReceiptPolicy.Receipt(true, credentialUpdated: false));
        Assert.Equal(LoginReceiptPolicy.Success, LoginReceiptPolicy.Receipt(true, credentialUpdated: true));
    }

    [Fact]
    public void IsRewritten_DetectsRealRewrite()
    {
        var t0 = new DateTime(2026, 11, 6, 2, 0, 0, DateTimeKind.Utc);
        Assert.True(LoginReceiptPolicy.IsRewritten(null, t0));            // 首次登录：之前没有 → 算写入
        Assert.True(LoginReceiptPolicy.IsRewritten(t0, t0.AddSeconds(3))); // 重新扫码写入了
        Assert.False(LoginReceiptPolicy.IsRewritten(t0, t0));              // 时间没动 → 旧凭据
        Assert.False(LoginReceiptPolicy.IsRewritten(t0, null));            // 文件反而没了
    }
}

public class DiagnosticExportTests
{
    [Fact]
    public void Scrub_SessDataValue_Masked()
    {
        var s = DiagnosticExport.Scrub("cookie=SESSDATA=abc123def; bili_jct=zzz");
        Assert.DoesNotContain("abc123def", s);
        Assert.DoesNotContain("zzz", s);
        Assert.Contains("***", s);
    }

    [Fact]
    public void Scrub_NormalLine_Unchanged()
    {
        const string line = "下载完成，保存到 D:\\videos";
        Assert.Equal(line, DiagnosticExport.Scrub(line));
    }

    // —— v3.9 红队 P2-1 回归：以下形态在 v3.8 全部/部分失守 ——

    [Fact]
    public void Scrub_JsonFormat_Masked()
    {
        // BBDown --debug 输出 / 用户贴的 HTTP 响应体里的 JSON 形态（v3.8 完全泄漏）
        var s = DiagnosticExport.Scrub("{\"SESSDATA\":\"a1b2c3d4e5f6\",\"bili_jct\":\"deadbeef\"}");
        Assert.DoesNotContain("a1b2c3d4e5f6", s);
        Assert.DoesNotContain("deadbeef", s);
        Assert.Contains("\"SESSDATA\":\"***\"", s);
        Assert.Contains("\"bili_jct\":\"***\"", s);
    }

    [Fact]
    public void Scrub_JsonQuotedKeyWithSpace_Masked()
    {
        // 键带引号、冒号后带空格（v3.8 完全泄漏）
        var s = DiagnosticExport.Scrub("\"SESSDATA\": \"a1b2c3d4\"");
        Assert.DoesNotContain("a1b2c3d4", s);
        Assert.Contains("***", s);
    }

    [Fact]
    public void Scrub_TabSeparated_Masked()
    {
        // tab 分隔、无 = 或 :（v3.8 完全泄漏）
        var s = DiagnosticExport.Scrub("SESSDATA\ttabsecret99");
        Assert.DoesNotContain("tabsecret99", s);
        Assert.Contains("***", s);
    }

    [Fact]
    public void Scrub_MultiSpaceSeparated_Masked()
    {
        // 多空白分隔（对齐日志列常见）
        var s = DiagnosticExport.Scrub("refresh_token   spacedsecret88");
        Assert.DoesNotContain("spacedsecret88", s);
        Assert.Contains("***", s);
    }

    [Fact]
    public void Scrub_QuotedValueWithSpaces_Masked()
    {
        // 引号包裹的带空格值（v3.8 \\S+ 截断导致后段明文残留）
        var s = DiagnosticExport.Scrub("SESSDATA=\"a1b2 c3d4 e5f6\"");
        Assert.DoesNotContain("a1b2", s);
        Assert.DoesNotContain("c3d4", s);
        Assert.DoesNotContain("e5f6", s);
        Assert.Contains("SESSDATA=\"***\"", s);
    }

    // —— 本轮（v1.0.5）回归：TV 通道凭据、换票 ticket、纯键名行不误伤 ——

    [Fact]
    public void Scrub_TvChannelAccessToken_Masked()
    {
        // 内核 TV 通道原版会打 `登录成功: AccessToken=<token>`；Scrub 旧表无此键名，原样漏出。
        var s = DiagnosticExport.Scrub("登录成功: AccessToken=eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9");
        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9", s);
        Assert.Contains("AccessToken=", s);          // 保留键名，便于读日志的人判断发生了哪一步
        Assert.Contains("***", s);
    }

    [Fact]
    public void Scrub_QrTicketInUrl_Masked()
    {
        // 换票失败的异常 Message 带完整请求 URL（`?ticket=...`），旧表无 ticket，原样漏出。
        var s = DiagnosticExport.Scrub(
            "HttpRequestException: 412 at https://passport.bilibili.com/x/passport-login/web/cookie/info?ticket=abcd1234efgh&x=1");
        Assert.DoesNotContain("abcd1234efgh", s);
        Assert.Contains("***", s);
    }

    [Fact]
    public void Scrub_CookieNameList_NotOverMasked()
    {
        // 反例护栏：这行只列 Cookie **名**、不含任何值（BBDownLoginUtil 每次登录都打）。
        // 旧表含 `cookie` 键名，会把 `SESSDATA` 打成 `***`，把有用的诊断信息抹掉。
        const string line = "收到 3 个Cookie: SESSDATA, bili_jct, buvid3";
        var s = DiagnosticExport.Scrub(line);
        Assert.Equal(line, s);                       // 一个字都不该变
        Assert.Contains("SESSDATA", s);
        Assert.Contains("bili_jct", s);
        Assert.Contains("buvid3", s);
    }

    [Fact]
    public void Scrub_CookieHeaderWithValues_StillMasked()
    {
        // 与上一条配对：真正带值的 Cookie 头必须每个键值对都打码（每个键名各自命中）。
        var s = DiagnosticExport.Scrub("Cookie: SESSDATA=1a2b3c; bili_jct=4d5e6f; buvid3=778899");
        Assert.DoesNotContain("1a2b3c", s);
        Assert.DoesNotContain("4d5e6f", s);
    }

    [Fact]
    public void Scrub_KernelFixedLine_Unchanged()
    {
        // 内核修好后的登录行不含明文，Scrub 不应把它改花（否则诊断包里的关键行读不懂）。
        const string line = "登录成功: 已写入 BBDown.data（SESSDATA 长度 42，内容不打印）";
        Assert.Equal(line, DiagnosticExport.Scrub(line));
    }

    [Fact]
    public void BuildContent_ContainsVersionAndTail_NoRawSecret()
    {
        var content = DiagnosticExport.BuildContent("v1.0.3", "1.6.3",
            new[] { "正常行", "SESSDATA=supersecretvalue" });
        Assert.Contains("v1.0.3", content);
        Assert.Contains("1.6.3", content);
        Assert.Contains("正常行", content);
        Assert.DoesNotContain("supersecretvalue", content);   // Cookie 值绝不进诊断文件
        Assert.Contains("已打码", content);
        Assert.DoesNotContain("不含 Cookie", content);        // v3.9：不做绝对化宣称，按实际覆盖能力如实描述
    }

    [Fact]
    public void BuildContent_NullBbdownVersion_Unknown()
    {
        Assert.Contains("未知", DiagnosticExport.BuildContent("v1.0.3", null, Array.Empty<string>()));
    }

    [Fact]
    public void TryGetBbdownVersion_MissingFile_Null()
    {
        Assert.Null(DiagnosticExport.TryGetBbdownVersion(
            Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".exe")));
    }
}

public class CrashHandlerTests
{
    [Fact]
    public void WriteCrashFile_WritesExceptionAndTail()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = CrashHandler.WriteCrashFile(dir,
                new InvalidOperationException("boom-marker"),
                new[] { "tail-line-1", "tail-line-2" });
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.Contains("crash-", Path.GetFileName(path));
            var text = File.ReadAllText(path);
            Assert.Contains("boom-marker", text);
            Assert.Contains("tail-line-2", text);
            Assert.Contains("v1.0.5", text);   // 版本号随现场落盘
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void WriteCrashFile_ExceptionWithTicket_Scrubbed()
    {
        // 本轮 P2 回归：崩溃现场里「异常全文」和「日志尾部」都要脱敏。
        // B 站 412 的异常 Message 带完整请求 URL，QR 换票的 ticket 就在 query 里。
        var dir = Path.Combine(Path.GetTempPath(), "crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = new HttpRequestException(
                "Response status code does not indicate success: 412 at " +
                "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?ticket=qrsecret7788&x=1");
            var path = CrashHandler.WriteCrashFile(dir, ex,
                new[] { "tail", "AccessToken=tvsecret9900" });
            Assert.NotNull(path);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("qrsecret7788", text);      // 异常全文里的 ticket 已打码
            Assert.DoesNotContain("tvsecret9900", text);      // 日志尾部里的 token 已打码
            Assert.Contains("——— 异常 ———", text);            // 结构仍在，便于排查
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void WriteCrashFile_NoException_NoTail_StillWrites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = CrashHandler.WriteCrashFile(dir, null, null);
            Assert.NotNull(path);
            Assert.Contains("无异常对象", File.ReadAllText(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// 本轮 P2 回归：后台任务异常不弹「程序出错了」、且文件抬头写明「程序仍在运行」，
    /// 以免用户以为程序挂了。
    /// </summary>
    [Fact]
    public void WriteCrashFile_NonFatal_MarkedAsStillRunning()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = CrashHandler.WriteCrashFile(dir, new Exception("task-marker"), null, fatal: false);
            Assert.NotNull(path);
            var text = File.ReadAllText(path);
            Assert.Contains("后台任务异常（程序仍在运行）", text);
            Assert.Contains("task-marker", text);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// 本轮 P2 回归（闸门语义）：任务异常先发生，不能把「致命崩溃」的名额用掉——
    /// 否则真正让进程死掉的那次崩溃不留现场，用户手里什么也没有。
    /// </summary>
    [Fact]
    public void Gates_TaskExceptionFirst_FatalCrashStillRecorded()
    {
        CrashHandler.ResetGatesForTest();
        Assert.True(CrashHandler.TryBegin(fatal: false));    // 一条被丢弃的异步任务异常先来
        Assert.False(CrashHandler.TryBegin(fatal: false));   // 同类不再重复处理
        Assert.True(CrashHandler.TryBegin(fatal: true));     // 致命崩溃仍能留现场 ← 关键
        Assert.False(CrashHandler.TryBegin(fatal: true));
        CrashHandler.ResetGatesForTest();
    }

    [Fact]
    public void WriteCrashFile_BadDir_ReturnsNull_NoThrow()
    {
        // 父路径是一个已存在的文件：任何平台上在此之下写文件都会失败（跨平台可靠触发）。
        var blocker = Path.Combine(Path.GetTempPath(), "crash-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            var path = CrashHandler.WriteCrashFile(
                Path.Combine(blocker, "logs"), new Exception("x"), null);
            Assert.Null(path);
        }
        finally { try { File.Delete(blocker); } catch { } }
    }
}
