namespace BBDownHelper.Core;

/// <summary>
/// v3.4 预览卡取数门面：识别完成后自动联网取标题+封面。
/// v4.0（批次3 R3.7-9）：类型扩展——番剧/课程/收藏夹也出预览卡（标题+封面+列表信息）。
/// 设计红线（纯增强，绝不影响识别→解析→下载主流程）：
///  - 网络失败/超时/取消/JSON 损坏：返回 null（由 UI 走降级提示，不再是静默空白，R3.10）；
///  - 接口明确 code != 0（内容不存在/已删除/无权限）返回 NotFound 温和提示；
///  - UP 主主页/裸数字多候选未选定：不出预览卡（沿用现有引导文案）；
///  - 本类任何异常一律吞掉返回 null，不得抛出。
/// </summary>
public sealed class PreviewService
{
    private readonly BiliApiProbe _probe;

    public PreviewService(BiliApiProbe? probe = null) => _probe = probe ?? new BiliApiProbe();

    /// <summary>
    /// v3.7（R1.6）：最近一次预览探测是否因网络原因失败（用于 UI 断网归因提示）。
    /// 透传底层 BiliApiProbe.LastNetworkFailed。
    /// </summary>
    public bool LastNetworkFailed => _probe.LastNetworkFailed;

    /// <summary>v4.0（R3.10）：当前是否处于 412 风控冷却期（降级文案归因用，转发门面状态）。</summary>
    public bool InRiskCooldown => _probe.InRiskCooldown;

    /// <summary>
    /// v4.0（R3.10）：该识别结果是否支持出预览卡。
    /// 支持：单视频 / 合集 / 系列 / 番剧 / 课程 / 收藏夹；
    /// 不支持：Unknown / UP 主主页 / 短链残留 / 裸数字多候选未选定。
    /// UI 用此判定要不要亮骨架卡与降级提示——不支持的类型保持完全静默（连骨架都不出）。
    /// </summary>
    public static bool SupportsPreview(RecognitionResult r)
    {
        if (r is null || r.NeedsUserChoice) return false;
        return r.Kind is InputKind.SingleVideo or InputKind.Collection or InputKind.Series
            or InputKind.Bangumi or InputKind.Cheese or InputKind.Favorites;
    }

    /// <summary>
    /// 从识别结果取预览数据。返回 null = 取不到（UI 走降级提示）；NotFound=true = 显示温和提示。
    /// </summary>
    public async Task<VideoPreview?> GetPreviewAsync(RecognitionResult r, string? cookie, CancellationToken ct)
    {
        if (r is null || ct.IsCancellationRequested) return null;
        // 多候选未选定（BareNumber）：用户选定前不出预览卡
        if (r.NeedsUserChoice) return null;
        try
        {
            switch (r.Kind)
            {
                case InputKind.SingleVideo:
                    return await _probe.ProbeViewPreviewAsync(r.FinalInput, cookie, ct);

                case InputKind.Collection:
                case InputKind.Series:
                {
                    string? sid = InputRecognizer.GetQueryValue(r.FinalInput, "sid")
                                  ?? InputRecognizer.GetQueryValue(r.FinalInput, "business_id");
                    if (sid is null || !InputRecognizer.DigitsRegex().IsMatch(sid)
                        || !long.TryParse(sid, out long id) || id <= 0)
                        return null;
                    int type = r.Kind == InputKind.Series ? 5 : 8;
                    return await _probe.ProbeMediaListPreviewAsync(id, type, cookie, ct);
                }

                case InputKind.Bangumi:   // v4.0（R3.7）：番剧/影视
                    return await _probe.ProbeBangumiPreviewAsync(r.FinalInput, cookie, ct);

                case InputKind.Cheese:    // v4.0（R3.8）：课程
                    return await _probe.ProbeCheesePreviewAsync(r.FinalInput, cookie, ct);

                case InputKind.Favorites: // v4.0（R3.9）：收藏夹
                    return await _probe.ProbeFavoritesPreviewAsync(r.FinalInput, cookie, ct);

                default:
                    // Unknown / 短链残留 / UP 主主页：不出预览卡
                    return null;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }
}
