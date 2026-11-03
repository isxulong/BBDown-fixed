namespace BBDownHelper.Core;

/// <summary>
/// 识别竞态代际闸（红蓝共识 P2）。
/// 每次发起新识别取一个递增代号；异步识别返回时比对代号，
/// 过期（期间用户又改了输入）的结果必须丢弃，不得覆盖新结果卡片。
/// 典型场景：b23 短链还在解析 → 用户改粘 BV → 慢返回的旧结果不得落地。
/// </summary>
public sealed class GenerationGate
{
    private int _generation;

    /// <summary>开启新一轮识别，返回本轮代号。</summary>
    public int Next() => Interlocked.Increment(ref _generation);

    /// <summary>该代号是否仍是当前最新一轮（不是则结果应丢弃）。</summary>
    public bool IsCurrent(int generation) => generation == Volatile.Read(ref _generation);
}
