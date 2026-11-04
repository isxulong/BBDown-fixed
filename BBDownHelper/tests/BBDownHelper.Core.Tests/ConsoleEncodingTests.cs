using System.Text;
using Xunit;
using BBDownHelper.Core;

namespace BBDownHelper.Core.Tests;

/// <summary>
/// v3.2：子进程输出解码。BBDown 在中文 Windows 上按 GBK/936 写 stdout，
/// GUI 侧硬按 UTF-8 解码会把中文全变成 U+FFFD——必须按系统 ANSI 代码页解码。
/// </summary>
public class ConsoleEncodingTests
{
    private static readonly Encoding Gbk = ConsoleEncoding.Resolve(936);

    [Fact]
    public void Resolve_Gbk936_GbkBytesDecodeToChinese()
    {
        // 模拟 BBDown 实机输出：登录状态 / 视频标题 / 合并提示（GBK 字节流）
        string[] lines =
        {
            "检测登录状态…",
            "正在下载：【崩坏：星穹铁道】2.7 版本前瞻特别节目",
            "视频合并中，请稍候",
            "下载完成！"
        };
        foreach (var expected in lines)
        {
            byte[] gbkBytes = Gbk.GetBytes(expected);
            // 断言这确实不是合法 UTF-8（否则本用例失去意义：UTF-8 解码也能过）
            Assert.Throws<System.Text.DecoderFallbackException>(() =>
                new UTF8Encoding(false, true).GetString(gbkBytes));
            Assert.Equal(expected, ConsoleEncoding.Resolve(936).GetString(gbkBytes));
        }
    }

    [Fact]
    public void Resolve_Gbk936_AsciiBannerUnchanged()
    {
        // BBDown banner 是纯 ASCII：GBK 解码与 UTF-8 解码结果一致，不应退化
        const string banner = "BBDown version 1.6.3";
        Assert.Equal(banner, ConsoleEncoding.Resolve(936).GetString(Encoding.ASCII.GetBytes(banner)));
    }

    [Fact]
    public void Resolve_InvalidCodePage_FallsBackToGb18030()
    {
        foreach (int bad in new[] { 0, -1, int.MinValue, 999999 })
        {
            var enc = ConsoleEncoding.Resolve(bad);
            Assert.Equal("gb18030", enc.WebName);
        }
    }

    [Fact]
    public void Resolve_Gb18030Fallback_StillDecodesGbkBytes()
    {
        // 回落 GB18030 是 GBK 超集：GBK 字节流同样正确解码
        const string expected = "合并音视频为 MP4 格式";
        byte[] gbkBytes = Gbk.GetBytes(expected);
        Assert.Equal(expected, ConsoleEncoding.Resolve(0).GetString(gbkBytes));
    }

    [Fact]
    public void Resolve_Utf8CodePage65001_DecodesUtf8Chinese()
    {
        // 代码页为 65001（UTF-8）的系统：按 UTF-8 解码，不得误用 GBK
        const string expected = "检测登录状态…";
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(expected);
        Assert.Equal(expected, ConsoleEncoding.Resolve(65001).GetString(utf8Bytes));
    }

    [Fact]
    public void Current_ResolvesAndAsciiRoundTrips()
    {
        // 系统当前解析不抛异常、ASCII 往返无损；
        // 中文往返由 Resolve(936)/Resolve(65001) 用例分别覆盖——
        // 西文系统（如本测试机的 1252/20127）本就无法编码中文，不做超系统能力断言
        var enc = ConsoleEncoding.Current;
        Assert.NotNull(enc);
        const string ascii = "BBDown version 1.6.3";
        Assert.Equal(ascii, enc.GetString(enc.GetBytes(ascii)));
        // 幂等：两次取值是同一个实例
        Assert.Same(enc, ConsoleEncoding.Current);
    }

    [Fact]
    public void RegisterProvider_IsIdempotent()
    {
        ConsoleEncoding.RegisterProvider();
        ConsoleEncoding.RegisterProvider();   // 重复注册不抛异常
        Assert.Equal("检测登录状态", ConsoleEncoding.Resolve(936).GetString(Gbk.GetBytes("检测登录状态")));
    }
}
