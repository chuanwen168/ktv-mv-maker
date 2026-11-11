using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace KTV_MV_maker
{
    /// <summary>MV 合成参数</summary>
    public class ExportOptions
    {
        /// <summary>是否生成"下一句"小字行（双排歌词）</summary>
        public bool ShowNextLine = true;
        /// <summary>H.264 质量（18-28，越小越清晰）</summary>
        public int Crf = 20;
        /// <summary>编码速度预设</summary>
        public string Preset = "medium";
        /// <summary>视频编码器：libx264 或 libx265</summary>
        public string VideoCodec = "libx264";
        /// <summary>歌词字号基准（按 720p 画面高度，实际随视频高度等比缩放）</summary>
        public int FontSize = 80;
        /// <summary>未唱文字颜色（ASS 格式 &amp;H00BBGGRR，上下排同色）</summary>
        public string TextColor = "&H00FFFFFF";
        /// <summary>已唱部分颜色（卡拉OK填充，ASS 格式 &amp;H00BBGGRR）</summary>
        public string KaraokeColor = "&H0000C8FF";
        /// <summary>开场标题/词曲信息行颜色（ASS 格式 &amp;H00BBGGRR），与正文歌词分开设置</summary>
        public string TitleColor = "&H00FFFFFF";
        /// <summary>整首歌时间轴偏移（毫秒）：负数=歌词提前、正数=歌词延后；标题/词曲信息行不参与</summary>
        public int TimeOffsetMs = 0;
        /// <summary>字幕字体族名（默认随程序携带的阿里普惠体 3.0）</summary>
        public string FontName = AssBuilder.DefaultFontName;
        /// <summary>卡拉OK变色特效</summary>
        public KaraokeEffect Effect = KaraokeEffect.Sweep;
        /// <summary>是否在第一句开唱前显示倒计时提示</summary>
        public bool Countdown = true;
        /// <summary>倒计时图案（单色符号）</summary>
        public string CountdownChar = "★";
        /// <summary>倒计时图案颜色（ASS 格式 &amp;H00BBGGRR）</summary>
        public string CountdownColor = "&H00FFA800";
    }

    /// <summary>
    /// MV 合成导出器：把视频 + 双排卡拉OK歌词字幕烧录为带歌词的 MV。
    /// 流程：ffprobe 探测分辨率 → 生成 ASS 字幕 → ffmpeg 调用 libass 烧录并输出 H.264 MP4。
    /// </summary>
    public class MvExporter
    {
        /// <summary>ffmpeg 可执行文件路径（程序目录，绿色便携）</summary>
        public string FfmpegPath = AppPaths.Combine("ffmpeg.exe");
        /// <summary>ffprobe 可执行文件路径</summary>
        public string FfprobePath = AppPaths.Combine("ffprobe.exe");
        /// <summary>临时目录（ASS 字幕等中间文件，纯 ASCII 路径避免滤镜转义问题）</summary>
        public string TempDir = AppPaths.Combine("mv_tmp");

        /// <summary>进度回调（百分比，消息）</summary>
        public Action<int, string> ProgressChanged;
        /// <summary>完成回调（是否成功，消息）</summary>
        public Action<bool, string> Completed;

        /// <summary>开始导出（异步，内部开后台线程）</summary>
        public void Export(string videoPath, KrcLyrics lyrics, string outputPath, ExportOptions options)
        {
            Thread worker = new Thread(delegate() { RunExport(videoPath, lyrics, outputPath, options); });
            worker.IsBackground = true;
            worker.Name = "MvExporter";
            worker.Start();
        }

        private void RunExport(string videoPath, KrcLyrics lyrics, string outputPath, ExportOptions options)
        {
            try
            {
                if (!File.Exists(FfmpegPath))
                {
                    Fail("未找到 ffmpeg.exe，请确认程序目录（exe 同目录）ffmpeg.exe 存在");
                    return;
                }
                if (!File.Exists(videoPath))
                {
                    Fail("视频文件不存在：" + videoPath);
                    return;
                }

                // 1. 探测视频分辨率与时长
                Report(2, "正在探测视频信息...");
                int width, height;
                double durationMs;
                if (!ProbeVideo(videoPath, out width, out height, out durationMs))
                {
                    Fail("无法探测视频分辨率/时长（ffprobe 执行失败）");
                    return;
                }

                // 2. 生成 ASS 字幕（按用户自定义的字号、颜色、字体、特效与倒计时）
                Report(5, "正在生成卡拉OK字幕...");
                Directory.CreateDirectory(TempDir);
                string assPath = Path.Combine(TempDir, "subtitle.ass");
                string ass = AssBuilder.Build(lyrics, width, height, options.ShowNextLine,
                    options.FontSize, options.TextColor, options.KaraokeColor, options.TimeOffsetMs,
                    options.FontName, options.Effect, options.Countdown, options.CountdownChar, options.CountdownColor,
                    options.TitleColor);
                File.WriteAllText(assPath, ass, new UTF8Encoding(false));

                // 字体随程序携带：复制到工作目录，让 subtitles 滤镜的 fontsdir=. 直接读到
                EnsureFontInTempDir();

                // 3. 调用 ffmpeg 烧录（subtitles 滤镜支持 fontsdir，ass 滤镜不支持）
                Report(8, "正在调用 ffmpeg 合成（subtitles=subtitle.ass，工作目录 " + TempDir + "）...");
                bool isH265 = string.Equals(options.VideoCodec, "libx265", StringComparison.OrdinalIgnoreCase);
                string pixFmt = isH265 ? "yuv420p10le" : "yuv420p";
                // H.265 MP4 容器加 -tag:v hvc1 让 QuickTime/iOS 能识别
                string extraArgs = "";
                if (isH265 && outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    extraArgs = " -tag:v hvc1";
                string args = string.Format(
                    "-y -i \"{0}\" -vf \"subtitles=subtitle.ass:fontsdir=.\" -c:v {1} -preset {2} -crf {3} -pix_fmt {4}{5} -c:a copy -movflags +faststart -progress pipe:1 -nostats \"{6}\"",
                    videoPath, options.VideoCodec, options.Preset, options.Crf, pixFmt, extraArgs, outputPath);

                Process p = new Process();
                p.StartInfo.FileName = FfmpegPath;
                p.StartInfo.Arguments = args;
                p.StartInfo.WorkingDirectory = TempDir;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;

                StringBuilder errorLog = new StringBuilder();
                double lastProgress = 8;   // 与调用前 Report(8) 对齐，保证进度单调不回退

                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    // -progress pipe:1 输出 key=value 行；注意 ffmpeg 的 out_time_ms 实际输出的是【微秒】
                    // （与 out_time_us 同值，历史命名遗留），必须 /1000 转毫秒再与时长比较
                    if (e.Data.StartsWith("out_time_ms="))
                    {
                        double us;
                        if (double.TryParse(e.Data.Substring(12), out us))
                        {
                            double ms = us / 1000.0;
                            int percent = durationMs > 0 ? (int)(ms / durationMs * 100) : 0;
                            if (percent > 100) percent = 100;
                            if (percent < 0) percent = 0;
                            if (percent > lastProgress)
                            {
                                lastProgress = percent;
                                Report(percent, "正在合成... " + percent + "%");
                            }
                        }
                    }
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        if (errorLog.Length > 4000) errorLog.Remove(0, 1000);
                        errorLog.AppendLine(e.Data);
                    }
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                p.WaitForExit(3000); // 确保异步输出回调完成

                if (p.ExitCode != 0)
                {
                    Fail("ffmpeg 合成失败，退出码 " + p.ExitCode + "。\n错误信息（末尾）：\n" + Tail(errorLog.ToString(), 600));
                    return;
                }

                // 4. 完成
                if (File.Exists(outputPath))
                {
                    Report(100, "合成完成！输出：" + outputPath);
                    if (Completed != null) Completed(true, "合成完成！\n输出文件：\n" + outputPath);
                }
                else
                {
                    Fail("ffmpeg 未生成输出文件");
                }
            }
            catch (Exception ex)
            {
                Fail("导出异常：" + ex.Message);
            }
        }

        /// <summary>字幕字体文件名（随程序携带，位于程序目录 fonts\\）</summary>
        public const string FontFileName = "AlibabaPuHuiTi-3-55-Regular.ttf";

        /// <summary>
        /// 把随程序携带的字体复制到临时目录：ffmpeg 的 subtitles 滤镜用 fontsdir=. 读取，
        /// 这样不依赖系统是否安装该字体，换机器也不会掉字体。
        /// </summary>
        public void EnsureFontInTempDir()
        {
            try
            {
                Directory.CreateDirectory(TempDir);
                string src = AppPaths.Combine("fonts", FontFileName);
                string dst = Path.Combine(TempDir, FontFileName);
                if (!File.Exists(src)) return;
                FileInfo si = new FileInfo(src);
                FileInfo di = File.Exists(dst) ? new FileInfo(dst) : null;
                if (di == null || di.Length != si.Length || di.LastWriteTimeUtc < si.LastWriteTimeUtc)
                    File.Copy(src, dst, true);
            }
            catch
            {
                // 字体复制失败不影响合成（libass 会回退到系统字体）
            }
        }

        /// <summary>ffprobe 探测视频宽度/高度/时长（毫秒）</summary>
        public bool ProbeVideo(string videoPath, out int width, out int height, out double durationMs)
        {
            width = 0; height = 0; durationMs = 0;
            try
            {
                if (!File.Exists(FfprobePath)) return false;

                // 分辨率
                Process p = new Process();
                p.StartInfo.FileName = FfprobePath;
                p.StartInfo.Arguments = "-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"" + videoPath + "\"";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                string size = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                string[] parts = size.Split(',');
                if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out width) && int.TryParse(parts[1].Trim(), out height))
                {
                    // 时长（秒）
                    Process p2 = new Process();
                    p2.StartInfo.FileName = FfprobePath;
                    p2.StartInfo.Arguments = "-v error -show_entries format=duration -of csv=p=0 \"" + videoPath + "\"";
                    p2.StartInfo.UseShellExecute = false;
                    p2.StartInfo.RedirectStandardOutput = true;
                    p2.StartInfo.RedirectStandardError = true;
                    p2.StartInfo.CreateNoWindow = true;
                    p2.Start();
                    string dur = p2.StandardOutput.ReadToEnd().Trim();
                    p2.WaitForExit();
                    double sec;
                    if (double.TryParse(dur, out sec)) durationMs = sec * 1000;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void Report(int percent, string message)
        {
            if (ProgressChanged != null) ProgressChanged(percent, message);
        }

        private void Fail(string message)
        {
            if (Completed != null) Completed(false, message);
        }

        private static string Tail(string text, int maxChars)
        {
            if (text.Length <= maxChars) return text;
            return text.Substring(text.Length - maxChars);
        }
    }
}
