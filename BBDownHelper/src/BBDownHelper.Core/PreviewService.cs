namespace BBDownHelper.Core;

/// <summary>
/// v3.4 预览卡取数门面：识别完成后对「单视频 / 合集 / 系列」自动联网取标题+封面。
/// 设计红线（纯增强，绝不影响识别→解析→下载主流程）：
///  - 网络失败/超时/取消/JSON 损坏：返回 null 静默不显示卡片；
///  - 只有 view 接口明确 code != 0（视频不存在/已删除）才返回 NotFound 温和提示；
///  - UP 主主页/收藏夹/番剧/课程/裸数字多候选未选定：不出预览卡（沿用现有引导文案）；
///  - 本类任何异常一律吞掉返回 null，不得抛出。
/// </summary>
public sealed class PreviewService
{
    private readonly BiliApiProbe _probe;

    public PreviewService(BiliApiProbe? probe = null) => _probe = probe ?? new BiliApiProbe();

    /// <summary>
    /// 从识别结果取预览数据。返回 null = 静默不显示；NotFound=true = 显示温和提示。
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

                default:
                    // Unknown / 短链残留 / 番剧 / 课程 / 收藏夹 / UP 主主页：不出预览卡
                    return null;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }
}
