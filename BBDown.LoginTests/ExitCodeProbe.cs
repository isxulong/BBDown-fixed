// 探针：验证 System.CommandLine 的 InvokeAsync 返回值是否会「吞掉」处理器内设置的 Environment.ExitCode。
// 背景：Program.Main 的最后一行是 `return await parser.InvokeAsync(...)`，
// 而 Task<int> Main 的返回值会成为进程退出码，覆盖此前设置的 Environment.ExitCode。
// 若成立，则 DownloadPagesAsync 里“有分P失败置 1”等于没写。
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;

namespace BBDown.LoginTests
{
    internal static class ExitCodeProbe
    {
        /// <summary>返回 (InvokeAsync 返回值, 处理器内设置的 Environment.ExitCode, 二者合并后应有的进程码)。</summary>
        public static async Task<(int invokeResult, int envExitCode, int shouldBe)> RunAsync()
        {
            var cmd = new RootCommand();
            var opt = new Option<string>("--x") { };
            cmd.AddOption(opt);
            cmd.SetHandler(() => { Environment.ExitCode = 1; });   // 处理器显式置 1
            var parser = new CommandLineBuilder(cmd).UseDefaults().Build();

            Environment.ExitCode = 0;           // 干净起点
            int invoke = await parser.InvokeAsync(new[] { "--x", "v" });
            int env = Environment.ExitCode;
            return (invoke, env, 1);
        }

        /// <summary>
        /// 端到端：模拟修好后的 Main —— 先跑 InvokeAsync，再用 CombineExitCode 合并，
        /// 返回合并结果。断言这个返回值确实为 1（证明失败码能被传到进程外）。
        /// </summary>
        public static async Task<int> RunFixedMainAsync()
        {
            var cmd = new RootCommand();
            cmd.AddOption(new Option<string>("--x") { });
            cmd.SetHandler(() => { Environment.ExitCode = 1; });   // 处理器标记失败
            var parser = new CommandLineBuilder(cmd).UseDefaults().Build();

            Environment.ExitCode = 0;
            int invoke = await parser.InvokeAsync(new[] { "--x", "v" });
            // 修好后的 Main 就返回这一行 —— 模拟真实进程退出码
            return BBDown.ArchivePolicy.CombineExitCode(invoke, Environment.ExitCode);
        }

        /// <summary>对照组：旧写法 `return await parser.InvokeAsync(...)`，用来证明缺陷确实存在。</summary>
        public static async Task<int> RunOldMainAsync()
        {
            var cmd = new RootCommand();
            cmd.AddOption(new Option<string>("--x") { });
            cmd.SetHandler(() => { Environment.ExitCode = 1; });
            var parser = new CommandLineBuilder(cmd).UseDefaults().Build();

            Environment.ExitCode = 0;
            return await parser.InvokeAsync(new[] { "--x", "v" });
        }
    }
}
