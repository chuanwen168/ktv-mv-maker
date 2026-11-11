using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace KTV_MV_maker
{
    /// <summary>
    /// KRC 解密插件加载器：通过反射加载 KugouDecoder.dll 插件。
    /// 插件为 GitHub 开源项目 WXRIW/Kugou-Lyrics-Decoder（MIT License）的 .NET 3.5 移植版，
    /// 存放于程序目录 plugins\KugouDecoder.dll（绿色便携，与 exe 同目录）。
    /// </summary>
    public static class KrcDecoderLoader
    {
        /// <summary>插件候选路径（程序目录 plugins\ 优先，其次程序运行目录）</summary>
        public static readonly string[] CandidatePaths = new string[]
        {
            AppPaths.Combine("plugins", "KugouDecoder.dll"),
            AppPaths.Combine("KugouDecoder.dll")
        };

        /// <summary>实际加载成功的插件路径</summary>
        public static string LoadedDllPath = "";
        /// <summary>最近一次加载错误信息</summary>
        public static string LastError = "";

        /// <summary>插件文件是否就位（仅检查存在性，不加载程序集）</summary>
        public static bool PluginAvailable()
        {
            foreach (string p in CandidatePaths)
            {
                if (File.Exists(p)) return true;
            }
            return false;
        }

        /// <summary>
        /// 取得歌词明文，供解析器使用：
        ///  1) 插件在 → 用插件解密 KRC；
        ///  2) 插件不在（或解密失败）→ 若文件本身已是明文歌词，直接加载；
        ///  3) 仍是加密内容 → 提示需要插件（作者默认不随包提供解码插件）。
        /// </summary>
        public static string LoadPlainText(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("歌词文件不存在：" + filePath);

            Exception pluginError = null;
            if (PluginAvailable())
            {
                try
                {
                    return DecryptFile(filePath);
                }
                catch (Exception ex)
                {
                    pluginError = ex;
                }
            }

            string text = ReadTextFile(filePath);
            if (text != null && IsPlainLyric(text)) return text;

            throw new InvalidOperationException(
                "需要插件支持，作者默认不提供解码插件。\n" +
                "（把 KugouDecoder.dll 放到程序目录 plugins\\ 下即可解码；或改用未加密的明文歌词文件）" +
                (pluginError == null ? "" : "\n插件解密失败：" + pluginError.Message));
        }

        /// <summary>
        /// 判断文本是否为明文歌词（未加密的 KRC XML、KSC 增强歌词或普通文本歌词）。
        /// 加密后的 KRC 是二进制，UTF-8 严格解码会失败，或内容里没有任何歌词结构标签。
        /// </summary>
        public static bool IsPlainLyric(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (t.StartsWith("<krc", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.IndexOf('[') < 0 || t.IndexOf(']') < 0) return false;
            string head = t.Length > 2000 ? t.Substring(0, 2000) : t;
            // 头部标签（[ti:...]）或逐字行（[0,1000]<...>）
            return Regex.IsMatch(head, @"\[\s*(ti|ar|al|by|offset)\s*:", RegexOptions.IgnoreCase)
                || Regex.IsMatch(head, @"\[\s*\d+\s*,\s*\d+\s*\]");
        }

        /// <summary>读取文本文件：优先严格 UTF-8，回退 GB18030（国内歌词常见），再回退默认编码</summary>
        public static string ReadTextFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            }
            catch (DecoderFallbackException) { }
            try
            {
                return Encoding.GetEncoding(936).GetString(bytes);
            }
            catch (Exception) { }
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>解密 KRC 歌词文件，返回明文歌词文本（供解析器使用）。</summary>
        public static string DecryptFile(string filePath)
        {
            Assembly asm = LoadPlugin();
            if (asm == null)
                throw new InvalidOperationException("未加载 KugouDecoder.dll 插件：\n" + LastError);

            Type type = asm.GetType("KugouDecoder.KrcDecrypt", false);
            if (type == null)
                throw new InvalidOperationException("插件中未找到类型 KugouDecoder.KrcDecrypt");

            MethodInfo method = type.GetMethod("DecryptFile", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                throw new InvalidOperationException("插件中未找到静态方法 DecryptFile");

            return (string)method.Invoke(null, new object[] { filePath });
        }

        /// <summary>加载插件程序集；失败时返回 null 并记录错误。</summary>
        public static Assembly LoadPlugin()
        {
            foreach (string path in CandidatePaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        Assembly asm = Assembly.LoadFrom(path);
                        LoadedDllPath = path;
                        LastError = "";
                        return asm;
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                    }
                }
            }
            LastError = "插件文件不存在，应位于：\n" + string.Join("\n", CandidatePaths);
            return null;
        }
    }
}
