using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.IO.Compression;

namespace KaiZhongReleaseTool;

/// <summary>
/// 在 WPF 进程内托管 ASP.NET Core 服务，提供健康检查和指令执行接口。
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    private WebApplication? _app;
    /// <summary>服务是否已经成功启动。</summary>
    public bool IsRunning => _app is not null;
    /// <summary>产生服务运行日志时触发，由服务端窗口订阅并显示。</summary>
    public event Action<string>? LogReceived;

    /// <summary>使用指定监听地址启动内嵌 HTTP 服务。</summary>
    public async Task StartAsync(string url)
    {
        if (_app is not null) return;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        // 文件夹上传大小不固定，取消 Kestrel 默认的请求体大小上限。
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
        // 同时解除 multipart/form-data 文件的默认大小限制。
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = long.MaxValue);
        builder.Services.AddSingleton<CommandExecutor>();
        var app = builder.Build();
        // 根地址用于快速判断服务是否在线。
        app.MapGet("/", () => Results.Ok(new { name = "KaiZhongReleaseTool", status = "running" }));
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
        try
        {
            await app.StartAsync();
            _app = app;
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
            tempZip = Path.Combine(Path.GetTempPath(), $"KaiZhongReceive_{Guid.NewGuid():N}.zip");
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
            if (tempZip is not null && File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    /// <summary>程序退出时确保服务已经停止。</summary>
    public async ValueTask DisposeAsync() => await StopAsync();
}
