using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;

namespace KaiZhongReleaseTool;

/// <summary>按创建日期选择日志集，并用保存时的颜色显示日志明细。</summary>
public partial class LogViewerWindow : Window
{
    private readonly LogRepository _repository;
    public LogViewerWindow(LogRepository repository) { InitializeComponent(); _repository = repository; LogSetListBox.ItemsSource = repository.GetSets(); LogSetListBox.SelectedIndex = 0; }
    private void LogSetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogSetListBox.SelectedItem is not LogSetRecord set) return;
        TitleTextBlock.Text = set.Name; LogRichTextBox.Document.Blocks.Clear();
        foreach (var entry in _repository.GetEntries(set.Name))
        {
            var color = entry.Level == "Error" ? Brushes.Crimson : entry.Level == "Warning" ? Brushes.DarkGoldenrod : entry.Level == "Header" ? Brushes.SteelBlue : Brushes.SeaGreen;
            LogRichTextBox.Document.Blocks.Add(new Paragraph(new Run(entry.Message) { Foreground = color, FontWeight = entry.Level == "Header" ? FontWeights.Bold : FontWeights.Normal }) { Margin = new Thickness(0,2,0,2) });
        }
    }
}
