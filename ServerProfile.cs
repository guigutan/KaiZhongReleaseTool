using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KaiZhongReleaseTool;

/// <summary>保存在客户端 SQLite 数据库中的服务器配置及临时在线状态。</summary>
public sealed class ServerProfile : INotifyPropertyChanged
{
    private string _status = "未检测";
    private string _statusDetail = string.Empty;

    /// <summary>数据库主键。</summary>
    public long Id { get; set; }
    /// <summary>便于用户识别的服务器名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>服务器所属分组，用于客户端列表筛选。</summary>
    public string GroupName { get; set; } = string.Empty;
    /// <summary>服务器 IP 地址或主机名。</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>服务端监听端口。</summary>
    public int Port { get; set; } = 5050;
    /// <summary>远程桌面登录账户。</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>远程桌面明文密码。按用户要求直接保存在 SQLite 中。</summary>
    public string Password { get; set; } = string.Empty;
    /// <summary>远程桌面端口，与发布工具服务端口相互独立。</summary>
    public int RemoteDesktopPort { get; set; } = 3389;
    /// <summary>发布应用程序时所在的发布梯队，只允许第 1 或第 2 梯队。</summary>
    public int ReleaseTier { get; set; } = 2;
    /// <summary>发布前需要备份的 SIE.ScheduleServer 目录，留空表示跳过。</summary>
    public string ScheduleServerBackupPath { get; set; } = string.Empty;
    /// <summary>发布前需要备份的 SIE.WebApiHost 目录，留空表示跳过。</summary>
    public string WebApiHostBackupPath { get; set; } = string.Empty;
    /// <summary>发布前需要备份的 WebClient 目录，留空表示跳过。</summary>
    public string WebClientBackupPath { get; set; } = string.Empty;
    /// <summary>发布前需要备份的 WpfClient 目录，留空表示跳过。</summary>
    public string WpfClientBackupPath { get; set; } = string.Empty;
    /// <summary>服务端保存时间戳 ZIP 备份文件的目录。</summary>
    public string BackupDestinationPath { get; set; } = string.Empty;
    /// <summary>SIE.ScheduleServer 发布时需要停止并重启的 Windows 服务名，留空表示不处理。</summary>
    public string ScheduleServerServiceName { get; set; } = string.Empty;
    /// <summary>SIE.WebApiHost 发布时需要停止并重启的 Windows 服务名，留空表示不处理。</summary>
    public string WebApiHostServiceName { get; set; } = string.Empty;
    /// <summary>WebClient 发布时需要停止并重启的 Windows 服务名，留空表示不处理。</summary>
    public string WebClientServiceName { get; set; } = string.Empty;
    /// <summary>WpfClient 发布时需要停止并重启的 Windows 服务名，留空表示不处理。</summary>
    public string WpfClientServiceName { get; set; } = string.Empty;
    /// <summary>根据主机和端口生成的服务端根地址。</summary>
    public string BaseUrl => $"http://{Host}:{Port}/";

    /// <summary>客户端最近一次检测得到的状态。</summary>
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
    /// <summary>状态检测失败原因或检测时间。</summary>
    public string StatusDetail { get => _statusDetail; set { _statusDetail = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
