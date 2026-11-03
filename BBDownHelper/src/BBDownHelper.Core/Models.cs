namespace BBDownHelper.Core;

/// <summary>识别出的输入类型（对应共享规格第二节分类表）</summary>
public enum InputKind
{
    Unknown,        // 无法识别
    SingleVideo,    // 单视频（BV / av，可能多 P）
    Bangumi,        // 番剧/影视
    Cheese,         // 课程
    Collection,     // 合集
    Series,         // 系列
    Favorites,      // 收藏夹
    UpSpace,        // UP 主主页（全部投稿）
    ShortLink,      // b23.tv 短链（待解析）
    BareNumber      // 纯数字（待探测）
}

/// <summary>裸数字探测得到的一个候选</summary>
public sealed class NumberCandidate
{
    public InputKind Kind { get; init; }        // Collection / Series / SingleVideo
    public string Title { get; init; } = "";
    public string UpName { get; init; } = "";   // 取不到时为 "UP主ID: xxx"
    public int MediaCount { get; init; }        // 单视频为 0（不适用）
    public string FinalInput { get; init; } = "";  // 交给 BBDown 的链接或 av 号

    public string Describe() => Kind switch
    {
        InputKind.Series => $"系列《{Title}》 - {UpName} - {MediaCount} 个视频",
        InputKind.Collection => $"合集《{Title}》 - {UpName} - {MediaCount} 个视频",
        InputKind.SingleVideo => $"单视频《{Title}》(av号)",
        _ => Title
    };
}

/// <summary>识别结果</summary>
public sealed class RecognitionResult
{
    public InputKind Kind { get; set; } = InputKind.Unknown;

    /// <summary>最终交给 BBDown 的输入：BV 号 / av 号 / 白名单重建后的 URL</summary>
    public string FinalInput { get; set; } = "";

    /// <summary>类型中文名（展示用）</summary>
    public string KindName { get; set; } = "";

    /// <summary>标题（能探测到时）</summary>
    public string? Title { get; set; }

    /// <summary>视频数（能探测到时）</summary>
    public int? MediaCount { get; set; }

    /// <summary>从 URL 的 ?p=N 提取出的选集建议（映射为 -p N）</summary>
    public string? SelectPageSuggestion { get; set; }

    /// <summary>UP 主主页：强制二次确认 + 默认勾 -info（红队 P1-④）</summary>
    public bool RequiresConfirm { get; set; }

    /// <summary>裸数字多候选：必须弹窗让用户单选，禁止静默选第一个</summary>
    public bool NeedsUserChoice { get; set; }

    public List<NumberCandidate> Candidates { get; set; } = new();

    /// <summary>提示/警告信息（展示给用户）</summary>
    public string Message { get; set; } = "";

    /// <summary>是否属于列表类（合集/系列/收藏夹/UP主页），用于显示列表下载辅助选项</summary>
    public bool IsListKind => Kind is InputKind.Collection or InputKind.Series
        or InputKind.Favorites or InputKind.UpSpace;
}
