using System.Windows;

namespace KaiZhongReleaseTool;

/// <summary>客户端 WPF 程序入口；服务端已拆分为独立的无界面 Windows 服务。</summary>
public partial class App : System.Windows.Application
{
    private TrayIcon? _trayIcon;
    private Mutex? _clientMutex;
    private bool _isExiting;

    public bool IsExiting => _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _clientMutex = new Mutex(true, "Local\\KaiZhongReleaseTool.Client", out var isFirstInstance);
        if (!isFirstInstance)
        {
            _clientMutex.Dispose();
            _clientMutex = null;
            System.Windows.MessageBox.Show("客户端已经在运行，请不要重复打开。\n\n如需关闭，请右键系统托盘中的程序图标并选择“退出”。",
                "请勿重复运行", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        _trayIcon = new TrayIcon(window, ExitApplication);
        window.Show();
    }

    private void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;
        _trayIcon?.Dispose();
        _clientMutex?.ReleaseMutex();
        _clientMutex?.Dispose();
        Shutdown();
    }
}
