using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>v3.8 LogDisplayPolicy（R2.7 日志降噪）：简洁模式可见性与关键行高亮判定。</summary>
public class LogDisplayPolicyTests
{
    [Fact]
    public void Simple_OurOwnLines_AlwaysVisible()   // 我们写的提示/命令回显不带 BBDown 时间戳，全显示
    {
        Assert.True(LogDisplayPolicy.IsSimpleVisible("> BBDown -mt BV1xx411c7mD"));
        Assert.True(LogDisplayPolicy.IsSimpleVisible("已把老版本留下的 BBDown.data 移入 core 文件夹。"));
        Assert.True(LogDisplayPolicy.IsSimpleVisible("———"));
    }

    [Fact]
    public void Simple_BbdTimestampDetail_Hidden()   // BBDown 逐行细节默认折叠
    {
        Assert.False(LogDisplayPolicy.IsSimpleVisible("[2026-11-05 12:00:00.123] - Downloading 1 of 50"));
        Assert.False(LogDisplayPolicy.IsSimpleVisible("[2026-11-05 12:00:00.124] - 开始下载分P"));
    }

    [Fact]
    public void Simple_BbdProgressLine_Visible()
    {
        Assert.True(LogDisplayPolicy.IsSimpleVisible("[2026-11-05 12:00:00.125] - 45.3%"));
    }

    [Fact]
    public void Simple_BbdKeyLine_Visible()
    {
        Assert.True(LogDisplayPolicy.IsSimpleVisible("[2026-11-05 12:00:01.000] - 下载完成"));
        Assert.True(LogDisplayPolicy.IsSimpleVisible("[2026-11-05 12:00:01.000] - 下载失败"));
    }

    [Fact]
    public void KeyLine_CompletionFailureError_Highlighted()
    {
        Assert.True(LogDisplayPolicy.IsKeyLine("下载完成！"));
        Assert.True(LogDisplayPolicy.IsKeyLine("导出失败：…"));
        Assert.True(LogDisplayPolicy.IsKeyLine("出错了：…"));
        Assert.True(LogDisplayPolicy.IsKeyLine("登录成功，以后不用再扫。"));
        Assert.True(LogDisplayPolicy.IsKeyLine("已取消，未完成的临时文件可在保存文件夹删除"));
    }

    [Fact]
    public void KeyLine_NormalProgress_NotKey()
    {
        Assert.False(LogDisplayPolicy.IsKeyLine("[2026-11-05 12:00:00.125] - 45.3%"));
        Assert.False(LogDisplayPolicy.IsKeyLine("> BBDown -mt BV1xx411c7mD"));
    }
}
