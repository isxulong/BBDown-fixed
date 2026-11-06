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
            Assert.Contains("v1.0.4", text);   // 版本号随现场落盘（AppVersion.Release 未动）
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
