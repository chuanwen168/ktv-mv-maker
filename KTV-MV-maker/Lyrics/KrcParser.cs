using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace KTV_MV_maker
{
    /// <summary>
    /// KRC/KSC 歌词解析器。
    /// 输入为 KugouDecoder 插件解密后的明文文本，支持两种形态：
    ///  1) 新版 KRC：<krc> 开头的 XML（<timeline><s><l t="字偏移">字</l></s></timeline>）
    ///  2) 旧版 KSC：LRC 头 + [句开始,句时长]<字偏移,字长,0>字... 逐字增强格式
    /// </summary>
    public static class KrcParser
    {
        /// <summary>解析解密后的歌词文本</summary>
        public static KrcLyrics Parse(string decodedText)
        {
            if (string.IsNullOrEmpty(decodedText))
                throw new ArgumentException("歌词内容为空");

            string text = decodedText.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (text.StartsWith("<krc", StringComparison.OrdinalIgnoreCase))
                return ParseXml(text);
            return ParseKsc(text);
        }

        /// <summary>解析新版 KRC XML 格式</summary>
        private static KrcLyrics ParseXml(string xml)
        {
            KrcLyrics lyrics = new KrcLyrics();
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            XmlNode root = doc.SelectSingleNode("/krc");
            if (root == null) throw new ArgumentException("KRC XML 缺少 <krc> 根节点");

            XmlNode songname = root.SelectSingleNode("songname");
            if (songname != null) lyrics.SongName = songname.InnerText;

            XmlNode artist = root.SelectSingleNode("artist");
            if (artist != null) lyrics.Artist = artist.InnerText;

            XmlNode timeline = root.SelectSingleNode("timeline");
            if (timeline == null) throw new ArgumentException("KRC XML 缺少 <timeline> 节点");

            // 全局偏移（毫秒）
            XmlAttribute offsetAttr = timeline.Attributes["offset"];
            if (offsetAttr != null)
            {
                int.TryParse(offsetAttr.Value, out lyrics.GlobalOffset);
            }

            List<int> sentenceStarts = new List<int>();
            List<LyricLine> lines = new List<LyricLine>();

            XmlNodeList sentences = timeline.SelectNodes("s");
            foreach (XmlNode s in sentences)
            {
                XmlAttribute pAttr = s.Attributes["p"];
                if (pAttr == null) continue;
                int p;
                if (!int.TryParse(pAttr.Value, out p)) continue;

                LyricLine line = new LyricLine(p + lyrics.GlobalOffset, 0);
                foreach (XmlNode l in s.SelectNodes("l"))
                {
                    XmlAttribute tAttr = l.Attributes["t"];
                    int t = 0;
                    if (tAttr != null) int.TryParse(tAttr.Value, out t);
                    string wordText = l.InnerText;
                    if (wordText == null) wordText = "";
                    line.Words.Add(new LyricWord(p + lyrics.GlobalOffset + t, 0, wordText));
                }
                if (line.Words.Count > 0) lines.Add(line);
            }

            // 计算每字/每句时长：当前句结束时间取下一句开始时间（或最后字时间+估算）
            for (int i = 0; i < lines.Count; i++)
            {
                LyricLine line = lines[i];
                int sentenceEnd = (i + 1 < lines.Count) ? lines[i + 1].Start : line.Words[line.Words.Count - 1].Start + 2000;
                // 句子时长
                line.Duration = Math.Max(0, sentenceEnd - line.Start);
                // 逐字时长
                for (int j = 0; j < line.Words.Count; j++)
                {
                    LyricWord w = line.Words[j];
                    int wordEnd = (j + 1 < line.Words.Count) ? line.Words[j + 1].Start : sentenceEnd;
                    w.Duration = Math.Max(1, wordEnd - w.Start);
                }
            }

            lyrics.Lines = lines;
            MarkMetaLines(lyrics);
            DetectAnnotations(lyrics);
            MergeLabelOnlyLines(lyrics);
            return lyrics;
        }

        /// <summary>
        /// 标记歌曲信息行（歌名-歌手、词/曲/编曲/混音/母带/和声等），
        /// 这类行不是正式歌词：开头作为"标题+作词作曲"静态显示，不参与卡拉OK交替。
        /// 只在开头连续的"头部块"内识别，避免把正文里恰好等于歌名的句子（如副歌反复）误标成标题。
        /// </summary>
        private static void MarkMetaLines(KrcLyrics lyrics)
        {
            if (lyrics == null || lyrics.Lines == null) return;
            string songFull = NormalizeKey(lyrics.SongName);
            string songBase = NormalizeKey(StripBrackets(lyrics.SongName));
            List<string> artists = ArtistTokens(lyrics.Artist);
            string[] prefixes = new string[] {
                "词：", "曲：", "编曲：", "混音：", "母带：", "母带处理：", "和声：", "和声编写：",
                "录音：", "录音室：", "混音室：", "制作：", "制作人：", "监制：", "出品：", "出品人：", "统筹：", "企划：", "OP：", "SP：",
                "文案：", "古筝：", "古筝编写：", "吉他：", "吉他编写：", "贝斯：", "贝斯编写：", "钢琴：", "钢琴编写：",
                "二胡：", "二胡编写：", "笛子：", "笛子编写：", "鼓：", "鼓编写：", "小提琴：", "小提琴编写：",
                "大提琴：", "大提琴编写：", "琵琶：", "琵琶编写：", "萨克斯：", "萨克斯编写：", "键盘：", "键盘编写：",
                "作词：", "作曲：", "编曲人：", "人声：", "人声录音：", "发行：", "特别鸣谢：",
                "词曲提供：", "营销推广：", "推广：", "宣发：", "版权：", "版权管理：", "音乐发行：",
                "总策划：", "策划：", "封面设计：", "视觉：", "后期：", "母带工作室：", "发行公司："
            };

            bool inHeader = true;
            bool firstNonEmpty = false;
            foreach (LyricLine line in lyrics.Lines)
            {
                string t = line.FullText.Trim();
                bool candidate = t.Length == 0 || IsMetaText(t, songFull, songBase, artists, prefixes);
                // 元数据为空（ar/ti 都是空）时，首行"某某 - 某某"按标题行处理
                if (inHeader && !candidate && !firstNonEmpty)
                    candidate = LooksLikeTitleLine(t, songBase, artists);
                if (t.Length > 0) firstNonEmpty = true;
                if (inHeader && candidate)
                {
                    line.IsMeta = true;
                }
                else
                {
                    line.IsMeta = false;
                    inHeader = false;   // 头部块结束，后面的行一律按正文处理
                }
            }
        }

        /// <summary>
        /// 判断是否为"歌手 - 歌名"形式的标题行。
        /// 兼容多歌手（"庄心妍、祁隆 - 一万个舍不得"）、歌名带序号（ti="两只老虎 01"、标题="两只老虎 - 贝瓦儿歌"），
        /// 以及 ar/ti 都为空、只能从首行推断标题的 KRC。
        /// </summary>
        private static bool LooksLikeTitleLine(string t, string songBase, List<string> artists)
        {
            string key = NormalizeKey(t);
            if (key.Length == 0 || key.Length > 40) return false;
            if (key.IndexOf('，') >= 0 || key.IndexOf('。') >= 0 || key.IndexOf('！') >= 0 ||
                key.IndexOf('？') >= 0 || key.IndexOf('：') >= 0) return false;
            int dash = key.IndexOf('-');
            if (dash <= 0 || dash >= key.Length - 1) return false;
            string left = key.Substring(0, dash);
            string right = key.Substring(dash + 1);
            if (left.Length < 2 || right.Length < 2) return false;

            foreach (string a in artists)
            {
                if (left == a || right == a) return true;
            }
            if (songBase.Length >= 2)
            {
                if (left == songBase || right == songBase) return true;
                if (left.StartsWith(songBase, StringComparison.Ordinal) || songBase.StartsWith(left, StringComparison.Ordinal)) return true;
                if (right.StartsWith(songBase, StringComparison.Ordinal) || songBase.StartsWith(right, StringComparison.Ordinal)) return true;
            }
            // 歌曲元数据为空时（ar/ti 缺失），首行含" - "且左右都像名字，直接按标题行
            return songBase.Length == 0 && artists.Count == 0;
        }

        /// <summary>判断一行文本是否属于歌曲信息（标题/词曲等）</summary>
        private static bool IsMetaText(string t, string songFull, string songBase, List<string> artists, string[] prefixes)
        {
            string key = NormalizeKey(t);
            if (key.Length == 0) return true;
            if (key == songFull || key == songBase) return true;
            foreach (string a in artists)
            {
                if (key == a) return true;
                // 标题行两种顺序："歌手 - 歌名" / "歌名 - 歌手"（含括号副标题的全称也匹配）
                if (key == a + "-" + songBase || key == songBase + "-" + a) return true;
                if (songFull.Length > 0 && (key == a + "-" + songFull || key == songFull + "-" + a)) return true;
            }
            // 多歌手（"庄心妍、祁隆 - 一万个舍不得"）：只要破折号一侧是歌名即可
            int dash = key.IndexOf('-');
            if (dash > 0 && dash < key.Length - 1 && songBase.Length >= 2)
            {
                string left = key.Substring(0, dash);
                string right = key.Substring(dash + 1);
                if (left == songBase || right == songBase) return true;
            }
            foreach (string p in prefixes)
            {
                if (key.StartsWith(p, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 识别句首的演唱者/角色注释（"男：" "女：" "Selina：" "S.H.E：" 等），
        /// 注释部分不参与卡拉OK，保持默认底色。以冒号作为分隔标记。
        /// </summary>
        public static void DetectAnnotations(KrcLyrics lyrics)
        {
            if (lyrics == null || lyrics.Lines == null) return;
            foreach (LyricLine line in lyrics.Lines)
            {
                line.AnnotationChars = 0;
                if (line.IsMeta) continue;
                string t = line.FullText;
                if (string.IsNullOrEmpty(t)) continue;
                int colon = -1;
                int max = Math.Min(t.Length, 15);
                for (int i = 0; i < max; i++)
                {
                    char c = t[i];
                    if (c == '：' || c == ':') { colon = i; break; }
                    // 句读先出现说明不是演唱者注释（如"他说，..."）
                    if (c == '，' || c == '。' || c == '！' || c == '？' || c == '；' || c == '、') break;
                }
                if (colon <= 0) continue;
                string prefix = t.Substring(0, colon).Trim();
                if (!IsSpeakerLabel(prefix)) continue;
                line.AnnotationChars = colon + 1;   // 含冒号
            }
        }

        /// <summary>
        /// 把"只有演唱者标签、后面没有正文"的行（如单独的 "Miko：" "Yumi：" "合："）
        /// 并入下一句正文的开头，显示成 "Miko：把爱留在身边"。
        /// 标签部分按注释词处理（不参与卡拉OK变色），这样既保留演唱者提示，
        /// 又不会白白占掉一整行、打乱上下排交替。
        /// 带正文的（"Miko：xxx"）不在此处处理，由 DetectAnnotations 标记注释词。
        /// </summary>
        public static void MergeLabelOnlyLines(KrcLyrics lyrics)
        {
            if (lyrics == null || lyrics.Lines == null) return;
            string pending = "";
            int i = 0;
            while (i < lyrics.Lines.Count)
            {
                LyricLine line = lyrics.Lines[i];
                if (line.IsMeta) { i++; continue; }
                string label = LabelOnlyText(line);
                if (label != null)
                {
                    pending += label;               // 连续多个标签会按原顺序拼接
                    lyrics.Lines.RemoveAt(i);
                    continue;
                }
                if (pending.Length > 0)
                {
                    if (!line.FullText.StartsWith(pending))
                    {
                        // 标签作为零时长的字插入句首，只用于显示，不参与扫过计时
                        line.Words.Insert(0, new LyricWord(line.Start, 0, pending));
                    }
                    pending = "";
                }
                i++;
            }
            // 重新计算注释字数（含刚并入的标签）
            DetectAnnotations(lyrics);
        }

        /// <summary>若整行只有演唱者标签（如 "Miko："），返回该标签文本（含冒号）；否则返回 null</summary>
        private static string LabelOnlyText(LyricLine line)
        {
            string t = line.FullText;
            if (string.IsNullOrEmpty(t)) return null;
            string s = t.Trim();
            int colon = -1;
            int max = Math.Min(s.Length, 15);
            for (int n = 0; n < max; n++)
            {
                char c = s[n];
                if (c == '：' || c == ':') { colon = n; break; }
                if ("，。！？；、,.!?;".IndexOf(c) >= 0) return null;
            }
            if (colon <= 0) return null;
            string prefix = s.Substring(0, colon).Trim();
            string rest = s.Substring(colon + 1).Trim();
            if (rest.Length > 0) return null;       // 冒号后有正文 → 正常的注释词行
            if (!IsSpeakerLabel(prefix)) return null;
            return s.Substring(0, colon + 1);
        }

        /// <summary>判断冒号前的文本是否像演唱者/角色标签（男、女、合、Selina、S.H.E 等）</summary>
        private static bool IsSpeakerLabel(string prefix)
        {
            if (prefix.Length == 0 || prefix.Length > 12) return false;
            bool hasWord = false;
            foreach (char c in prefix)
            {
                if (char.IsWhiteSpace(c)) return false;          // 名字里一般不带空格，避免把句子前半段误判
                if (char.IsDigit(c)) return false;
                if ("，。！？；、,.!?;".IndexOf(c) >= 0) return false;
                if (char.IsLetter(c)) hasWord = true;
            }
            if (!hasWord) return false;
            string k = NormalizeKey(prefix);
            // 词曲/制作等署名不是演唱者，避免把"词：祁隆"当成注释词
            string[] credit = new string[] {
                "词", "曲", "编曲", "作词", "作曲", "混音", "母带", "母带处理", "和声", "和声编写",
                "录音", "录音室", "混音室", "制作", "制作人", "监制", "出品", "出品人", "统筹", "企划",
                "文案", "古筝", "古筝编写", "吉他", "吉他编写", "贝斯", "贝斯编写", "钢琴", "钢琴编写",
                "二胡", "二胡编写", "笛子", "笛子编写", "鼓", "鼓编写", "小提琴", "小提琴编写",
                "大提琴", "大提琴编写", "琵琶", "琵琶编写", "萨克斯", "萨克斯编写", "键盘", "键盘编写",
                "发行", "词曲提供", "营销推广", "推广", "宣发", "版权", "版权管理", "音乐发行",
                "总策划", "策划", "封面设计", "视觉", "后期", "特别鸣谢", "编曲人", "人声", "人声录音"
            };
            foreach (string c in credit) if (k == c) return false;
            string[] black = new string[] {
                "我", "你", "他", "她", "它", "我们", "你们", "他们", "她们", "咱们",
                "这", "那", "这个", "那个", "这里", "那里",
                "我说", "你说", "他说", "她说", "它说", "听说", "据说",
                "如果", "因为", "所以", "但是", "可是", "然后", "于是", "就是", "只是", "还有",
                "时间", "地点", "注意", "提示", "歌词", "字幕"
            };
            foreach (string b in black) if (k == b) return false;
            return true;
        }

        /// <summary>去掉括号内容（( ) （ ） [ ] 【 】 { }），用于比较标题简称</summary>
        private static string StripBrackets(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            char[] opens = new char[] { '(', '（', '[', '【', '{' };
            char[] closes = new char[] { ')', '）', ']', '】', '}' };
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < opens.Length; i++)
                {
                    int a = s.IndexOf(opens[i]);
                    while (a >= 0)
                    {
                        int b = s.IndexOf(closes[i], a + 1);
                        if (b < 0) break;
                        s = s.Remove(a, b - a + 1);
                        changed = true;
                        a = s.IndexOf(opens[i], a);
                    }
                }
            }
            return s.Trim();
        }

        /// <summary>歌手字段拆分（音阙诗听/赵方婧 → 两个名字），并规范化</summary>
        private static List<string> ArtistTokens(string artist)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(artist)) return list;
            string[] parts = artist.Split(new char[] { '/', '、', '，', ',', ';', '；', '&' });
            foreach (string p in parts)
            {
                string k = NormalizeKey(p);
                if (k.Length > 0 && !list.Contains(k)) list.Add(k);
            }
            return list;
        }

        /// <summary>比较用规范化：去空白、统一冒号/破折号</summary>
        private static string NormalizeKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (c == ':') { sb.Append('：'); continue; }
                if (c == '－' || c == '—' || c == '–' || c == '~' || c == '～') { sb.Append('-'); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>解析旧版 KSC 增强格式：LRC 头 + [句开始,句时长]<字偏移,字长,0>字...</summary>
        private static KrcLyrics ParseKsc(string text)
        {
            KrcLyrics lyrics = new KrcLyrics();
            Regex headRegex = new Regex(@"^\[(\w+):(.*?)\]$", RegexOptions.Compiled);
            // 逐字行：[start,dur]<off,len,flag>字<...>
            Regex lineRegex = new Regex(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled);
            Regex wordRegex = new Regex(@"<(\d+),(\d+),\d*>(.*?)(?=<|$)", RegexOptions.Compiled);

            int offset = 0;
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            List<LyricLine> result = new List<LyricLine>();

            foreach (string rawLine in lines)
            {
                string lineText = rawLine.Trim();
                if (lineText.Length == 0) continue;

                // LRC 头标签
                Match headMatch = headRegex.Match(lineText);
                if (headMatch.Success)
                {
                    string tag = headMatch.Groups[1].Value.ToLowerInvariant();
                    string value = headMatch.Groups[2].Value;
                    switch (tag)
                    {
                        case "ti": lyrics.SongName = value; break;
                        case "ar": lyrics.Artist = value; break;
                        case "offset":
                            int.TryParse(value, out offset);
                            lyrics.GlobalOffset = offset;
                            break;
                    }
                    continue;
                }

                // 逐字增强行
                Match lineMatch = lineRegex.Match(lineText);
                if (lineMatch.Success)
                {
                    int start = int.Parse(lineMatch.Groups[1].Value) + offset;
                    int duration = int.Parse(lineMatch.Groups[2].Value);
                    LyricLine line = new LyricLine(start, duration);
                    string body = lineMatch.Groups[3].Value;

                    MatchCollection wordMatches = wordRegex.Matches(body);
                    if (wordMatches.Count > 0)
                    {
                        foreach (Match wm in wordMatches)
                        {
                            int wOffset = int.Parse(wm.Groups[1].Value);
                            int wDuration = int.Parse(wm.Groups[2].Value);
                            string wText = wm.Groups[3].Value;
                            if (wText.Length == 0) continue;
                            line.Words.Add(new LyricWord(start + wOffset, Math.Max(1, wDuration), wText));
                        }
                    }

                    if (line.Words.Count > 0) result.Add(line);
                    continue;
                }

                // 兼容纯 LRC 行：[mm:ss.xx] 文本（无逐字时间，整句为一个"字"，唱时整句渐变）
                Match lrcMatch = Regex.Match(lineText, @"^\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\](.*)$");
                if (lrcMatch.Success)
                {
                    int min = int.Parse(lrcMatch.Groups[1].Value);
                    int sec = int.Parse(lrcMatch.Groups[2].Value);
                    int ms = lrcMatch.Groups[3].Success ? int.Parse(lrcMatch.Groups[3].Value.PadRight(3, '0')) : 0;
                    int start = min * 60000 + sec * 1000 + ms + offset;
                    string content = lrcMatch.Groups[4].Value.Trim();
                    if (content.Length == 0) continue;
                    LyricLine line = new LyricLine(start, 0);
                    line.Words.Add(new LyricWord(start, 0, content));
                    result.Add(line);
                }
            }

            // 补算缺失的句子/字时长：仅当 KSC 行未给出时长（如纯 LRC 行）时计算
            for (int i = 0; i < result.Count; i++)
            {
                LyricLine line = result[i];
                if (line.Duration <= 0)
                {
                    int end = (i + 1 < result.Count) ? result[i + 1].Start : line.Words[0].Start + 3000;
                    line.Duration = Math.Max(0, end - line.Start);
                }
                for (int j = 0; j < line.Words.Count; j++)
                {
                    LyricWord w = line.Words[j];
                    if (w.Duration <= 0)
                    {
                        int wordEnd = (j + 1 < line.Words.Count) ? line.Words[j + 1].Start : line.End;
                        w.Duration = Math.Max(1, wordEnd - w.Start);
                    }
                }
            }

            lyrics.Lines = result;
            MarkMetaLines(lyrics);
            DetectAnnotations(lyrics);
            MergeLabelOnlyLines(lyrics);
            return lyrics;
        }
    }
}
