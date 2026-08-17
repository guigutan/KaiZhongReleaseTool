using System.IO;

namespace KaiZhongReleaseTool;

/// <summary>集中管理客户端与 Windows 服务端使用的数据目录。</summary>
public static class AppPaths
{
    /// <summary>Windows 服务端的公共数据根目录，所有登录用户看到的是同一份数据。</summary>
    public static string ServerDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KaiZhong", "KaiZhongReleaseTool");

    public static string ServerTempDirectory => Path.Combine(ServerDataDirectory, "Temp");
    public static string ServerSmomDllDirectory => Path.Combine(ServerDataDirectory, "SMOMDLL");
    public static string ServerUpdateDirectory => Path.Combine(ServerDataDirectory, "Update");

    /// <summary>在服务启动前创建所有必须的可写目录。</summary>
    public static void EnsureServerDirectories()
    {
        Directory.CreateDirectory(ServerDataDirectory);
        Directory.CreateDirectory(ServerTempDirectory);
        Directory.CreateDirectory(ServerSmomDllDirectory);
        Directory.CreateDirectory(ServerUpdateDirectory);
    }
}
