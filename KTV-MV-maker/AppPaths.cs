using System;
using System.IO;

namespace KTV_MV_maker
{
    /// <summary>
    /// 便携版路径：所有资源（ffmpeg/ffprobe/ffplay/plugins/libvlc/mv_tmp）都相对程序 exe 所在目录，
    /// 整个工具文件夹可复制到任意电脑任意路径直接运行（绿色便携版）。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>程序 exe 所在目录（带结尾分隔符）</summary>
        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>相对 exe 目录组合路径</summary>
        public static string Combine(string name)
        {
            return Path.Combine(BaseDir, name);
        }

        /// <summary>相对 exe 目录多级组合路径</summary>
        public static string Combine(string sub, string name)
        {
            return Path.Combine(Path.Combine(BaseDir, sub), name);
        }
    }
}
