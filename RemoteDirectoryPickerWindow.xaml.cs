using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;

namespace KaiZhongReleaseTool;

/// <summary>通过服务端目录接口浏览并选择远程服务器本机文件夹。</summary>
public partial class RemoteDirectoryPickerWindow : Window
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _baseUrl;
    private string? _parentPath;
    public string? SelectedPath { get; private set; }

    public RemoteDirectoryPickerWindow(string baseUrl, string serverName, string? initialPath)
    {
        InitializeComponent();
        _baseUrl = baseUrl.TrimEnd('/') + "/";
        ServerTextBlock.Text = $"远程服务器：{serverName}（{_baseUrl}）";
        Loaded += async (_, _) => await LoadDirectoriesAsync(string.IsNullOrWhiteSpace(initialPath) ? null : initialPath);
    }

    /// <summary>读取远程磁盘或指定目录下的子文件夹。</summary>
    private async Task LoadDirectoriesAsync(string? path)
    {
        try
        {
            StatusTextBlock.Text = "正在读取服务端目录...";
            var url = _baseUrl + "api/directories" + (string.IsNullOrWhiteSpace(path) ? string.Empty : $"?path={Uri.EscapeDataString(path)}");
            using var response = await _client.GetAsync(url);
            var responseText = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(responseText))
            {
                var message = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "当前服务端版本不支持远程目录浏览，请更新并重新启动服务端程序。"
                    : $"服务端返回了空内容（HTTP {(int)response.StatusCode}），请检查服务端运行状态。";
                throw new InvalidOperationException(message);
            }

            DirectoryBrowseResponse? result;
            try
            {
                result = JsonSerializer.Deserialize<DirectoryBrowseResponse>(responseText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"服务端返回的不是有效目录数据（HTTP {(int)response.StatusCode}），请确认客户端和服务端版本一致。 ");
            }
            if (result?.Success != true) throw new InvalidOperationException(result?.Message ?? "服务端未返回目录信息。");
            CurrentPathTextBox.Text = result.CurrentPath ?? string.Empty;
            _parentPath = result.ParentPath;
            DirectoryListBox.ItemsSource = result.Directories;
            SelectButton.IsEnabled = !string.IsNullOrWhiteSpace(result.CurrentPath);
            StatusTextBlock.Text = result.Message;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"读取失败：{ex.Message}";
            DirectoryListBox.ItemsSource = null;
            SelectButton.IsEnabled = false;
        }
    }

    private async void Drives_Click(object sender, RoutedEventArgs e) => await LoadDirectoriesAsync(null);
    private async void Up_Click(object sender, RoutedEventArgs e) => await LoadDirectoriesAsync(_parentPath);
    private async void DirectoryListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DirectoryListBox.SelectedItem is RemoteDirectoryEntry entry) await LoadDirectoriesAsync(entry.FullPath);
    }
    private void Select_Click(object sender, RoutedEventArgs e) { SelectedPath = CurrentPathTextBox.Text; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
