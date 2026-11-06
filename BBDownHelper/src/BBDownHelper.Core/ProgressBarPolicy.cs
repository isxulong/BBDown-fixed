namespace BBDownHelper.Core;

/// <summary>进度条落值决策结果。IsIndeterminate=true 时界面走不确定态动画，Percent 不参与显示。</summary>
public readonly record struct ProgressBarDecision(bool IsIndeterminate, int Percent);

/// <summary>
/// v1.0.4 补修（红队 P2-3 闭合）：进度条落值决策纯函数。
/// 事实依据（fork 源码 BBDown-1.6.3，2026-11-05 实读 + 逐行复刻渲染路径实测）：
///  - ProgressBar.cs L44 `if (!Console.IsOutputRedirected || RelatedTask is not null)`：helper 以
///    `RedirectStandardOutput = true` 的隐藏子进程运行 BBDown（MainWindow.axaml.cs L975）且非服务器
///    下载任务，故 ResetTimer 不执行、定时器不启动；
///  - L115 `UpdateText` 首行 `if (Console.IsOutputRedirected) return;`：重定向下不写任何标签，
///    L96-99 拼出的唯一进度百分比文本（format 里的 {2,3}%）因此永不落地。
/// 结论：重定向下没有任何进度百分比数据源。
///
/// 落值规则：
///  - 非批量（单条目）：既无 x/N 也不可能有百分比行 ⇒ **不确定态**，不落任何定值；
///    直到任务结束由完成态出口置 100%（成功）或归 0%（失败/取消）。
///  - 批量（多条目）：唯一可信数据源是 x/N（Done 条 / Total 条），按条目完成度落值；
///    仅当真收到可见百分比行（非重定向来源，如 -ia 可见控制台）才按百分比兜底，
///    批量下取与 x/N 的较大者防回跳。
/// </summary>
public static class ProgressBarPolicy
{
    /// <summary>
    /// 进度条落值决策。
    /// </summary>
    /// <param name="isBatch">多条目模式（ProgressTracker.IsBatch，即 Total &gt; 1）。</param>
    /// <param name="done">已完成条目数（仅批量模式参与计算）。</param>
    /// <param name="total">条目总数（仅批量模式参与计算）。</param>
    /// <param name="percentLineVisible">是否真的收到一行可见百分比（重定向下 fork 永不输出）。</param>
    /// <param name="percentValue">该百分比行的数值（percentLineVisible 为 false 时忽略）。</param>
    public static ProgressBarDecision Decide(
        bool isBatch, int done, int total, bool percentLineVisible, int percentValue = 0)
    {
        if (!isBatch)
        {
            // 单条目：没有 x/N 也没有百分比行 ⇒ 不确定态（不落定值，避免 0% 卡死误导）
            if (!percentLineVisible) return new ProgressBarDecision(true, 0);
            return new ProgressBarDecision(false, Math.Clamp(percentValue, 0, 100));
        }

        int byCount = total > 0
            ? (int)Math.Round(100.0 * Math.Clamp(done, 0, total) / total)
            : 0;

        // 批量：x/N 为主，可见百分比行只作兜底，取较大者防回跳
        int p = percentLineVisible ? Math.Max(byCount, Math.Clamp(percentValue, 0, 100)) : byCount;
        return new ProgressBarDecision(false, p);
    }
}

/// <summary>一次界面落值的产物：IsIndeterminate/Percent 是应写入的值，Changed=true 才需要真的写控件。</summary>
public readonly record struct ProgressBarPaint(bool IsIndeterminate, int Percent, bool Changed);

/// <summary>
/// v1.0.4 修复批 v2：界面落值端。
/// 上一批的缺口是 Decide 只在测试里被调用、界面直接读 OverallPercent 落值；本类把
/// 「决策 → 是否需要写控件」也做成可单测的纯逻辑，界面只按它的返回值写控件，不再自己判分支。
///
/// 关键规则（-p 单分P 回归的根因）：**不确定态与定值是两种状态，切换本身就是一次必须落地的写入**。
/// 旧实现在 IsBatch 转假后不再写进度条，于是留下先前落下的 0% 定值；这里把「回到不确定态」
/// 记为 Changed=true，界面随之写回 IsIndeterminate=true，0% 定值不会残留。
/// </summary>
public sealed class ProgressBarPresenter
{
    private bool _lastIndeterminate = true;   // 开跑即不确定态（主窗口启动时置 IsIndeterminate = true）
    private int _lastPercent = -1;

    /// <summary>新一轮下载前复位（主窗口起跑处调用）。</summary>
    public void Reset()
    {
        _lastIndeterminate = true;
        _lastPercent = -1;
    }

    public ProgressBarPaint Apply(ProgressBarDecision d)
    {
        if (d.IsIndeterminate)
        {
            bool changed = !_lastIndeterminate;   // 定值 → 不确定态：必须写一次，否则旧定值留在界面上
            _lastIndeterminate = true;
            _lastPercent = -1;                    // 丢弃旧定值，之后回到定值必然重写
            return new ProgressBarPaint(true, d.Percent, changed);
        }

        bool changedToValue = _lastIndeterminate || d.Percent != _lastPercent;
        _lastIndeterminate = false;
        _lastPercent = d.Percent;
        return new ProgressBarPaint(false, d.Percent, changedToValue);
    }
}
