namespace BBDownHelper.Core;

/// <summary>
/// v3.8 日志通道地基（架构方案 §1 LogStore / 需求 R2.7/R2.8/R2.9 的共同依赖）：
/// 内存环形缓冲（默认保留最近 500 行，超限裁头）+ 滚动落盘 core\logs\helper-yyyyMMdd.log
/// （异步、绝不挡 UI）+ 合帧批量上屏事件（默认 80ms，把子进程逐行输出合并成批次，
/// 消除「每行一次 UI Post」的卡顿来源——高工#3）。
/// Append 线程安全（读管线线程与 UI 线程都会调），且绝不抛异常、绝不做 IO。
/// v3.9（红队 P2-2 修复）：落盘写路径整体纳入 _gate 串行化（合帧 Timer 线程与 UI 线程
/// Flush 并发安全）；落盘失败改为退避重试（下次合帧自动重开 writer），连续失败达上限才停用，
/// 停用时在内存缓冲留一行标记——任何情况下内存环形缓冲保持完整可用。
/// </summary>
public sealed class LogStore : IDisposable
{
    /// <summary>环形缓冲默认容量（行）。</summary>
    public const int DefaultMaxLines = 500;

    /// <summary>默认合帧间隔（需求口径 50~100ms，取中值 80ms）。</summary>
    public const int DefaultFlushMs = 80;

    /// <summary>落盘连续失败达到此次数后停用落盘（退避重试上限）。</summary>
    public const int MaxConsecutiveFileFailures = 3;

    private readonly object _gate = new();
    private readonly Queue<string> _ring;          // 完整保真缓冲（简洁模式只折叠展示，不销毁信息）
    private readonly List<string> _pending;        // 上次合帧以来新进的行
    private readonly int _maxLines;
    private readonly string? _logDir;              // null = 落盘不可用（构造时给 null 即纯内存模式）
    private readonly Timer _timer;
    private StreamWriter? _writer;
    private string? _currentFile;
    private string _currentDate = "";
    private int _fileFailCount;                    // 连续落盘失败次数（写成功即清零）
    private bool _fileDisabled;                    // 连续失败达上限后停用落盘（内存缓冲不受影响）

    /// <summary>合帧后推送本批次新行（在后台线程触发，UI 侧需自行 Post 到界面线程）。</summary>
    public event Action<IReadOnlyList<string>>? LinesAdded;

    /// <summary>当前日志文件完整路径；未开启落盘或落盘已失败关闭时为 null。</summary>
    public string? CurrentFile => _currentFile;

    /// <summary>缓冲内当前行数（≤ maxLines）。</summary>
    public int Count { get { lock (_gate) return _ring.Count; } }

    /// <summary>
    /// logDir 传 null 表示纯内存模式（测试用）；目录不存在时首次落盘才创建，创建失败静默转纯内存。
    /// </summary>
    public LogStore(string? logDir, int maxLines = DefaultMaxLines, int flushMs = DefaultFlushMs)
    {
        _maxLines = Math.Max(1, maxLines);
        _logDir = logDir;
        _ring = new Queue<string>(_maxLines);
        _pending = new List<string>();
        _timer = new Timer(_ => Flush(), null, flushMs, Math.Max(10, flushMs));
    }

    /// <summary>追加一行（线程安全、无 IO、绝不抛）。超长行截断到 4000 字符防内存膨胀。</summary>
    public void Append(string line)
    {
        if (line is null) return;
        if (line.Length > 4000) line = line[..4000] + " …（超长行已截断）";
        lock (_gate)
        {
            _ring.Enqueue(line);
            while (_ring.Count > _maxLines) _ring.Dequeue();   // 环形裁头
            _pending.Add(line);
        }
    }

    /// <summary>取最近 n 行（n 超过存量则全部返回）。</summary>
    public IReadOnlyList<string> Recent(int n)
    {
        lock (_gate)
        {
            if (n >= _ring.Count) return _ring.ToArray();
            return _ring.Skip(_ring.Count - n).ToArray();
        }
    }

    /// <summary>
    /// 合帧：把 pending 批次推给 LinesAdded 并异步落盘。可由定时器自动触发，
    /// 也可手动调用（进程退出/崩溃兜底前冲刷现场）。绝不抛异常。
    /// </summary>
    public void Flush()
    {
        string[] batch;
        lock (_gate)
        {
            if (_pending.Count == 0) { WriteFileNoThrow(Array.Empty<string>()); return; }
            batch = _pending.ToArray();
            _pending.Clear();
        }
        try { LinesAdded?.Invoke(batch); } catch { /* 订阅方异常不得炸日志通道 */ }
        WriteFileNoThrow(batch);
    }

    /// <summary>把缓冲内容导出到指定文件（求助导出用）。失败返回 false，绝不抛。</summary>
    public bool Export(string path, int maxLines = int.MaxValue)
    {
        try
        {
            var lines = maxLines == int.MaxValue ? Recent(Count) : Recent(maxLines);
            File.WriteAllLines(path, lines);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 滚动落盘：按日期分文件 helper-yyyyMMdd.log。绝不抛异常。
    /// v3.9：写路径整体在 _gate 内串行（StreamWriter 非线程安全）；失败退避重试——
    /// 本次放弃、下次合帧重开 writer 再试，写成功清零计数；连续失败达
    /// MaxConsecutiveFileFailures 次才停用落盘，并在内存缓冲留一行标记，
    /// 让求助导出能提示日志文件不完整。任何失败路径都不影响内存缓冲与上屏。
    /// </summary>
    private void WriteFileNoThrow(string[] batch)
    {
        if (_logDir is null) return;
        lock (_gate)
        {
            if (_fileDisabled) return;
            try
            {
                string today = DateTime.Now.ToString("yyyyMMdd");
                if (_writer is null || today != _currentDate)
                {
                    _writer?.Dispose();
                    _writer = null;
                    Directory.CreateDirectory(_logDir);
                    _currentDate = today;
                    _currentFile = Path.Combine(_logDir, $"helper-{today}.log");
                    _writer = new StreamWriter(new FileStream(
                        _currentFile, FileMode.Append, FileAccess.Write, FileShare.Read))
                    { AutoFlush = true };
                }
                foreach (var line in batch)
                    _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
                _fileFailCount = 0;   // 写成功即清零退避计数
            }
            catch (Exception ex)
            {
                // 落盘失败（目录不可写/被杀软拦/并发写被抢等）：本次静默放弃，下次合帧重试；
                // 连续失败达上限才停用落盘，内存缓冲留标记，之后不再尝试
                _fileFailCount++;
                try { _writer?.Dispose(); } catch { }
                _writer = null;
                if (_fileFailCount >= MaxConsecutiveFileFailures)
                {
                    _fileDisabled = true;
                    _currentFile = null;
                    _ring.Enqueue($"【落盘已停用：连续 {_fileFailCount} 次写入失败（{ex.GetType().Name}），本会话日志文件不完整，内存缓冲未受影响】");
                    while (_ring.Count > _maxLines) _ring.Dequeue();
                }
            }
        }
    }

    public void Dispose()
    {
        try { _timer.Dispose(); } catch { }
        Flush();
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
