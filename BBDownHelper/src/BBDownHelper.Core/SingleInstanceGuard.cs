using System.IO.Pipes;

namespace BBDownHelper.Core;

/// <summary>
/// v3.8 单实例守卫（架构#2 小档 / 需求 R2.10）：
/// 命名 Mutex 保证全机只开一个主窗口；第二实例经命名管道给首实例发「激活」消息后退出。
/// 两实例不再同时读写 core\BBDown.data / core\logs。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private CancellationTokenSource? _cts;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    private static string MutexName(string appName) => appName + ".SingleInstance";
    private static string PipeName(string appName) => appName + ".Activate";

    /// <summary>
    /// 尝试成为主实例。成功返回守卫（须保持存活到进程退出）；
    /// 已存在主实例时：尽力发「激活」通知后返回 null（调用方应直接退出本进程）。
    /// </summary>
    public static SingleInstanceGuard? TryAcquirePrimary(string appName)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName(appName), out bool createdNew);
        if (createdNew) return new SingleInstanceGuard(mutex);
        try { mutex.Dispose(); } catch { }
        try { NotifyPrimary(appName); } catch { /* 通知不上就静默退出 */ }
        return null;
    }

    /// <summary>给已在运行的主实例发一次「激活窗口」消息（尽力而为，失败静默）。</summary>
    public static void NotifyPrimary(string appName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(appName),
                PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(500);
            client.WriteByte(1);
            client.Flush();
        }
        catch { /* 首实例可能在退出中：静默 */ }
    }

    /// <summary>
    /// 主实例启动激活监听：每收到一次管道消息就回调一次 onActivate（在线程池线程，
    /// UI 侧需自行切回界面线程）。监听循环任何异常都只结束当前连接、继续等下一次。
    /// </summary>
    public void StartActivationListener(string appName, Action onActivate)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName(appName),
                        PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    while (server.ReadByte() != -1) { /* 读完即可，内容无意义 */ }
                    try { onActivate(); } catch { /* 回调异常不炸监听 */ }
                }
                catch (OperationCanceledException) { return; }
                catch { /* 本轮监听失败：重建管道继续 */ }
            }
        });
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _mutex.ReleaseMutex(); } catch { }
        try { _mutex.Dispose(); } catch { }
    }
}
