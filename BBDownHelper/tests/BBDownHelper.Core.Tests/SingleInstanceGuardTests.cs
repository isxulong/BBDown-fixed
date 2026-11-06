using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>v3.8 SingleInstanceGuard（R2.10）：主/次实例判定、释放后可再抢、激活通知管道。</summary>
public class SingleInstanceGuardTests
{
    private static string Unique() => "BBDownHelperTest-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void SecondAcquire_SameName_NotPrimary()
    {
        var name = Unique();
        using var first = SingleInstanceGuard.TryAcquirePrimary(name);
        Assert.NotNull(first);
        using var second = SingleInstanceGuard.TryAcquirePrimary(name);
        Assert.Null(second);   // 第二实例：已通知首实例激活，自己应退出（返回 null）
    }

    [Fact]
    public void AfterDispose_CanAcquireAgain()
    {
        var name = Unique();
        var first = SingleInstanceGuard.TryAcquirePrimary(name);
        Assert.NotNull(first);
        first!.Dispose();
        using var third = SingleInstanceGuard.TryAcquirePrimary(name);
        Assert.NotNull(third);
    }

    [Fact]
    public void DifferentNames_BothPrimary()
    {
        using var a = SingleInstanceGuard.TryAcquirePrimary(Unique());
        using var b = SingleInstanceGuard.TryAcquirePrimary(Unique());
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public async Task ActivationListener_ReceivesNotify()
    {
        var name = Unique();
        using var guard = SingleInstanceGuard.TryAcquirePrimary(name);
        Assert.NotNull(guard);
        var got = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        guard!.StartActivationListener(name, () => got.TrySetResult(true));

        // 给监听循环一点建立管道的时间，然后模拟第二实例发激活消息
        await Task.Delay(300);
        SingleInstanceGuard.NotifyPrimary(name);

        var done = await Task.WhenAny(got.Task, Task.Delay(10_000));
        Assert.Same(got.Task, done);   // 10 秒内必须收到激活回调
    }

    [Fact]
    public void NotifyPrimary_NoListener_SilentNoThrow()
    {
        SingleInstanceGuard.NotifyPrimary(Unique());   // 无监听者：连接超时静默，不抛
    }
}
