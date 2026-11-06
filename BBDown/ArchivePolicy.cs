namespace BBDown
{
    /// <summary>
    /// 修复（红队#3）的判定核心，抽成纯函数以便脱离真实下载/网络做单元测试。
    ///
    /// 缺陷原状：DownloadPagesAsync 调用 DownloadPageAsync 后**无条件** SaveAidToFile(p.aid)，
    /// 而 DownloadPageAsync 在「合并失败」「解析此分P失败」时只打红字就 return（不抛异常），
    /// 因此失败的分P照样进了 BBDown.archives；下次同一链接勾选「记录已下载」会命中
    /// CheckAidFromFile 直接跳过 —— 用户再也拿不到这个视频，换清晰度也不下载。这是不可逆的数据损失。
    ///
    /// 现在：按分P结果决定是否归档；只要有失败分P就置非零退出码，让助手/脚本/bat 能识别。
    /// 注意成功路径退出码保持 0 —— 不能让既有自动化脚本因为「本就能成功」而误判失败。
    /// </summary>
    internal static class ArchivePolicy
    {
        /// <summary>该分P是否应写入已下载归档。只有真正成功（或用户要求静默跳过）才算。</summary>
        public static bool ShouldArchive(bool pageOk) => pageOk;

        /// <summary>本次运行的退出码：有任一失败分P则 1，否则 0（成功路径不得改动既有行为）。</summary>
        public static int ExitCode(int failedPages) => failedPages > 0 ? 1 : 0;

        /// <summary>
        /// 合并两条退出码来源：`Main` 的返回值来自 `parser.InvokeAsync`（处理器成功恒为 0），
        /// 会覆盖处理器内设置的 `Environment.ExitCode`。若直接 `return await parser.InvokeAsync(...)`，
        /// 则「有分P失败置 1」形同虚设——进程仍以 0 退出，助手会判成功。
        /// 规则：任一方非 0 即失败，且优先保留 InvokeAsync 的非 0（参数错误等更早的失败信号）。
        /// </summary>
        public static int CombineExitCode(int invokeResult, int envExitCode)
            => invokeResult != 0 ? invokeResult : envExitCode;

        /// <summary>
        /// 清理阶段抛异常时，是否应把该分P判为「下载失败」。
        ///
        /// 第三轮自查发现的原缺陷：成品校验已通过（文件存在且非空）之后，清理临时文件
        /// 仍写在同一个 try 里。若某临时文件被占用导致 File.Delete 抛异常，
        /// 外层 catch 会误判成下载失败 → 无谓重试重下 → 3 次耗尽 throw → 整批中断，
        /// 且已成功的分P被记入归档 → 下次永久跳过。
        /// 结论：成品已生成即算成功，清理失败只记警告，永不改变成功判定。
        /// </summary>
        public static bool CleanupFailureCountsAsDownloadFailure(bool outputValid) => false;

        /// <summary>是否应打印「任务完成」——有失败时必须改打失败汇总，不能报完成。</summary>
        public static bool ShouldReportTaskDone(int failedPages) => failedPages == 0;
    }
}
