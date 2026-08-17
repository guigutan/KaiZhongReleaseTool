using Microsoft.Extensions.Hosting;

namespace KaiZhongReleaseTool;

/// <summary>在 Windows 服务生命周期内启动并保持发布 HTTP 服务运行。</summary>
public sealed class ReleaseServerWorker : BackgroundService
{
    private readonly ServerHost _serverHost;

    public ReleaseServerWorker(ServerHost serverHost)
    {
        _serverHost = serverHost;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AppPaths.EnsureServerDirectories();
        await _serverHost.StartAsync("http://0.0.0.0:5050");

        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _serverHost.StopAsync();
        await base.StopAsync(cancellationToken);
    }
}
