namespace BBDownHelper.Core;

/// <summary>识别门面：净化 → 分类 →（需要时）探测。UI 只调这一个入口。</summary>
public sealed class RecognitionService
{
    private readonly BiliApiProbe _probe;
    public RecognitionService(BiliApiProbe? probe = null) => _probe = probe ?? new BiliApiProbe();

    /// <summary>完整识别流程。cookie 可为 null（有 BBDown.data 时上层传入更稳）。</summary>
    public async Task<RecognitionResult> RecognizeAsync(string rawText, string? cookie)
    {
        string? token = InputRecognizer.ExtractToken(rawText);
        if (token is null)
            return new RecognitionResult
            {
                Kind = InputKind.Unknown, KindName = "无法识别",
                Message = "没认出这段内容。请粘贴 B 站链接、BV 号或 av 号（支持直接粘贴整段分享文字）。"
            };

        var r = InputRecognizer.Classify(token);

        // b23.tv：跟 302 解析后重新分类
        if (r.Kind == InputKind.ShortLink)
        {
            string? finalUrl = await _probe.ResolveShortLinkAsync(token);
            if (finalUrl is null)
            {
                r.Message = "短链接解析失败（可能是网络问题）。请在浏览器里打开这个短链接，再复制地址栏里的完整链接粘贴过来。";
                return r;
            }
            r = InputRecognizer.Classify(finalUrl);
            if (r.Kind == InputKind.Unknown)
                r.Message = "短链接解析出来了，但目标地址不是能识别的 B 站内容：" + finalUrl;
        }

        // 裸数字：三路探测决策树
        if (r.Kind == InputKind.BareNumber)
        {
            var candidates = await _probe.ProbeBareNumberAsync(token, cookie);
            if (candidates.Count == 0)
            {
                r.Kind = InputKind.Unknown; r.KindName = "无法识别";
                r.Message = $"没认出数字 {token} 是什么。请粘贴完整链接或 BV 号。";
                return r;
            }
            if (candidates.Count == 1)
            {
                var c = candidates[0];
                return FromCandidate(c,
                    $"已识别为：{c.Describe()}。如果不对，请粘贴完整链接重新识别。");
            }
            // 多候选：禁止静默选第一个，必须让用户单选
            r.NeedsUserChoice = true;
            r.Candidates = candidates;
            r.KindName = "纯数字（有多个可能）";
            r.Message = $"数字 {token} 对应到多个内容，请在下方选择一个：";
            return r;
        }

        // 合集/系列：尽力探测标题与视频数（失败不影响使用）
        if (r.Kind is InputKind.Collection or InputKind.Series)
            await TryFillListInfoAsync(r, cookie);

        return r;
    }

    /// <summary>用户从候选列表里选定后，由候选生成最终识别结果。</summary>
    public static RecognitionResult FromCandidate(NumberCandidate c) => FromCandidate(c, "");

    private static RecognitionResult FromCandidate(NumberCandidate c, string extraMsg)
    {
        var r = new RecognitionResult
        {
            Kind = c.Kind,
            KindName = c.Kind switch
            {
                InputKind.Series => "系列",
                InputKind.Collection => "合集",
                InputKind.SingleVideo => "单个视频",
                _ => ""
            },
            FinalInput = c.FinalInput,
            Title = c.Title,
            MediaCount = c.MediaCount > 0 ? c.MediaCount : null,
            Message = extraMsg
        };
        return r;
    }

    private async Task TryFillListInfoAsync(RecognitionResult r, string? cookie)
    {
        try
        {
            string? sid = InputRecognizer.GetQueryValue(r.FinalInput, "sid")
                          ?? InputRecognizer.GetQueryValue(r.FinalInput, "business_id");
            if (sid is null || !InputRecognizer.DigitsRegex().IsMatch(sid)) return;
            int type = r.Kind == InputKind.Series ? 5 : 8;
            var c = await _probe.ProbeMediaListAsync(long.Parse(sid), type, cookie);
            if (c is null) return;
            r.Title = c.Title;
            r.MediaCount = c.MediaCount;
        }
        catch { /* 探测失败不影响识别结果 */ }
    }
}
