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
            // v3.7（R1.5）：半成品短链（还在输入/粘贴中途）不发联网请求，只走本地提示；
            // 用户补全后防抖重识别会自动走正常解析。粘贴完整分享文案场景 LooksComplete=true，行为不变。
            if (!InputRecognizer.LooksComplete(rawText))
            {
                r.Kind = InputKind.Unknown; r.KindName = "无法识别";
                r.Message = "短链接看起来还没粘贴完整，完整粘贴后会自动识别。";
                return r;
            }
            string? finalUrl = await _probe.ResolveShortLinkAsync(token);
            if (finalUrl is null)
            {
                // v3.7（R1.6）：断网归因单列——网络失败 / 风控冷却 / 其他，文案可区分
                r.Kind = InputKind.Unknown; r.KindName = "无法识别";
                r.Message = _probe.LastNetworkFailed
                    ? "短链接解析失败：请检查网络连接后重试。也可以在浏览器里打开这个短链接，再复制地址栏里的完整链接粘贴过来。"
                    : _probe.InRiskCooldown
                        ? "短链接解析失败：触发了 B 站访问限制，请稍等约 1 分钟后再试。也可以在浏览器里打开这个短链接，再复制地址栏里的完整链接粘贴过来。"
                        : "短链接解析失败（可能是网络问题）。请在浏览器里打开这个短链接，再复制地址栏里的完整链接粘贴过来。";
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
                // v3.7（R1.6）：断网归因单列——网络失败 / 风控冷却 / 确实没认出，文案可区分
                r.Message = _probe.LastNetworkFailed
                    ? $"联网探测数字 {token} 失败：请检查网络连接后重试。也可以直接粘贴完整链接或 BV 号。"
                    : _probe.InRiskCooldown
                        ? $"联网探测数字 {token} 失败：触发了 B 站访问限制，请稍等约 1 分钟后再试。也可以直接粘贴完整链接或 BV 号。"
                        : $"没认出数字 {token} 是什么。请粘贴完整链接或 BV 号。";
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
            // sid 缺失/非法 = 没发起联网，不算「取标题失败」，不置 ListInfoFailed
            if (sid is null || !InputRecognizer.DigitsRegex().IsMatch(sid)) return;
            int type = r.Kind == InputKind.Series ? 5 : 8;
            var c = await _probe.ProbeMediaListAsync(long.Parse(sid), type, cookie);
            // v3.7（R1.7）：确实发起了联网补全但没取到 → 置标记，UI 显示
            // 「没取到合集标题（网络原因），不影响下载」灰字，不改变可用性。
            if (c is null) { r.ListInfoFailed = true; return; }
            r.Title = c.Title;
            r.MediaCount = c.MediaCount;
        }
        catch { r.ListInfoFailed = true; /* 探测失败不影响识别结果，仅置标记供 UI 补一句说明 */ }
    }
}
