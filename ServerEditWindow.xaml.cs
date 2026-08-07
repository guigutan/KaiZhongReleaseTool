using System.Windows;

namespace KaiZhongReleaseTool;

/// <summary>新增或修改服务器配置的对话框。</summary>
public partial class ServerEditWindow : Window
{
    /// <summary>用户确认后生成的服务器配置。</summary>
    public ServerProfile? Result { get; private set; }

    public ServerEditWindow(IEnumerable<string> groups, ServerProfile? source = null)
    {
        InitializeComponent();
        GroupComboBox.ItemsSource = groups.ToArray();
        if (source is null) return;
        NameTextBox.Text = source.Name;
        GroupComboBox.SelectedItem = source.GroupName;
        HostTextBox.Text = source.Host;
        PortTextBox.Text = source.Port.ToString();
        UsernameTextBox.Text = source.Username;
        RemotePortTextBox.Text = source.RemoteDesktopPort.ToString();
        PasswordTextBox.Text = source.Password;
        Result = new ServerProfile { Id = source.Id };
    }

    /// <summary>校验输入并关闭对话框。</summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        var host = HostTextBox.Text.Trim().TrimEnd('/');
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host[7..].TrimEnd('/');
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host))
        {
            System.Windows.MessageBox.Show("服务器名称和主机地址不能为空。", "输入提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            System.Windows.MessageBox.Show("端口必须是 1 到 65535 之间的整数。", "输入提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(RemotePortTextBox.Text, out var remotePort) || remotePort is < 1 or > 65535)
        {
            System.Windows.MessageBox.Show("远程端口必须是 1 到 65535 之间的整数。", "输入提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (GroupComboBox.SelectedItem is not string selectedGroup)
        {
            System.Windows.MessageBox.Show("服务器分组为必填项，请先选择分组。", "输入提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result ??= new ServerProfile();
        Result.Name = name;
        Result.GroupName = selectedGroup;
        Result.Host = host;
        Result.Port = port;
        Result.Username = UsernameTextBox.Text.Trim();
        Result.RemoteDesktopPort = remotePort;
        Result.Password = PasswordTextBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
