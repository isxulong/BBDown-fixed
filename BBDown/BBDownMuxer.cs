using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using static BBDown.Core.Entity.Entity;
using static BBDown.BBDownUtil;
using static BBDown.Core.Util.SubUtil;
using static BBDown.Core.Logger;
using System.IO;
using BBDown.Core;

namespace BBDown
{
    partial class BBDownMuxer
    {
        public static string FFMPEG = "ffmpeg";
        public static string MP4BOX = "mp4box";

        /// <summary>
        /// 运行外部混流程序并返回它的真实退出码。
        ///
        /// 修复（v1.0.6，真机反馈 #3/#4）：原实现 <c>int code = 0;</c> 声明后从未赋值，
        /// 直接 <c>return code;</c> —— 于是本方法**恒返回 0**，
        /// 上层「合并失败」判定只剩「成品文件不存在或长度 0」这一条。
        /// 后果有两类：
        ///  ① ffmpeg 中途失败时会留下一个非空的半成品文件，被判成功 → 报「任务完成」，
        ///     用户拿到一个播不了的视频，且该分P还会进归档、下次永久跳过；
        ///  ② 任何异常退出（参数错误、被替换成别的程序）只要没生成文件也被记成 0。
        /// 真机日志里出现过「Enter command / Parse error」之后仍打印「任务完成」，就是这个洞。
        ///
        /// 另一处修复：<c>RedirectStandardInput = true</c> 并在启动后立刻关闭标准输入。
        /// 原实现只重定向 stderr，子进程直接继承用户的控制台输入 ——
        /// 被误当成 ffmpeg 的 <c>timeout.exe</c> 会进入它自己的交互命令模式，
        /// 把用户随后敲进命令行的内容（<c>echo 退出码=%ERRORLEVEL%</c>）吃掉半截，
        /// 表现为日志里出现 <c>Parse error ... 'ho ?????=%ERRORLEVEL%'</c> 这种诡异行。
        /// 关掉标准输入后，子进程读到 EOF 立即退出，绝不碰用户的键盘输入。
        /// </summary>
        private static int RunExe(string app, string parms, bool customBin = false)
        {
            Process p = new();
            p.StartInfo.FileName = app;
            p.StartInfo.Arguments = parms;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.RedirectStandardInput = true;   // v1.0.6：子进程不得继承/抢占控制台输入
            p.StartInfo.CreateNoWindow = false;
            p.ErrorDataReceived += delegate (object sendProcess, DataReceivedEventArgs output) {
                if (!string.IsNullOrWhiteSpace(output.Data))
                    Log(output.Data);
            };
            p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            p.Start();
            try { p.StandardInput.Close(); } catch { /* 关闭失败不影响主流程 */ }
            p.BeginErrorReadLine();
            p.WaitForExit();
            int code = p.ExitCode;   // v1.0.6：修复恒返回 0
            p.Close();
            p.Dispose();
            return code;
        }

        private static string EscapeString(string str)
        {
            return string.IsNullOrEmpty(str) ? str : str.Replace("\"", "'").Replace("\\", "\\\\");
        }

        private static int MuxByMp4box(string videoPath, string audioPath, string outPath, string desc, string title, string author, string episodeId, string pic, string lang, List<Subtitle>? subs, bool audioOnly, bool videoOnly, List<ViewPoint>? points)
        {
            StringBuilder inputArg = new();
            StringBuilder metaArg = new();
            int nowId = 0;
            inputArg.Append(" -inter 500 -noprog ");
            if (!string.IsNullOrEmpty(videoPath))
            {
                inputArg.Append($" -add \"{videoPath}#trackID={(audioOnly && audioPath == "" ? "2" : "1")}:name=\" ");
                nowId++;
            }
            if (!string.IsNullOrEmpty(audioPath))
            {
                inputArg.Append($" -add \"{audioPath}:lang={(lang == "" ? "und" : lang)}\" ");
                nowId++;
            }
            if (points != null && points.Any())
            {
                var meta = GetMp4boxMetaString(points);
                var metaFile = Path.Combine(Path.GetDirectoryName(string.IsNullOrEmpty(videoPath) ? audioPath : videoPath)!, "chapters");
                File.WriteAllText(metaFile, meta);
                inputArg.Append($" -chap  \"{metaFile}\"  ");
            }
            if (!string.IsNullOrEmpty(pic))
                metaArg.Append($":cover=\"{pic}\"");
            if (!string.IsNullOrEmpty(episodeId))
                metaArg.Append($":album=\"{title}\":title=\"{episodeId}\"");
            else
                metaArg.Append($":title=\"{title}\"");
            metaArg.Append($":comment=\"{desc}\"");
            metaArg.Append($":artist=\"{author}\"");

            if (subs != null)
            {
                for (int i = 0; i < subs.Count; i++)
                {
                    if (File.Exists(subs[i].path) && File.ReadAllText(subs[i].path!) != "")
                    {
                        nowId++;
                        inputArg.Append($" -add \"{subs[i].path}#trackID=1:name=:hdlr=sbtl:lang={GetSubtitleCode(subs[i].lan).Item1}\" ");
                        inputArg.Append($" -udta {nowId}:type=name:str=\"{GetSubtitleCode(subs[i].lan).Item2}\" ");
                    }
                }
            }

            //----分析完毕
            var arguments = (Config.DEBUG_LOG ? " -verbose " : "") + inputArg.ToString() + (metaArg.ToString() == "" ? "" : " -itags tool=" + metaArg.ToString()) + $" -new -- \"{outPath}\"";
            LogDebug("mp4box命令: {0}", arguments);
            return RunExe(MP4BOX, arguments, MP4BOX != "mp4box");
        }

        public static int MuxAV(bool useMp4box, string videoPath, string audioPath, List<AudioMaterial> audioMaterial, string outPath, string desc = "", string title = "", string author = "", string episodeId = "", string pic = "", string lang = "", List<Subtitle>? subs = null, bool audioOnly = false, bool videoOnly = false, List<ViewPoint>? points = null, long pubTime = 0, bool simplyMux = false)
        {
            if (audioOnly && audioPath != "")
                videoPath = "";
            if (videoOnly)
                audioPath = "";
            desc = EscapeString(desc);
            title = EscapeString(title);
            episodeId = EscapeString(episodeId);

            if (useMp4box)
            {
                return MuxByMp4box(videoPath, audioPath, outPath, desc, title, author, episodeId, pic, lang, subs, audioOnly, videoOnly, points);
            }

            if (outPath.Contains('/') && ! Directory.Exists(Path.GetDirectoryName(outPath)))
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            //----分析并生成-i参数
            StringBuilder inputArg = new();
            StringBuilder metaArg = new();
            byte inputCount = 0;
            foreach (string path in new string[] { videoPath, audioPath })
            {
                if (!string.IsNullOrEmpty(path))
                {
                    inputCount++;
                    inputArg.Append($"-i \"{path}\" ");
                }
            }

            if (audioMaterial.Any())
            {
                byte audioCount = 0;
                metaArg.Append("-metadata:s:a:0 title=\"原音频\" ");
                foreach (var audio in audioMaterial)
                {
                    inputCount++;
                    audioCount++;
                    inputArg.Append($"-i \"{audio.path}\" ");
                    if (!string.IsNullOrWhiteSpace(audio.title)) metaArg.Append($"-metadata:s:a:{audioCount} title=\"{audio.title}\" ");
                    if (!string.IsNullOrWhiteSpace(audio.personName)) metaArg.Append($"-metadata:s:a:{audioCount} artist=\"{audio.personName}\" ");
                }
            }

            if (!string.IsNullOrEmpty(pic))
            {
                inputCount++;
                inputArg.Append($"-i \"{pic}\" ");
            }

            if (subs != null)
            {
                for (int i = 0; i < subs.Count; i++)
                {
                    if(File.Exists(subs[i].path) && File.ReadAllText(subs[i].path!) != "")
                    {
                        inputCount++;
                        inputArg.Append($"-i \"{subs[i].path}\" ");
                        metaArg.Append($"-metadata:s:s:{i} title=\"{GetSubtitleCode(subs[i].lan).Item2}\" -metadata:s:s:{i} language={GetSubtitleCode(subs[i].lan).Item1} ");
                    }
                }
            }

            if (!string.IsNullOrEmpty(pic))
                metaArg.Append($"-disposition:v:{(audioOnly ? "0" : "1")} attached_pic ");
            // var inputCount = InputRegex().Matches(inputArg.ToString()).Count;

            if (points != null && points.Any())
            {
                var meta = GetFFmpegMetaString(points);
                var metaFile = Path.Combine(Path.GetDirectoryName(string.IsNullOrEmpty(videoPath) ? audioPath : videoPath)!, "chapters");
                File.WriteAllText(metaFile, meta);
                inputArg.Append($"-i \"{metaFile}\" -map_chapters {inputCount} ");
            }

            inputArg.Append(string.Concat(Enumerable.Range(0, inputCount).Select(i => $"-map {i} ")));

            //----分析完毕
            StringBuilder argsBuilder = new StringBuilder();
            argsBuilder.Append($"-loglevel {(Config.DEBUG_LOG ? "verbose" : "warning")} -y ");
            argsBuilder.Append(inputArg);
            argsBuilder.Append(metaArg);
            if (!simplyMux) {
                argsBuilder.Append($"-metadata title=\"{(episodeId == "" ? title : episodeId)}\" ");
                if (lang != "") argsBuilder.Append($"-metadata:s:a:0 language={lang} ");
                if (!string.IsNullOrWhiteSpace(desc)) argsBuilder.Append($"-metadata description=\"{desc}\" ");
                if (!string.IsNullOrEmpty(author)) argsBuilder.Append($"-metadata artist=\"{author}\" ");
                if (episodeId != "") argsBuilder.Append($"-metadata album=\"{title}\" ");
                if (pubTime != 0) argsBuilder.Append($"-metadata creation_time=\"{(DateTimeOffset.FromUnixTimeSeconds(pubTime).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"))}\" ");
            }
            argsBuilder.Append("-c copy ");
            if (audioOnly && audioPath == "") argsBuilder.Append("-vn ");
            if (subs != null) argsBuilder.Append("-c:s mov_text ");
            argsBuilder.Append($"-movflags faststart -strict unofficial -strict -2 -f mp4 -- \"{outPath}\"");

            string arguments = argsBuilder.ToString();

            LogDebug("ffmpeg命令: {0}", arguments);
            return RunExe(FFMPEG, arguments, FFMPEG != "ffmpeg");
        }

        public static void MergeFLV(string[] files, string outPath)
        {
            if (files.Length == 1)
            {
                File.Move(files[0], outPath);
            }
            else
            {
                foreach (var file in files)
                {
                    var tmpFile = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".ts");
                    var arguments = $"-loglevel warning -y -i \"{file}\" -map 0 -c copy -f mpegts -bsf:v h264_mp4toannexb \"{tmpFile}\"";
                    LogDebug("ffmpeg命令: {0}", arguments);
                    RunExe("ffmpeg", arguments);
                    File.Delete(file);
                }
                var f = GetFiles(Path.GetDirectoryName(files[0])!, ".ts");
                CombineMultipleFilesIntoSingleFile(f, outPath);
                foreach (var s in f) File.Delete(s);
            }
        }
    }
}
