using System;
using Avalonia;
using Avalonia.Controls;
using BBDownHelper.Core;

namespace BBDownHelper;

internal static class Program
{
    // v3.8（R2.10）：单实例守卫句柄必须活到进程退出，用静态字段持有
    private static SingleInstanceGuard? _instanceGuard;

    [STAThread]
    public static void Main(string[] args)
    {
        // v3.8（R2.10 架构#2 小档）：命名 Mutex 单实例——第二实例给首实例发「激活」消息后退出。
        // 两个实例同时读写 core\BBDown.data / core\logs 会互相踩，这里从源头挡住。
        _instanceGuard = SingleInstanceGuard.TryAcquirePrimary("BBDownHelper");
        if (_instanceGuard is null) return;   // 已有实例在跑：TryAcquirePrimary 内部已发激活通知

        // v3.8（QC#3 / 地基三件套）：全局未处理异常 → core\logs\crash-日期.log + 尽力提示
        CrashHandler.Register(CoreLayout.LogDir(AppContext.BaseDirectory));

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>v3.8（R2.10）：主窗口创建后接起激活管道监听——第二实例启动时把本窗口提到前台。</summary>
    internal static void WireActivation(Window mainWindow)
    {
        _instanceGuard?.StartActivationListener("BBDownHelper", () =>
        {
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (mainWindow.WindowState == WindowState.Minimized)
                        mainWindow.WindowState = WindowState.Normal;
                    mainWindow.Show();
                    mainWindow.Activate();
                });
            }
            catch { /* 激活失败静默 */ }
        });
    }
}
