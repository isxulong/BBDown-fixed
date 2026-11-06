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

    /// <summary>
    /// v3.7（R1.7）：合集/系列标题联网补全失败（网络原因）时置 true，
    /// UI 显示灰字「没取到合集标题（网络原因），不影响下载」，不改变可用性。
    /// </summary>
    public bool ListInfoFailed { get; set; }

    public List<NumberCandidate> Candidates { get; set; } = new();

    /// <summary>提示/警告信息（展示给用户）</summary>
    public string Message { get; set; } = "";

    /// <summary>是否属于列表类（合集/系列/收藏夹/UP主页），用于显示列表下载辅助选项</summary>
    public bool IsListKind => Kind is InputKind.Collection or InputKind.Series
        or InputKind.Favorites or InputKind.UpSpace;
}

/// <summary>
/// v3.4 预览卡数据：识别完成后自动联网取的标题+封面。
/// 字段口径（2026-11-04 实测 api.bilibili.com）：
///  - view?bvid=/aid=：data.pic（封面，注意返回 http:// 前缀）、data.pages（数组长度=分P数）、
///    data.title、data.owner.name；不存在的视频 code=-400（请求错误）/-403（权限不足）≠ 0；
///  - medialist/info：data.title、data.media_count、data.cover（可能为空串）、data.upper.name。
/// </summary>
public sealed class VideoPreview
{
    public InputKind Kind { get; init; }         // SingleVideo / Collection / Series

    /// <summary>标题（空=接口没给，UI 显示占位）</summary>
    public string Title { get; init; } = "";

    /// <summary>UP 主名（空=不显示该行）</summary>
    public string UpName { get; init; } = "";

    /// <summary>封面 URL，已规范成 https 直链；空=无封面（UI 显示占位色块）</summary>
    public string CoverUrl { get; init; } = "";

    /// <summary>单视频分 P 数（0=接口没给；1=不显示分 P 行）</summary>
    public int PageCount { get; init; }

    /// <summary>合集/系列视频数（单视频为 0）</summary>
    public int MediaCount { get; init; }

    /// <summary>true=接口明确 code != 0（视频不存在/已删除），UI 显示一句温和提示</summary>
    public bool NotFound { get; init; }

    /// <summary>
    /// v4.0（R3.6/R3.7-9）：分P/分集标题列表（单视频 pages[].part、番剧 episodes、课程 episodes）。
    /// 供预览卡勾选行展示；合集/系列/收藏夹为空数组。数量可能与 PageCount 不一致时以本列表为准出勾选行。
    /// </summary>
    public IReadOnlyList<string> PageTitles { get; init; } = Array.Empty<string>();
}
