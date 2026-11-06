using System.Runtime.InteropServices;

namespace BBDownHelper.Core;

/// <summary>
/// v3.8 完成提示音（高工#5 小档 / 需求 R2.5）：Windows MessageBeep P/Invoke。
/// 只在 Windows 上发声；Linux/macOS 与任何失败一律静默（声明参与 Linux 编译但不会被调用）。
/// </summary>
public static class CompletionSound
{
    private const uint MB_ICONASTERISK = 0x40;   // 成功：提示音
    private const uint MB_ICONHAND = 0x10;       // 失败：警示音

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    /// <summary>播一声系统提示音。success=true 成功音，false 失败音。非 Windows 或失败静默。</summary>
    public static void Play(bool success)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { MessageBeep(success ? MB_ICONASTERISK : MB_ICONHAND); }
        catch { /* 无声环境就算了 */ }
    }
}
