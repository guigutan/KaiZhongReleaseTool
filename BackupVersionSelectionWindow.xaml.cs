using System.Windows;

namespace KaiZhongReleaseTool;

/// <summary>使用独立列表选择某台服务器的回滚备份版本。</summary>
public partial class BackupVersionSelectionWindow : Window
{
    public string? SelectedVersion { get; private set; }
    public BackupVersionSelectionWindow(string serverName, IEnumerable<string> versions, string? selected)
    {
        InitializeComponent(); TitleTextBlock.Text = $"{serverName} · 选择备份版本"; VersionListBox.ItemsSource = versions; VersionListBox.SelectedItem = selected; if (VersionListBox.SelectedIndex < 0) VersionListBox.SelectedIndex = 0;
    }
    private void Confirm_Click(object sender, RoutedEventArgs e) { if (VersionListBox.SelectedItem is not string value) { System.Windows.MessageBox.Show("请选择一个备份版本。", "选择版本", MessageBoxButton.OK, MessageBoxImage.Information); return; } SelectedVersion = value; DialogResult = true; }
    private void VersionListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Confirm_Click(sender, e);
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
