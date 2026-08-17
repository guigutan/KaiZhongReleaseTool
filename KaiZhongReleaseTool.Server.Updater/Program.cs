using System.Diagnostics;
using System.IO.Compression;

const string serviceName = "KaiZhongReleaseToolServer";

if (args.Length != 4 || !int.TryParse(args[0], out var serviceProcessId))
    return 2;

var packagePath = Path.GetFullPath(args[1]);
var installDirectory = Path.GetFullPath(args[2]);
var backupDirectory = Path.GetFullPath(args[3]);
var stagingDirectory = Path.Combine(Path.GetDirectoryName(packagePath)!, "Staging");

try
{
    // 等待旧服务进程完全退出，避免正在使用的程序集无法被覆盖。
    try
    {
        using var process = Process.GetProcessById(serviceProcessId);
        process.WaitForExit(30000);
    }
    catch (ArgumentException) { }

    if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
    Directory.CreateDirectory(stagingDirectory);
    ZipFile.ExtractToDirectory(packagePath, stagingDirectory, overwriteFiles: true);

    Directory.CreateDirectory(backupDirectory);
    CopyDirectory(installDirectory, backupDirectory);
    CopyDirectory(stagingDirectory, installDirectory);

    var startResult = RunServiceCommand("start", serviceName);
    if (startResult != 0) throw new InvalidOperationException($"新版覆盖完成，但服务启动失败，退出码：{startResult}");
    if (!await WaitForHealthyServerAsync())
        throw new InvalidOperationException("新版服务已启动，但90秒内未通过5050健康检查。");
    return 0;
}
catch
{
    // 更新失败时恢复旧文件，并尽最大努力重新启动旧服务。
    try
    {
        RunServiceCommand("stop", serviceName);
        await Task.Delay(3000);
        if (Directory.Exists(backupDirectory)) CopyDirectory(backupDirectory, installDirectory);
        RunServiceCommand("start", serviceName);
    }
    catch { }
    return 1;
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
}

static int RunServiceCommand(string action, string name)
{
    using var process = Process.Start(new ProcessStartInfo("sc.exe")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { action, name }
    }) ?? throw new InvalidOperationException("无法运行 Windows 服务控制命令。");
    process.WaitForExit(30000);
    return process.ExitCode;
}

static async Task<bool> WaitForHealthyServerAsync()
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    for (var attempt = 1; attempt <= 30; attempt++)
    {
        try
        {
            using var response = await client.GetAsync("http://127.0.0.1:5050/");
            if (response.IsSuccessStatusCode) return true;
        }
        catch { }
        await Task.Delay(3000);
    }
    return false;
}
