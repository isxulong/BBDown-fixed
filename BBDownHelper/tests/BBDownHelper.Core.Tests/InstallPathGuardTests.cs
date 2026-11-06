using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>v3.8 InstallPathGuard（R2.11 安放路径自检）：Temp/压缩软件缓存/同步盘命中，正常位置不误报。</summary>
public class InstallPathGuardTests
{
    [Fact]
    public void TempDir_Warns()
    {
        Assert.Equal(InstallPathGuard.Warning,
            InstallPathGuard.Check("C:\\Users\\xiaoming\\AppData\\Local\\Temp\\BBDownHelper"));
    }

    [Fact]
    public void SevenZipCache_Warns()   // 压缩软件直跑的随机临时目录
    {
        Assert.Equal(InstallPathGuard.Warning,
            InstallPathGuard.Check("C:\\Users\\x\\AppData\\Local\\Temp\\7zO1A2B\\BBDown-fixed"));
        Assert.Equal(InstallPathGuard.Warning,
            InstallPathGuard.Check("C:\\Users\\x\\AppData\\Local\\Temp\\Rar$DIa1234\\bb"));
    }

    [Fact]
    public void SyncDirs_Warn()
    {
        Assert.NotNull(InstallPathGuard.Check("C:\\Users\\x\\OneDrive\\工具\\BBDown-fixed"));
        Assert.NotNull(InstallPathGuard.Check("D:\\百度网盘\\BBDown-fixed"));
        Assert.NotNull(InstallPathGuard.Check("D:\\坚果云\\tools"));
        Assert.NotNull(InstallPathGuard.Check("C:\\Users\\x\\Dropbox\\bb"));
        Assert.NotNull(InstallPathGuard.Check("/home/x/onedrive/bb"));   // 大小写不敏感
    }

    [Fact]
    public void NormalDirs_NoWarn()
    {
        Assert.Null(InstallPathGuard.Check("C:\\Users\\x\\Desktop\\BBDown-fixed"));
        Assert.Null(InstallPathGuard.Check("D:\\Tools\\BBDown-fixed"));
        Assert.Null(InstallPathGuard.Check("D:\\Template\\bb"));   // Template ≠ Temp，不误命中
        Assert.Null(InstallPathGuard.Check("C:\\迅雷下载\\BBDown-fixed"));
    }

    [Fact]
    public void NullOrEmpty_NoWarn()
    {
        Assert.Null(InstallPathGuard.Check(null));
        Assert.Null(InstallPathGuard.Check(""));
        Assert.Null(InstallPathGuard.Check("   "));
    }
}
