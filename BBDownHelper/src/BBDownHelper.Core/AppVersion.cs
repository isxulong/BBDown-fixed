namespace BBDownHelper.Core;

/// <summary>
/// v3.5：发布版本号单一来源。窗口标题、界面角落版本小字、检查更新的「当前版本」
/// 全部从这里取——禁止在界面或代码里再散落第二处版本字面量。
/// 内部程序集版本（exe 文件属性）在 BBDownHelper.csproj 的 &lt;Version&gt;（v3.6 起=3.6.0）。
/// </summary>
public static class AppVersion
{
    /// <summary>对外发布版本号（v1.0.x 套件版本），不带 v 前缀。</summary>
    public const string Release = "1.0.5";

    /// <summary>带 v 前缀的发布版本号，用于界面展示与更新比较。</summary>
    public const string ReleaseTag = "v" + Release;

    /// <summary>窗口标题（MainWindow 构造时设置）。</summary>
    public const string WindowTitle = "BBDown-fixed 下载助手 " + ReleaseTag;

    /// <summary>界面右下角常驻版本小字。</summary>
    public const string CornerLabel = ReleaseTag;
}
