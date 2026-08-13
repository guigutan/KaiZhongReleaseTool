using System.Windows;

namespace KaiZhongReleaseTool;

/// <summary>要求用户输入指定文字后才允许删除服务器配置。</summary>
public partial class DeleteServerConfirmationWindow : Window
{
    private const string ConfirmationPhrase = "我确认删除";

    public DeleteServerConfirmationWindow(string serverName)
    {
        InitializeComponent();
        ServerNameTextBlock.Text = $"即将删除：{serverName}";
        Loaded += (_, _) => ConfirmationTextBox.Focus();
    }

    /// <summary>只有输入内容完全一致时才启用删除按钮。</summary>
    private void ConfirmationTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        DeleteButton.IsEnabled = string.Equals(ConfirmationTextBox.Text.Trim(), ConfirmationPhrase, StringComparison.Ordinal);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!DeleteButton.IsEnabled) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
