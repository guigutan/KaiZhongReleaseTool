using KaiZhongReleaseTool;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Windows 服务没有任何界面，由服务控制管理器负责开机启动、停止和故障恢复。
await Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "凯中发布工具服务")
    .ConfigureServices(services =>
    {
        services.AddSingleton<ServerHost>();
        services.AddHostedService<ReleaseServerWorker>();
    })
    .Build()
    .RunAsync();
