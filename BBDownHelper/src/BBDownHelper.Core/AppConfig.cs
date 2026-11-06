using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBDownHelper.Core;

/// <summary>
/// v3.8 配置持久化地基（架构方案 §1 AppConfig）：载体 core\.config.json，
/// 沿用 .update-check.json 的静默读写模式——读失败/文件损坏一律回退默认值，写失败静默，绝不抛。
/// v3.8 接线字段：VerboseLog（R2.7 详细日志开关）、DontRemindLogin4K（R2.4「本次不再提醒」）。
/// v4.0（批次3 PM R3.10 冲突裁决4）：保存目录 + 全部下载选项持久化，重启后恢复上次设置。
/// 枚举类字段（Quality/Encoding/Danmaku）读取时做白名单校验：非法值静默回退默认，
/// 防止手改 config 或跨版本字段漂移导致命令组装出界。
/// 例外：InfoOnly（-info 只解析）不持久化——它是会话级排障/查看模式，重启后默认恢复实际下载，
/// 避免「重启后不知为何只解析不下载」类求助。
/// </summary>
public sealed class AppConfig
{
    /// <summary>R2.7：日志区默认简洁模式；用户打开「显示详细输出」后记 true，重启保持。</summary>
    public bool VerboseLog { get; set; }

    /// <summary>R2.4：未登录选高画质的拦截确认，勾了「本次不再提醒」后记 true（本进程与重启后都不再弹）。</summary>
    public bool DontRemindLogin4K { get; set; }

    // ---------- v4.0（R3.11 / PM R3.10）：下载目录记忆 + 全部选项持久化 ----------

    /// <summary>下载保存目录；null=未设置过（沿用程序所在目录）。空串在读取时归一为 null。</summary>
    public string? SaveDir { get; set; }

    /// <summary>清晰度：auto / ia / q1080 / q4k / q480（与 HelperOptions.Quality 取值一致）。</summary>
    public string Quality { get; set; } = "auto";

    /// <summary>编码：auto / hevc / avc。</summary>
    public string Encoding { get; set; } = "auto";

    /// <summary>弹幕：none / dd / only。</summary>
    public string Danmaku { get; set; } = "none";

    public bool MultiThread { get; set; } = true;
    public bool AudioOnly { get; set; }
    public bool CoverOnly { get; set; }
    public bool SubOnly { get; set; }

    /// <summary>列表类辅助：每个视频之间歇 5 秒（默认勾）。</summary>
    public bool DelayPerPage { get; set; } = true;

    /// <summary>列表类辅助：记住已下载，中断后自动跳过（默认勾）。</summary>
    public bool SaveArchives { get; set; } = true;

    /// <summary>枚举白名单：读取时校验，非法值回退默认（防手改 config / 跨版本漂移）。</summary>
    public static readonly string[] QualityWhitelist = { "auto", "ia", "q1080", "q4k", "q480" };
    public static readonly string[] EncodingWhitelist = { "auto", "hevc", "avc" };
    public static readonly string[] DanmakuWhitelist = { "none", "dd", "only" };

    private static string Pick(string? v, string[] whitelist, string fallback) =>
        v is not null && Array.IndexOf(whitelist, v) >= 0 ? v : fallback;

    /// <summary>静默读取：文件缺失/损坏/字段非法一律回退默认值，绝不抛。</summary>
    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppConfig();
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
            if (dto is null) return new AppConfig();
            return new AppConfig
            {
                VerboseLog = dto.VerboseLog ?? false,
                DontRemindLogin4K = dto.DontRemindLogin4K ?? false,
                SaveDir = string.IsNullOrWhiteSpace(dto.SaveDir) ? null : dto.SaveDir,
                Quality = Pick(dto.Quality, QualityWhitelist, "auto"),
                Encoding = Pick(dto.Encoding, EncodingWhitelist, "auto"),
                Danmaku = Pick(dto.Danmaku, DanmakuWhitelist, "none"),
                MultiThread = dto.MultiThread ?? true,
                AudioOnly = dto.AudioOnly ?? false,
                CoverOnly = dto.CoverOnly ?? false,
                SubOnly = dto.SubOnly ?? false,
                DelayPerPage = dto.DelayPerPage ?? true,
                SaveArchives = dto.SaveArchives ?? true
            };
        }
        catch { return new AppConfig(); }
    }

    /// <summary>原子写（tmp + move 覆盖），目标目录不存在则先创建；任何失败静默，绝不抛。</summary>
    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dto
            {
                VerboseLog = VerboseLog,
                DontRemindLogin4K = DontRemindLogin4K,
                SaveDir = SaveDir,
                Quality = Quality,
                Encoding = Encoding,
                Danmaku = Danmaku,
                MultiThread = MultiThread,
                AudioOnly = AudioOnly,
                CoverOnly = CoverOnly,
                SubOnly = SubOnly,
                DelayPerPage = DelayPerPage,
                SaveArchives = SaveArchives
            }));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* 配置写失败静默：下次启动用默认值，不影响主流程 */ }
    }

    /// <summary>序列化载体（字段名即 JSON 键；缺字段反序列化为 null → 回退默认）。</summary>
    private sealed class Dto
    {
        [JsonPropertyName("verboseLog")] public bool? VerboseLog { get; set; }
        [JsonPropertyName("dontRemindLogin4K")] public bool? DontRemindLogin4K { get; set; }
        [JsonPropertyName("saveDir")] public string? SaveDir { get; set; }
        [JsonPropertyName("quality")] public string? Quality { get; set; }
        [JsonPropertyName("encoding")] public string? Encoding { get; set; }
        [JsonPropertyName("danmaku")] public string? Danmaku { get; set; }
        [JsonPropertyName("multiThread")] public bool? MultiThread { get; set; }
        [JsonPropertyName("audioOnly")] public bool? AudioOnly { get; set; }
        [JsonPropertyName("coverOnly")] public bool? CoverOnly { get; set; }
        [JsonPropertyName("subOnly")] public bool? SubOnly { get; set; }
        [JsonPropertyName("delayPerPage")] public bool? DelayPerPage { get; set; }
        [JsonPropertyName("saveArchives")] public bool? SaveArchives { get; set; }
    }
}
