using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace KaiZhongReleaseTool;

/// <summary>
/// 管理系统托盘图标、右键菜单以及双击恢复窗口的行为。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _window;

    /// <param name="window">需要隐藏和恢复的客户端或服务端窗口。</param>
    /// <param name="exit">用户点击托盘“退出”时执行的回调。</param>
    public TrayIcon(Window window, Action exit)
    {
        _window = window;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());
        // 优先读取项目内置的 32 像素图标；读取失败时使用 Windows 默认程序图标。
        var resource = System.Windows.Application.GetResourceStream(new Uri("imges/logo32.ico", UriKind.Relative));
        var trayIcon = resource is null ? SystemIcons.Application : (Icon)new Icon(resource.Stream).Clone();
        resource?.Stream.Dispose();
        _icon = new Forms.NotifyIcon
        {
            Icon = trayIcon, Text = "凯中发布工具", Visible = true, ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowWindow();
    }

    /// <summary>从托盘恢复窗口，并把它带到桌面最前方。</summary>
    private void ShowWindow()
    {
        _window.Dispatcher.Invoke(() => { _window.Show(); _window.WindowState = WindowState.Normal; _window.Activate(); });
    }

    /// <summary>退出前隐藏并释放托盘图标，避免通知区域留下无效图标。</summary>
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
