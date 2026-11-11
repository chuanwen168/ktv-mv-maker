using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace KTV_MV_maker
{
    /// <summary>
    /// KRC 解密后明文预览窗口：只读文本框显示原始明文（[ti:] [ar:] 与 [start,duration] 逐字行），可一键复制。
    /// </summary>
    public partial class PlainTextWindow : Window
    {
        private readonly string _text;
        private readonly string _fileName;

        public PlainTextWindow(string text, string fileName)
        {
            InitializeComponent();
            _text = text ?? "";
            _fileName = fileName ?? "";
            txtPlain.Text = _text;

            int lines = 0;
            if (_text.Length > 0)
            {
                lines = 1;
                foreach (char c in _text) if (c == '\n') lines++;
            }
            txtInfo.Text = string.Format("文件：{0}　|　KRC 解密后原始明文：{1} 字符 / {2} 行　|　[H] 头信息，[mm:ss,毫秒] 句子起始与时长，括号内为该字时长",
                fileName, _text.Length, lines);
            txtCount.Text = "只读预览";
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_text);
                MessageBox.Show(this, "已复制全部明文到剪贴板（" + _text.Length + " 字符）。", "复制成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSaveAs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveFileDialog dlg = new SaveFileDialog();
                dlg.Filter = "LRC 歌词 (*.lrc)|*.lrc|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dlg.DefaultExt = ".lrc";
                dlg.FileName = Path.GetFileNameWithoutExtension(_fileName ?? "lyrics");
                if (dlg.ShowDialog(this) == true)
                {
                    File.WriteAllText(dlg.FileName, _text, System.Text.Encoding.UTF8);
                    MessageBox.Show(this, "已保存到：\n" + dlg.FileName, "另存为成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
