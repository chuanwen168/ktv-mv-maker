using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KTV_MV_maker
{
    /// <summary>编辑窗口中的一行歌词（绑定到 DataGrid）</summary>
    public class LyricEditRow
    {
        public int Index { get; set; }
        public string TimeText { get; set; }
        public string Text { get; set; }
        /// <summary>是否为标题/信息行（可勾选修改）</summary>
        public bool IsMeta { get; set; }
        public LyricLine Line { get; set; }
    }

    /// <summary>
    /// 歌词编辑窗口：逐行查看歌词，可勾选"标题"（信息行）或删除行（手动剔除）。
    /// 点"确定"后把修改直接应用到 KrcLyrics（IsMeta 标记 + 删除的行）。
    /// </summary>
    public partial class LyricEditorWindow : Window
    {
        private readonly KrcLyrics _lyrics;
        private readonly ObservableCollection<LyricEditRow> _rows = new ObservableCollection<LyricEditRow>();
        private readonly List<LyricLine> _toDelete = new List<LyricLine>();
        private bool _applied;
        /// <summary>简繁转换前的逐字原文（点"取消"时还原）</summary>
        private Dictionary<LyricLine, string[]> _textBackup;

        public LyricEditorWindow(KrcLyrics lyrics)
        {
            InitializeComponent();
            _lyrics = lyrics;
            int i = 0;
            foreach (LyricLine l in lyrics.Lines)
            {
                _rows.Add(new LyricEditRow
                {
                    Index = ++i,
                    TimeText = Fmt(l.Start),
                    Text = l.FullText,
                    IsMeta = l.IsMeta,
                    Line = l
                });
            }
            grid.ItemsSource = _rows;
            txtTitle.Text = string.IsNullOrEmpty(lyrics.SongName) ? lyrics.EffectiveTitle : lyrics.SongName;
            UpdateCount();
        }

        /// <summary>是否已应用修改</summary>
        public bool Applied { get { return _applied; } }

        private void UpdateCount()
        {
            int meta = 0;
            foreach (LyricEditRow r in _rows) if (r.IsMeta) meta++;
            if (txtCount != null)
                txtCount.Text = string.Format("共 {0} 行：标题 {1} 行 / 正文 {2} 行", _rows.Count, meta, _rows.Count - meta);
        }

        private void BtnAllMeta_Click(object sender, RoutedEventArgs e)
        {
            foreach (LyricEditRow r in _rows) r.IsMeta = true;
            grid.Items.Refresh();
            UpdateCount();
        }

        private void BtnAllReal_Click(object sender, RoutedEventArgs e)
        {
            foreach (LyricEditRow r in _rows) r.IsMeta = false;
            grid.Items.Refresh();
            UpdateCount();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (grid.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "请先在列表中选中要删除的行（可按住 Ctrl / Shift 多选）。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            List<LyricEditRow> sel = new List<LyricEditRow>();
            foreach (object o in grid.SelectedItems) if (o is LyricEditRow) sel.Add((LyricEditRow)o);
            if (sel.Count == 0) return;
            string preview2 = "";
            foreach (LyricEditRow r in sel)
            {
                if (preview2.Length < 60) preview2 += (preview2.Length > 0 ? " / " : "") + r.Text;
            }
            if (MessageBox.Show(this, "确定删除选中的 " + sel.Count + " 行歌词？\n" + preview2 + "\n（删除后需重新加载 KRC 才能恢复）",
                "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            foreach (LyricEditRow r in sel)
            {
                _toDelete.Add(r.Line);
                _rows.Remove(r);
            }
            UpdateCount();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Apply();
            _applied = true;
            _textBackup = null;   // 已确认，不再还原
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            RestoreTexts();       // 简繁转换未确认时还原原文
            DialogResult = false;
        }

        // ---------------- 简体 → 繁体 ----------------

        /// <summary>把全部歌词按 OpenCC 词表转成繁体（逐字写回，时间轴与标记不变）</summary>
        private void BtnToTraditional_Click(object sender, RoutedEventArgs e)
        {
            if (!ChineseConverter.IsReady)
            {
                MessageBox.Show(this, "繁体词表未找到，无法转换。\n" + ChineseConverter.LastError,
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_rows.Count == 0) return;
            if (_textBackup == null) _textBackup = new Dictionary<LyricLine, string[]>();

            int changedLines = 0;
            int changedChars = 0;
            foreach (LyricEditRow r in _rows)
            {
                LyricLine line = r.Line;
                List<LyricWord> words = line.Words;
                if (words == null || words.Count == 0) continue;

                string[] orig = new string[words.Count];
                bool any = false;
                for (int i = 0; i < words.Count; i++)
                {
                    orig[i] = words[i].Text;
                    string conv = ChineseConverter.Convert(orig[i]);
                    if (conv != orig[i]) { any = true; changedChars++; }
                    words[i].Text = conv;
                }
                if (!_textBackup.ContainsKey(line)) _textBackup[line] = orig;   // 只记最初原文
                if (any) changedLines++;
                r.Text = line.FullText;
            }
            grid.Items.Refresh();
            UpdateCount();
            if (changedLines == 0)
            {
                MessageBox.Show(this, "歌词已经是繁体（或无可转换字符）。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                if (txtCount != null)
                    txtCount.Text = string.Format("共 {0} 行：标题 {1} 行 / 正文 {2} 行　（已转换 {3} 行 / {4} 字）",
                        _rows.Count, CountMeta(), _rows.Count - CountMeta(), changedLines, changedChars);
            }
        }

        private int CountMeta()
        {
            int meta = 0;
            foreach (LyricEditRow r in _rows) if (r.IsMeta) meta++;
            return meta;
        }

        /// <summary>还原简繁转换（点"取消"时）</summary>
        private void RestoreTexts()
        {
            if (_textBackup == null) return;
            foreach (KeyValuePair<LyricLine, string[]> kv in _textBackup)
            {
                LyricLine line = kv.Key;
                string[] orig = kv.Value;
                if (line == null || line.Words == null) continue;
                for (int i = 0; i < line.Words.Count && i < orig.Length; i++)
                    line.Words[i].Text = orig[i];
            }
            _textBackup = null;
        }

        /// <summary>仅取歌名：去掉括号副标题（( ) （ ） [ ] 【 】）与" - 歌手"，只留歌名</summary>
        private void BtnTrimTitle_Click(object sender, RoutedEventArgs e)
        {
            string t = (txtTitle.Text ?? "").Trim();
            if (t.Length == 0) t = _lyrics.SongName;
            int cut = -1;
            char[] opens = new char[] { '(', '（', '[', '【' };
            for (int i = 0; i < opens.Length; i++)
            {
                int p = t.IndexOf(opens[i]);
                if (p > 0 && (cut < 0 || p < cut)) cut = p;
            }
            if (cut > 0) t = t.Substring(0, cut).Trim();
            int dash = t.IndexOf(" - ");
            if (dash > 0) t = t.Substring(0, dash).Trim();
            if (t.Length == 0) t = _lyrics.SongName;
            txtTitle.Text = t;
        }

        // ---------------- 另起一行（手动拆分过长歌词） ----------------

        /// <summary>把选中行在光标处拆成两句，避免字号自动缩小</summary>
        private void BtnSplit_Click(object sender, RoutedEventArgs e)
        {
            if (grid.SelectedItems.Count != 1)
            {
                MessageBox.Show(this, "请先选中要拆分的**一行**歌词。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            LyricEditRow row = grid.SelectedItems[0] as LyricEditRow;
            if (row == null || row.Line == null) return;
            if (row.IsMeta)
            {
                MessageBox.Show(this, "这一行被标记为「标题/信息行」，不需要拆分。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (row.Text.Length < 2)
            {
                MessageBox.Show(this, "这句太短，无法再拆分。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SplitLineDialog dlg = new SplitLineDialog(row.Text, this);
            if (dlg.ShowDialog() != true) return;

            if (!SplitLine(_lyrics, row.Line, dlg.SplitIndex))
            {
                MessageBox.Show(this, "拆分失败：断句位置无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ReloadRows();
        }

        /// <summary>按字符位置拆分歌词行：前半段留在原行，后半段成为新的一句</summary>
        private static bool SplitLine(KrcLyrics lyrics, LyricLine line, int charCut)
        {
            string full = line.FullText;
            if (charCut <= 0 || charCut >= full.Length) return false;

            List<LyricWord> first = new List<LyricWord>();
            List<LyricWord> second = new List<LyricWord>();
            int consumed = 0;
            foreach (LyricWord w in line.Words)
            {
                int len = w.Text.Length;
                if (len == 0) continue;
                if (consumed >= charCut)
                {
                    second.Add(new LyricWord(w.Start, w.Duration, w.Text));
                }
                else if (consumed + len <= charCut)
                {
                    first.Add(new LyricWord(w.Start, w.Duration, w.Text));
                }
                else
                {
                    // 断点落在字中间：按字数比例分配时长
                    int n = charCut - consumed;
                    int dur1 = (int)Math.Round(w.Duration * (double)n / len);
                    first.Add(new LyricWord(w.Start, dur1, w.Text.Substring(0, n)));
                    second.Add(new LyricWord(w.Start + dur1, Math.Max(1, w.Duration - dur1), w.Text.Substring(n)));
                }
                consumed += len;
            }
            if (first.Count == 0 || second.Count == 0) return false;

            int lineEnd = line.End;
            int secondStart = second[0].Start;
            if (secondStart <= line.Start) secondStart = line.Start + 1;

            LyricLine l2 = new LyricLine(secondStart, Math.Max(1, lineEnd - secondStart));
            l2.Words = second;
            l2.IsMeta = false;

            line.Words = first;
            line.Duration = Math.Max(1, secondStart - line.Start);

            int idx = lyrics.Lines.IndexOf(line);
            if (idx < 0) return false;
            lyrics.Lines.Insert(idx + 1, l2);
            KrcParser.DetectAnnotations(lyrics);
            return true;
        }

        /// <summary>重建列表行（拆分后行数/内容变化）</summary>
        private void ReloadRows()
        {
            SyncMetaToLines();
            _rows.Clear();
            int i = 0;
            foreach (LyricLine l in _lyrics.Lines)
            {
                if (_toDelete.Contains(l)) continue;
                _rows.Add(new LyricEditRow
                {
                    Index = ++i,
                    TimeText = Fmt(l.Start),
                    Text = l.FullText,
                    IsMeta = l.IsMeta,
                    Line = l
                });
            }
            grid.Items.Refresh();
            UpdateCount();
        }

        /// <summary>把列表中勾选的"标题"立即写回模型（重建行前调用）</summary>
        private void SyncMetaToLines()
        {
            foreach (LyricEditRow r in _rows) r.Line.IsMeta = r.IsMeta;
        }

        /// <summary>把勾选的"标题"标记与删除操作写回歌词模型</summary>
        private void Apply()
        {
            foreach (LyricEditRow r in _rows) r.Line.IsMeta = r.IsMeta;
            foreach (LyricLine l in _toDelete) _lyrics.Lines.Remove(l);
            // 标题直接写回 ti（SongName）；留空则保留原歌名
            string t = (txtTitle.Text ?? "").Trim();
            if (t.Length > 0) _lyrics.SongName = t;
            _lyrics.DisplayTitle = "";   // 统一以 ti 为准
            // 把单独的演唱者标签行（如 "Miko："）并入下一句开头，并重新识别句首注释词
            KrcParser.MergeLabelOnlyLines(_lyrics);
            KrcParser.DetectAnnotations(_lyrics);
        }

        private static string Fmt(int ms)
        {
            if (ms < 0) ms = 0;
            int cs = ms / 10;
            int s = cs / 100;
            int m = s / 60;
            return string.Format("{0:00}:{1:00}.{2:00}", m, s % 60, cs % 100);
        }
    }

    /// <summary>
    /// "另起一行"对话框：只读展示整句，用户把光标放到断句处（或选中要移到下一行的文字），
    /// 光标前的文字留在本句，光标后的文字成为新的一句。
    /// </summary>
    public class SplitLineDialog : Window
    {
        private readonly TextBox _box;
        private readonly TextBlock _preview;
        /// <summary>拆分位置（字符索引），-1 表示取消</summary>
        public int SplitIndex = -1;

        public SplitLineDialog(string text, Window owner)
        {
            Title = "另起一行 - 选择断句位置";
            Width = 660;
            Height = 320;
            MinWidth = 480;
            MinHeight = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResize;

            DockPanel root = new DockPanel();
            root.Margin = new Thickness(12);

            TextBlock tip = new TextBlock();
            tip.TextWrapping = TextWrapping.Wrap;
            tip.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            tip.Text = "把光标放到需要断开的位置（或选中要移到下一行的文字），点「拆分」：\n" +
                       "光标前的文字留在本句，光标后的文字成为新的一句。\n" +
                       "红色竖线就是插入位置（此框只用来定位，不能改字），下面会实时显示拆分结果。";
            tip.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(tip, Dock.Top);
            root.Children.Add(tip);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Margin = new Thickness(0, 10, 0, 0);
            DockPanel.SetDock(buttons, Dock.Bottom);

            Button ok = new Button();
            ok.Content = "拆分";
            ok.Width = 88;
            ok.IsDefault = true;
            ok.Click += delegate { Accept(); };
            Button cancel = new Button();
            cancel.Content = "取消";
            cancel.Width = 88;
            cancel.Margin = new Thickness(8, 0, 0, 0);
            cancel.IsCancel = true;
            cancel.Click += delegate { DialogResult = false; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            _box = new TextBox();
            _box.Text = text;
            // 注意：只读 TextBox 不显示光标，用户看不到"另起一行"的位置，
            // 所以这里保持可编辑以显示光标，但拦截所有改字操作，正文不会被修改。
            _box.IsReadOnly = false;
            _box.CaretBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20));
            _box.FontSize = 20;
            _box.TextWrapping = TextWrapping.Wrap;
            _box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _box.Padding = new Thickness(6);
            _box.Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
            // 屏蔽输入/删除/粘贴/剪切/回车，只允许移动光标与选择文字
            _box.PreviewTextInput += delegate(object s, TextCompositionEventArgs e) { e.Handled = true; };
            _box.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Back || e.Key == Key.Delete || e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    e.Handled = true;
                }
                else if (Keyboard.Modifiers == ModifierKeys.Control &&
                         (e.Key == Key.V || e.Key == Key.X || e.Key == Key.A))
                {
                    if (e.Key != Key.A) e.Handled = true;   // 保留 Ctrl+A 全选
                }
            };
            _box.SelectionChanged += delegate { UpdatePreview(); };

            _preview = new TextBlock();
            _preview.TextWrapping = TextWrapping.Wrap;
            _preview.FontSize = 13;
            _preview.Margin = new Thickness(2, 8, 2, 0);
            _preview.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x66, 0x99));
            DockPanel.SetDock(_preview, Dock.Bottom);
            root.Children.Add(_preview);

            root.Children.Add(buttons);
            root.Children.Add(_box);
            Content = root;

            Loaded += delegate
            {
                _box.Focus();
                _box.CaretIndex = text.Length / 2;
                UpdatePreview();
            };
        }

        /// <summary>实时显示拆分后的两句，让用户确认断句位置</summary>
        private void UpdatePreview()
        {
            int cut = _box.SelectionLength > 0 ? _box.SelectionStart : _box.CaretIndex;
            if (cut < 0) cut = 0;
            if (cut > _box.Text.Length) cut = _box.Text.Length;
            string a = _box.Text.Substring(0, cut).Trim();
            string b = _box.Text.Substring(cut).Trim();
            string sa = a.Length == 0 ? "(空)" : a;
            string sb = b.Length == 0 ? "(空)" : b;
            _preview.Text = "断句处：" + cut + " / " + _box.Text.Length +
                            "\n  ① " + sa + "\n  ② " + sb;
        }

        private void Accept()
        {
            int cut = _box.SelectionLength > 0 ? _box.SelectionStart : _box.CaretIndex;
            if (cut <= 0 || cut >= _box.Text.Length)
            {
                MessageBox.Show(this, "请把光标放在句子中间（不能放在开头或结尾）。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SplitIndex = cut;
            DialogResult = true;
        }
    }
}
