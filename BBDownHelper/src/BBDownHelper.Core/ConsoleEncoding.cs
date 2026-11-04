using System;
using System.Globalization;
using System.Text;

namespace BBDownHelper.Core;

/// <summary>
/// 子进程控制台输出解码：BBDown 这类 .NET 控制台程序按系统 ANSI 代码页写 stdout
/// （中文 Windows = GBK/936）。GUI 侧若按 UTF-8 解码，GBK 字节流会全部变成 U+FFFD（）。
/// 这里统一解析「该用什么编码读子进程输出」：注册代码页提供程序后取系统 ANSI 代码页，
/// 取不到或不受支持时回落 GB18030（GBK 超集，其它语言系统不退化）。
/// </summary>
public static class ConsoleEncoding
{
    private static readonly Lazy<Encoding> CurrentLazy = new(ResolveCurrent);

    /// <summary>当前系统应使用的子进程输出编码（懒解析、线程安全）。</summary>
    public static Encoding Current => CurrentLazy.Value;

    /// <summary>
    /// 按 ANSI 代码页号解析编码；代码页非法（&lt;=0）或不支持时回落 GB18030。
    /// 纯函数，可注入代码页号直接单测。
    /// </summary>
    public static Encoding Resolve(int ansiCodePage)
    {
        RegisterProvider();
        if (ansiCodePage > 0)
        {
            try { return Encoding.GetEncoding(ansiCodePage); }
            catch (ArgumentException) { /* 未知代码页 → 回落 */ }
            catch (NotSupportedException) { /* 同上 */ }
        }
        return Encoding.GetEncoding("GB18030");
    }

    /// <summary>
    /// 注册 CodePagesEncodingProvider（.NET Core/.NET 5+ 默认不带 GBK 等非 Unicode 代码页）。幂等。
    /// </summary>
    public static void RegisterProvider() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding ResolveCurrent()
    {
        int acp = 0;
        try { acp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage; }
        catch { /* 取不到按 0 处理 → 回落 GB18030 */ }
        return Resolve(acp);
    }
}
