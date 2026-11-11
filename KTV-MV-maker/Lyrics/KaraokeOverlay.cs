using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KTV_MV_maker
{
    /// <summary>
    /// KTV 双排歌词渲染控件（叠加在视频预览之上，与合成 ASS 效果一致）。
    /// 交替双排模式（KTV 点歌机风格）：
    ///   - 上下两排字号相同，且**永不缩小**（过长的句子请用歌词编辑器"另起一行"手动拆句）；
    ///   - 上排左对齐、下排右对齐，左右各留约 2 字宽内边距（短句也不会贴死在边缘）；
    ///   - 偶数句在上排卡拉OK（逐字金色变色），此时下排显示下一句预告；
    ///   - 奇数句在下排卡拉OK，上排预告延迟到"下排唱了一部分"后才切换显示下一句；
    ///   - 标题/作词作曲等信息行不在此控件中显示（由 ASS 片头单独渲染）；
    ///   - 句首的演唱者注释（"男：""Selina："等）保持未唱色，不参与卡拉OK。
    /// </summary>
    public class KaraokeOverlay : FrameworkElement
    {
        private KrcLyrics _lyrics;
        private double _positionMs;
        private List<LyricLine> _real = new List<LyricLine>();

        private static readonly Typeface FontTypeface = new Typeface(
            new FontFamily("Microsoft YaHei"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        /// <summary>歌词数据（解析后）</summary>
        public KrcLyrics Lyrics
        {
            get { return _lyrics; }
            set { _lyrics = value; RefreshRealLines(); InvalidateVisual(); }
        }

        /// <summary>当前播放位置（毫秒）</summary>
        public double PositionMs
        {
            get { return _positionMs; }
            set { _positionMs = value; InvalidateVisual(); }
        }

        /// <summary>是否显示下一句（双排）</summary>
        public bool ShowNextLine { get; set; }

        // ---- 可调样式 ----
        /// <summary>未唱文字颜色（上下排同色）</summary>
        public Color TextColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
        /// <summary>描边颜色</summary>
        public Color OutlineColor = Color.FromRgb(0x10, 0x10, 0x28);
        /// <summary>卡拉OK已唱部分颜色</summary>
        public Color KaraokeColor = Color.FromRgb(0xFF, 0xB8, 0x00);

        /// <summary>上下两排共用字号 = 画布高度 × 该系数</summary>
        public double FontScale = 0.10;
        /// <summary>下排主句卡拉OK走过该比例后，上排才切换显示下一句预告</summary>
        public double NextDelayRatio = 0.4;
        /// <summary>上排切换延迟下限（毫秒）</summary>
        public double MinDelayMs = 500;

        public KaraokeOverlay()
        {
            ShowNextLine = true;
        }

        /// <summary>只把正文歌词参与双排显示（标题/作词作曲等信息行不显示）</summary>
        private void RefreshRealLines()
        {
            _real = new List<LyricLine>();
            if (_lyrics == null) return;
            foreach (LyricLine l in _lyrics.Lines)
                if (!l.IsMeta) _real.Add(l);
            // 全是信息行时退化为原样显示，避免整屏空白
            if (_real.Count == 0)
                foreach (LyricLine l in _lyrics.Lines) _real.Add(l);
        }

        private int IndexInReal(int ms)
        {
            int idx = -1;
            for (int n = 0; n < _real.Count; n++)
            {
                if (ms >= _real[n].Start) idx = n;
                else break;
            }
            return idx;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double w = ActualWidth;
            double h = ActualHeight;
            if (w < 10 || h < 10) return;
            if (_lyrics == null || _lyrics.Lines.Count == 0) return;
            if (_real.Count == 0) RefreshRealLines();
            if (_real.Count == 0) return;

            int pos = (int)_positionMs;
            int idx = IndexInReal(pos);
            if (idx < 0) return;
            LyricLine cur = _real[idx];

            double font = Math.Max(20, h * FontScale);
            double lineH = font * 1.35;
            double marginX = Math.Max(8, font * 2);   // 左右各缩进约 2 个字宽

            // 字幕组整体靠近底部：下排中线距底边为画面高度的 10%
            double bottomCenter = h - h * 0.10;
            double topCenter = bottomCenter - lineH;

            // 偶数句卡拉OK在上排（左对齐）、预告在下排（右对齐）；
            // 奇数句卡拉OK在下排（右对齐）、预告在上排（左对齐，延迟切换）
            if (idx % 2 == 0)
            {
                DrawKaraokeLine(dc, cur, pos, font, topCenter, false, marginX, w);
                if (ShowNextLine && idx + 1 < _real.Count)
                    DrawStaticLine(dc, _real[idx + 1], font, bottomCenter, true, marginX, w);
            }
            else
            {
                double delay = Math.Max(MinDelayMs, cur.Duration * NextDelayRatio);
                if (ShowNextLine && idx + 1 < _real.Count && pos >= cur.Start + delay)
                    DrawStaticLine(dc, _real[idx + 1], font, topCenter, false, marginX, w);
                DrawKaraokeLine(dc, cur, pos, font, bottomCenter, true, marginX, w);
            }
        }

        /// <summary>绘制卡拉OK行：白字深描边 + 已唱部分金色（从左往右逐字，不缩小字号）</summary>
        private void DrawKaraokeLine(DrawingContext dc, LyricLine line, int posMs,
            double font, double centerY, bool rightAlign, double marginX, double w)
        {
            SolidColorBrush textBrush = new SolidColorBrush(TextColor);
            FormattedText ft = BuildText(line.FullText, font, textBrush);
            double x = rightAlign ? (w - marginX - ft.Width) : marginX;
            double y = centerY - ft.Height / 2;

            // 深色描边 + 文字
            Geometry geo = ft.BuildGeometry(new Point(x, y));
            double outlineWidth = Math.Max(1.2, font / 14);
            SolidColorBrush outlineBrush = new SolidColorBrush(OutlineColor);
            dc.DrawGeometry(outlineBrush, new Pen(outlineBrush, outlineWidth), geo);
            dc.DrawText(ft, new Point(x, y));

            // 金色卡拉OK层：按已唱宽度裁剪（句首注释词不参与，保持白色）
            double annWidth = AnnotationWidth(line, font);
            double sungWidth = ComputeKaraokeWidth(line, posMs, font);
            if (sungWidth > annWidth + 1)
            {
                SolidColorBrush goldBrush = new SolidColorBrush(KaraokeColor);
                FormattedText ftGold = BuildText(line.FullText, font, goldBrush);
                double left = x + annWidth;
                double width = sungWidth - annWidth;
                dc.PushClip(new RectangleGeometry(new Rect(left, y, width, ft.Height + 4)));
                dc.DrawText(ftGold, new Point(x, y));
                dc.Pop();
            }
        }

        /// <summary>绘制静态预告行：文字深描边（不卡拉OK，不缩小字号）</summary>
        private void DrawStaticLine(DrawingContext dc, LyricLine line,
            double font, double centerY, bool rightAlign, double marginX, double w)
        {
            SolidColorBrush textBrush = new SolidColorBrush(TextColor);
            FormattedText ft = BuildText(line.FullText, font, textBrush);
            double x = rightAlign ? (w - marginX - ft.Width) : marginX;
            double y = centerY - ft.Height / 2;

            Geometry geo = ft.BuildGeometry(new Point(x, y));
            double outlineWidth = Math.Max(1.2, font / 14);
            SolidColorBrush outlineBrush = new SolidColorBrush(OutlineColor);
            dc.DrawGeometry(outlineBrush, new Pen(outlineBrush, outlineWidth), geo);
            dc.DrawText(ft, new Point(x, y));
        }

        /// <summary>句首注释词（如"男："）的像素宽度</summary>
        private static double AnnotationWidth(LyricLine line, double fontSize)
        {
            int n = line.AnnotationChars;
            if (n <= 0) return 0;
            string text = line.FullText;
            if (text == null) return 0;
            if (n > text.Length) n = text.Length;
            if (n <= 0) return 0;
            FormattedText ft = BuildText(text.Substring(0, n), fontSize, Brushes.White);
            return ft.Width;
        }

        /// <summary>计算卡拉OK已唱部分的文字宽度（逐字测量，精确，从左往右累计）</summary>
        private double ComputeKaraokeWidth(LyricLine line, int posMs, double fontSize)
        {
            double width = 0;
            foreach (LyricWord word in line.Words)
            {
                FormattedText ftWord = BuildText(word.Text, fontSize, Brushes.White);
                if (posMs >= word.End)
                {
                    width += ftWord.Width; // 已唱完
                }
                else if (posMs >= word.Start)
                {
                    double progress = (posMs - word.Start) / (double)Math.Max(1, word.Duration);
                    if (progress > 1) progress = 1;
                    if (progress < 0) progress = 0;
                    width += ftWord.Width * progress; // 正在唱
                    break;
                }
                else
                {
                    break; // 未唱到
                }
            }
            return width;
        }

        /// <summary>构造文本</summary>
        private static FormattedText BuildText(string text, double fontSize, Brush brush)
        {
            return new FormattedText(text, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, FontTypeface, fontSize, brush);
        }
    }
}
