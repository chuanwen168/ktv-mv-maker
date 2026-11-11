using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace KTV_MV_maker
{
    /// <summary>libvlc 原生 API（P/Invoke，仅用到播放/控制/字幕所需子集）</summary>
    internal static class VlcNative
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetDllDirectory(string path);

        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libvlc_new(int argc, string[] argv);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libvlc_media_new_path(IntPtr p_instance, IntPtr path);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libvlc_media_player_new_from_media(IntPtr p_md);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libvlc_media_player_set_hwnd(IntPtr p_mi, IntPtr drawable);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libvlc_media_player_play(IntPtr p_mi);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libvlc_media_player_pause(IntPtr p_mi);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libvlc_media_player_stop(IntPtr p_mi);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern long libvlc_media_player_get_time(IntPtr p_mi);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libvlc_media_player_set_time(IntPtr p_mi, long time);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern long libvlc_media_player_get_length(IntPtr p_mi);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libvlc_media_player_is_playing(IntPtr p_mi);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern int libvlc_media_player_add_slave(IntPtr p_mi, int type, IntPtr uri, int select);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libvlc_media_release(IntPtr p_md);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libvlc_media_player_release(IntPtr p_mi);
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libvlc_release(IntPtr p_instance);

        /// <summary>UTF-8 字节串 → 非托管内存指针（libvlc 路径/URI 均要求 UTF-8）</summary>
        public static IntPtr Utf8(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            IntPtr p = Marshal.AllocHGlobal(b.Length + 1);
            Marshal.Copy(b, 0, p, b.Length);
            Marshal.WriteByte(p, b.Length, 0);
            return p;
        }
    }

    /// <summary>视频嵌入宿主：WPF 子窗口句柄，供 libvlc 渲染视频画面</summary>
    public class VlcHost : HwndHost
    {
        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            IntPtr h = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE,
                0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return new HandleRef(this, h);
        }
        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            DestroyWindow(hwnd.Handle);
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            MoveWindow(Handle, 0, 0, (int)Math.Max(1, sizeInfo.NewSize.Width),
                (int)Math.Max(1, sizeInfo.NewSize.Height), true);
        }
        [DllImport("user32.dll")]
        static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    }

    /// <summary>
    /// libvlc 播放器封装：全格式内嵌播放（自带解码器，不依赖系统）、
    /// 进度读取/跳转、暂停/停止、ASS 卡拉OK字幕加载。
    /// 库文件存放于程序目录 libvlc\ 子目录（libvlc.dll + libvlccore.dll + plugins\，绿色便携）。
    /// </summary>
    public class VlcPlayer : IDisposable
    {
        private IntPtr _inst = IntPtr.Zero;
        private IntPtr _media = IntPtr.Zero;
        private IntPtr _mp = IntPtr.Zero;
        private string _videoPath;
        private string _playKey;

        /// <summary>宿主窗口句柄（由 VlcHost.Handle 提供）</summary>
        public IntPtr HostHandle { get; set; }

        /// <summary>libvlc 库是否就位（程序目录 libvlc\）</summary>
        public static bool IsAvailable()
        {
            return File.Exists(AppPaths.Combine("libvlc", "libvlc.dll"))
                && Directory.Exists(AppPaths.Combine("libvlc", "plugins"));
        }

        /// <summary>初始化 libvlc 实例（首次调用；可重复调用）</summary>
        public bool Initialize()
        {
            if (_inst != IntPtr.Zero) return true;
            if (!IsAvailable()) return false;
            VlcNative.SetDllDirectory(AppPaths.Combine("libvlc"));
            string[] argv = new string[] { "--no-video-title-show", "--quiet" };
            _inst = VlcNative.libvlc_new(2, argv);
            return _inst != IntPtr.Zero;
        }

        /// <summary>
        /// 播放视频（全格式）。assPath 非空时加载 ASS 卡拉OK字幕。
        /// key 为字幕内容的标识：同一个视频 + 同一份字幕时只从头重播，
        /// 不再反复 stop/新建播放器（libvlc 的同步 stop 会阻塞 UI，连续点击容易卡死）。
        /// </summary>
        public void Play(string videoPath, string assPath, string key)
        {
            key = key ?? "";
            if (_mp != IntPtr.Zero && _videoPath == videoPath && _playKey == key)
            {
                VlcNative.libvlc_media_player_set_time(_mp, 0);
                VlcNative.libvlc_media_player_play(_mp);
                return;
            }

            Stop();
            _videoPath = videoPath;
            _playKey = key;
            if (_inst == IntPtr.Zero) Initialize();
            if (_inst == IntPtr.Zero) return;

            IntPtr pPath = VlcNative.Utf8(videoPath);
            try
            {
                _media = VlcNative.libvlc_media_new_path(_inst, pPath);
            }
            finally
            {
                Marshal.FreeHGlobal(pPath);   // libvlc 已复制路径，托管侧必须释放
            }
            if (_media == IntPtr.Zero) return;
            _mp = VlcNative.libvlc_media_player_new_from_media(_media);
            if (_mp == IntPtr.Zero) return;

            if (HostHandle != IntPtr.Zero)
                VlcNative.libvlc_media_player_set_hwnd(_mp, HostHandle);

            if (!string.IsNullOrEmpty(assPath) && File.Exists(assPath))
            {
                string uri = "file:///" + assPath.Replace('\\', '/');
                IntPtr pUri = VlcNative.Utf8(uri);
                try
                {
                    VlcNative.libvlc_media_player_add_slave(_mp, 0 /*subtitle*/, pUri, 1);
                }
                finally
                {
                    Marshal.FreeHGlobal(pUri);
                }
            }

            VlcNative.libvlc_media_player_play(_mp);
        }

        public void Pause()
        {
            if (_mp != IntPtr.Zero) VlcNative.libvlc_media_player_pause(_mp);
        }

        public void Stop()
        {
            if (_mp != IntPtr.Zero)
            {
                VlcNative.libvlc_media_player_stop(_mp);
                VlcNative.libvlc_media_player_release(_mp);
                _mp = IntPtr.Zero;
            }
            if (_media != IntPtr.Zero)
            {
                VlcNative.libvlc_media_release(_media);
                _media = IntPtr.Zero;
            }
        }

        public long GetTime()
        {
            return _mp == IntPtr.Zero ? 0 : VlcNative.libvlc_media_player_get_time(_mp);
        }

        public long GetLength()
        {
            return _mp == IntPtr.Zero ? 0 : VlcNative.libvlc_media_player_get_length(_mp);
        }

        public void Seek(long ms)
        {
            if (_mp != IntPtr.Zero) VlcNative.libvlc_media_player_set_time(_mp, ms);
        }

        public bool IsPlaying()
        {
            return _mp != IntPtr.Zero && VlcNative.libvlc_media_player_is_playing(_mp) == 1;
        }

        public void Dispose()
        {
            Stop();
            if (_inst != IntPtr.Zero)
            {
                VlcNative.libvlc_release(_inst);
                _inst = IntPtr.Zero;
            }
        }
    }
}
