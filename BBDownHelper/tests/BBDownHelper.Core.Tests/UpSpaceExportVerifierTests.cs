using BBDownHelper.Core;
using Xunit;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v3.7 UP 主导出验证器（真 bug ①：导出假成功）单测。
/// 判据唯一可信口径：本次运行时间窗内工作目录下真实生成非空的『UP昵称的投稿视频.txt』。
/// 覆盖：命中 / 空清单不算 / 时间窗外的旧清单不算 / 多个取最新 / 目录不存在不抛。
/// </summary>
public class UpSpaceExportVerifierTests : IDisposable
{
    private readonly string _dir;

    public UpSpaceExportVerifierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bbdh-v37-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败不影响断言 */ }
    }

    private string WriteList(string upName, string content, DateTime? lastWrite = null)
    {
        string p = Path.Combine(_dir, upName + UpSpaceExportVerifier.ListFileSuffix);
        File.WriteAllText(p, content);
        if (lastWrite is not null) File.SetLastWriteTime(p, lastWrite.Value);
        return p;
    }

    [Fact]
    public void TryFindList_Hit_NonEmptyRecentFile()
    {
        var since = DateTime.Now.AddMinutes(-1);
        string expected = WriteList("某UP主", "https://www.bilibili.com/video/BV1GJ411x7h7\nhttps://www.bilibili.com/video/BV1xx411c7mD\n");
        Assert.True(UpSpaceExportVerifier.TryFindList(_dir, since, out var path));
        Assert.Equal(expected, path);
    }

    [Fact]
    public void TryFindList_EmptyFile_DoesNotCount()
    {
        var since = DateTime.Now.AddMinutes(-1);
        WriteList("某UP主", "");
        Assert.False(UpSpaceExportVerifier.TryFindList(_dir, since, out var path));
        Assert.Null(path);                                   // 空清单 = 没导出来，不算成功
    }

    [Fact]
    public void TryFindList_StaleFile_OutsideWindow_DoesNotCount()
    {
        var since = DateTime.Now.AddMinutes(-1);
        // 上次运行留下的旧清单（写入时间在 since 之前）
        WriteList("某UP主", "https://www.bilibili.com/video/BV1GJ411x7h7\n", DateTime.Now.AddHours(-2));
        Assert.False(UpSpaceExportVerifier.TryFindList(_dir, since, out var path));
        Assert.Null(path);
    }

    [Fact]
    public void TryFindList_MultipleRecentFiles_LatestWins()
    {
        var since = DateTime.Now.AddMinutes(-10);
        var older = WriteList("UP甲", "https://www.bilibili.com/video/BV1GJ411x7h7\n",
            DateTime.Now.AddMinutes(-5));
        var newer = WriteList("UP乙", "https://www.bilibili.com/video/BV1xx411c7mD\n",
            DateTime.Now.AddMinutes(-1));
        Assert.True(UpSpaceExportVerifier.TryFindList(_dir, since, out var path));
        Assert.Equal(newer, path);                            // 取最新一个
        Assert.NotEqual(older, path);
    }

    [Fact]
    public void TryFindList_WindowEdge_ExactlyAtSince_Counts()
    {
        var since = DateTime.Now.AddMinutes(-5);
        // 写入时间 == since（边界）：fi.LastWriteTime < since 为 false → 算本次产物
        var p = WriteList("某UP主", "https://www.bilibili.com/video/BV1GJ411x7h7\n", since);
        Assert.True(UpSpaceExportVerifier.TryFindList(_dir, since, out var path));
        Assert.Equal(p, path);
    }

    [Fact]
    public void TryFindList_MissingDir_ReturnsFalse_NoThrow()
    {
        string nope = Path.Combine(Path.GetTempPath(), "bbdh-v37-noexist-" + Guid.NewGuid().ToString("N"));
        Assert.False(UpSpaceExportVerifier.TryFindList(nope, DateTime.Now.AddMinutes(-1), out var path));
        Assert.Null(path);                                   // 目录不可读/被删：按未导出处理，不抛
    }

    [Fact]
    public void TryFindList_UnrelatedTxt_Ignored()
    {
        var since = DateTime.Now.AddMinutes(-1);
        WriteList("某UP主", "https://www.bilibili.com/video/BV1GJ411x7h7\n");
        File.WriteAllText(Path.Combine(_dir, "普通笔记.txt"), "不是投稿清单");
        Assert.True(UpSpaceExportVerifier.TryFindList(_dir, since, out var path));
        Assert.EndsWith(UpSpaceExportVerifier.ListFileSuffix, path);
    }

    // ---------- GuessCause：导出失败的最可能原因（纯函数三段式 + 兜底） ----------

    [Theory]
    [InlineData(1, "HTTP 412 Precondition Failed")]            // 风控拦截
    [InlineData(0, "code: 412")]
    public void GuessCause_RiskControl_When412(int exitCode, string logTail)
    {
        string msg = UpSpaceExportVerifier.GuessCause(exitCode, logTail);
        Assert.Contains("风控", msg);
        Assert.Contains("412", msg);
    }

    [Theory]
    [InlineData(1, "HTTP 403 Forbidden")]
    [InlineData(1, "错误：需要登录后才能访问")]
    [InlineData(1, "未登录")]
    [InlineData(1, "登录凭据已失效")]
    [InlineData(1, "cookie 失效")]
    [InlineData(1, "COOKIE invalid")]
    public void GuessCause_Login_WhenCredentialHints(int exitCode, string logTail)
    {
        string msg = UpSpaceExportVerifier.GuessCause(exitCode, logTail);
        Assert.Contains("扫码登录", msg);
        Assert.DoesNotContain("网络连接问题", msg);
    }

    [Theory]
    [InlineData(1, "网络不可达")]
    [InlineData(1, "timeout after 30s")]
    [InlineData(1, "Operation timed out")]
    [InlineData(1, "连接被重置")]
    [InlineData(1, "dns resolve failed")]
    public void GuessCause_Network_WhenConnectionHints(int exitCode, string logTail)
    {
        string msg = UpSpaceExportVerifier.GuessCause(exitCode, logTail);
        Assert.Contains("网络连接", msg);
        Assert.DoesNotContain("扫码登录", msg);
    }

    // ---------- B-01：裸 Contains("403") 把 aria2 流量文本误判成 HTTP 403 ----------
    // 导出走普通下载链路（CommandBuilder MultiThread 默认 true → 拼 -mt → aria2），
    // 其进度文本天然带「403MiB」，旧实现会据此报「凭据失效」，用户重扫码也修不好。

    [Theory]
    [InlineData(1, "[#x 403MiB/1.5GiB(26%) CN:1 DL:...]")]                       // aria2 流量文本
    [InlineData(1, "Response status code does not indicate success: 403")]        // EnsureSuccessStatusCode
    public void GuessCause_NotLogin_When403IsNotHttpStatus(int exitCode, string logTail)
    {
        string msg = UpSpaceExportVerifier.GuessCause(exitCode, logTail);
        // 不得落进登录分支的专属归因（兜底句本身也提「未登录」，故锚定登录分支的确切措辞）
        Assert.DoesNotContain("未登录或登录凭据已失效", msg);
        // 应落到网络或兜底分支：两句都明确把网络列为可能原因
        Assert.Contains("网络", msg);
    }

    [Fact]
    public void GuessCause_Login_WhenExplicitHttp403Status()
    {
        // 正例：显式 HTTP 状态行仍是登录/凭据信号，修复不得把它一并丢掉
        string msg = UpSpaceExportVerifier.GuessCause(1, "ERROR: HTTP 403 Forbidden");
        Assert.Contains("未登录或登录凭据已失效", msg);
        Assert.DoesNotContain("网络连接问题", msg);
    }

    [Fact]
    public void GuessCause_Login_WhenPaidHint_SharedFeatureSet()
    {
        // 沿用 ErrorMapper.IsLoginRelated 的同一特征集（含「付费」），不本地另抄一份
        string msg = UpSpaceExportVerifier.GuessCause(1, "该视频为付费内容，需购买后观看");
        Assert.Contains("未登录或登录凭据已失效", msg);
    }

    [Fact]
    public void GuessCause_412_StillPrecedesLoginBranch()
    {
        // 412 优先级不变：同时含 412 与登录信号时仍归风控
        string msg = UpSpaceExportVerifier.GuessCause(1, "HTTP 412 Precondition Failed; cookie 失效");
        Assert.Contains("风控", msg);
        Assert.DoesNotContain("未登录或登录凭据已失效", msg);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "")]
    [InlineData(1, "暂不支持该功能")]                          // BBDown 固定红字，特征不足
    public void GuessCause_Fallback_CoversThreeCommonCauses(int exitCode, string logTail)
    {
        string msg = UpSpaceExportVerifier.GuessCause(exitCode, logTail);
        Assert.Contains("未登录", msg);
        Assert.Contains("网络", msg);
        Assert.Contains("风控", msg);                           // 兜底句同时覆盖三种常见原因
    }
}
