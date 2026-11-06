using System.ComponentModel;
using System.Text.RegularExpressions;

namespace BBDownHelper.Core;

/// <summary>三段式人话错误（高工#9/质检#6 的统一口径）：出了什么事 → 为什么 → 你现在怎么做。</summary>
public sealed record MappedError(string What, string Why, string Action)
{
    /// <summary>拼成一段完整提示（状态栏/横幅直接用）。</summary>
    public string ToText() => $"{What}：{Why}。建议：{Action}";
}

/// <summary>
/// v3.8 错误映射表（架构方案 §1 ErrorMapper，纯函数可单测）：
/// 「已知特征 → 人话 + 建议动作」。供下载失败分支、UP 导出失败、启动失败 catch、
/// 预检失败等所有错误出口共用；在册 P3「付费视频 -403 文案口径」收编进 403 分支，不重复实现。
/// </summary>
public static class ErrorMapper
{
    /// <summary>
    /// P2-2 修复：裸 Contains("403") 子串匹配把两类非登录向量误判成登录失效——
    /// aria2 流量文本「403MiB」与 EnsureSuccessStatusCode 的
    /// 「Response status code does not indicate success: 403」（CDN upos 链路过期/封禁）。
    /// 改锚定正则「HTTP 403」（显式 HTTP 状态行，如「ERROR: HTTP 403 Forbidden」），
    /// 排除流量单位与异常消息语境；其余明确文本信号保持不变。
    /// </summary>
    private static readonly Regex Http403Rx =
        new(@"HTTP(/[0-9](?:[.][0-9])?)?[ ]*403", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 本轮 P1 修复：412 与 403 同构锚定。
    /// 原判据是裸 Contains("412")，而日志尾部里 412 这个数字到处都有——
    /// aria2 分段字节数、文件大小、cid 片段、时间戳都可能撞上，
    /// 结果任何失败都被说成「被 B 站风控拦住」，用户按错误方向等待重试。
    /// </summary>
    private static readonly Regex Http412Rx =
        new(@"HTTP(/[0-9](?:[.][0-9])?)?[ ]*412", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 本轮 P1 修复：内核「组件缺失」信号。
    /// Program.Methods.cs:148/156/168 在缺 mp4box / ffmpeg / aria2c 时抛
    /// 「找不到可执行的XXX文件」；原判据里的裸 Contains("找不到") 会把它
    /// 归因到「保存位置不可用（U 盘已拔出）」，用户在一个完全正常的目录上
    /// 反复点「浏览…」永远修不好。必须在路径判据之前单列。
    /// </summary>
    private static bool IsMissingComponent(string t) =>
        t.Contains("找不到可执行的", StringComparison.Ordinal)
        || t.Contains("可执行的ffmpeg", StringComparison.OrdinalIgnoreCase)
        || t.Contains("可执行的mp4box", StringComparison.OrdinalIgnoreCase)
        || t.Contains("可执行的aria2c", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// v4.0（R3.12）：日志尾部是否指向「登录/凭据」问题（下载失败信号 → 登录态联动刷新用）。
    /// 与 MapExit 的 403/登录分支同一特征集，两处必须一致（MapExit 复用本方法）。
    ///
    /// 本轮 P1 修复：去掉裸 Contains("cookie")。
    /// 内核只要存在 BBDown.data，每次启动必打「加载本地cookie...」（Program.Methods.cs:227），
    /// 这行天然坐在日志尾部，于是**任何一次下载失败**（风控、断网、磁盘满、合并失败）
    /// 都会命中裸 cookie 子串 → 全部误报成「需要登录」。
    /// 改为只认明确语义短语；「加载本地cookie」「Cookie 已自动刷新」这类陈述句不参与判定。
    /// </summary>
    public static bool IsLoginRelated(string logTail)
    {
        string t = logTail ?? "";
        return Http403Rx.IsMatch(t)
            || t.Contains("需要登录", StringComparison.Ordinal)
            || t.Contains("未登录", StringComparison.Ordinal)
            || t.Contains("尚未登录", StringComparison.Ordinal)
            || t.Contains("cookie 失效", StringComparison.OrdinalIgnoreCase)
            || t.Contains("cookie失效", StringComparison.OrdinalIgnoreCase)
            || t.Contains("cookie 已失效", StringComparison.OrdinalIgnoreCase)
            || t.Contains("cookie已失效", StringComparison.OrdinalIgnoreCase)
            || t.Contains("凭据已失效", StringComparison.Ordinal)
            || t.Contains("凭据失效", StringComparison.Ordinal)
            || t.Contains("付费", StringComparison.Ordinal);
    }

    /// <summary>
    /// 按 BBDown 退出码 + 日志尾部特征归因。特征优先级：风控 412 → 登录/付费 403 →
    /// 组件缺失 → 磁盘/路径 → 网络超时 → 杀软拦截 → 兜底（退出码 + 通用三因）。
    /// </summary>
    public static MappedError MapExit(int exitCode, string logTail)
    {
        string t = logTail ?? "";

        if (Http412Rx.IsMatch(t))
            return new("下载被 B 站访问限制拦住了（412）",
                "短时间内请求太多，触发了 B 站风控",
                "稍等几分钟、减少操作频率后重试");

        if (IsLoginRelated(t))
            return new("这个内容需要登录或付费才能下载",
                "未登录、登录凭据已失效，或它是大会员/付费内容",
                "点「扫码登录」完成登录后重试；付费内容需购买后才能下载");

        // 组件缺失必须排在路径判据之前：内核文案「找不到可执行的ffmpeg文件」里的
        // 「找不到」原会被路径分支吃掉，把缺组件误报成「U 盘已拔出」。
        if (IsMissingComponent(t))
            return new("内核组件缺失",
                "core 文件夹里少了 ffmpeg / mp4box / aria2c 其中之一",
                "用完整压缩包重新解压（core 文件夹里应包含 ffmpeg.exe、aria2c.exe、BBDown.exe），"
                + "并检查杀毒软件是否把它们隔离了");

        if (t.Contains("磁盘", StringComparison.Ordinal)
            || t.Contains("空间不足", StringComparison.Ordinal)
            || t.Contains("No space left", StringComparison.OrdinalIgnoreCase)
            || t.Contains("disk full", StringComparison.OrdinalIgnoreCase))
            return new("保存位置的磁盘空间不足",
                "下载到一半磁盘满了",
                "清理保存文件夹所在的磁盘，或点「浏览…」换一个空间充足的文件夹");

        if (t.Contains("路径不存在", StringComparison.Ordinal)
            || t.Contains("目录不存在", StringComparison.Ordinal)
            || t.Contains("Could not find", StringComparison.OrdinalIgnoreCase))
            return new("保存位置不可用",
                "保存文件夹不存在（可能被删除，或 U 盘/移动硬盘已拔出）",
                "点「浏览…」重新选一个保存文件夹后重试");


        if (t.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || t.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || t.Contains("超时", StringComparison.Ordinal)
            || t.Contains("网络", StringComparison.Ordinal)
            || t.Contains("连接", StringComparison.Ordinal)
            || t.Contains("resolve", StringComparison.OrdinalIgnoreCase))
            return new("网络连接出了问题",
                "请求超时或连不上 B 站服务器",
                "检查网络连接后重试；公司/校园网可换手机热点试试");

        if (t.Contains("拒绝访问", StringComparison.Ordinal)
            || t.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || t.Contains("UnauthorizedAccess", StringComparison.Ordinal))
            return new("系统拒绝了文件访问",
                "core\\BBDown.exe 可能被杀毒软件拦截，或保存位置没有写入权限",
                "到杀毒软件的隔离区恢复并加白名单，或换一个普通文件夹保存");

        return new("下载没有完成",
            $"BBDown 退出代码 {exitCode}，日志里没有识别出具体原因",
            "最常见的是未登录、网络不通或被风控——先点「扫码登录」再重试；仍失败请点「导出求助文件」把日志发给维护者");
    }

    /// <summary>按启动子进程时抛出的异常归因（Process.Start 失败路径，含蓝队 P2-1 场景）。</summary>
    public static MappedError MapStartError(Exception ex)
    {
        if (ex is Win32Exception w)
        {
            switch (w.NativeErrorCode)
            {
                case 2:   // ERROR_FILE_NOT_FOUND
                    return new("没找到 core\\BBDown.exe",
                        "文件缺失或在启动瞬间被删除",
                        "请用完整压缩包重新解压（core 文件夹里应包含 BBDown.exe），不要只拷贝单个 exe");
                case 5:   // ERROR_ACCESS_DENIED
                    return new("系统拒绝启动 core\\BBDown.exe",
                        "很可能被杀毒软件拦截或隔离了",
                        "到杀毒软件的隔离区恢复 BBDown.exe 并加白名单，然后重试");
                case 112: // ERROR_DISK_FULL
                    return new("磁盘空间不足，无法启动下载",
                        "程序所在磁盘已满",
                        "清理磁盘后重试");
            }
        }
        return new("启动下载失败",
            ex.Message,
            "请重试；反复出现请点「导出求助文件」把日志发给维护者");
    }
}
