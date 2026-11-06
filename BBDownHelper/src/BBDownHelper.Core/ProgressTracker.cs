using System.Text.RegularExpressions;

namespace BBDownHelper.Core;

/// <summary>
/// v4.0（批次3 R3.3 / 架构 §3）：批量/多P 下载总体进度跟踪（纯逻辑可单测）。
/// 日志口径全部锚定 BBDown-fixed 源码 Program.cs（2026-11-05 实读）：
///  - 「共计 {N} 个分P」（L332）：多P视频解析完成时的总数；
///  - 「开始解析P{index}: {aid}... ({i} of {N})」（L356）：每个条目（分P/列表项）解析开始，
///    i 是全局序号、N 是总数——列表下载时每个条目一个 aid，i of N 就是条目计数；
///  - 「下载P{index}完毕」（L647/L734）：单条目完成。⚠️ 已下载过的条目打
///    「aid: {aid}已下载过, 跳过下载...」（L363）后无「完毕」行——所以 Done 不以「完毕」计数为准，
///    而以解析序号推进为准：第 i 条开始解析 ⇒ 前 i-1 条已落定（完成或跳过）；
///  - 「P{index}: [{cid}] [{title}] [{dur}]」（L318）：分P标题；>5 页时只打前 5 + 最后 1 条
///    （L309-312 more 逻辑），缺号标题回退「P{index}」；
///  - 「任务完成」（L377）：全部结束，Done 直接顶到 Total。
/// 进度条不回跳：OverallPercent 单调不减（_maxPercent 钉住）。
/// IsBatch = Total > 1：多P视频与列表下载都走 x/N 模式；单P视频仍走原百分比解析。
/// </summary>
public sealed class ProgressTracker
{
    private static readonly Regex TotalRx = new(@"共计\s*(\d+)\s*个分P", RegexOptions.Compiled);
    private static readonly Regex ParseStartRx =
        new(@"开始解析P(\d+):.*\((\d+)\s*of\s*(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex PageDoneRx = new(@"下载P(\d+)完毕", RegexOptions.Compiled);
    private static readonly Regex PageTitleRx =
        new(@"^P(\d+):\s*\[\d+\]\s*\[(.+?)\]\s*\[", RegexOptions.Compiled);

    private readonly Dictionary<int, string> _pageTitles = new();
    private readonly HashSet<int> _completed = new();
    private int _maxPercent;
    private int _currentIndex;

    /// <summary>条目总数（0=还不知道，按单条百分比模式显示）。</summary>
    public int Total { get; private set; }

    /// <summary>已落定（完成或跳过）的条目数。</summary>
    public int Done { get; private set; }

    /// <summary>当前正在下载的条目展示名（分P标题；取不到时回退「P{序号}」）。</summary>
    public string? CurrentTitle { get; private set; }

    /// <summary>true=多条目模式（多P视频/合集/系列/收藏夹），界面显示「已完成 x/N」。</summary>
    public bool IsBatch => Total > 1;

    /// <summary>总体百分比（单调不减，杜绝进度条 0→100 回跳）。</summary>
    public int OverallPercent => _maxPercent;

    /// <summary>新一轮下载前复位。</summary>
    public void Reset()
    {
        Total = 0; Done = 0; _maxPercent = 0; _currentIndex = 0;
        CurrentTitle = null;
        _pageTitles.Clear();
        _completed.Clear();
    }

    /// <summary>喂一行日志；返回 true 表示批量进度状态有变化（界面需要刷新）。</summary>
    public bool Feed(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;

        var m = PageTitleRx.Match(line);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int ti))
        {
            _pageTitles[ti] = m.Groups[2].Value;
            return false;   // 标题先存着，等解析到该条目时取用
        }

        m = TotalRx.Match(line);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > 0)
        {
            bool changed = n != Total;
            Total = Math.Max(Total, n);
            return changed;
        }

        m = ParseStartRx.Match(line);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out int index)
            && int.TryParse(m.Groups[2].Value, out int order)
            && int.TryParse(m.Groups[3].Value, out int total) && total > 0)
        {
            // P2-1 修复：「(i of N)」的 N 是 -p 过滤后的真实条目数（fork 在过滤后才打此行），
            // 是分母的权威来源——直接覆盖，不能用 Math.Max 钉住「共计 N 个分P」的过滤前全量。
            Total = total;
            // 第 order 条开始解析 ⇒ 前 order-1 条已落定（完成或跳过都算上）
            Done = Math.Max(Done, Math.Min(order - 1, Total));
            _currentIndex = index;
            CurrentTitle = _pageTitles.TryGetValue(index, out var t) ? t : $"P{index}";
            BumpPercent();
            return true;
        }

        m = PageDoneRx.Match(line);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int doneIdx))
        {
            _completed.Add(doneIdx);
            // 「完毕」计数与解析序号推进取大者（跳过条目无完毕行，靠解析序号兜住）
            int byCount = Math.Min(_completed.Count, Total > 0 ? Total : _completed.Count);
            Done = Math.Max(Done, byCount);
            BumpPercent();
            return true;
        }

        if (line.Contains("任务完成", StringComparison.Ordinal))
        {
            if (Total > 0) Done = Total;
            _maxPercent = Total > 0 ? 100 : _maxPercent;
            CurrentTitle = null;
            return true;
        }

        return false;
    }

    private void BumpPercent()
    {
        if (Total <= 0) return;
        int p = (int)Math.Round(100.0 * Done / Total);
        if (p > _maxPercent) _maxPercent = p;
    }
}
