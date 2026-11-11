using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KTV_MV_maker
{
    /// <summary>
    /// 简体 → 繁体转换（OpenCC 词表，纯 C# 实现，无外部依赖）。
    /// 先用 STPhrases 词组表做最长匹配（解决"头发→頭發"这类单字直转会出错的词），
    /// 词组未命中时再逐字查 STCharacters 单字表。
    /// 词表随程序携带：程序目录 opencc\STCharacters.txt、opencc\STPhrases.txt。
    /// </summary>
    public static class ChineseConverter
    {
        private static Dictionary<string, string> _phrases;
        private static Dictionary<char, char> _chars;
        private static int _maxPhraseLen = 1;
        private static bool _loaded;
        private static string _loadError;

        /// <summary>词表加载失败原因（为空表示正常）</summary>
        public static string LastError { get { return _loadError; } }

        /// <summary>是否已成功加载词表</summary>
        public static bool IsReady
        {
            get { EnsureLoaded(); return _loadError == null; }
        }

        /// <summary>加载词表（只加载一次，失败可重试）</summary>
        public static bool EnsureLoaded()
        {
            if (_loaded) return _loadError == null;
            _loaded = true;
            _loadError = null;
            try
            {
                string dir = AppPaths.Combine("opencc");
                string charsPath = Path.Combine(dir, "STCharacters.txt");
                string phrasesPath = Path.Combine(dir, "STPhrases.txt");

                _chars = new Dictionary<char, char>(40000);
                _phrases = new Dictionary<string, string>(60000);

                if (File.Exists(charsPath))
                {
                    foreach (string line in File.ReadAllLines(charsPath, Encoding.UTF8))
                        ParseLine(line, false);
                }
                if (File.Exists(phrasesPath))
                {
                    foreach (string line in File.ReadAllLines(phrasesPath, Encoding.UTF8))
                        ParseLine(line, true);
                }

                if (_chars.Count == 0 && _phrases.Count == 0)
                {
                    _loadError = "未找到 OpenCC 词表：请在程序目录放置 opencc\\STCharacters.txt 与 opencc\\STPhrases.txt";
                    _loaded = false;
                    return false;
                }

                // 最长词长（超过 16 字的词条忽略，避免逐字尝试时反复 Substring 变慢）
                foreach (KeyValuePair<string, string> kv in _phrases)
                {
                    if (kv.Key.Length > _maxPhraseLen && kv.Key.Length <= 16)
                        _maxPhraseLen = kv.Key.Length;
                }
                return true;
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                _loaded = false;
                return false;
            }
        }

        /// <summary>解析词表行：key&lt;TAB&gt;value1 value2 ...（取第一个值；# 开头为注释）</summary>
        private static void ParseLine(string line, bool phrase)
        {
            if (line == null || line.Length == 0) return;
            if (line[0] == '#') return;
            int tab = line.IndexOf('\t');
            if (tab <= 0) return;
            string key = line.Substring(0, tab).Trim();
            string rest = line.Substring(tab + 1).Trim();
            if (key.Length == 0 || rest.Length == 0) return;
            int sp = rest.IndexOf(' ');
            if (sp < 0) sp = rest.IndexOf('\t');
            string val = sp > 0 ? rest.Substring(0, sp) : rest;
            if (val.Length == 0) return;

            if (phrase)
            {
                if (key.Length >= 2) _phrases[key] = val;
            }
            else
            {
                // 单字表：一对一映射（多字对应多字的情形交给词组表处理）
                if (key.Length == 1 && val.Length == 1) _chars[key[0]] = val[0];
            }
        }

        /// <summary>简体字符串 → 繁体字符串（已繁体或无需转换的字符原样保留）</summary>
        public static string Convert(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!EnsureLoaded()) return text;

            StringBuilder sb = new StringBuilder(text.Length + 8);
            int i = 0;
            int n = text.Length;
            while (i < n)
            {
                bool matched = false;
                // 先按最长词组匹配
                if (_maxPhraseLen >= 2 && _phrases != null && _phrases.Count > 0)
                {
                    int maxLen = Math.Min(_maxPhraseLen, n - i);
                    for (int len = maxLen; len >= 2; len--)
                    {
                        string key = text.Substring(i, len);
                        string val = null;
                        if (_phrases.TryGetValue(key, out val) && val.Length > 0)
                        {
                            sb.Append(val);
                            i += len;
                            matched = true;
                            break;
                        }
                    }
                }
                if (matched) continue;

                // 词组未命中：逐字映射
                char c = text[i];
                char v;
                if (_chars != null && _chars.TryGetValue(c, out v)) sb.Append(v);
                else sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
