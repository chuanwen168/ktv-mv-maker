using System.Collections.Generic;

namespace KTV_MV_maker
{
    /// <summary>歌词单个字（含逐字时间轴）</summary>
    public class LyricWord
    {
        /// <summary>该字在整首歌中的起始时间（毫秒）</summary>
        public int Start;
        /// <summary>该字持续时长（毫秒）</summary>
        public int Duration;
        /// <summary>字文本</summary>
        public string Text;

        public LyricWord(int start, int duration, string text)
        {
            Start = start;
            Duration = duration;
            Text = text;
        }

        /// <summary>该字结束时间（毫秒）</summary>
        public int End { get { return Start + Duration; } }
    }

    /// <summary>歌词句子（KTV 双排歌词的基本单元）</summary>
    public class LyricLine
    {
        /// <summary>句子起始时间（毫秒）</summary>
        public int Start;
        /// <summary>句子持续时长（毫秒）</summary>
        public int Duration;
        /// <summary>句内逐字（含字级时间轴）</summary>
        public List<LyricWord> Words = new List<LyricWord>();
        /// <summary>是否为歌曲信息行（歌名-歌手、词/曲/编曲/混音等，非正式歌词）</summary>
        public bool IsMeta;
        /// <summary>开头作为演唱者/角色注释的字数（如“男：”“Selina：”），这些字不参与卡拉OK变色，保持默认底色</summary>
        public int AnnotationChars;

        public LyricLine(int start, int duration)
        {
            Start = start;
            Duration = duration;
        }

        /// <summary>整句文本（逐字拼接）</summary>
        public string FullText
        {
            get
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (LyricWord w in Words) sb.Append(w.Text);
                return sb.ToString();
            }
        }

        /// <summary>句子结束时间（毫秒）</summary>
        public int End { get { return Start + Duration; } }

        /// <summary>定位到指定时间（毫秒）所演唱的字；返回 null 表示尚未唱到或已唱完</summary>
        public LyricWord WordAt(int ms)
        {
            if (Words.Count == 0) return null;
            LyricWord current = null;
            foreach (LyricWord w in Words)
            {
                if (ms >= w.Start) current = w;
                else break;
            }
            return current;
        }
    }

    /// <summary>整首歌词</summary>
    public class KrcLyrics
    {
        /// <summary>歌曲名（ti 标签）</summary>
        public string SongName = "";
        /// <summary>歌手（ar 标签）</summary>
        public string Artist = "";
        /// <summary>全局时间偏移（毫秒，KRC timeline offset / LRC offset 标签）</summary>
        public int GlobalOffset = 0;
        /// <summary>歌词句子列表（按时间升序）</summary>
        public List<LyricLine> Lines = new List<LyricLine>();

        /// <summary>用户自定义的开场标题（为空时用"歌名 - 歌手"）</summary>
        public string DisplayTitle = "";

        /// <summary>开场居中显示的标题：优先用 DisplayTitle，否则回退到"歌名 - 歌手"</summary>
        public string EffectiveTitle
        {
            get
            {
                string custom = (DisplayTitle ?? "").Trim();
                if (custom.Length > 0) return custom;
                string s = (SongName ?? "").Trim();
                string a = (Artist ?? "").Trim();
                if (s.Length > 0 && a.Length > 0) return s + " - " + a;
                return s.Length > 0 ? s : a;
            }
        }

        /// <summary>定位指定时间（毫秒）正在演唱的句子；返回 null 表示无歌词或尚未开始</summary>
        public LyricLine LineAt(int ms)
        {
            int idx = IndexAt(ms);
            return idx < 0 ? null : Lines[idx];
        }

        /// <summary>定位指定时间（毫秒）正在演唱的句子索引；返回 -1 表示无歌词或尚未开始</summary>
        public int IndexAt(int ms)
        {
            int idx = -1;
            for (int n = 0; n < Lines.Count; n++)
            {
                if (ms >= Lines[n].Start) idx = n;
                else break;
            }
            return idx;
        }

        /// <summary>指定句子之后的下一个句子（用于双排歌词的"下一句"）</summary>
        public LyricLine NextLine(LyricLine line)
        {
            if (line == null || Lines.Count == 0) return null;
            int idx = Lines.IndexOf(line);
            if (idx < 0 || idx + 1 >= Lines.Count) return null;
            return Lines[idx + 1];
        }

        /// <summary>总字数</summary>
        public int WordCount
        {
            get
            {
                int n = 0;
                foreach (LyricLine l in Lines) n += l.Words.Count;
                return n;
            }
        }
    }
}
