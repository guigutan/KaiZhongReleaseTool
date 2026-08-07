using System.Windows;

namespace KaiZhongReleaseTool;
/// <summary>程序可以运行的两种角色。</summary>
public enum AppRole
{
    /// <summary>发送远程指令的客户端。</summary>
    Client,
    /// <summary>接收并执行远程指令的服务端。</summary>
    Server
}

/// <summary>启动时显示的角色选择窗口。</summary>
public partial class RoleSelectionWindow : Window
{
    /// <summary>用户最终选择的运行角色。</summary>
    public AppRole SelectedRole { get; private set; }

    /// <summary>初始化角色选择窗口。</summary>
    public RoleSelectionWindow() => InitializeComponent();

    /// <summary>选择客户端，并通知应用程序继续启动。</summary>
    private void Client_Click(object sender, RoutedEventArgs e)
    {
        SelectedRole = AppRole.Client;
        DialogResult = true;
    }

    /// <summary>选择服务端，并通知应用程序继续启动。</summary>
    private void Server_Click(object sender, RoutedEventArgs e)
    {
        SelectedRole = AppRole.Server;
        DialogResult = true;
    }

    /// <summary>未选择角色，直接关闭应用程序。</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
