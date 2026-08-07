using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace KaiZhongReleaseTool;

/// <summary>
/// 服务端主窗口，负责控制监听状态并实时展示服务执行日志。
/// </summary>
public partial class ServerWindow : Window
{
    private readonly ServerHost _server;

    /// <summary>绑定服务宿主，并在窗口加载后自动启动监听。</summary>
    public ServerWindow(ServerHost server)
    {
        InitializeComponent();
        _server = server;
        _server.LogReceived += AppendLog;
        Loaded += async (_, _) => await StartServerAsync();
    }

    /// <summary>响应“启动”按钮。</summary>
    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartServerAsync();

    /// <summary>启动服务并同步更新输入框、按钮和状态文字。</summary>
    private async Task StartServerAsync()
    {
        try
        {
            StartButton.IsEnabled = false;
            await _server.StartAsync(UrlTextBox.Text.Trim());
            UrlTextBox.IsEnabled = false; StopButton.IsEnabled = true;
            StatusTextBlock.Text = "运行中"; StatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        }
        catch (Exception ex) { AppendLog("启动失败：" + ex.Message); StartButton.IsEnabled = true; }
    }

    /// <summary>响应“停止”按钮，释放监听端口并恢复可编辑状态。</summary>
    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        await _server.StopAsync();
        UrlTextBox.IsEnabled = true; StartButton.IsEnabled = true;
        StatusTextBlock.Text = "已停止"; StatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkRed;
    }

    /// <summary>安全地切回界面线程追加日志，并自动滚动到最后一行。</summary>
    private void AppendLog(string message) => Dispatcher.Invoke(() =>
    {
        LogTextBox.AppendText(message + Environment.NewLine); LogTextBox.ScrollToEnd();
    });

    /// <summary>普通关闭只隐藏到托盘，托盘菜单“退出”才真正关闭。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if ((System.Windows.Application.Current as App)?.IsExiting != true) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
