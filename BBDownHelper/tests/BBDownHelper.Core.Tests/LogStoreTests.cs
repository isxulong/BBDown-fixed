using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>v3.8 LogStore（R2.7/R2.8/R2.9 地基）：环形缓冲、合帧批量、滚动落盘、失败静默、并发。</summary>
public class LogStoreTests
{
    [Fact]
    public void Append_OverCapacity_TrimsHead()
    {
        using var store = new LogStore(null, maxLines: 5, flushMs: 10_000);
        for (int i = 1; i <= 8; i++) store.Append("line" + i);
        var recent = store.Recent(100);
        Assert.Equal(5, recent.Count);
        Assert.Equal("line4", recent[0]);   // 1~3 被裁头
        Assert.Equal("line8", recent[^1]);
        Assert.Equal(5, store.Count);
    }

    [Fact]
    public void Recent_TakesLastN()
    {
        using var store = new LogStore(null, maxLines: 10, flushMs: 10_000);
        for (int i = 1; i <= 6; i++) store.Append("l" + i);
        var r = store.Recent(3);
        Assert.Equal(new[] { "l4", "l5", "l6" }, r);
    }

    [Fact]
    public void Flush_RaisesLinesAddedWithBatch()
    {
        using var store = new LogStore(null, flushMs: 10_000);
        var batches = new List<IReadOnlyList<string>>();
        store.LinesAdded += b => batches.Add(b);
        store.Append("a");
        store.Append("b");
        store.Append("c");
        store.Flush();
        Assert.Single(batches);             // 合帧：三行合并成一个批次
        Assert.Equal(new[] { "a", "b", "c" }, batches[0]);
        store.Flush();                      // 无新增 → 不再触发事件
        Assert.Single(batches);
    }

    [Fact]
    public void Append_NeverThrows_AndTruncatesLongLines()
    {
        using var store = new LogStore(null, flushMs: 10_000);
        store.Append(null!);                                 // null 静默忽略
        store.Append(new string('x', 10_000));               // 超长行截断
        var r = store.Recent(10);
        Assert.Single(r);
        Assert.True(r[0].Length < 4100);
        Assert.EndsWith("（超长行已截断）", r[0]);
    }

    [Fact]
    public void FileSink_WritesRollingLog_AndSurvivesReadback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "logstore-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? file;
            using (var store = new LogStore(dir, flushMs: 10_000))
            {
                store.Append("第一行中文");
                store.Append("second");
                store.Flush();
                file = store.CurrentFile;
                Assert.NotNull(file);
                Assert.True(File.Exists(file));
                Assert.Contains("helper-", Path.GetFileName(file));
            }
            var text = File.ReadAllText(file!);
            Assert.Contains("第一行中文", text);
            Assert.Contains("second", text);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void FileSink_ScrubsCredentials_MemoryKeepsRaw()
    {
        // 本轮 P2 回归：core\logs\helper-*.log 会被用户整个文件夹打包外发，
        // 磁盘这份必须打码；同时内存缓冲保留原始行，让用户当场看得懂。
        var dir = Path.Combine(Path.GetTempPath(), "logstore-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? file;
            const string raw = "登录成功: AccessToken=tvsecret123456";
            using (var store = new LogStore(dir, flushMs: 10_000))
            {
                store.Append(raw);
                store.Flush();
                file = store.CurrentFile;
                Assert.NotNull(file);
                // 内存里仍是原文——界面要显示用户能看懂的原始行
                Assert.Contains(raw, store.Recent(10));
            }
            var text = File.ReadAllText(file!);
            Assert.DoesNotContain("tvsecret123456", text);   // 磁盘这份已打码
            Assert.Contains("AccessToken=", text);           // 键名保留，仍能看出是哪一步
            Assert.Contains("***", text);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void FileSink_BadDir_FailsSilently_KeepsMemoryWorking()
    {
        // 父路径是一个已存在的文件：任何平台上在此之下建目录都会失败（跨平台可靠触发）。
        // 落盘失败必须静默，内存缓冲照常。
        var blocker = Path.Combine(Path.GetTempPath(), "logstore-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            var bad = Path.Combine(blocker, "logs");
            using var store = new LogStore(bad, flushMs: 10_000);
            store.Append("hello");
            store.Flush();                                       // 不抛
            Assert.Null(store.CurrentFile);                      // 落盘已静默关闭
            Assert.Equal("hello", store.Recent(1)[0]);           // 内存缓冲仍在
        }
        finally { try { File.Delete(blocker); } catch { } }
    }

    [Fact]
    public void ConcurrentAppend_KeepsAllLines()
    {
        using var store = new LogStore(null, maxLines: 2000, flushMs: 10_000);
        Parallel.For(0, 8, t =>
        {
            for (int i = 0; i < 200; i++) store.Append($"t{t}-{i}");
        });
        Assert.Equal(1600, store.Count);
        var all = store.Recent(2000);
        Assert.Equal(1600, all.Count);
        Assert.Equal(1600, all.Distinct().Count());          // 无丢行无重行
    }

    [Fact]
    public void Export_WritesRecentLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "logstore-exp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "export.txt");
        try
        {
            using var store = new LogStore(null, flushMs: 10_000);
            store.Append("x1");
            store.Append("x2");
            Assert.True(store.Export(path));
            Assert.Equal(new[] { "x1", "x2" }, File.ReadAllLines(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // —— v3.9 红队 P2-2 回归：落盘退避重试 / 停用后内存缓冲完整 / 并发 Flush ——

    [Fact]
    public void FileSink_ConsecutiveFailures_DisablesAfterRetries_RingStaysIntact()
    {
        // 父路径是已存在文件：CreateDirectory 必失败（跨平台可靠触发）。
        var blocker = Path.Combine(Path.GetTempPath(), "logstore-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            var bad = Path.Combine(blocker, "logs");
            using var store = new LogStore(bad, flushMs: 10_000);
            store.Append("m1");
            store.Append("m2");
            store.Append("m3");
            // 连续失败达上限（3 次）才停用：前两次失败不应留停用标记
            store.Flush();
            store.Flush();
            Assert.DoesNotContain(store.Recent(100), l => l.Contains("落盘已停用"));
            store.Flush();                                        // 第 3 次连续失败 → 停用
            store.Flush();                                        // 停用后不再尝试（不抛、不重复留标记）

            Assert.Null(store.CurrentFile);
            var all = store.Recent(100);
            // 内存环形缓冲完整：业务行一行不少
            Assert.Contains("m1", all);
            Assert.Contains("m2", all);
            Assert.Contains("m3", all);
            // 停用时缓冲里恰好留一行标记，求助导出可据此提示日志文件不完整
            Assert.Single(all, l => l.Contains("落盘已停用"));
            // 停用后追加与合帧照常工作
            store.Append("m4");
            store.Flush();
            Assert.Equal("m4", store.Recent(1)[0]);
        }
        finally { try { File.Delete(blocker); } catch { } }
    }

    [Fact]
    public void FileSink_TransientFailure_RecoversOnNextFlush()
    {
        // 退避重试：瞬时失败（目录暂时建不了）恢复后，下次合帧自动重开 writer 继续落盘。
        var blocker = Path.Combine(Path.GetTempPath(), "logstore-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        var logDir = Path.Combine(blocker, "logs");
        string? file = null;
        try
        {
            using var store = new LogStore(logDir, flushMs: 10_000);
            store.Append("before-fail");
            store.Flush();
            Assert.Null(store.CurrentFile);                       // 第 1 次失败：未停用（退避中）
            File.Delete(blocker);                                 // 故障恢复
            store.Append("after-recover");
            store.Flush();                                        // 自动重试成功
            file = store.CurrentFile;
            Assert.NotNull(file);
            Assert.True(File.Exists(file));
            Assert.Contains("after-recover", File.ReadAllText(file!));
            Assert.Equal(new[] { "before-fail", "after-recover" }, store.Recent(10));   // 内存始终完整
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
            try { Directory.Delete(logDir, true); } catch { }
        }
    }

    [Fact]
    public void ConcurrentFlushAndAppend_AllLinesLandInFileAndRing()
    {
        // v3.8 竞态：Timer 线程与 UI 线程 Flush 并发进 WriteFileNoThrow，StreamWriter 非线程安全，
        // 一次异常即永久停写。v3.9 写路径串行化后：多线程 Append+Flush 混合并发，行不丢不重。
        var dir = Path.Combine(Path.GetTempPath(), "logstore-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? file;
            using (var store = new LogStore(dir, maxLines: 2000, flushMs: 10))
            {
                Parallel.For(0, 4, t =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        store.Append($"t{t}-{i}");
                        store.Flush();                            // UI 线程手动 Flush 与 Timer 并发
                    }
                });
                store.Flush();
                file = store.CurrentFile;
                Assert.NotNull(file);                             // 落盘未被并发异常打停用
                Assert.Equal(400, store.Count);
                Assert.Equal(400, store.Recent(2000).Distinct().Count());
            }
            var lines = File.ReadAllLines(file!);
            Assert.Equal(400, lines.Length);                      // 每行恰好落盘一次
            Assert.Equal(400, lines.Distinct().Count());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
