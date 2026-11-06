using System.Net;

namespace BBDownHelper.Core;

/// <summary>
/// 单次请求策略：超时与是否允许自动跳转。
/// 默认档（Default）= 统一超时 15s + 允许跳转；NoRedirect 供 b23 短链手动跟随 302 用。
/// </summary>
public readonly record struct NetPolicy(TimeSpan Timeout, bool AllowRedirect)
{
    /// <summary>默认策略：统一超时 + 允许自动跳转。</summary>
    public static NetPolicy Default => new(NetFacade.DefaultTimeout, true);

    /// <summary>手动跟随重定向（b23.tv 短链解析用）。</summary>
    public static NetPolicy NoRedirect => new(NetFacade.DefaultTimeout, false);
}

/// <summary>
/// v3.7 NetFacade 薄层（架构方案 §1，提前并入批次 1）：
/// 共享 HttpClient + 统一 UA/超时 + 412 全局冷却。
///  - 任一组件任一请求收到 HTTP 412 → Report412 置 60 秒全局风控冷却；
///  - 冷却期内各组件自行降级（识别探测跳过全头首试直接走降级档、封面/检查更新静默不发请求）；
///  - UA 与默认超时集中在 NetFacade.UserAgent / NetFacade.DefaultTimeout，全组件唯一来源。
/// 组件经构造函数注入：生产一律注入 NetFacade.Shared（共享冷却）；测试用独立实例
/// + SendOverride / NowUtc 隔离，互不影响。
/// 本类只做门面，不含任何业务逻辑；各组件内部逻辑零改动。
/// </summary>
public sealed class NetFacade
{
    /// <summary>进程级共享实例（412 冷却跨组件生效的前提）。</summary>
    public static NetFacade Shared { get; } = new();

    /// <summary>
    /// 全组件统一匿名 UA（Chrome 桌面端口径）。
    /// 原 BiliApiProbe.UserAgent 保留为指向本常量的别名，供既有引用与生成器口径注释兼容。
    /// </summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>统一默认超时（单次请求）。UpdateChecker 的 5s 是 v3.5 规格明文例外，经 NetPolicy 显式传入。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>412 全局冷却时长（任一组件收 412 → 全体降级 60s）。</summary>
    public static readonly TimeSpan RiskCooldownDuration = TimeSpan.FromSeconds(60);

    // 共享 HttpClient：Timeout 置 Infinite，真实超时由每次请求的链接 CTS 按 NetPolicy 控制
    // （共享实例无法按请求改 Timeout）。
    private readonly HttpClient _client = new(
        new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false })
    { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private readonly HttpClient _noRedirectClient = new(
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>
    /// 底层 HTTP 发送可注入（测试 412/网络异常/降级断言用）。参数：请求、是否允许跳转、取消令牌。
    /// 生产为 null 走共享 HttpClient。
    /// </summary>
    internal Func<HttpRequestMessage, bool, CancellationToken, Task<HttpResponseMessage>>? SendOverride { get; set; }

    /// <summary>当前时刻（UTC）可注入，冷却窗口测试用。</summary>
    internal Func<DateTimeOffset> NowUtc { get; set; } = () => DateTimeOffset.UtcNow;

    private long _cooldownUntilUtcTicks;

    /// <summary>当前是否处于 412 风控冷却期（任一组件收 412 后 60s 内为 true）。</summary>
    public bool InRiskCooldown => NowUtc().UtcTicks < Interlocked.Read(ref _cooldownUntilUtcTicks);

    /// <summary>收到 HTTP 412 时调用：置/续 60s 全局冷却。线程安全。</summary>
    public void Report412() =>
        Interlocked.Exchange(ref _cooldownUntilUtcTicks, NowUtc().Add(RiskCooldownDuration).UtcTicks);

    /// <summary>
    /// 统一发送入口。
    /// 返回 null 的语义：请求被 ct 取消（调用方按既有「取消静默」契约处理）。
    /// 其他行为：412 响应自动触发全局冷却；超时经链接 CTS 按 policy.Timeout 控制，
    /// 超时抛出的 OperationCanceledException（ct 未取消时）原样上抛，由调用方归入网络失败；
    /// 其余网络异常原样上抛（调用方各自有兜底 catch）。
    /// 注意：本方法不做冷却拦截——冷却期降级决策在各组件（跳过全头首试 / 不发请求）。
    /// </summary>
    public async Task<HttpResponseMessage?> SendAsync(
        HttpRequestMessage req, NetPolicy policy, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(policy.Timeout);
        var resp = SendOverride is not null
            ? await SendOverride(req, policy.AllowRedirect, linked.Token)
            : await (policy.AllowRedirect ? _client : _noRedirectClient).SendAsync(req, linked.Token);
        if ((int)resp.StatusCode == 412) Report412();
        return resp;
    }
}
