namespace KaiZhongReleaseTool;

/// <summary>客户端支持发送的全部指令类型。</summary>
public enum CommandType
{
    /// <summary>把文件从源路径移动到目标路径。</summary>
    FileMove,
    /// <summary>复制文件，目标文件已存在时返回失败。</summary>
    FileCopy,
    /// <summary>复制并覆盖目标文件，对应界面中的“粘贴”。</summary>
    FilePaste,
    /// <summary>使用指定文本覆盖文件内容。</summary>
    FileModify,
    /// <summary>删除文件或目录。</summary>
    FileDelete,
    /// <summary>为指定文件创建副本。</summary>
    FileBackup,
    /// <summary>创建文件夹，路径中的上级目录不存在时一并创建。</summary>
    DirectoryCreate,
    /// <summary>只压缩指定文件夹第一层中的文件，不包含子文件夹。</summary>
    DirectoryCompressFiles,
    /// <summary>压缩指定文件夹中的全部文件和子文件夹。</summary>
    DirectoryCompressAll,
    /// <summary>把 ZIP 压缩文件解压到指定文件夹。</summary>
    FileExtract,
    /// <summary>把客户端本地文件夹及其完整内容上传到服务端。</summary>
    FolderUpload,
    /// <summary>读取服务端指定文本文件的内容。</summary>
    FileRead,
    /// <summary>获取当前计算机中的 Windows 服务列表。</summary>
    ServiceList,
    /// <summary>停止指定 Windows 服务。</summary>
    ServiceStop,
    /// <summary>启动指定 Windows 服务。</summary>
    ServiceStart,
    /// <summary>重新启动指定 Windows 服务。</summary>
    ServiceRestart,
    /// <summary>获取指定 Windows 服务的当前状态。</summary>
    ServiceStatus
}

/// <summary>客户端发送给服务端的统一指令请求。</summary>
public sealed class CommandRequest
{
    /// <summary>需要执行的具体操作。</summary>
    public CommandType Type { get; set; }
    /// <summary>文件操作的源路径；修改和删除操作也使用此字段。</summary>
    public string? SourcePath { get; set; }
    /// <summary>移动、复制、粘贴或备份操作的目标路径。</summary>
    public string? DestinationPath { get; set; }
    /// <summary>修改文件时需要写入的文本内容。</summary>
    public string? Content { get; set; }
    /// <summary>Windows 服务的系统服务名，例如 Spooler。</summary>
    public string? ServiceName { get; set; }
}

/// <summary>服务端返回给客户端的统一执行结果。</summary>
public sealed class CommandResponse
{
    /// <summary>指令是否成功执行。</summary>
    public bool Success { get; init; }
    /// <summary>便于用户阅读的结果或错误说明。</summary>
    public string Message { get; init; } = string.Empty;
    /// <summary>可选的附加数据，例如服务列表或实际备份路径。</summary>
    public object? Data { get; init; }

    /// <summary>快速创建成功响应。</summary>
    public static CommandResponse Ok(string message, object? data = null) => new() { Success = true, Message = message, Data = data };
    /// <summary>快速创建失败响应。</summary>
    public static CommandResponse Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>客户端发给服务端的发布前目录备份请求。</summary>
public sealed class ServerBackupRequest
{
    public string? LogSetName { get; set; }
    /// <summary>客户端发出发布指令时生成的十四位时间戳。</summary>
    public string Timestamp { get; set; } = string.Empty;
    public string? ScheduleServerPath { get; set; }
    public string? WebApiHostPath { get; set; }
    public string? WebClientPath { get; set; }
    public string? WpfClientPath { get; set; }
    /// <summary>服务端本机保存 ZIP 文件的目标目录。</summary>
    public string? BackupDestinationPath { get; set; }
}

/// <summary>备份成功后，服务端执行应用发布时使用的配置。</summary>
public class DeploymentApplyRequest
{
    public string? LogSetName { get; set; }
    public string? ScheduleServerPath { get; set; }
    public string? WebApiHostPath { get; set; }
    public string? WebClientPath { get; set; }
    public string? WpfClientPath { get; set; }
    public string? ScheduleServerServices { get; set; }
    public string? WebApiHostServices { get; set; }
    public string? WebClientServices { get; set; }
    public string? WpfClientServices { get; set; }
}

/// <summary>发布阶段中单个应用或服务的执行结果。</summary>
public sealed class DeploymentStageItem
{
    public string ApplicationName { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public bool HasFiles { get; set; }
    public bool HasBackupPath { get; set; }
    public bool HasServices { get; set; }
    /// <summary>是否填写了备份路径。</summary>
    public bool BackupPathConfigured { get; set; }
    /// <summary>已填写的备份路径是否在服务端真实存在。</summary>
    public bool BackupPathExists { get; set; }
    /// <summary>是否填写了一个或多个 Windows 服务名。</summary>
    public bool ServicesConfigured { get; set; }
    /// <summary>填写的所有 Windows 服务是否都在服务端真实存在。</summary>
    public bool ServicesExist { get; set; }
    public bool Success { get; set; }
    public int Attempts { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Version { get; set; }
}

/// <summary>服务端某个发布阶段返回的明细。</summary>
public sealed class DeploymentStageResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<DeploymentStageItem> Items { get; set; } = new();
}

/// <summary>查询或执行发布回滚时使用的服务器配置。</summary>
public sealed class DeploymentRollbackRequest : DeploymentApplyRequest
{
    public string? BackupDestinationPath { get; set; }
    public string? BackupFileName { get; set; }
}

/// <summary>服务端目录浏览接口返回的数据。</summary>
public sealed class DirectoryBrowseResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? CurrentPath { get; set; }
    public string? ParentPath { get; set; }
    public List<RemoteDirectoryEntry> Directories { get; set; } = new();
}

/// <summary>远程服务端中的一个磁盘或文件夹。</summary>
public sealed class RemoteDirectoryEntry
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
}
