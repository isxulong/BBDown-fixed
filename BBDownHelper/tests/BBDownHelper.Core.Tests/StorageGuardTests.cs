using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>v3.8 StorageGuard（R2.3 开始前预检）：三态（可用/不可用/低空间）。</summary>
public class StorageGuardTests
{
    [Fact]
    public void Check_MissingDir_NotOk()
    {
        var r = StorageGuard.Check("D:\\gone", _ => false, _ => true, _ => 100L);
        Assert.False(r.Ok);
        Assert.Contains("不存在", r.Error);
    }

    [Fact]
    public void Check_NotWritable_NotOk()
    {
        var r = StorageGuard.Check("D:\\ro", _ => true, _ => false, _ => 100L);
        Assert.False(r.Ok);
        Assert.Contains("不可写", r.Error);
    }

    [Fact]
    public void Check_EmptyDir_NotOk()
    {
        var r = StorageGuard.Check("  ", _ => true, _ => true, _ => 100L);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Check_LowSpace_OkButWarns()
    {
        var r = StorageGuard.Check("D:\\ok", _ => true, _ => true,
            _ => StorageGuard.LowSpaceThresholdBytes - 1);
        Assert.True(r.Ok);                 // 不阻塞
        Assert.True(r.LowSpace);           // 只黄条提醒
    }

    [Fact]
    public void Check_PlentySpace_NoWarn()
    {
        var r = StorageGuard.Check("D:\\ok", _ => true, _ => true,
            _ => StorageGuard.LowSpaceThresholdBytes);
        Assert.True(r.Ok);
        Assert.False(r.LowSpace);
    }

    [Fact]
    public void Check_FreeSpaceUnknown_NoWarn()   // 取不到空间信息不阻塞不误报
    {
        var r = StorageGuard.Check("D:\\ok", _ => true, _ => true, _ => -1L);
        Assert.True(r.Ok);
        Assert.False(r.LowSpace);
    }

    [Fact]
    public void Check_ProbeThrows_TreatedAsFailure_NoThrow()
    {
        var r = StorageGuard.Check("D:\\x",
            _ => throw new IOException("enum boom"), _ => true, _ => 100L);
        Assert.False(r.Ok);                // 存在性探针抛异常 = 不可用，不抛给调用方
    }

    [Fact]
    public void Check_RealWritableDir_Ok()   // 真实临时目录端到端（建删探针文件）
    {
        var dir = Path.Combine(Path.GetTempPath(), "sg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var r = StorageGuard.Check(dir);
            Assert.True(r.Ok);
            Assert.Empty(Directory.GetFiles(dir));   // 探针文件已删
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Check_RealMissingDir_NotOk()
    {
        var r = StorageGuard.Check(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N")));
        Assert.False(r.Ok);
        Assert.Contains("不存在", r.Error);
    }
}
