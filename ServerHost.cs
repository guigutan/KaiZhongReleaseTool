using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Diagnostics;
using System.Xml.Linq;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Reflection;

namespace KaiZhongReleaseTool;

/// <summary>
/// 在 WPF 进程内托管 ASP.NET Core 服务，提供健康检查和指令执行接口。
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _rollbackStoppedServices = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>服务是否已经成功启动。</summary>
    public bool IsRunning => _app is not null;
    /// <summary>产生服务运行日志时触发，由服务端窗口订阅并显示。</summary>
    public event Action<string>? LogReceived;

    /// <summary>使用指定监听地址启动内嵌 HTTP 服务。</summary>
    public async Task StartAsync(string url)
    {
        if (_app is not null) return;

        // ASP.NET Core 会把超过内存阈值的 multipart 上传文件先缓冲到临时目录。
        // 固定使用程序同级 Temp，避免域用户或远程会话返回不存在的 Temp\会话编号目录。
        AppPaths.EnsureServerDirectories();
        var aspNetCoreTempDirectory = AppPaths.ServerTempDirectory;
        Environment.SetEnvironmentVariable(
            "ASPNETCORE_TEMP", aspNetCoreTempDirectory, EnvironmentVariableTarget.Process);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        // 文件夹上传大小不固定，取消 Kestrel 默认的请求体大小上限。
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
        // 同时解除 multipart/form-data 文件的默认大小限制。
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = long.MaxValue);
        builder.Services.AddSingleton<CommandExecutor>();
        var app = builder.Build();
        // 根地址用于快速判断服务是否在线。
        app.MapGet("/", () => Results.Ok(new { name = "KaiZhongReleaseTool.Server", status = "running", apiVersion = 3, version = GetServerVersion(), directoryBrowser = true }));
        app.MapPost("/api/system/update", ReceiveServerUpdateAsync);
        app.MapGet("/api/directories", (string? path) => BrowseServerDirectories(path));
        // 客户端把所有文件和服务指令发送到这个统一接口。
        app.MapPost("/api/command", async (CommandRequest request, CommandExecutor executor, CancellationToken token) =>
        {
            Log($"收到指令：{request.Type}");
            var result = await executor.ExecuteAsync(request, token);
            Log($"执行结果：{(result.Success ? "成功" : "失败")} - {result.Message}");
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });
        // 文件夹会由客户端压缩成 ZIP，以表单文件方式上传到此接口。
        app.MapPost("/api/upload-folder", ReceiveFolderAsync);
        // 发布第一步：接收客户端完整的 SMOMDLL，并替换服务端程序同级目录中的 SMOMDLL。
        app.MapPost("/api/deploy/smomdll", ReceiveSmomDllAsync);
        // 发布第二步：按客户端为该服务器配置的路径创建时间戳 ZIP 备份。
        app.MapPost("/api/deploy/backup", CreateDeploymentBackupAsync);
        app.MapPost("/api/deploy/apply", ApplyDeploymentAsync);
        app.MapPost("/api/deploy/check", CheckDeployment);
        app.MapPost("/api/deploy/stop", StopDeploymentServicesAsync);
        app.MapPost("/api/deploy/publish", PublishDeploymentFilesAsync);
        app.MapPost("/api/deploy/start", StartDeploymentServicesAsync);
        app.MapPost("/api/deploy/backups", ListDeploymentBackups);
        app.MapPost("/api/deploy/rollback", RollbackDeploymentAsync);
        app.MapPost("/api/deploy/rollback-stop", StopRollbackServicesAsync);
        app.MapPost("/api/deploy/rollback-files", RollbackFilesAsync);
        app.MapPost("/api/deploy/rollback-start", StartRollbackServicesAsync);
        try
        {
            await app.StartAsync();
            _app = app;
            Log($"上传临时目录：{aspNetCoreTempDirectory}");
            Log($"服务已启动：{url}");
        }
        catch { await app.DisposeAsync(); throw; }
    }

    /// <summary>停止 HTTP 监听并释放 ASP.NET Core 占用的资源。</summary>
    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        await app.StopAsync();
        await app.DisposeAsync();
        Log("服务已停止。");
    }

    /// <summary>给日志添加时间戳后通知界面。</summary>
    private void Log(string message) => LogReceived?.Invoke($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");

    /// <summary>服务端只把本机实际执行的发布或回滚信息写入同级 log.db。</summary>
    private void WriteServerLog(string? setName, string message, string level, string type = "Push", string? backupFileName = null)
    {
        // 服务端不保存业务日志数据库；完整发布与回滚日志统一由客户端保存。
    }

    /// <summary>接收经过密钥和 SHA-256 校验的服务升级包，并交给独立更新器完成替换和重启。</summary>
    private async Task<IResult> ReceiveServerUpdateAsync(HttpRequest request, CancellationToken token)
    {
        try
        {
            var form = await request.ReadFormAsync(token);
            var package = form.Files.GetFile("updatePackage");
            var expectedHash = form["sha256"].ToString();
            if (package is null || package.Length == 0)
                return Results.BadRequest(CommandResponse.Fail("没有收到服务端升级包。"));

            var updateId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var workDirectory = Path.Combine(AppPaths.ServerUpdateDirectory, updateId);
            Directory.CreateDirectory(workDirectory);
            var packagePath = Path.Combine(workDirectory, "ServerUpdate.zip");
            await using (var output = File.Create(packagePath)) await package.CopyToAsync(output, token);

            await using (var input = File.OpenRead(packagePath))
            {
                using var sha256 = SHA256.Create();
                var actualHash = Convert.ToHexString(await sha256.ComputeHashAsync(input, token));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(CommandResponse.Fail("升级包 SHA-256 校验失败。"));
            }

            using (var archive = ZipFile.OpenRead(packagePath))
                if (!archive.Entries.Any(item => item.FullName.EndsWith("KaiZhongReleaseTool.Server.dll", StringComparison.OrdinalIgnoreCase)))
                    return Results.BadRequest(CommandResponse.Fail("升级包中缺少凯中发布工具服务文件。"));

            var updaterName = "KaiZhongReleaseTool.Server.Updater";
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, updaterName + ".*"))
                File.Copy(file, Path.Combine(workDirectory, Path.GetFileName(file)), true);
            var updaterPath = Path.Combine(workDirectory, updaterName + ".exe");
            if (!File.Exists(updaterPath))
                return Results.BadRequest(CommandResponse.Fail("当前服务端缺少独立更新器，请先重新安装一次服务。"));

            var backupDirectory = Path.Combine(AppPaths.ServerUpdateDirectory, "Backup-" + updateId);
            var startInfo = new ProcessStartInfo(updaterPath) { UseShellExecute = false, CreateNoWindow = true };
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add(packagePath);
            startInfo.ArgumentList.Add(AppContext.BaseDirectory);
            startInfo.ArgumentList.Add(backupDirectory);
            Process.Start(startInfo);

            // 先把“已接受更新”返回客户端，再退出旧服务进程供更新器覆盖。
            _ = Task.Run(async () => { await Task.Delay(1500); Environment.Exit(0); });
            return Results.Accepted(value: CommandResponse.Ok($"升级包已接收，当前版本：{GetServerVersion()}，服务即将自动重启。"));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(CommandResponse.Fail($"服务端自动更新失败：{ex.Message}"));
        }
    }

    private static string GetServerVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

    /// <summary>接收客户端上传的 ZIP，并解压到指定的服务端文件夹。</summary>
    private async Task<IResult> ReceiveFolderAsync(HttpRequest request, CancellationToken token)
    {
        string? tempZip = null;
        try
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(CommandResponse.Fail("上传请求必须使用表单格式。"));

            var form = await request.ReadFormAsync(token);
            var archive = form.Files.GetFile("folderArchive");
            var destinationValue = form["destinationPath"].ToString();
            if (archive is null || archive.Length == 0)
                return Results.BadRequest(CommandResponse.Fail("没有收到文件夹压缩数据。"));
            if (string.IsNullOrWhiteSpace(destinationValue))
                return Results.BadRequest(CommandResponse.Fail("服务端保存路径不能为空。"));

            var destination = Path.GetFullPath(destinationValue);
            tempZip = CreateServerTemporaryPath("KaiZhongReceive", ".zip");
            Log($"开始接收文件夹：{archive.Length:N0} 字节，目标：{destination}");
            await using (var output = File.Create(tempZip))
                await archive.CopyToAsync(output, token);

            Directory.CreateDirectory(destination);
            // .NET 的解压方法会拒绝逃逸目标目录的 ZIP 条目，防止路径穿越。
            ZipFile.ExtractToDirectory(tempZip, destination, overwriteFiles: true);
            var result = CommandResponse.Ok($"文件夹上传成功：{destination}", destination);
            Log(result.Message);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            Log($"文件夹上传失败：{ex.Message}");
            return Results.BadRequest(CommandResponse.Fail(ex.Message));
        }
        finally
        {
            TryDeleteTemporaryFile(tempZip);
        }
    }

    /// <summary>接收 SMOMDLL 压缩包，先清空服务端旧目录，再解压本次上传内容。</summary>
    private async Task<IResult> ReceiveSmomDllAsync(HttpRequest request, CancellationToken token)
    {
        string? tempZip = null;
        try
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(CommandResponse.Fail("发布请求必须使用表单格式。"));
            var form = await request.ReadFormAsync(token);
            var archive = form.Files.GetFile("smomDllArchive");
            var logSetName = form["logSetName"].ToString();
            if (archive is null || archive.Length == 0)
                return Results.BadRequest(CommandResponse.Fail("没有收到 SMOMDLL 压缩数据。"));

            // 必须先完整接收到系统临时目录，确认上传完成后才清理正式目录。
            // 使用程序同级临时目录，避免远程会话返回了不存在的“Temp\会话编号”目录。
            var tempDirectory = AppPaths.ServerTempDirectory;
            Directory.CreateDirectory(tempDirectory);
            tempZip = Path.Combine(tempDirectory, $"KaiZhongSmomDll_{Guid.NewGuid():N}.zip");
            await using (var output = File.Create(tempZip))
                await archive.CopyToAsync(output, token);

            var targetDirectory = AppPaths.ServerSmomDllDirectory;
            Log($"开始更新 SMOMDLL：收到 {archive.Length:N0} 字节，目标：{targetDirectory}");
            if (Directory.Exists(targetDirectory))
            {
                try { Directory.Delete(targetDirectory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"无法清空服务端 SMOMDLL，文件可能被占用：{ex.Message}", ex);
                }
            }
            Directory.CreateDirectory(targetDirectory);
            ZipFile.ExtractToDirectory(tempZip, targetDirectory, overwriteFiles: true);
            var dllCount = Directory.GetFiles(targetDirectory, "*.dll", SearchOption.AllDirectories).Length;
            var result = CommandResponse.Ok($"SMOMDLL 同步成功，共接收 {dllCount} 个 DLL。", new { TargetDirectory = targetDirectory, DllCount = dllCount });
            WriteServerLog(logSetName, result.Message, "Success");
            Log(result.Message);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            Log($"SMOMDLL 同步失败：{ex.Message}");
            return Results.BadRequest(CommandResponse.Fail(ex.Message));
        }
        finally
        {
            TryDeleteTemporaryFile(tempZip);
        }
    }

    /// <summary>根据客户端配置备份服务端目录，ZIP 文件名使用客户端发出的时间戳。</summary>
    private Task<IResult> CreateDeploymentBackupAsync(ServerBackupRequest request, CancellationToken token)
    {
        try
        {
            if (!DateTime.TryParseExact(request.Timestamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return Task.FromResult(Results.BadRequest(CommandResponse.Fail("备份时间戳格式无效。")) as IResult);

            var smomRoot = AppPaths.ServerSmomDllDirectory;
            var configuredPaths = new[]
            {
                (Label: "SIE.ScheduleServer", Path: request.ScheduleServerPath),
                (Label: "SIE.WebApiHost", Path: request.WebApiHostPath),
                (Label: "WebClient", Path: request.WebClientPath),
                (Label: "WpfClient", Path: request.WpfClientPath)
            }.Where(item => !string.IsNullOrWhiteSpace(item.Path)
                && Directory.Exists(Path.Combine(smomRoot, item.Label))
                && Directory.EnumerateFiles(Path.Combine(smomRoot, item.Label), "*", SearchOption.AllDirectories).Any()).ToArray();

            if (configuredPaths.Length == 0)
                return Task.FromResult(Results.Ok(CommandResponse.Ok("未配置备份目录，已跳过备份。")) as IResult);

            var sources = configuredPaths.Select(item =>
            {
                var fullPath = Path.GetFullPath(item.Path!);
                if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"{item.Label} 备份目录不存在：{fullPath}");
                return (item.Label, FullPath: fullPath);
            }).ToArray();

            if (string.IsNullOrWhiteSpace(request.BackupDestinationPath))
                return Task.FromResult(Results.BadRequest(CommandResponse.Fail("已配置备份源目录，但“备份到”目录为空。")) as IResult);
            var backupDirectory = Path.GetFullPath(request.BackupDestinationPath);
            foreach (var source in sources)
            {
                var sourcePrefix = Path.TrimEndingDirectorySeparator(source.FullPath) + Path.DirectorySeparatorChar;
                if (backupDirectory.Equals(Path.TrimEndingDirectorySeparator(source.FullPath), StringComparison.OrdinalIgnoreCase) ||
                    backupDirectory.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(Results.BadRequest(CommandResponse.Fail($"“备份到”目录不能位于备份源目录内部：{source.FullPath}")) as IResult);
            }
            Directory.CreateDirectory(backupDirectory);
            var zipPath = Path.Combine(backupDirectory, request.Timestamp + ".zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);

            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            var usedRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileCount = 0;
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                var rootName = source.Label;
                archive.CreateEntry(rootName.TrimEnd('/') + "/");
                foreach (var file in EnumerateBackupFiles(source.FullPath, token))
                {
                    var relativePath = Path.GetRelativePath(source.FullPath, file).Replace(Path.DirectorySeparatorChar, '/');
                    archive.CreateEntryFromFile(file, $"{rootName}/{relativePath}", CompressionLevel.Optimal);
                    fileCount++;
                }
            }
            var result = CommandResponse.Ok($"备份成功：{Path.GetFileName(zipPath)}，共 {fileCount} 个文件。", new { ZipPath = zipPath, FileCount = fileCount });
            WriteServerLog(request.LogSetName, $"{Path.GetFileName(zipPath)} 备份✔", "Success");
            Log(result.Message);
            return Task.FromResult(Results.Ok(result) as IResult);
        }
        catch (Exception ex)
        {
            Log($"备份失败：{ex.Message}");
            return Task.FromResult(Results.BadRequest(CommandResponse.Fail(ex.Message)) as IResult);
        }
    }

    /// <summary>备份成功后依次发布四个应用；路径留空的应用不会执行任何操作。</summary>
    private async Task<IResult> ApplyDeploymentAsync(DeploymentApplyRequest request, CancellationToken token)
    {
        try
        {
            var smomRoot = AppPaths.ServerSmomDllDirectory;
            var messages = new List<string>();
            var success = true;
            success &= await ApplyApplicationAsync("SIE.ScheduleServer", Path.Combine(smomRoot, "SIE.ScheduleServer"), request.ScheduleServerPath, request.ScheduleServerServices, false, messages, token);
            success &= await ApplyApplicationAsync("SIE.WebApiHost", Path.Combine(smomRoot, "SIE.WebApiHost"), request.WebApiHostPath, request.WebApiHostServices, false, messages, token);
            success &= await ApplyApplicationAsync("WebClient", Path.Combine(smomRoot, "WebClient"), request.WebClientPath, request.WebClientServices, false, messages, token);
            success &= await ApplyApplicationAsync("WpfClient", Path.Combine(smomRoot, "WpfClient"), request.WpfClientPath, request.WpfClientServices, true, messages, token);
            var result = success ? CommandResponse.Ok("发布完成：" + string.Join("；", messages)) : CommandResponse.Fail("发布存在失败项：" + string.Join("；", messages));
            Log(result.Message);
            return success ? Results.Ok(result) : Results.BadRequest(result);
        }
        catch (Exception ex)
        {
            Log($"发布失败：{ex.Message}");
            return Results.BadRequest(CommandResponse.Fail(ex.Message));
        }
    }

    /// <summary>检查四个应用是否有待发布文件，以及所需路径和服务是否已配置。</summary>
    private IResult CheckDeployment(DeploymentApplyRequest request)
    {
        var root = AppPaths.ServerSmomDllDirectory;
        var items = GetApplicationSettings(request).Select(item =>
        {
            var source = Path.Combine(root, item.Name);
            var hasFiles = Directory.Exists(source) && Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Any();
            var backupPathConfigured = !string.IsNullOrWhiteSpace(item.Path);
            var backupPathExists = backupPathConfigured && IsExistingDirectory(item.Path!);
            var serviceNames = ParseServiceNames(item.Services);
            var servicesConfigured = serviceNames.Length > 0;
            var servicesExist = servicesConfigured && serviceNames.All(ServiceExists);

            // 未配置路径表示本服务器不发布该应用，不因上传目录中有文件而阻断。
            // 已配置的路径、服务必须真实存在；除 WpfClient 外，配置路径后必须配置服务。
            var configuredPathPassed = !backupPathConfigured || backupPathExists;
            var configuredServicesPassed = !servicesConfigured || servicesExist;
            var requiredServicePassed = item.Name == "WpfClient" || !backupPathConfigured || servicesConfigured;
            var success = configuredPathPassed && configuredServicesPassed && requiredServicePassed;
            return new DeploymentStageItem
            {
                ApplicationName = item.Name,
                HasFiles = hasFiles,
                HasBackupPath = backupPathExists,
                HasServices = servicesExist,
                BackupPathConfigured = backupPathConfigured,
                BackupPathExists = backupPathExists,
                ServicesConfigured = servicesConfigured,
                ServicesExist = servicesExist,
                Success = success
            };
        }).ToList();
        var success = items.All(item => item.Success);
        return Results.Json(new DeploymentStageResponse { Success = success, Message = success ? "服务器配置检查通过。" : "存在不满足发布条件的服务器配置。", Items = items }, statusCode: success ? 200 : 400);
    }

    /// <summary>安全检查服务端目录是否真实存在，非法路径按不存在处理。</summary>
    private static bool IsExistingDirectory(string path)
    {
        try { return Directory.Exists(Path.GetFullPath(path)); }
        catch { return false; }
    }

    /// <summary>通过 Windows 服务注册表项判断服务名是否真实存在。</summary>
    private static bool ServiceExists(string serviceName)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key is not null;
        }
        catch { return false; }
    }

    /// <summary>停止所有本次有待发布文件的应用服务；服务名为空时直接跳过。</summary>
    private async Task<IResult> StopDeploymentServicesAsync(DeploymentApplyRequest request, CancellationToken token)
    {
        var root = AppPaths.ServerSmomDllDirectory;
        var results = new List<DeploymentStageItem>();
        foreach (var app in GetApplicationSettings(request))
        {
            if (string.IsNullOrWhiteSpace(app.Path)) continue;
            var source = Path.Combine(root, app.Name);
            if (!Directory.Exists(source) || !Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Any()) continue;
            foreach (var service in ParseServiceNames(app.Services))
            {
                var result = await RunServiceWithRetryAsync("stop", service, 5, token);
                results.Add(new DeploymentStageItem { ApplicationName = app.Name, ServiceName = service, Success = result.Success, Attempts = result.Attempts, Message = result.Message });
            }
        }
        var success = results.All(item => item.Success);
        foreach (var item in results) WriteServerLog(request.LogSetName, $"{item.ApplicationName}${item.ServiceName} 停止{(item.Success ? "✔" : "×")}", item.Success ? "Success" : "Error");
        return Results.Json(new DeploymentStageResponse { Success = success, Message = success ? "服务停止完成。" : "部分服务停止失败。", Items = results }, statusCode: success ? 200 : 400);
    }

    /// <summary>仅覆盖本次有文件的应用，不在此阶段控制服务。</summary>
    private async Task<IResult> PublishDeploymentFilesAsync(DeploymentApplyRequest request, CancellationToken token)
    {
        var root = AppPaths.ServerSmomDllDirectory;
        var results = new List<DeploymentStageItem>();
        foreach (var app in GetApplicationSettings(request))
        {
            if (string.IsNullOrWhiteSpace(app.Path)) continue;
            var source = Path.Combine(root, app.Name);
            if (!Directory.Exists(source) || !Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Any()) continue;
            string? version = null;
            var result = await RetryAsync(() =>
            {
                if (app.Name == "WpfClient") version = UpdateWpfPackage(source, Path.GetFullPath(app.Path));
                else CopyDirectoryFiles(source, Path.GetFullPath(app.Path));
            }, 10, TimeSpan.FromSeconds(3), token);
            results.Add(new DeploymentStageItem { ApplicationName = app.Name, Success = result.Success, Attempts = result.Attempts, Message = result.Message, Version = version });
        }
        var success = results.All(item => item.Success);
        foreach (var item in results) WriteServerLog(request.LogSetName, $"{item.ApplicationName} 发布{(item.Success ? "✔" : "×")}{(item.Version is null ? string.Empty : " 当前版本" + item.Version)}", item.Success ? "Success" : "Error");
        return Results.Json(new DeploymentStageResponse { Success = success, Message = success ? "应用发布完成。" : "部分应用发布失败。", Items = results }, statusCode: success ? 200 : 400);
    }

    /// <summary>启动所有本次有待发布文件的应用服务，每个服务失败后最多重试十次。</summary>
    private async Task<IResult> StartDeploymentServicesAsync(DeploymentApplyRequest request, CancellationToken token)
    {
        var root = AppPaths.ServerSmomDllDirectory;
        var results = new List<DeploymentStageItem>();
        foreach (var app in GetApplicationSettings(request))
        {
            if (string.IsNullOrWhiteSpace(app.Path)) continue;
            var source = Path.Combine(root, app.Name);
            if (!Directory.Exists(source) || !Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Any()) continue;
            foreach (var service in ParseServiceNames(app.Services))
            {
                var result = await RunServiceWithRetryAsync("start", service, 10, token);
                results.Add(new DeploymentStageItem { ApplicationName = app.Name, ServiceName = service, Success = result.Success, Attempts = result.Attempts, Message = result.Message });
            }
        }
        var success = results.All(item => item.Success);
        foreach (var item in results) WriteServerLog(request.LogSetName, $"{item.ApplicationName}${item.ServiceName} 启动{(item.Success ? "✔" : "×")}", item.Success ? "Success" : "Error");
        return Results.Json(new DeploymentStageResponse { Success = success, Message = success ? "服务启动完成。" : "部分服务启动失败。", Items = results }, statusCode: success ? 200 : 400);
    }

    private static (string Name, string? Path, string? Services)[] GetApplicationSettings(DeploymentApplyRequest request) => new[]
    {
        ("SIE.ScheduleServer", request.ScheduleServerPath, request.ScheduleServerServices),
        ("SIE.WebApiHost", request.WebApiHostPath, request.WebApiHostServices),
        ("WebClient", request.WebClientPath, request.WebClientServices),
        ("WpfClient", request.WpfClientPath, request.WpfClientServices)
    };

    /// <summary>列出服务器备份目录中的 ZIP，供客户端选择回滚版本。</summary>
    private static IResult ListDeploymentBackups(DeploymentRollbackRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.BackupDestinationPath)) return Results.BadRequest(CommandResponse.Fail("未配置“备份到”目录。"));
            var directory = Path.GetFullPath(request.BackupDestinationPath);
            if (!Directory.Exists(directory)) return Results.BadRequest(CommandResponse.Fail($"备份目录不存在：{directory}"));
            var files = Directory.GetFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTime)
                .Select(file => file.Name).ToArray();
            return Results.Ok(CommandResponse.Ok($"找到 {files.Length} 个备份文件。", files));
        }
        catch (Exception ex) { return Results.BadRequest(CommandResponse.Fail(ex.Message)); }
    }

    /// <summary>根据所选备份实际包含的应用停止对应服务。</summary>
    private async Task<IResult> StopRollbackServicesAsync(DeploymentRollbackRequest request, CancellationToken token)
    {
        try
        {
            var apps = GetRollbackApplications(request); var items = new List<DeploymentStageItem>();
            foreach (var app in GetApplicationSettings(request).Where(item => apps.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
                foreach (var service in ParseServiceNames(app.Services))
                {
                    var result = await RunServiceWithRetryAsync("stop", service, 5, token);
                    if (result.Success && !string.IsNullOrWhiteSpace(request.LogSetName))
                        _rollbackStoppedServices.GetOrAdd(request.LogSetName, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase)).TryAdd(service, 0);
                    items.Add(new DeploymentStageItem { ApplicationName = app.Name, ServiceName = service, Success = result.Success, Attempts = result.Attempts, Message = result.Message });
                    WriteServerLog(request.LogSetName, $"{app.Name}${service} 停止{(result.Success ? "✔" : "×")}", result.Success ? "Success" : "Error", "RollBack", request.BackupFileName);
                }
            var success = items.All(item => item.Success);
            return Results.Json(new DeploymentStageResponse { Success = success, Message = success ? "回滚服务停止完成。" : "部分服务停止失败。", Items = items }, statusCode: success ? 200 : 400);
        }
        catch (Exception ex) { return Results.BadRequest(new DeploymentStageResponse { Success = false, Message = ex.Message }); }
    }

    /// <summary>不创建新备份，只将所选 ZIP 的应用目录完整替换回去。</summary>
    private async Task<IResult> RollbackFilesAsync(DeploymentRollbackRequest request, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            temporary = CreateServerTemporaryPath("KaiZhongRollbackFiles");
            ZipFile.ExtractToDirectory(ResolveRollbackZip(request), temporary);
            var items = new List<DeploymentStageItem>();
            foreach (var app in GetApplicationSettings(request))
            {
                var source = Path.Combine(temporary, app.Name); if (!Directory.Exists(source)) continue;
                if (string.IsNullOrWhiteSpace(app.Path)) { items.Add(new DeploymentStageItem { ApplicationName = app.Name, Success = false, Message = "未配置还原目录。" }); continue; }
                var retry = await RetryAsync(() => ReplaceDirectoryFromBackup(source, Path.GetFullPath(app.Path)), 10, TimeSpan.FromSeconds(3), token);
                var version = app.Name == "WpfClient" && retry.Success ? ReadPluginsVersion(Path.Combine(app.Path, "Manifest.xml")) : null;
                items.Add(new DeploymentStageItem { ApplicationName = app.Name, Success = retry.Success, Attempts = retry.Attempts, Message = retry.Message, Version = version });
                WriteServerLog(request.LogSetName, $"{app.Name} 回滚{(retry.Success ? "✔" : "×")}{(version is null ? string.Empty : " 当前版本" + version)}", retry.Success ? "Success" : "Error", "RollBack", request.BackupFileName);
            }
            var success = items.All(item => item.Success);
            return Results.Json(new DeploymentStageResponse { Success = success, Message = success ? "备份集回滚完成。" : "部分应用回滚失败。", Items = items }, statusCode: success ? 200 : 400);
        }
        catch (Exception ex) { return Results.BadRequest(new DeploymentStageResponse { Success = false, Message = ex.Message }); }
        finally { TryDeleteTemporaryDirectory(temporary); }
    }

    /// <summary>根据所选备份包含的应用启动对应服务。</summary>
    private async Task<IResult> StartRollbackServicesAsync(DeploymentRollbackRequest request, CancellationToken token)
    {
        try
        {
            var apps = GetRollbackApplications(request); var items = new List<DeploymentStageItem>();
            var stoppedServices = !string.IsNullOrWhiteSpace(request.LogSetName) && _rollbackStoppedServices.TryGetValue(request.LogSetName, out var recorded) ? recorded.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in GetApplicationSettings(request).Where(item => apps.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
                foreach (var service in ParseServiceNames(app.Services).Where(stoppedServices.Contains))
                {
                    var result = await RunServiceWithRetryAsync("start", service, 10, token);
                    items.Add(new DeploymentStageItem { ApplicationName = app.Name, ServiceName = service, Success = result.Success, Attempts = result.Attempts, Message = result.Message });
                    WriteServerLog(request.LogSetName, $"{app.Name}${service} 启动{(result.Success ? "✔" : "×")}", result.Success ? "Success" : "Error", "RollBack", request.BackupFileName);
                }
            var success = items.All(item => item.Success);
            if (!string.IsNullOrWhiteSpace(request.LogSetName)) _rollbackStoppedServices.TryRemove(request.LogSetName, out _);
            return Results.Json(new DeploymentStageResponse { Success = success, Message = success ? "回滚服务启动完成。" : "部分服务启动失败。", Items = items }, statusCode: success ? 200 : 400);
        }
        catch (Exception ex) { return Results.BadRequest(new DeploymentStageResponse { Success = false, Message = ex.Message }); }
    }

    private static string ResolveRollbackZip(DeploymentRollbackRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BackupDestinationPath) || string.IsNullOrWhiteSpace(request.BackupFileName)) throw new InvalidOperationException("备份目录和备份文件不能为空。");
        var directory = Path.GetFullPath(request.BackupDestinationPath); var zip = Path.GetFullPath(Path.Combine(directory, request.BackupFileName));
        if (!zip.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(zip)) throw new FileNotFoundException("选择的备份文件不存在或路径无效。", zip);
        return zip;
    }

    private static string[] GetRollbackApplications(DeploymentRollbackRequest request)
    {
        using var archive = ZipFile.OpenRead(ResolveRollbackZip(request));
        return archive.Entries.Select(entry => entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()).Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? ReadPluginsVersion(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return null;
        var document = XDocument.Load(manifestPath);
        return document.Descendants("Module").FirstOrDefault(item => string.Equals(item.Element("Name")?.Value.Trim(), "Plugins", StringComparison.OrdinalIgnoreCase))?.Element("Version")?.Value.Trim();
    }

    /// <summary>停止对应服务，把所选 ZIP 的应用目录还原到配置路径，最后恢复服务。</summary>
    private async Task<IResult> RollbackDeploymentAsync(DeploymentRollbackRequest request, CancellationToken token)
    {
        string? tempDirectory = null;
        try
        {
            if (string.IsNullOrWhiteSpace(request.BackupDestinationPath) || string.IsNullOrWhiteSpace(request.BackupFileName))
                return Results.BadRequest(CommandResponse.Fail("备份目录和备份文件不能为空。"));
            var backupDirectory = Path.GetFullPath(request.BackupDestinationPath);
            var zipPath = Path.GetFullPath(Path.Combine(backupDirectory, request.BackupFileName));
            if (!zipPath.StartsWith(Path.TrimEndingDirectorySeparator(backupDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(zipPath))
                return Results.BadRequest(CommandResponse.Fail("选择的备份文件不存在或路径无效。"));
            tempDirectory = CreateServerTemporaryPath("KaiZhongRollback");
            ZipFile.ExtractToDirectory(zipPath, tempDirectory);
            var messages = new List<string>();
            var success = true;
            success &= await RestoreApplicationAsync("SIE.ScheduleServer", request.ScheduleServerPath, request.ScheduleServerServices, tempDirectory, messages, token);
            success &= await RestoreApplicationAsync("SIE.WebApiHost", request.WebApiHostPath, request.WebApiHostServices, tempDirectory, messages, token);
            success &= await RestoreApplicationAsync("WebClient", request.WebClientPath, request.WebClientServices, tempDirectory, messages, token);
            success &= await RestoreApplicationAsync("WpfClient", request.WpfClientPath, request.WpfClientServices, tempDirectory, messages, token);
            var result = success
                ? CommandResponse.Ok($"回滚 {request.BackupFileName} 完成：" + string.Join("；", messages))
                : CommandResponse.Fail($"回滚 {request.BackupFileName} 存在失败项：" + string.Join("；", messages));
            Log(result.Message);
            WriteServerLog(request.LogSetName, result.Message, success ? "Success" : "Error", "RollBack", request.BackupFileName);
            return success ? Results.Ok(result) : Results.BadRequest(result);
        }
        catch (Exception ex) { Log($"回滚失败：{ex.Message}"); return Results.BadRequest(CommandResponse.Fail(ex.Message)); }
        finally { TryDeleteTemporaryDirectory(tempDirectory); }
    }

    private async Task<bool> ApplyApplicationAsync(string appName, string source, string? destination, string? serviceNames, bool updateWpfPackage, List<string> messages, CancellationToken token)
    {
        if (!Directory.Exists(source) || !Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Any()) { messages.Add($"{appName} 无待发布文件，已跳过"); return true; }
        if (string.IsNullOrWhiteSpace(destination)) { messages.Add($"{appName} 未配置目标目录，已跳过"); return true; }
        var stopResult = await StopServicesAsync(serviceNames, token);
        if (!stopResult.Success)
        {
            var recovery = await StartServicesWithRetryAsync(stopResult.StoppedServices, token);
            messages.Add($"{appName} 停止服务失败，未覆盖文件：{stopResult.Message}；恢复服务：{recovery.Message}");
            return false;
        }
        var copyResult = await RetryAsync(() =>
        {
            if (updateWpfPackage) UpdateWpfPackage(source, Path.GetFullPath(destination));
            else CopyDirectoryFiles(source, Path.GetFullPath(destination));
        }, 10, TimeSpan.FromSeconds(3), token);
        var startResult = await StartServicesWithRetryAsync(stopResult.StoppedServices, token);
        messages.Add($"{appName} 停止服务成功；覆盖{(copyResult.Success ? "成功" : "失败")}（尝试 {copyResult.Attempts} 次）{(copyResult.Success ? string.Empty : "：" + copyResult.Message)}；启动服务{(startResult.Success ? "成功" : "失败")}（最多尝试 {startResult.Attempts} 次）{(startResult.Success ? string.Empty : "：" + startResult.Message)}");
        return copyResult.Success && startResult.Success;
    }

    private async Task<bool> RestoreApplicationAsync(string appName, string? destination, string? serviceNames, string extractedRoot, List<string> messages, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(destination)) return true;
        var source = Path.Combine(extractedRoot, appName);
        if (!Directory.Exists(source)) { messages.Add($"{appName} 在备份中不存在，已跳过"); return true; }
        var stopResult = await StopServicesAsync(serviceNames, token);
        if (!stopResult.Success)
        {
            var recovery = await StartServicesWithRetryAsync(stopResult.StoppedServices, token);
            messages.Add($"{appName} 停止服务失败，未执行回滚：{stopResult.Message}；恢复服务：{recovery.Message}");
            return false;
        }
        var restoreResult = await RetryAsync(() => ReplaceDirectoryFromBackup(source, Path.GetFullPath(destination)), 10, TimeSpan.FromSeconds(3), token);
        var startResult = await StartServicesWithRetryAsync(stopResult.StoppedServices, token);
        messages.Add($"{appName} 完整还原{(restoreResult.Success ? "成功" : "失败")}（尝试 {restoreResult.Attempts} 次）{(restoreResult.Success ? string.Empty : "：" + restoreResult.Message)}；启动服务{(startResult.Success ? "成功" : "失败")}{(startResult.Success ? string.Empty : "：" + startResult.Message)}");
        return restoreResult.Success && startResult.Success;
    }

    /// <summary>回滚时先清空目标目录，再完整复制历史备份，确保发布后新增的文件也被移除。</summary>
    private static void ReplaceDirectoryFromBackup(string source, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        CopyDirectoryFiles(source, destination);
    }

    private static void CopyDirectoryFiles(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), true);
    }

    private static string UpdateWpfPackage(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        var pluginsPath = Path.Combine(destination, "Plugins.zip");
        if (!File.Exists(pluginsPath)) throw new FileNotFoundException("WpfClient 目标目录中不存在 Plugins.zip。", pluginsPath);
        using (var archive = ZipFile.Open(pluginsPath, ZipArchiveMode.Update))
        {
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var entryName = Path.GetRelativePath(source, file).Replace('\\', '/');
                archive.GetEntry(entryName)?.Delete();
                archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
            }
        }
        var manifestPath = Path.Combine(destination, "Manifest.xml");
        var document = XDocument.Load(manifestPath, LoadOptions.PreserveWhitespace);
        var module = document.Descendants("Module").FirstOrDefault(item => string.Equals(item.Element("Name")?.Value.Trim(), "Plugins", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Manifest.xml 中未找到 Plugins 模块。");
        var versionElement = module.Element("Version") ?? throw new InvalidDataException("Plugins 模块中未找到 Version。 ");
        var parts = versionElement.Value.Trim().Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[^1], out var last)) throw new InvalidDataException("Plugins 版本号格式无效。");
        parts[^1] = checked(last + 1).ToString(CultureInfo.InvariantCulture);
        versionElement.Value = string.Join('.', parts);
        document.Save(manifestPath);
        return versionElement.Value;
    }

    private static string[] ParseServiceNames(string? value) => string.IsNullOrWhiteSpace(value)
        ? Array.Empty<string>()
        : value.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private async Task<(bool Success, List<string> StoppedServices, string Message)> StopServicesAsync(string? serviceNames, CancellationToken token)
    {
        var stopped = new List<string>();
        foreach (var service in ParseServiceNames(serviceNames))
        {
            var result = await RunScAsync("stop", service, token);
            if (!result.Success) return (false, stopped, $"停止服务 {service} 失败：{result.Message}");
            stopped.Add(service);
        }
        return (true, stopped, stopped.Count == 0 ? "未配置服务，无需停止" : $"已停止 {stopped.Count} 个服务");
    }

    private static async Task StartServicesAsync(IEnumerable<string> services, CancellationToken token)
    {
        var errors = new List<string>();
        foreach (var service in services.Reverse())
        {
            var result = await RunScAsync("start", service, token);
            if (!result.Success) errors.Add($"{service}：{result.Message}");
        }
        if (errors.Count > 0) throw new InvalidOperationException("启动服务失败：" + string.Join("；", errors));
    }

    /// <summary>逐个启动服务，每个失败的服务最多尝试十次，每次间隔三秒。</summary>
    private static async Task<(bool Success, int Attempts, string Message)> StartServicesWithRetryAsync(IEnumerable<string> services, CancellationToken token)
    {
        var serviceArray = services.Reverse().ToArray();
        if (serviceArray.Length == 0) return (true, 0, "无需启动服务");
        var maximumAttempts = 0;
        var errors = new List<string>();
        foreach (var service in serviceArray)
        {
            CommandResponse? last = null;
            var attempts = 0;
            for (var attempt = 1; attempt <= 10; attempt++)
            {
                attempts = attempt; last = await RunScAsync("start", service, token);
                if (last.Success) break;
                if (attempt < 10) await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
            maximumAttempts = Math.Max(maximumAttempts, attempts);
            if (last?.Success != true) errors.Add($"{service}：{last?.Message}");
        }
        return errors.Count == 0 ? (true, maximumAttempts, $"已启动 {serviceArray.Length} 个服务") : (false, maximumAttempts, string.Join("；", errors));
    }

    /// <summary>执行可能因文件占用失败的操作，失败后按固定间隔重试。</summary>
    private static async Task<(bool Success, int Attempts, string Message)> RetryAsync(Action action, int maximumAttempts, TimeSpan delay, CancellationToken token)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try { action(); return (true, attempt, "成功"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (attempt < maximumAttempts) await Task.Delay(delay, token);
            }
        }
        return (false, maximumAttempts, last?.Message ?? "未知错误");
    }

    private static async Task<CommandResponse> RunScAsync(string action, string service, CancellationToken token)
    {
        var start = new ProcessStartInfo("sc.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(action); start.ArgumentList.Add(service);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动服务控制程序 sc.exe。");
        var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(token);
        var text = ((await outputTask) + Environment.NewLine + (await errorTask)).Trim();
        return process.ExitCode == 0 ? CommandResponse.Ok(text) : CommandResponse.Fail(text);
    }

    /// <summary>按指定次数重试服务启停，并返回最终状态和实际尝试次数。</summary>
    private static async Task<(bool Success, int Attempts, string Message)> RunServiceWithRetryAsync(string action, string service, int maximumAttempts, CancellationToken token)
    {
        CommandResponse? last = null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            last = await RunScAsync(action, service, token);
            if (last.Success) return (true, attempt, last.Message);
            if (attempt < maximumAttempts) await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
        return (false, maximumAttempts, last?.Message ?? "未知错误");
    }

    /// <summary>递归枚举备份文件，并跳过目录连接点，防止循环遍历。</summary>
    private static IEnumerable<string> EnumerateBackupFiles(string rootPath, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory)) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            }
        }
    }

    /// <summary>返回服务端磁盘或指定目录下的子文件夹，供客户端远程选择路径。</summary>
    private static IResult BrowseServerDirectories(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => new RemoteDirectoryEntry
                {
                    Name = $"{drive.Name}  {drive.VolumeLabel}",
                    FullPath = drive.RootDirectory.FullName
                }).ToList();
                return Results.Ok(new DirectoryBrowseResponse { Success = true, Message = "请选择磁盘。", Directories = drives });
            }

            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
                return Results.BadRequest(new DirectoryBrowseResponse { Success = false, Message = $"服务端目录不存在：{fullPath}" });
            var directories = new List<RemoteDirectoryEntry>();
            foreach (var directory in Directory.EnumerateDirectories(fullPath))
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    directories.Add(new RemoteDirectoryEntry { Name = info.Name, FullPath = info.FullName });
                }
                catch { }
            }
            directories.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            return Results.Ok(new DirectoryBrowseResponse
            {
                Success = true,
                CurrentPath = fullPath,
                ParentPath = Directory.GetParent(fullPath)?.FullName,
                Message = directories.Count == 0 ? "当前目录没有子文件夹。" : $"共 {directories.Count} 个子文件夹。",
                Directories = directories
            });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new DirectoryBrowseResponse { Success = false, Message = ex.Message });
        }
    }

    /// <summary>程序退出时确保服务已经停止。</summary>
    /// <summary>生成程序同级 Temp 下的唯一临时文件或目录路径。</summary>
    private static string CreateServerTemporaryPath(string prefix, string extension = "")
    {
        var tempRoot = AppPaths.ServerTempDirectory;
        Directory.CreateDirectory(tempRoot);
        return Path.Combine(tempRoot, $"{prefix}_{Guid.NewGuid():N}{extension}");
    }

    /// <summary>清理临时文件；清理失败不覆盖原始业务执行结果。</summary>
    private void TryDeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { File.Delete(path); }
        catch (Exception ex) { Log($"临时文件清理失败：{path}；{ex.Message}"); }
    }

    /// <summary>清理临时目录；清理失败不覆盖原始业务执行结果。</summary>
    private void TryDeleteTemporaryDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Log($"临时目录清理失败：{path}；{ex.Message}"); }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
