namespace BBDownHelper.Core;

/// <summary>
/// v4.0（批次3 R3.6 / PM R3.5）：预览卡分P勾选行 ⇄ 选集文本（-p 写法）双向换算（纯逻辑可单测）。
/// 语义约定：
///  - 全选 → ""（留空=全部，与既有「留空=全部」口径一致，不生成 1-N 这种冗余写法）；
///  - 全不选 → ""（蓝 P2-③：文本无法区分全不选/全选，UI 开始前用 HasAnyChecked 判定并阻断）；
///  - 部分选 → 连段压缩：1,2,3,5 → "1-3,5"；
///  - 文本 → 勾选状态：非法写法（IsValidSelectPage 不认）或序号越界（&lt;1 或 &gt;pageCount）
///    返回 null——调用方不得拿 null 去覆盖勾选行（用户手写的中间态不应被勾选行打回去）。
/// </summary>
public static class PageSelectionPolicy
{
    /// <summary>P1：分P序号数字段位数上限（蓝队 B1 建议；6 位=最多 999999，远超真实分P量级）。</summary>
    public const int MaxPageDigits = 6;

    /// <summary>蓝 P2-③：勾选数组里是否至少勾了一个。全不选时文本与全选同为 ""，UI 开始前必须据此判定并阻断。</summary>
    public static bool HasAnyChecked(IReadOnlyList<bool> states)
    {
        for (int i = 0; i < states.Count; i++)
            if (states[i]) return true;
        return false;
    }

    /// <summary>勾选状态数组 → 选集文本。全选/全不选 → ""；其余压缩连段。</summary>
    public static string ToSelectPageText(IReadOnlyList<bool> states)
    {
        if (states.Count == 0) return "";
        var picked = new List<int>();
        for (int i = 0; i < states.Count; i++)
            if (states[i]) picked.Add(i + 1);
        if (picked.Count == 0 || picked.Count == states.Count) return "";

        var parts = new List<string>();
        int start = picked[0], prev = picked[0];
        for (int i = 1; i <= picked.Count; i++)
        {
            if (i < picked.Count && picked[i] == prev + 1) { prev = picked[i]; continue; }
            parts.Add(start == prev ? start.ToString() : $"{start}-{prev}");
            if (i < picked.Count) { start = picked[i]; prev = picked[i]; }
        }
        return string.Join(",", parts);
    }

    /// <summary>
    /// 选集文本 → 勾选状态数组（长度 = pageCount）。
    /// 空/ALL → 全 true；LAST → 仅最后一项 true；非法写法或序号越界 → null。
    /// </summary>
    public static bool[]? ToCheckedState(string? selectPage, int pageCount)
    {
        if (pageCount <= 0) return null;
        var states = new bool[pageCount];
        string s = (selectPage ?? "").Trim();
        if (s.Length == 0 || s.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            Array.Fill(states, true);
            return states;
        }
        if (s.Equals("LAST", StringComparison.OrdinalIgnoreCase))
        {
            states[pageCount - 1] = true;
            return states;
        }
        if (!CommandBuilder.IsValidSelectPage(s)) return null;

        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (InputRecognizer.DigitsRegex().IsMatch(part))
            {
                // P1 修复：int.Parse 遇超长数字抛 OverflowException 崩溃；改 long 安全解析 + 位数上限（>6 位非正常分P量级，直接非法）。
                if (part.Length > MaxPageDigits || !long.TryParse(part, out long v) || v < 1 || v > pageCount) return null;
                states[(int)v - 1] = true;
                continue;
            }
            var seg = part.Split('-', StringSplitOptions.TrimEntries);
            // P1 修复：区间两端同改 long 安全解析 + 位数上限。
            if (seg[0].Length > MaxPageDigits || seg[1].Length > MaxPageDigits
                || !long.TryParse(seg[0], out long a) || !long.TryParse(seg[1], out long b)
                || a < 1 || b > pageCount || a > b) return null;
            for (long i = a; i <= b; i++) states[(int)i - 1] = true;
        }
        return states;
    }
}
