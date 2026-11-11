using System;
using System.Collections.Generic;
using System.Text;

namespace KTV_MV_maker
{
    /// <summary>卡拉OK变色特效</summary>
    public enum KaraokeEffect
    {
        /// <summary>跳过：唱到的字整字瞬间变金（\k）</summary>
        Clip = 0,
        /// <summary>渐变：每个字在自己演唱的时长内由白渐金，无方向</summary>
        Fade = 1,
        /// <summary>扫过：金色从字左缘推进到右缘（\kf，预览端可能不支持，以导出为准）</summary>
        Sweep = 2
    }

    /// <summary>
    /// 把 KRC/KSC 歌词转换为 ASS 卡拉OK字幕（供 FFmpeg libass 烧录进视频）。
    /// 交替双排模式：
    ///   - 上下两排字号相同且**永不缩小**（过长由用户手动拆句）；
    ///   - 上排左对齐、下排右对齐（左右各留约 2 字宽内边距，短句也不会贴死在边缘）；
    ///   - 偶数句在上排卡拉OK，下排同时显示下一句预告；
    ///   - 奇数句在下排卡拉OK，上排预告延迟到"下排唱了一部分"后才切换显示下一句。
    /// </summary>
    public static class AssBuilder
    {
        /// <summary>下排主句卡拉OK走过该比例后，上排才切换显示下一句预告</summary>
        public const double NextDelayRatio = 0.4;
        /// <summary>上排切换延迟下限（毫秒）</summary>
        public const int MinDelayMs = 500;
        /// <summary>标题（信息行）最长显示时长（毫秒）：第一句来得晚则只显示 5 秒，之后留空</summary>
        public const int MetaMaxMs = 5000;
        /// <summary>片头较短时标题显示的时长（毫秒）：第一句在 5 秒内就开唱时，标题只显示 1 秒</summary>
        public const int MetaShortMs = 1000;
        /// <summary>正文第一句提前显示时长（毫秒）：仅第一句在开唱前提前出现（白色），其余句严格同步</summary>
        public const int PreShowMs = 5000;

        /// <summary>
        /// 默认字幕字体：随程序携带的阿里普惠体 3.0。
        /// 注意必须用 Windows 平台族名（TTF name 表 platform=3 的 nameID=1）：
        /// "Alibaba PuHuiTi 3.0 55 Regular"。
        /// </summary>
        public const string DefaultFontName = "Alibaba PuHuiTi 3.0 55 Regular";
        /// <summary>开唱倒计时最多颗数</summary>
        public const int CountdownMaxDots = 5;
        /// <summary>第一句开唱前至少有这么多秒才显示倒计时（不足即不做）</summary>
        public const int CountdownMinLeadMs = 3000;
        /// <summary>作词/作曲行字号相对标题的比例</summary>
        public const double CreditFontRatio = 0.875;
        /// <summary>Fade 渐变：每个字每步的时间（毫秒），越小越平滑</summary>
        private const int FadeStepMs = 28;
        /// <summary>Fade 渐变：单个字的最少/最多步数</summary>
        private const int FadeMinSteps = 4;
        private const int FadeMaxSteps = 12;

        /// <summary>一句歌词被注释词切开后的显示片段</summary>
        private class Seg
        {
            public string Text;
            public int Start;
            public int Duration;
            public bool IsAnnotation;
        }

        /// <summary>Fade 渐变用的单字时间片</summary>
        private class FadeChar
        {
            public string Text;
            public int Start;
            public int End;
            public bool IsAnnotation;
        }

        /// <summary>生成 ASS 字幕文本。</summary>
        public static string Build(KrcLyrics lyrics, int width, int height, bool showNextLine,
            int baseFontSize = 80, string textColor = "&H00FFFFFF", string karaokeColor = "&H0000B8FF",
            int timeOffsetMs = 0, string fontName = DefaultFontName, KaraokeEffect effect = KaraokeEffect.Sweep,
            bool countdown = false, string countdownChar = "★", string countdownColor = "&H00FFA800",
            string titleColor = "&H00FFFFFF")
        {
            StringBuilder sb = new StringBuilder();
            if (string.IsNullOrEmpty(fontName)) fontName = DefaultFontName;

            // 时间轴偏移：负=歌词提前、正=歌词延后（结果不小于 0）；标题/词曲信息行不参与
            Func<int, int> S = delegate(int t) { int v = t + timeOffsetMs; return v < 0 ? 0 : v; };

            int font = Math.Max(16, baseFontSize * height / 720);   // 上下两排等大，且永不缩小
            int lineH = (int)(font * 1.35);
            // 歌词左右内边距：约 2 个字宽（短句不会贴死在左右边缘）
            int padX = Math.Max(20, font * 2);
            // 字幕组整体靠近底部，距离底边占画面高度 10%
            int baseY = height - (int)(height * 0.10) - lineH;
            int yTop = baseY;                 // 上排文字垂直中线
            int yBottom = baseY + lineH;      // 下排文字垂直中线

            sb.AppendLine("[Script Info]");
            sb.AppendLine("ScriptType: v4.00+");
            sb.AppendLine("PlayResX: " + width);
            sb.AppendLine("PlayResY: " + height);
            sb.AppendLine("WrapStyle: 2");
            sb.AppendLine("ScaledBorderAndShadow: yes");
            sb.AppendLine("");

            sb.AppendLine("[V4+ Styles]");
            sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
            // 上排：左对齐（Alignment=4 左中，MarginL=内边距，左侧缩进约 2 字）
            sb.AppendLine("Style: LyricTop," + fontName + "," + font +
                "," + karaokeColor + "," + textColor + ",&H00281810,&H96000000,-1,0,0,0,100,100,0,0,1,3,2,4," + padX + ",0,0,134");
            // 下排：右对齐（Alignment=6 右中，MarginR=内边距，右侧缩进约 2 字）
            sb.AppendLine("Style: LyricBottom," + fontName + "," + font +
                "," + karaokeColor + "," + textColor + ",&H00281810,&H96000000,-1,0,0,0,100,100,0,0,1,3,2,6,0," + padX + ",0,134");
            sb.AppendLine("");

            sb.AppendLine("[Events]");
            sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

            // 上下两排固定贴边定位，缩进（1~2 字）按每句长度动态计算，见 IndentCharsForText

            // ---- 信息行与正式歌词分离 ----
            List<LyricLine> metas = new List<LyricLine>();
            List<LyricLine> real = new List<LyricLine>();
            foreach (LyricLine l in lyrics.Lines)
            {
                if (l.IsMeta) metas.Add(l); else real.Add(l);
            }
            if (real.Count == 0) { real = metas; metas = new List<LyricLine>(); }

            // 倒计时星号与第一排左缘对齐
            int firstIndentPx = 0;
            if (real.Count > 0)
                firstIndentPx = IndentCharsForText(real[0].FullText, width, font) * (int)(font * CharWidthRatio);

            // ---- 歌曲信息行（标题"歌名-作者"+作词作曲，静态，不卡拉OK）----
            int metaEnd = 0;
            if (metas.Count > 0 && real.Count > 0 && S(real[0].Start) > 0)
            {
                int realStart = S(real[0].Start);
                metaEnd = realStart >= MetaMaxMs ? MetaMaxMs : Math.Min(realStart, MetaShortMs);
                int centerX = width / 2;
                int centerY = height / 2;
                string title = lyrics.EffectiveTitle;
                List<string> credits = new List<string>();
                foreach (LyricLine m in metas)
                {
                    string t = m.FullText.Trim();
                    if (t.StartsWith("词：") || t.StartsWith("曲：") ||
                        t.StartsWith("作词：") || t.StartsWith("作曲：")) credits.Add(t);
                }
                int creditFont = Math.Max(10, (int)(font * CreditFontRatio));
                int rows = (title.Length > 0 ? 1 : 0) + credits.Count;
                int firstY = centerY - (rows - 1) * lineH / 2;
                int row = 0;
                if (title.Length > 0)
                {
                    int tf = FitFont(title, font, width, padX);
                    int y = firstY + row * lineH;
                    string tag = "{\\an5\\pos(" + centerX + "," + y + ")}";
                    sb.AppendLine("Dialogue: 0," + AssTime(0) + "," + AssTime(metaEnd) +
                        ",LyricTop,,0,0,0,," + "{\\c" + titleColor + "}" + "{\\fs" + tf + "}" + tag + EscapeAss(title));
                    row++;
                }
                foreach (string cr in credits)
                {
                    int cf = FitFont(cr, creditFont, width, padX);
                    int y = firstY + row * lineH;
                    string tag = "{\\an5\\pos(" + centerX + "," + y + ")}";
                    sb.AppendLine("Dialogue: 0," + AssTime(0) + "," + AssTime(metaEnd) +
                        ",LyricBottom,,0,0,0,," + "{\\c" + titleColor + "}" + "{\\fs" + cf + "}" + tag + EscapeAss(cr));
                    row++;
                }
            }

            // ---- 开唱倒计时（仅正文第一句；可用时间 ≥3 秒才做，最多 5 颗）----
            // 位置：第一排歌词正上方、与第一排右缘对齐
            if (countdown && real.Count > 0 && !string.IsNullOrEmpty(countdownChar))
            {
                int startAbs = S(real[0].Start);
                int dots = Math.Min(CountdownMaxDots, startAbs / 1000);
                if (dots * 1000 >= CountdownMinLeadMs)
                {
                    int cdFont = Math.Max(10, (int)(font * 0.6));
                    int cdY = yTop - (int)(font * 0.62);
                    if (cdY < cdFont) cdY = cdFont;
                    string cdTag = "{\\an4\\pos(" + firstIndentPx + "," + cdY + ")}";
                    for (int j = dots; j >= 1; j--)
                    {
                        int from = startAbs - j * 1000;
                        int to = startAbs - (j - 1) * 1000;
                        if (from < 0) from = 0;
                        if (to <= from) continue;
                        StringBuilder dotText = new StringBuilder();
                        for (int c = 0; c < j; c++)
                        {
                            if (c > 0) dotText.Append(" ");
                            dotText.Append(countdownChar);
                        }
                        sb.AppendLine("Dialogue: 0," + AssTime(from) + "," + AssTime(to) +
                            ",LyricTop,,0,0,0,," + "{\\c" + countdownColor + "\\fs" + cdFont + "}" + cdTag +
                            EscapeAss(dotText.ToString()));
                    }
                }
            }

            // ---- 正式歌词：交替双排（正式第一句=上排卡拉OK）----
            for (int k = 0; k < real.Count; k++)
            {
                LyricLine cur = real[k];
                LyricLine next = (k + 1 < real.Count) ? real[k + 1] : null;

                int effFont = font;   // 字号永不缩小

                // 每句按自身长度决定缩进：很长→不缩进，中等→1 字，短句→2 字，并保证不超出安全边距
                string curTopAlign = TopAlignTag(cur.FullText, width, font, yTop);
                string curTopTag = "{" + curTopAlign + "}";
                string curBottomAlign = BottomAlignTag(cur.FullText, width, font, yBottom);
                string curBottomTag = "{" + curBottomAlign + "}";
                string nextTopTag = null, nextBottomTag = null;
                if (next != null)
                {
                    nextTopTag = "{" + TopAlignTag(next.FullText, width, font, yTop) + "}";
                    nextBottomTag = "{" + BottomAlignTag(next.FullText, width, font, yBottom) + "}";
                }

                if (k % 2 == 0)
                {
                    // ---- 偶数句：上排卡拉OK ----
                    int displayStart = S(cur.Start);
                    if (k == 0)
                    {
                        int firstPre = S(cur.Start) - PreShowMs;
                        if (firstPre < metaEnd) firstPre = metaEnd;
                        if (firstPre < displayStart) displayStart = firstPre;
                        if (effect != KaraokeEffect.Fade && firstPre < S(cur.Start))
                        {
                            sb.AppendLine("Dialogue: 0," + AssTime(firstPre) + "," + AssTime(S(cur.Start)) +
                                ",LyricTop,,0,0,0,," + "{\\c" + textColor + "}" + "{\\fs" + effFont + "}" + curTopTag + EscapeAss(cur.FullText));
                        }
                    }
                    // 卡拉OK行结束时间延长到"下排唱过一部分后上排切换下一句"的时刻
                    int karaEnd = S(cur.End);
                    if (showNextLine && next != null)
                    {
                        int nextDelay = Math.Max(MinDelayMs, (int)(next.Duration * NextDelayRatio));
                        int previewStart = S(next.Start + nextDelay);
                        if (previewStart < S(next.End)) karaEnd = previewStart;
                    }
                    if (effect == KaraokeEffect.Fade)
                    {
                        AppendFadeLine(sb, cur, displayStart, karaEnd, "LyricTop", effFont, curTopAlign,
                            textColor, karaokeColor);
                    }
                    else
                    {
                        string karaokeText = "{\\fs" + effFont + "}" + BuildKaraokeText(cur, effect, textColor, karaokeColor);
                        sb.AppendLine("Dialogue: 0," + AssTime(S(cur.Start)) + "," + AssTime(karaEnd) +
                            ",LyricTop,,0,0,0,," + curTopTag + karaokeText);
                    }
                    // ---- 下排预告：直到下一句卡拉OK开始（无缝衔接）----
                    if (showNextLine && next != null)
                    {
                        int bottomEnd = S(next.Start);
                        if (bottomEnd < S(cur.Start)) bottomEnd = S(cur.End);
                        sb.AppendLine("Dialogue: 0," + AssTime(S(cur.Start)) + "," + AssTime(bottomEnd) +
                            ",LyricBottom,,0,0,0,," + "{\\c" + textColor + "}" + "{\\fs" + effFont + "}" + nextBottomTag + EscapeAss(next.FullText));
                    }
                }
                else
                {
                    // ---- 奇数句：下排卡拉OK ----
                    if (effect == KaraokeEffect.Fade)
                    {
                        AppendFadeLine(sb, cur, S(cur.Start), S(cur.End), "LyricBottom", effFont, curBottomAlign,
                            textColor, karaokeColor);
                    }
                    else
                    {
                        string karaokeText2 = "{\\fs" + effFont + "}" + BuildKaraokeText(cur, effect, textColor, karaokeColor);
                        sb.AppendLine("Dialogue: 0," + AssTime(S(cur.Start)) + "," + AssTime(S(cur.End)) +
                            ",LyricBottom,,0,0,0,," + curBottomTag + karaokeText2);
                    }
                    // ---- 上排预告：下排唱了一部分后切换显示下一句 ----
                    if (showNextLine && next != null)
                    {
                        int delay = Math.Max(MinDelayMs, (int)(cur.Duration * NextDelayRatio));
                        int start = S(cur.Start + delay);
                        int end = S(cur.End);
                        if (S(next.Start) > end) end = S(next.Start);
                        if (start < end)
                        {
                            sb.AppendLine("Dialogue: 0," + AssTime(start) + "," + AssTime(end) +
                                ",LyricTop,,0,0,0,," + "{\\c" + textColor + "}" + "{\\fs" + effFont + "}" + nextTopTag + EscapeAss(next.FullText));
                        }
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>libass 实测 CJK 字宽约 0.70em，这里取 0.75em 作保守估计</summary>
        private const double CharWidthRatio = 0.75;

        /// <summary>上排左对齐定位：左边缩进按本句长度决定（0/1/2 字）</summary>
        private static string TopAlignTag(string text, int width, int font, int y)
        {
            int px = IndentCharsForText(text, width, font) * (int)(font * CharWidthRatio);
            return "\\an4\\pos(" + px + "," + y + ")";
        }

        /// <summary>下排右对齐定位：右边缩进按本句长度决定（0/1/2 字）</summary>
        private static string BottomAlignTag(string text, int width, int font, int y)
        {
            int px = IndentCharsForText(text, width, font) * (int)(font * CharWidthRatio);
            return "\\an6\\pos(" + (width - px) + "," + y + ")";
        }

        /// <summary>
        /// 一行歌词离边缘的缩进字数：句子很长（接近占满画面）→ 0；
        /// 中等长度 → 1；短句 → 2。字号永不缩小，长句必须允许贴边，否则会被挤出画面。
        /// </summary>
        private static int IndentCharsForText(string text, int width, int font)
        {
            double charW = font * CharWidthRatio;
            double textW = EstTextWidth(text, font);
            if (textW >= width * 0.78 || textW >= width - charW * 2) return 0;
            if (textW >= width * 0.5) return 1;
            return 2;
        }

        /// <summary>粗略估计整句渲染宽度（CJK/全角 1em，ASCII 约 0.6em，空格约 0.3em）</summary>
        private static double EstTextWidth(string text, int font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double w = 0;
            foreach (char c in text)
            {
                if (c == ' ') w += font * 0.30;
                else if (c < 0x2E80) w += font * 0.60;
                else w += font * CharWidthRatio;
            }
            return w;
        }

        /// <summary>按画面宽度适配字号（仅用于片头标题/词曲行，正文歌词永不缩小）</summary>
        private static int FitFont(string text, int font, int width, int padX)
        {
            int maxChars = Math.Max(1, (width - padX * 2) / Math.Max(1, font));
            if (text.Length > maxChars)
                return Math.Max(10, font * maxChars / text.Length);
            return font;
        }

        /// <summary>
        /// 把一句歌词按注释词边界切成片段：
        /// 冒号注释（"男：" "Selina：" 等）部分 IsAnnotation=true，不参与变色。
        /// 注释边界落在字中间时（如 "Yumi：一"），按字数比例拆开时长。
        /// </summary>
        private static List<Seg> BuildSegments(LyricLine line)
        {
            List<Seg> segs = new List<Seg>();
            int annotation = Math.Max(0, line.AnnotationChars);
            int consumed = 0;
            foreach (LyricWord w in line.Words)
            {
                if (w == null || string.IsNullOrEmpty(w.Text)) continue;
                int len = w.Text.Length;
                int annLen = 0;
                if (consumed < annotation) annLen = Math.Min(len, annotation - consumed);
                if (annLen > 0)
                {
                    int annDur = len > 0 ? (int)Math.Round(w.Duration * (double)annLen / len) : 0;
                    Seg a = new Seg();
                    a.Text = w.Text.Substring(0, annLen);
                    a.Start = w.Start;
                    a.Duration = Math.Max(0, annDur);
                    a.IsAnnotation = true;
                    segs.Add(a);
                    if (annLen < len)
                    {
                        Seg b = new Seg();
                        b.Text = w.Text.Substring(annLen);
                        b.Start = w.Start + a.Duration;
                        b.Duration = Math.Max(1, w.Duration - a.Duration);
                        b.IsAnnotation = false;
                        segs.Add(b);
                    }
                }
                else
                {
                    Seg s = new Seg();
                    s.Text = w.Text;
                    s.Start = w.Start;
                    s.Duration = Math.Max(1, w.Duration);
                    s.IsAnnotation = false;
                    segs.Add(s);
                }
                consumed += len;
            }
            return segs;
        }

        /// <summary>
        /// 生成逐字卡拉OK文本（Clip/Sweep）：
        /// 注释词用未唱色作为该字的已唱色，所以扫描/硬切经过它时颜色不变（保持默认底色）。
        /// </summary>
        private static string BuildKaraokeText(LyricLine line, KaraokeEffect effect,
            string textColor, string karaokeColor)
        {
            StringBuilder sb = new StringBuilder();
            List<Seg> segs = BuildSegments(line);
            foreach (Seg seg in segs)
            {
                int cs = Math.Max(1, seg.Duration / 10);
                string color = seg.IsAnnotation ? textColor : karaokeColor;
                if (effect == KaraokeEffect.Sweep)
                    sb.Append("{\\kf").Append(cs).Append("\\1c").Append(color).Append("&}");
                else
                    sb.Append("{\\k").Append(cs).Append("\\1c").Append(color).Append("&}");
                sb.Append(EscapeAss(seg.Text));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Fade（字内渐变）实现：**不做任何字宽测量**，每一条事件都包含完整一句
        /// （由 libass 自然排版，因此与其它句的排版完全一致，不存在错位/字距异常）。
        /// 做法是把整句按时间切成若干片，每一片里每个字用静态 <c>\1c</c> 设成
        /// "未唱色 / 渐变的中间色 / 已唱色"，相邻片之间颜色连续变化，即为逐字渐变。
        /// 注释词（"男：" "Selina：" 等）始终用未唱色，不参与渐变。
        /// </summary>
        private static void AppendFadeLine(StringBuilder sb, LyricLine line, int displayStart, int lineEnd,
            string style, int fontSize, string alignTag, string textColor, string karaokeColor)
        {
            List<Seg> segs = BuildSegments(line);
            if (segs.Count == 0) return;
            if (lineEnd <= displayStart) lineEnd = displayStart + 1;

            int tr, tg, tb, kr, kg, kb;
            ParseAssColor(textColor, out tr, out tg, out tb);
            ParseAssColor(karaokeColor, out kr, out kg, out kb);
            string textTag = AssColorTag(tr, tg, tb);
            string karaokeTag = AssColorTag(kr, kg, kb);

            // 拆成单字，并算出每个字的起止时间（同一片段内按字数均分）
            List<FadeChar> chars = new List<FadeChar>();
            foreach (Seg seg in segs)
            {
                if (string.IsNullOrEmpty(seg.Text)) continue;
                int n = seg.Text.Length;
                for (int i = 0; i < n; i++)
                {
                    FadeChar c = new FadeChar();
                    c.Text = seg.Text.Substring(i, 1);
                    c.Start = seg.Start + (int)Math.Round(seg.Duration * (double)i / n);
                    c.End = seg.Start + (int)Math.Round(seg.Duration * (double)(i + 1) / n);
                    if (c.End < c.Start) c.End = c.Start;
                    c.IsAnnotation = seg.IsAnnotation;
                    chars.Add(c);
                }
            }
            if (chars.Count == 0) return;

            // 时间切片点：提前显示起点、每个演唱字的起止与中间渐进步
            List<int> times = new List<int>();
            times.Add(displayStart);
            foreach (FadeChar c in chars)
            {
                if (c.IsAnnotation) continue;
                int dur = c.End - c.Start;
                int steps = dur / FadeStepMs;
                if (steps < FadeMinSteps) steps = FadeMinSteps;
                if (steps > FadeMaxSteps) steps = FadeMaxSteps;
                times.Add(c.Start);
                for (int s = 1; s <= steps; s++)
                    times.Add(c.Start + (int)Math.Round((double)dur * s / steps));
            }
            times.Add(lineEnd);
            times.Sort();
            List<int> marks = new List<int>();
            for (int i = 0; i < times.Count; i++)
            {
                int t = times[i];
                if (t < displayStart) t = displayStart;
                if (t > lineEnd) t = lineEnd;
                if (marks.Count == 0 || t != marks[marks.Count - 1]) marks.Add(t);
            }
            if (marks.Count < 2) return;

            // 逐片生成事件；相邻片若颜色完全相同则合并，减少事件数
            List<int> evFrom = new List<int>();
            List<int> evTo = new List<int>();
            List<string> evBody = new List<string>();
            for (int i = 0; i < marks.Count - 1; i++)
            {
                int from = marks[i];
                int to = marks[i + 1];
                if (to <= from) continue;
                string body = BuildFadeBody(chars, from, tr, tg, tb, kr, kg, kb, textTag, karaokeTag);
                if (evBody.Count > 0 && evBody[evBody.Count - 1] == body)
                {
                    evTo[evTo.Count - 1] = to;
                }
                else
                {
                    evFrom.Add(from);
                    evTo.Add(to);
                    evBody.Add(body);
                }
            }
            for (int i = 0; i < evBody.Count; i++)
            {
                sb.AppendLine("Dialogue: 0," + AssTime(evFrom[i]) + "," + AssTime(evTo[i]) + "," + style +
                    ",,0,0,0,,{\\fs" + fontSize + alignTag + "}" + evBody[i]);
            }
        }

        /// <summary>生成某一时刻整句的逐字着色文本</summary>
        private static string BuildFadeBody(List<FadeChar> chars, int time,
            int tr, int tg, int tb, int kr, int kg, int kb, string textTag, string karaokeTag)
        {
            StringBuilder body = new StringBuilder();
            string lastTag = null;
            foreach (FadeChar c in chars)
            {
                string tag;
                if (c.IsAnnotation || c.End <= c.Start || time <= c.Start)
                {
                    tag = textTag;
                }
                else if (time >= c.End)
                {
                    tag = karaokeTag;
                }
                else
                {
                    double f = (double)(time - c.Start) / (c.End - c.Start);
                    tag = AssColorTag(
                        (int)Math.Round(tr + (kr - tr) * f),
                        (int)Math.Round(tg + (kg - tg) * f),
                        (int)Math.Round(tb + (kb - tb) * f));
                }
                if (tag != lastTag)
                {
                    body.Append("{\\1c").Append(tag).Append("}");
                    lastTag = tag;
                }
                body.Append(EscapeAss(c.Text));
            }
            return body.ToString();
        }

        /// <summary>解析 ASS 颜色（"&H00FFFFFF" 形如 AABBGGRR）为 RGB 分量</summary>
        private static void ParseAssColor(string ass, out int r, out int g, out int b)
        {
            r = g = b = 0;
            if (string.IsNullOrEmpty(ass)) return;
            string hex = ass.Trim();
            if (hex.StartsWith("&H") || hex.StartsWith("&h")) hex = hex.Substring(2);
            hex = hex.TrimEnd('&');
            int v;
            try { v = Convert.ToInt32(hex, 16); }
            catch { return; }
            // AABBGGRR：最低字节是红色
            r = v & 0xFF;
            g = (v >> 8) & 0xFF;
            b = (v >> 16) & 0xFF;
        }

        /// <summary>RGB 分量 → ASS 颜色标签 "&H00BBGGRR&"</summary>
        private static string AssColorTag(int r, int g, int b)
        {
            return "&H00" + b.ToString("X2") + g.ToString("X2") + r.ToString("X2") + "&";
        }

        /// <summary>毫秒 → ASS 时间码 H:MM:SS.cc</summary>
        private static string AssTime(int ms)
        {
            if (ms < 0) ms = 0;
            int cs = ms / 10;
            int s = cs / 100;
            int m = s / 60;
            int h = m / 60;
            return string.Format("{0}:{1:00}:{2:00}.{3:00}", h, m % 60, s % 60, cs % 100);
        }

        /// <summary>转义 ASS 文本中的特殊字符（{}、换行等）</summary>
        private static string EscapeAss(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\\", "\\\\")
                       .Replace("{", "\\{")
                       .Replace("}", "\\}")
                       .Replace("\r", "")
                       .Replace("\n", "\\N");
        }

    }
}
