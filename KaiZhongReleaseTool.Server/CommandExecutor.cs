using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
namespace KaiZhongReleaseTool;

/// <summary>
/// 服务端指令执行器，把统一请求分发到文件操作或 Windows 服务操作。
/// </summary>
public sealed class CommandExecutor
{
    /// <summary>执行一条指令，并把异常统一转换成失败响应返回给客户端。</summary>
    public async Task<CommandResponse> ExecuteAsync(CommandRequest request, CancellationToken token)
    {
        try
        {
            return request.Type switch
            {
                CommandType.FileMove => Move(request),
                CommandType.FileCopy => Copy(request, false),
                CommandType.FilePaste => Copy(request, true),
                CommandType.FileModify => await ModifyAsync(request, token),
                CommandType.FileDelete => Delete(request),
                CommandType.FileBackup => Backup(request),
                CommandType.DirectoryCreate => CreateDirectory(request),
                CommandType.DirectoryCompressFiles => CompressFiles(request),
                CommandType.DirectoryCompressAll => CompressAll(request),
                CommandType.FileExtract => Extract(request),
                CommandType.FileRead => await ReadFileAsync(request, token),
                CommandType.ServiceList => await RunScAsync("query state= all", token),
                CommandType.ServiceStop => await ServiceActionAsync("stop", request.ServiceName, token),
                CommandType.ServiceStart => await ServiceActionAsync("start", request.ServiceName, token),
                CommandType.ServiceRestart => await RestartServiceAsync(request.ServiceName, token),
                CommandType.ServiceStatus => await ServiceActionAsync("query", request.ServiceName, token),
                _ => CommandResponse.Fail("不支持的指令。")
            };
        }
        catch (Exception ex) { return CommandResponse.Fail(ex.Message); }
    }

    /// <summary>移动文件；目标文件已存在时由系统返回错误。</summary>
    private static CommandResponse Move(CommandRequest request)
    {
        var source = RequiredPath(request.SourcePath, "源路径");
        var destination = RequiredPath(request.DestinationPath, "目标路径");
        EnsureParentDirectory(destination);
        File.Move(source, destination);
        return CommandResponse.Ok($"已移动：{source} -> {destination}");
    }

    /// <summary>复制文件，overwrite 参数决定是否允许覆盖已有目标文件。</summary>
    private static CommandResponse Copy(CommandRequest request, bool overwrite)
    {
        var source = RequiredPath(request.SourcePath, "源路径");
        var destination = RequiredPath(request.DestinationPath, "目标路径");
        EnsureParentDirectory(destination);
        File.Copy(source, destination, overwrite);
        return CommandResponse.Ok($"已复制：{source} -> {destination}");
    }

    /// <summary>使用 UTF-8 编码把指定内容完整写入文件。</summary>
    private static async Task<CommandResponse> ModifyAsync(CommandRequest request, CancellationToken token)
    {
        var path = RequiredPath(request.SourcePath, "源路径");
        EnsureParentDirectory(path);
        await File.WriteAllTextAsync(path, request.Content ?? string.Empty, Encoding.UTF8, token);
        return CommandResponse.Ok($"已修改：{path}");
    }

    /// <summary>以 UTF-8 编码读取服务端文本文件，并把内容返回给客户端。</summary>
    private static async Task<CommandResponse> ReadFileAsync(CommandRequest request, CancellationToken token)
    {
        const long maximumTextFileSize = 10 * 1024 * 1024;
        var path = RequiredPath(request.SourcePath, "文件路径");
        if (!File.Exists(path)) throw new FileNotFoundException("指定文件不存在。", path);

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > maximumTextFileSize)
            throw new IOException("文件超过 10 MB，为避免占用过多内存，拒绝读取。 ");

        var content = await File.ReadAllTextAsync(path, Encoding.UTF8, token);
        return CommandResponse.Ok($"文件读取成功：{path}", new
        {
            Path = path,
            Length = fileInfo.Length,
            Content = content
        });
    }

    /// <summary>删除文件或递归删除目录。</summary>
    private static CommandResponse Delete(CommandRequest request)
    {
        var path = RequiredPath(request.SourcePath, "源路径");
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, true);
        else throw new FileNotFoundException("文件或目录不存在。", path);
        return CommandResponse.Ok($"已删除：{path}");
    }

    /// <summary>备份文件；未填写目标路径时自动生成带时间戳的备份文件名。</summary>
    private static CommandResponse Backup(CommandRequest request)
    {
        var source = RequiredPath(request.SourcePath, "源路径");
        var destination = string.IsNullOrWhiteSpace(request.DestinationPath)
            ? source + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss")
            : Path.GetFullPath(request.DestinationPath);
        EnsureParentDirectory(destination);
        File.Copy(source, destination, false);
        return CommandResponse.Ok($"已备份：{destination}", destination);
    }

    /// <summary>递归创建指定文件夹，已存在时不会报错。</summary>
    private static CommandResponse CreateDirectory(CommandRequest request)
    {
        var path = RequiredPath(request.SourcePath, "文件夹路径");
        Directory.CreateDirectory(path);
        return CommandResponse.Ok($"文件夹已创建：{path}");
    }

    /// <summary>只把源文件夹第一层的文件写入 ZIP，不包含任何子文件夹。</summary>
    private static CommandResponse CompressFiles(CommandRequest request)
    {
        var source = RequiredPath(request.SourcePath, "源文件夹");
        var destination = RequiredPath(request.DestinationPath, "压缩文件路径");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"源文件夹不存在：{source}");
        EnsureZipExtension(destination);
        EnsureArchiveOutsideSource(source, destination);
        EnsureParentDirectory(destination);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly).ToArray();
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
        }
        return CommandResponse.Ok($"文件已压缩：{destination}", destination);
    }

    /// <summary>把源文件夹中的文件和完整子文件夹结构全部写入 ZIP。</summary>
    private static CommandResponse CompressAll(CommandRequest request)
    {
        var source = RequiredPath(request.SourcePath, "源文件夹");
        var destination = RequiredPath(request.DestinationPath, "压缩文件路径");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"源文件夹不存在：{source}");
        EnsureZipExtension(destination);
        EnsureArchiveOutsideSource(source, destination);
        EnsureParentDirectory(destination);
        ZipFile.CreateFromDirectory(source, destination, CompressionLevel.Optimal, includeBaseDirectory: false);
        return CommandResponse.Ok($"文件夹已压缩：{destination}", destination);
    }

    /// <summary>把 ZIP 文件解压到目标文件夹，遇到同名文件时覆盖。</summary>
    private static CommandResponse Extract(CommandRequest request)
    {
        var source = RequiredPath(request.SourcePath, "压缩文件路径");
        var destination = RequiredPath(request.DestinationPath, "解压目标文件夹");
        if (!File.Exists(source)) throw new FileNotFoundException("压缩文件不存在。", source);
        Directory.CreateDirectory(destination);
        ZipFile.ExtractToDirectory(source, destination, overwriteFiles: true);
        return CommandResponse.Ok($"文件已解压：{destination}", destination);
    }

    /// <summary>执行启动或停止 Windows 服务的 sc.exe 指令。</summary>
    private static async Task<CommandResponse> ServiceActionAsync(string action, string? serviceName, CancellationToken token)
    {
        ValidateServiceName(serviceName);
        return await RunScAsync($"{action} \"{serviceName}\"", token);
    }

    /// <summary>先停止再启动指定服务，实现服务重启。</summary>
    private static async Task<CommandResponse> RestartServiceAsync(string? serviceName, CancellationToken token)
    {
        ValidateServiceName(serviceName);
        var stop = await RunScAsync($"stop \"{serviceName}\"", token);
        if (!stop.Success) return stop;
        // 给服务控制管理器一点时间完成停止过程，再发送启动指令。
        await Task.Delay(1500, token);
        return await RunScAsync($"start \"{serviceName}\"", token);
    }

    /// <summary>无窗口运行 sc.exe，并同时收集标准输出和错误输出。</summary>
    private static async Task<CommandResponse> RunScAsync(string arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo("sc.exe", arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 sc.exe。");
        // 同时读取两个输出流，避免缓冲区写满造成进程互相等待。
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(token);
        var output = (await stdout) + (await stderr);
        return process.ExitCode == 0
            ? CommandResponse.Ok("服务指令执行成功。", output.Trim())
            : CommandResponse.Fail($"服务指令失败（退出码 {process.ExitCode}）：{output.Trim()}");
    }

    /// <summary>检查必填路径，并转换为便于服务端处理的绝对路径。</summary>
    private static string RequiredPath(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name}不能为空。");
        return Path.GetFullPath(value);
    }

    /// <summary>目标文件的上级目录不存在时自动创建。</summary>
    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

    /// <summary>限制压缩文件使用 ZIP 扩展名，避免产生格式与扩展名不一致的文件。</summary>
    private static void EnsureZipExtension(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("压缩文件的目标路径必须以 .zip 结尾。");
        if (File.Exists(path)) throw new IOException($"目标压缩文件已经存在：{path}");
    }

    /// <summary>防止把正在生成的 ZIP 放进源文件夹，造成压缩文件包含自身。</summary>
    private static void EnsureArchiveOutsideSource(string source, string destination)
    {
        var sourcePrefix = Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("目标压缩文件不能放在源文件夹内部。");
    }

    /// <summary>限制服务名字符，防止参数被拼接成额外的 sc.exe 指令。</summary>
    private static void ValidateServiceName(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) throw new ArgumentException("服务名称不能为空。");
        if (serviceName.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new ArgumentException("服务名称包含非法字符。");
    }
}
