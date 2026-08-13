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
        ScheduleServerBackupPathTextBox.Text = source.ScheduleServerBackupPath;
        WebApiHostBackupPathTextBox.Text = source.WebApiHostBackupPath;
        WebClientBackupPathTextBox.Text = source.WebClientBackupPath;
        WpfClientBackupPathTextBox.Text = source.WpfClientBackupPath;
        BackupDestinationPathTextBox.Text = source.BackupDestinationPath;
        ScheduleServerServiceNameTextBox.Text = source.ScheduleServerServiceName;
        WebApiHostServiceNameTextBox.Text = source.WebApiHostServiceName;
        WebClientServiceNameTextBox.Text = source.WebClientServiceName;
        WpfClientServiceNameTextBox.Text = source.WpfClientServiceName;
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
        var hasBackupSource = new[]
        {
            ScheduleServerBackupPathTextBox.Text, WebApiHostBackupPathTextBox.Text,
            WebClientBackupPathTextBox.Text, WpfClientBackupPathTextBox.Text
        }.Any(path => !string.IsNullOrWhiteSpace(path));
        if (hasBackupSource && string.IsNullOrWhiteSpace(BackupDestinationPathTextBox.Text))
        {
            System.Windows.MessageBox.Show("配置了备份源目录时，“备份到”目录为必填项。", "输入提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        Result.ScheduleServerBackupPath = ScheduleServerBackupPathTextBox.Text.Trim();
        Result.WebApiHostBackupPath = WebApiHostBackupPathTextBox.Text.Trim();
        Result.WebClientBackupPath = WebClientBackupPathTextBox.Text.Trim();
        Result.WpfClientBackupPath = WpfClientBackupPathTextBox.Text.Trim();
        Result.BackupDestinationPath = BackupDestinationPathTextBox.Text.Trim();
        Result.ScheduleServerServiceName = ScheduleServerServiceNameTextBox.Text.Trim();
        Result.WebApiHostServiceName = WebApiHostServiceNameTextBox.Text.Trim();
        Result.WebClientServiceName = WebClientServiceNameTextBox.Text.Trim();
        Result.WpfClientServiceName = WpfClientServiceNameTextBox.Text.Trim();
        DialogResult = true;
    }

    /// <summary>连接当前页面填写的服务端，并为对应输入框选择远程目录。</summary>
    private void SelectRemoteDirectory_Click(object sender, RoutedEventArgs e)
    {
        var host = HostTextBox.Text.Trim().TrimEnd('/');
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host[7..].TrimEnd('/');
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            System.Windows.MessageBox.Show("请先填写正确的服务器主机地址和服务端口。", "选择服务端目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string targetName) return;
        var targetTextBox = targetName switch
        {
            "Schedule" => ScheduleServerBackupPathTextBox,
            "Api" => WebApiHostBackupPathTextBox,
            "Web" => WebClientBackupPathTextBox,
            "Wpf" => WpfClientBackupPathTextBox,
            "Destination" => BackupDestinationPathTextBox,
            _ => null
        };
        if (targetTextBox is null) return;
        var baseUrl = $"http://{host}:{port}/";
        var dialog = new RemoteDirectoryPickerWindow(baseUrl, string.IsNullOrWhiteSpace(NameTextBox.Text) ? host : NameTextBox.Text.Trim(), targetTextBox.Text) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedPath)) targetTextBox.Text = dialog.SelectedPath;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
