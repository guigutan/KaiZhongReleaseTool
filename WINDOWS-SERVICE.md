# 凯中发布工具 Windows 服务端

服务端已经拆分为无界面的 Windows 服务，服务名称为 `KaiZhongReleaseToolServer`，默认监听 `5050` 端口。

## 数据目录

服务端上传文件统一保存在：

```text
%ProgramData%\KaiZhong\KaiZhongReleaseTool
```

目录内容：

- `SMOMDLL`：客户端上传的待发布 DLL。
- `Temp`：ASP.NET Core 上传缓冲和 ZIP 临时文件。
- `Update`：自动升级包、升级备份和更新器工作目录。

服务端不创建 `log.db`。发布和回滚业务日志只保存在客户端。

## 首次安装

进入服务发布目录，双击或右键以管理员身份运行：

```text
01-Install-Service.bat
```

辅助管理脚本：

```text
02-Start-Service.bat       启动服务
03-Restart-Service.bat     重启服务
04-Stop-Service.bat        停止服务
05-Uninstall-Service.bat   卸载服务
```

这些 BAT 会自动申请管理员权限。只需要安装 `KaiZhongReleaseToolServer` 一个服务；更新器不是服务，不需要单独安装。

## 生成安装文件和升级包

在源码目录执行：

```powershell
.\Scripts\Build-ServerPackage.ps1 -Version "1.0.1"
```

输出位置：

```text
artifacts\Server\KaiZhongReleaseToolServerFiles           首次安装使用
artifacts\Server\KaiZhongReleaseTool.Server-1.0.1.zip   后续自动升级使用
```

后续在客户端服务器列表右键选择“升级服务端”，选择 ZIP 和多台服务器即可批量升级。

> 安全提醒：自动升级接口未配置身份认证。所有能访问服务器5050端口的设备都可能提交升级包，应通过 Windows 防火墙限制5050端口只允许可信客户端访问。
