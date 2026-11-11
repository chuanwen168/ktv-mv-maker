using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace KTV_MV_maker
{
    /// <summary>
    /// 主窗口：选择视频与 KRC 歌词 → 预览双排歌词 → 调用 ffmpeg 合成带卡拉OK字幕的 MV。
    /// 预览：LibVLC 内嵌播放（自带解码器，全格式，进度条/歌词同步）；
    /// 备用：ffplay 外部预览（独立窗口）。
    /// </summary>
    public partial class Window1 : Window
    {
        private KrcLyrics _lyrics;              // 已解析的歌词
        private DispatcherTimer _timer;         // 预览刷新定时器
        private bool _seeking;                  // 用户正在拖动进度条
        private bool _exporting;                // 正在合成
        private Process _ffplayProcess;         // ffplay 外部预览进程（独立窗口）
        private VlcPlayer _vlc = new VlcPlayer();// LibVLC 内嵌播放器（全格式解码）
        private MvExporter _exporter = new MvExporter();

        // 歌词自定义样式（默认：未唱白色、已唱金色）
        private System.Drawing.Color _textColor = System.Drawing.Color.White;
        private System.Drawing.Color _karaokeColor = System.Drawing.Color.FromArgb(0xFF, 0xB8, 0x00);
        // 开唱倒计时图案颜色（默认青蓝，独立于未唱/已唱色）
        private System.Drawing.Color _countdownColor = System.Drawing.Color.FromArgb(0x00, 0xA8, 0xFF);
        // 开场标题/词曲信息行颜色（默认白色，独立于正文歌词）
        private System.Drawing.Color _titleColor = System.Drawing.Color.White;
        /// <summary>歌词列表焦点由程序同步时置 true，避免触发"点击跳转"</summary>
        private bool _lyricListSync;
        /// <summary>正在加载视频预览（防止连续点击重复创建播放器导致卡死）</summary>
        private bool _playBusy;
        // 视频探测结果缓存（ffprobe 每次都启动进程，连续点播放会卡死界面，这里只探一次）
        private string _probePath;
        private int _probeW, _probeH;
        private double _probeDur;

        public Window1()
        {
            InitializeComponent();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(80);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            slider.Minimum = 0;
            slider.Maximum = 1000;
            slider.Value = 0;

            // 绿色便携：ffmpeg 默认取程序目录
            txtFfmpeg.Text = AppPaths.Combine("ffmpeg.exe");

            if (!VlcPlayer.IsAvailable())
            {
                txtStatus.Text = "提示：未找到播放库（程序目录 libvlc\\，LibVLC），播放预览不可用（合成不受影响）。";
            }

            // 初始化歌词样式按钮显示
            UpdateColorButton(btnTextColor, _textColor, "未唱");
            UpdateColorButton(btnKaraokeColor, _karaokeColor, "已唱");
            UpdateColorButton(btnCountdownColor, _countdownColor, "倒计时");
            UpdateColorButton(btnTitleColor, _titleColor, "标题");
            UpdateEffectHint();
        }

        // ---------------- 卡拉OK特效 / 开唱倒计时 ----------------

        /// <summary>当前选择的卡拉OK特效</summary>
        private KaraokeEffect ReadEffect()
        {
            if (cmbEffect == null) return KaraokeEffect.Sweep;
            switch (cmbEffect.SelectedIndex)
            {
                case 0: return KaraokeEffect.Sweep;
                case 1: return KaraokeEffect.Fade;
                default: return KaraokeEffect.Clip;
            }
        }

        /// <summary>倒计时图案（空则回退为 ★；最多 3 个字符）</summary>
        private string ReadCountdownChar()
        {
            string s = txtCountdownChar == null ? "" : txtCountdownChar.Text.Trim();
            if (s.Length == 0) s = "★";
            if (s.Length > 3) s = s.Substring(0, 3);
            return s;
        }

        private void CmbEffect_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateEffectHint();
        }

        /// <summary>特效下拉旁的说明文字（选"扫过"时提示预览端可能不显示该效果）</summary>
        private void UpdateEffectHint()
        {
            if (txtEffectHint == null) return;
            switch (ReadEffect())
            {
                case KaraokeEffect.Sweep:
                    txtEffectHint.Text = "扫过：金色从左往右刷过；预览可能不显示，以导出结果为准";
                    break;
                case KaraokeEffect.Fade:
                    txtEffectHint.Text = "渐变：每个字在自己演唱时长内由白渐金";
                    break;
                default:
                    txtEffectHint.Text = "跳过：唱到的字整字瞬间变金（预览与导出一致）";
                    break;
            }
        }

        /// <summary>选择倒计时图案颜色</summary>
        private void BtnCountdownColor_Click(object sender, RoutedEventArgs e)
        {
            using (WinForms.ColorDialog dlg = new WinForms.ColorDialog())
            {
                dlg.Color = _countdownColor;
                dlg.FullOpen = true;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                {
                    _countdownColor = dlg.Color;
                    UpdateColorButton(btnCountdownColor, _countdownColor, "倒计时");
                }
            }
        }

        // ---------------- 歌词样式自定义 ----------------

        /// <summary>字号滑块变化 → 更新数值显示</summary>
        private void SldFont_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (txtFontVal != null)
            {
                txtFontVal.Text = (int)sldFont.Value + "px";
            }
        }

        /// <summary>滑块与秒数输入框联动标志，避免互相触发形成循环</summary>
        private bool _offsetSync = false;

        /// <summary>歌词整体时间偏移滑块：拖动后同步显示到秒数输入框</summary>
        private void SldOffset_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_offsetSync) return;
            if (txtOffsetSec == null) return;
            _offsetSync = true;
            try
            {
                double sec = sldOffset.Value / 1000.0;
                txtOffsetSec.Text = (sec > 0 ? "+" : "") + sec.ToString("0.0");
            }
            finally { _offsetSync = false; }
        }

        /// <summary>秒数输入框失焦：解析后同步到滑块（超范围时滑块夹取，但导出仍以输入值为准）</summary>
        private void TxtOffsetSec_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_offsetSync) return;
            double sec = ParseOffsetSec();
            _offsetSync = true;
            try
            {
                double ms = sec * 1000.0;
                if (ms < sldOffset.Minimum) ms = sldOffset.Minimum;
                if (ms > sldOffset.Maximum) ms = sldOffset.Maximum;
                sldOffset.Value = ms;
            }
            finally { _offsetSync = false; }
        }

        /// <summary>解析秒数输入框（支持 +/- 与小数），失败返回 0</summary>
        private double ParseOffsetSec()
        {
            if (txtOffsetSec == null) return 0;
            string t = txtOffsetSec.Text.Trim();
            if (t.StartsWith("+")) t = t.Substring(1);
            double sec;
            if (double.TryParse(t, out sec)) return sec;
            return 0;
        }

        /// <summary>读取当前歌词偏移（毫秒）：以秒数输入框为准，取不到则用滑块值</summary>
        private int ReadOffsetMs()
        {
            if (txtOffsetSec != null && txtOffsetSec.Text.Trim().Length > 0)
                return (int)Math.Round(ParseOffsetSec() * 1000.0);
            return (int)Math.Round(sldOffset.Value);
        }

        /// <summary>选择开场标题/词曲信息行颜色</summary>
        private void BtnTitleColor_Click(object sender, RoutedEventArgs e)
        {
            using (WinForms.ColorDialog dlg = new WinForms.ColorDialog())
            {
                dlg.Color = _titleColor;
                dlg.FullOpen = true;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                {
                    _titleColor = dlg.Color;
                    UpdateColorButton(btnTitleColor, _titleColor, "标题");
                }
            }
        }

        /// <summary>选择未唱部分歌词颜色</summary>
        private void BtnTextColor_Click(object sender, RoutedEventArgs e)
        {
            using (WinForms.ColorDialog dlg = new WinForms.ColorDialog())
            {
                dlg.Color = _textColor;
                dlg.FullOpen = true;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                {
                    _textColor = dlg.Color;
                    UpdateColorButton(btnTextColor, _textColor, "未唱");
                }
            }
        }

        /// <summary>选择卡拉OK已唱部分颜色</summary>
        private void BtnKaraokeColor_Click(object sender, RoutedEventArgs e)
        {
            using (WinForms.ColorDialog dlg = new WinForms.ColorDialog())
            {
                dlg.Color = _karaokeColor;
                dlg.FullOpen = true;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                {
                    _karaokeColor = dlg.Color;
                    UpdateColorButton(btnKaraokeColor, _karaokeColor, "已唱");
                }
            }
        }

        /// <summary>刷新颜色按钮（背景色 + 色值文本 + 提示）</summary>
        private void UpdateColorButton(System.Windows.Controls.Button btn, System.Drawing.Color c, string label)
        {
            btn.Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            string hex = string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
            btn.Content = hex;
            btn.ToolTip = "点击选择" + label + "歌词颜色（当前 " + hex + "）";
        }

        /// <summary>System.Drawing.Color → ASS 颜色（&amp;H00BBGGRR）</summary>
        private static string ToAssColor(System.Drawing.Color c)
        {
            return string.Format("&H00{0:X2}{1:X2}{2:X2}", c.B, c.G, c.R);
        }

        // ---------------- 文件选择 ----------------

        private void BtnBrowseVideo_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择视频文件";
            dlg.Filter = "视频文件|*.mp4;*.mkv;*.avi;*.wmv;*.mov;*.flv;*.m4v|所有文件|*.*";
            if (dlg.ShowDialog(this) == true)
            {
                txtVideo.Text = dlg.FileName;
                SuggestOutputPath();
                txtHint.Visibility = Visibility.Collapsed;
                txtTime.Text = "00:00 / 00:00";
            }
        }

        private void BtnBrowseKrc_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择 KRC 歌词文件";
            dlg.Filter = "KRC 歌词|*.krc;*.ksc|所有文件|*.*";
            if (dlg.ShowDialog(this) == true)
            {
                txtKrc.Text = dlg.FileName;
            }
        }

        private void BtnBrowseOut_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Title = "选择 MV 输出文件";
            dlg.Filter = "MP4 视频|*.mp4|MKV 视频|*.mkv";
            dlg.FileName = "我的MV.mp4";
            if (dlg.ShowDialog(this) == true)
            {
                txtOut.Text = dlg.FileName;
            }
        }

        /// <summary>在资源管理器里打开输出文件夹（优先输出文件所在目录，没有则用视频所在目录）</summary>
        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string outp = txtOut == null ? "" : txtOut.Text.Trim();
            if (outp.Length > 0)
            {
                string d = "";
                try { d = Path.GetDirectoryName(outp); }
                catch { }
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
                {
                    OpenFolder(d);
                    return;
                }
            }
            string vid = txtVideo == null ? "" : txtVideo.Text.Trim();
            if (vid.Length > 0)
            {
                string d2 = "";
                try { d2 = Path.GetDirectoryName(vid); }
                catch { }
                if (!string.IsNullOrEmpty(d2) && Directory.Exists(d2))
                {
                    OpenFolder(d2);
                    return;
                }
            }
            MessageBox.Show(this, "还没有可打开的位置：请先选择视频或设置输出文件路径。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>用资源管理器打开指定目录</summary>
        private void OpenFolder(string dir)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "explorer.exe";
                psi.Arguments = "\"" + dir + "\"";
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开文件夹失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>根据输入视频自动建议输出文件名（同目录 + 后缀）</summary>
        private void SuggestOutputPath()
        {
            if (string.IsNullOrEmpty(txtVideo.Text) || !File.Exists(txtVideo.Text)) return;
            string dir = Path.GetDirectoryName(txtVideo.Text);
            string name = Path.GetFileNameWithoutExtension(txtVideo.Text);
            string ext = (cmbFormat.SelectedIndex % 2 == 1) ? ".mkv" : ".mp4";
            txtOut.Text = Path.Combine(dir, name + "_双排歌词" + ext);
        }

        // ---------------- 歌词加载 ----------------

        private void BtnLoadLyrics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(txtKrc.Text))
                {
                    MessageBox.Show(this, "请先选择 KRC 歌词文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (!File.Exists(txtKrc.Text))
                {
                    MessageBox.Show(this, "KRC 歌词文件不存在：" + txtKrc.Text, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 1. 插件在 → 插件解密；插件不在 → 明文则直接加载，加密则提示需要插件
                string plain = KrcDecoderLoader.LoadPlainText(txtKrc.Text);
                // 2. 解析
                _lyrics = KrcParser.Parse(plain);
                RefreshLyricList();

                string pluginInfo = KrcDecoderLoader.LoadedDllPath;
                if (string.IsNullOrEmpty(pluginInfo)) pluginInfo = "未使用插件（明文加载）";
                txtStatus.Text = string.Format("歌曲：《{0}》 歌手：{1}　|　歌词 {2} 句 / {3} 字　|　插件：{4}",
                    string.IsNullOrEmpty(_lyrics.SongName) ? "未知" : _lyrics.SongName,
                    string.IsNullOrEmpty(_lyrics.Artist) ? "未知" : _lyrics.Artist,
                    _lyrics.Lines.Count, _lyrics.WordCount,
                    pluginInfo);

                MessageBox.Show(this, string.Format("歌词加载成功：{0} 句，{1} 字。\n现在可以播放视频预览歌词效果。",
                    _lyrics.Lines.Count, _lyrics.WordCount),
                    "加载成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "歌词加载失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>查看 KRC 解密后的原始明文（不要求先点「加载歌词」，只要 KRC 路径有效即可）</summary>
        private void BtnViewPlain_Click(object sender, RoutedEventArgs e)
        {
            string path = txtKrc == null ? "" : txtKrc.Text.Trim();
            if (path.Length == 0 || !File.Exists(path))
            {
                MessageBox.Show(this, "请先在「KRC歌词」处选择存在的 .krc 文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                string plain = KrcDecoderLoader.LoadPlainText(path);
                PlainTextWindow win = new PlainTextWindow(plain, Path.GetFileName(path));
                win.Owner = this;
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "读取失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>打开歌词编辑窗口：逐行标记标题（信息行）或删除行（手动剔除）</summary>
        private void BtnEditLyrics_Click(object sender, RoutedEventArgs e)
        {
            if (_lyrics == null || _lyrics.Lines.Count == 0)
            {
                MessageBox.Show(this, "请先加载 KRC 歌词，再进行编辑。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            LyricEditorWindow win = new LyricEditorWindow(_lyrics);
            win.Owner = this;
            if (win.ShowDialog() == true)
            {
                txtStatus.Text = string.Format("歌曲：《{0}》 歌手：{1}　|　歌词 {2} 句 / {3} 字　|　（已手动编辑）",
                    string.IsNullOrEmpty(_lyrics.SongName) ? "未知" : _lyrics.SongName,
                    string.IsNullOrEmpty(_lyrics.Artist) ? "未知" : _lyrics.Artist,
                    _lyrics.Lines.Count, _lyrics.WordCount);
                RefreshLyricList();
                MessageBox.Show(this, "歌词已更新，共 " + _lyrics.Lines.Count + " 行。\n重新点「播放」可预览新标记，或直接「开始合成」按新标记生成字幕。",
                    "已应用", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ---------------- 右侧歌词列表（点击跳转 / 焦点随视频） ----------------

        /// <summary>刷新右侧歌词列表（只显示正文歌词，跳过标题/词曲信息行）</summary>
        private void RefreshLyricList()
        {
            if (lstLyrics == null) return;
            _lyricListSync = true;
            try
            {
                lstLyrics.Items.Clear();
                if (_lyrics != null && _lyrics.Lines != null)
                {
                    foreach (LyricLine l in _lyrics.Lines)
                    {
                        if (l.IsMeta) continue;
                        LyricListItem item = new LyricListItem();
                        item.Text = l.FullText;
                        item.Line = l;
                        lstLyrics.Items.Add(item);
                    }
                }
            }
            finally { _lyricListSync = false; }
        }

        /// <summary>点击歌词行 → 视频跳转到该句开唱位置</summary>
        private void LstLyrics_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_lyricListSync) return;
            LyricListItem item = lstLyrics.SelectedItem as LyricListItem;
            if (item == null || item.Line == null) return;
            long len = _vlc.GetLength();
            if (len <= 0) return;
            long target = item.Line.Start + ReadOffsetMs();
            if (target < 0) target = 0;
            if (target > len) target = len;
            _vlc.Seek(target);
            if (!_seeking && len > 0) slider.Value = target * slider.Maximum / len;
        }

        /// <summary>播放位置变化时同步列表焦点（当前句高亮并滚动到可见）</summary>
        private void UpdateLyricListFocus(long pos)
        {
            if (_lyrics == null || lstLyrics == null || lstLyrics.Items.Count == 0) return;
            int lyricPos = (int)(pos - ReadOffsetMs());
            LyricListItem current = null;
            for (int i = 0; i < lstLyrics.Items.Count; i++)
            {
                LyricListItem it = lstLyrics.Items[i] as LyricListItem;
                if (it == null) continue;
                if (it.Line.Start <= lyricPos) current = it;
                else break;
            }
            if (current == null || lstLyrics.SelectedItem == current) return;
            _lyricListSync = true;
            try
            {
                lstLyrics.SelectedItem = current;
                lstLyrics.ScrollIntoView(current);
            }
            finally { _lyricListSync = false; }
        }

        // ---------------- 播放控制（LibVLC 内嵌预览） ----------------

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_playBusy) return;   // 上一次加载还没完成，忽略重复点击
            if (string.IsNullOrEmpty(txtVideo.Text) || !File.Exists(txtVideo.Text))
            {
                MessageBox.Show(this, "请先选择视频文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!VlcPlayer.IsAvailable())
            {
                MessageBox.Show(this, "未找到播放库：程序目录 libvlc\\libvlc.dll（需含 plugins 子目录）。\n请确认该目录完整。",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!_vlc.Initialize())
            {
                MessageBox.Show(this, "LibVLC 初始化失败，请检查程序目录 libvlc\\ 是否完整。",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _playBusy = true;
            btnPlay.IsEnabled = false;
            try
            {
                // 已加载歌词时生成与合成一致的 ASS 字幕，经 libvlc add_slave 渲染在视频画面内
                // （HwndHost 视频窗口会遮挡 WPF 叠加层，歌词必须走 libvlc 内部的 ASS 渲染）
                string ass = null;
                string assKey = "";
                if (_lyrics != null && _lyrics.Lines.Count > 0)
                {
                    try
                    {
                        int w, h;
                        double dur;
                        if (ProbeVideoCached(txtVideo.Text, out w, out h, out dur))
                        {
                            Directory.CreateDirectory(_exporter.TempDir);
                            string assPath = Path.Combine(_exporter.TempDir, "subtitle.ass");
                            ExportOptions opt = new ExportOptions();
                            opt.ShowNextLine = chkDual.IsChecked == true;
                            string assText = AssBuilder.Build(_lyrics, w, h, opt.ShowNextLine,
                                (int)sldFont.Value, ToAssColor(_textColor), ToAssColor(_karaokeColor),
                                ReadOffsetMs(), AssBuilder.DefaultFontName, ReadEffect(),
                                chkCountdown.IsChecked == true, ReadCountdownChar(), ToAssColor(_countdownColor),
                                ToAssColor(_titleColor));
                            File.WriteAllText(assPath, assText, new UTF8Encoding(false));
                            ass = assPath;
                            // 字幕内容没变时重复点"加载视频"只从头重播，不重建播放器（避免卡死）
                            assKey = assText.Length + "|" + assText.GetHashCode();
                        }
                    }
                    catch { }
                }

                _vlc.HostHandle = vlcHost.Handle;
                _vlc.Play(txtVideo.Text, ass, assKey);
                txtHint.Visibility = Visibility.Collapsed;
                txtStatus.Text = "内嵌预览中（LibVLC 全格式解码，双排卡拉OK字幕渲染在视频画面内）。";
            }
            finally
            {
                _playBusy = false;
                btnPlay.IsEnabled = true;
            }
        }

        /// <summary>探测视频分辨率/时长；同一路径只调用一次 ffprobe（避免反复起进程卡住界面）</summary>
        private bool ProbeVideoCached(string path, out int w, out int h, out double dur)
        {
            if (path == _probePath && _probeW > 0 && _probeH > 0)
            {
                w = _probeW; h = _probeH; dur = _probeDur;
                return true;
            }
            if (!_exporter.ProbeVideo(path, out w, out h, out dur)) return false;
            _probePath = path;
            _probeW = w;
            _probeH = h;
            _probeDur = dur;
            return true;
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            _vlc.Pause();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _vlc.Stop();
            txtTime.Text = "00:00 / 00:00";
            slider.Value = 0;
        }

        /// <summary>外部预览：ffplay 独立窗口（备用，可播所有格式，进度在播放窗口内操作）</summary>
        private void BtnExternal_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(txtVideo.Text) || !File.Exists(txtVideo.Text))
            {
                MessageBox.Show(this, "请先选择视频文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string ffplay = FindFfplay();
            if (ffplay == null)
            {
                MessageBox.Show(this, "未找到 ffplay.exe。\n请将 ffplay.exe 放入程序目录（与 ffmpeg 同套件的官方构建）。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            StartFfplayPreview(ffplay);
        }

        /// <summary>查找 ffplay.exe：程序目录（绿色便携，与 ffmpeg 同套件）</summary>
        private static string FindFfplay()
        {
            string ffplay = AppPaths.Combine("ffplay.exe");
            return File.Exists(ffplay) ? ffplay : null;
        }

        /// <summary>
        /// 用 ffplay 预览：ffmpeg 自带解码器可播几乎所有格式（mkv/h265 等），
        /// 已加载歌词时生成与合成一致的 ASS，经 libass 实时渲染双排卡拉OK字幕。
        /// </summary>
        private void StartFfplayPreview(string ffplay)
        {
            try
            {
                string video = txtVideo.Text;
                string args;
                if (_lyrics != null && _lyrics.Lines.Count > 0)
                {
                    int width, height;
                    double dur;
                    if (_exporter.ProbeVideo(video, out width, out height, out dur))
                    {
                        Directory.CreateDirectory(_exporter.TempDir);
                        string assPath = Path.Combine(_exporter.TempDir, "subtitle.ass");
                        ExportOptions opt = new ExportOptions();
                        opt.ShowNextLine = chkDual.IsChecked == true;
                        File.WriteAllText(assPath, AssBuilder.Build(_lyrics, width, height, opt.ShowNextLine,
                            (int)sldFont.Value, ToAssColor(_textColor), ToAssColor(_karaokeColor),
                            ReadOffsetMs(), AssBuilder.DefaultFontName, ReadEffect(),
                            chkCountdown.IsChecked == true, ReadCountdownChar(), ToAssColor(_countdownColor),
                            ToAssColor(_titleColor)),
                            new UTF8Encoding(false));
                        _exporter.EnsureFontInTempDir();   // 字体随程序携带，放到工作目录供 fontsdir 读取
                        args = "-window_title \"KTV 预览（双排歌词）\" -vf \"subtitles=subtitle.ass:fontsdir=.\" -autoexit \"" + video + "\"";
                    }
                    else
                    {
                        args = "-window_title \"KTV 预览\" -autoexit \"" + video + "\"";
                    }
                }
                else
                {
                    args = "-window_title \"KTV 预览\" -autoexit \"" + video + "\"";
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ffplay;
                psi.Arguments = args;
                psi.WorkingDirectory = _exporter.TempDir;   // ass= 相对路径在此解析
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                _ffplayProcess = Process.Start(psi);
                txtStatus.Text = "已启动外部预览（ffplay 独立窗口）：全格式解码 + 双排卡拉OK字幕。空格=暂停，进度在播放窗口内拖动，关闭窗口退出。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "ffplay 预览启动失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>定时刷新：从 LibVLC 读取播放位置，驱动歌词叠加层与进度条</summary>
        private void Timer_Tick(object sender, EventArgs e)
        {
            long len = _vlc.GetLength();
            if (len <= 0) return;

            long pos = _vlc.GetTime();
            if (pos < 0) pos = 0;
            if (pos > len) pos = len;

            txtTime.Text = FormatTime(TimeSpan.FromMilliseconds(pos)) + " / " + FormatTime(TimeSpan.FromMilliseconds(len));

            if (!_seeking)
            {
                slider.Value = pos * slider.Maximum / len;
            }
            UpdateLyricListFocus(pos);
        }

        private static string FormatTime(TimeSpan ts)
        {
            return string.Format("{0:00}:{1:00}", (int)ts.TotalMinutes, ts.Seconds);
        }

        // ---------------- 进度条拖动 ----------------

        private void slider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _seeking = true;
        }

        private void slider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _seeking = false;
            long len = _vlc.GetLength();
            if (len > 0)
            {
                _vlc.Seek((long)(slider.Value / slider.Maximum * len));
            }
        }

        // ---------------- MV 合成 ----------------

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_exporting)
            {
                MessageBox.Show(this, "正在合成中，请稍候。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(txtVideo.Text) || !File.Exists(txtVideo.Text))
            {
                MessageBox.Show(this, "请先选择存在的视频文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_lyrics == null || _lyrics.Lines.Count == 0)
            {
                MessageBox.Show(this, "请先加载 KRC 歌词。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(txtOut.Text))
            {
                MessageBox.Show(this, "请设置输出文件路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 收集参数
            ExportOptions options = new ExportOptions();
            options.ShowNextLine = chkDual.IsChecked == true;
            switch (cmbQuality.SelectedIndex)
            {
                case 0: options.Crf = 18; options.Preset = "medium"; break;
                case 1: options.Crf = 20; options.Preset = "medium"; break;
                case 2: options.Crf = 23; options.Preset = "faster"; break;
                default: options.Crf = 26; options.Preset = "faster"; break;
            }
            // 输出容器扩展名同步（mp4 / mkv）
            string wantExt = (cmbFormat.SelectedIndex % 2 == 1) ? ".mkv" : ".mp4";
            if (!txtOut.Text.EndsWith(wantExt, StringComparison.OrdinalIgnoreCase))
            {
                txtOut.Text = Path.ChangeExtension(txtOut.Text, wantExt);
            }
            // 视频编码器（H.264 / H.265）
            options.VideoCodec = (cmbFormat.SelectedIndex >= 2) ? "libx265" : "libx264";
            // 歌词自定义样式：字号 + 上下排颜色 + 整首时间偏移 + 特效 + 倒计时
            options.FontSize = (int)sldFont.Value;
            options.TextColor = ToAssColor(_textColor);
            options.KaraokeColor = ToAssColor(_karaokeColor);
            options.TitleColor = ToAssColor(_titleColor);
            options.TimeOffsetMs = ReadOffsetMs();
            options.FontName = AssBuilder.DefaultFontName;
            options.Effect = ReadEffect();
            options.Countdown = chkCountdown.IsChecked == true;
            options.CountdownChar = ReadCountdownChar();
            options.CountdownColor = ToAssColor(_countdownColor);
            _exporter.FfmpegPath = txtFfmpeg.Text.Trim();
            if (!File.Exists(_exporter.FfmpegPath))
            {
                MessageBox.Show(this, "未找到 ffmpeg：" + _exporter.FfmpegPath + "\n请将 ffmpeg.exe 放入程序目录（与 exe 同目录）。",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _exporting = true;
            btnExport.IsEnabled = false;
            progress.Value = 0;
            txtLog.Text = "准备合成...";

            _exporter.ProgressChanged = delegate(int percent, string message)
            {
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    progress.Value = percent;
                    txtLog.Text = message;
                }));
            };
            _exporter.Completed = delegate(bool ok, string message)
            {
                Dispatcher.BeginInvoke(new Action(delegate()
                {
                    _exporting = false;
                    btnExport.IsEnabled = true;
                    txtLog.Text = ok ? "合成完成" : "合成失败";
                    MessageBox.Show(this, message, ok ? "完成" : "失败",
                        MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
                }));
            };

            _exporter.Export(txtVideo.Text, _lyrics, txtOut.Text, options);
        }

        /// <summary>右侧歌词列表项：显示文本 + 对应的歌词行</summary>
        private class LyricListItem
        {
            public string Text { get; set; }
            public LyricLine Line { get; set; }
            public override string ToString() { return Text; }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _timer.Stop();
            try { _vlc.Dispose(); } catch { }
            if (_ffplayProcess != null && !_ffplayProcess.HasExited)
            {
                try { _ffplayProcess.Kill(); } catch { }
                _ffplayProcess = null;
            }
        }
    }
}
