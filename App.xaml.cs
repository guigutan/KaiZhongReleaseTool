using System.Windows;

namespace KaiZhongReleaseTool;

/// <summary>
/// 应用程序入口，负责选择运行角色、限制重复运行以及统一退出程序。
/// </summary>
public partial class App : System.Windows.Application
{
    // 托盘图标必须由应用程序持有，否则可能被垃圾回收而提前消失。
    private TrayIcon? _trayIcon;
    // 只有选择服务端模式时才会创建服务宿主。
    private ServerHost? _serverHost;
    private bool _isExiting;
    // 客户端和服务端使用不同名称的互斥锁，因此二者可以各运行一个。
    private Mutex? _roleMutex;

    /// <summary>指示程序是否正在通过托盘菜单正常退出。</summary>
    public bool IsExiting => _isExiting;

    /// <summary>显示角色选择窗口，并启动对应的客户端或服务端界面。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var selector = new RoleSelectionWindow();
        if (selector.ShowDialog() != true) { Shutdown(); return; }

        // 根据所选角色创建系统级命名锁，防止同一角色被重复启动。
        var roleName = selector.SelectedRole == AppRole.Server ? "服务端" : "客户端";
        _roleMutex = new Mutex(true, $"Local\\KaiZhongReleaseTool.{selector.SelectedRole}", out var isFirstInstance);
        if (!isFirstInstance)
        {
            _roleMutex.Dispose();
            _roleMutex = null;
            System.Windows.MessageBox.Show($"{roleName}已经在运行，请不要重复打开。\n\n如需关闭，请右键系统托盘中的程序图标并选择“退出”。",
                "请勿重复运行", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 服务端需要同时创建 HTTP 宿主；客户端只需要创建主窗口。
        Window window;
        if (selector.SelectedRole == AppRole.Server)
        {
            _serverHost = new ServerHost();
            window = new ServerWindow(_serverHost);
        }
        else window = new MainWindow();

        MainWindow = window;
        _trayIcon = new TrayIcon(window, ExitApplication);
        window.Show();
    }

    /// <summary>响应托盘“退出”菜单，依次释放托盘、服务宿主和单实例锁。</summary>
    private async void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;
        _trayIcon?.Dispose();
        if (_serverHost is not null) await _serverHost.DisposeAsync();
        _roleMutex?.ReleaseMutex();
        _roleMutex?.Dispose();
        Shutdown();
    }
}
