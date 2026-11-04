namespace BBDownHelper.Core;

/// <summary>
/// v3.4 预览卡防抖闸：输入变化后等待窗口期（默认 500ms），
/// 窗口内又有新输入则旧轮作废（旧等待被取消、旧结果不得上屏）。
/// 延迟函数可注入，便于单测防抖与作废语义；供 UI 线程独占使用（无锁）。
/// </summary>
public sealed class DebounceGate
{
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly GenerationGate _gate = new();
    private CancellationTokenSource _cts = new();

    public DebounceGate(Func<int, CancellationToken, Task>? delay = null)
        => _delay = delay ?? Task.Delay;

    /// <summary>登记一次输入变化：取消上一轮等待与请求，返回本轮代号。</summary>
    public int Submit()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        return _gate.Next();
    }

    /// <summary>等待防抖窗口；窗口内又有新输入（或令牌已取消）返回 false，本轮作废。</summary>
    public async Task<bool> ElapsedAsync(int version, int delayMs = 500)
    {
        try { await _delay(delayMs, _cts.Token); }
        catch (OperationCanceledException) { return false; }
        return _gate.IsCurrent(version);
    }

    /// <summary>该代号是否仍是最新一轮（不是则结果应丢弃，不得上屏）。</summary>
    public bool IsCurrent(int version) => _gate.IsCurrent(version);

    /// <summary>当前轮的取消令牌（取消窗口等待 + 在途 HTTP 请求）。</summary>
    public CancellationToken Token => _cts.Token;
}
