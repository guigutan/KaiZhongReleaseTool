# 凯中发布工具操作说明

本文档面向发布人员、IT运维人员和项目维护人员，详细说明客户端、Windows服务端的安装与日常操作，以及代码修改后的服务端升级方法。

> **重要：发布工具会备份和覆盖远程服务器文件，并停止、启动Windows服务。正式操作前必须先在测试服务器验证完整发布与回滚流程。**

## 1. 系统组成

| 组成 | 项目 | 用途 |
|---|---|---|
| WPF客户端 | `KaiZhongReleaseTool.csproj` | 管理服务器、获取DLL、发布、回滚、查看日志、触发服务端升级 |
| Windows服务端 | `KaiZhongReleaseTool.Server` | 监听5050端口，执行上传、备份、覆盖、服务启停和回滚 |
| 独立更新器 | `KaiZhongReleaseTool.Server.Updater` | 停止旧服务、覆盖新版本、重启、健康检查及失败恢复 |

Windows服务名称：

```text
KaiZhongReleaseToolServer
```

默认监听及健康检查地址：

```text
http://服务器IP:5050/
```

## 2. 环境要求

### 2.1 开发/打包电脑

- Windows 10/11或Windows Server；
- .NET 6 SDK；
- Visual Studio 2022，建议安装“.NET桌面开发”和“ASP.NET与Web开发”；
- 可访问NuGet，或本机已有完整NuGet缓存。

### 2.2 客户端电脑

- Windows系统；
- .NET 6 Desktop Runtime；
- 对客户端目录有读写权限，因为数据库、DLL清单和待发布文件都保存在程序目录。

### 2.3 目标服务器

- Windows Server；
- .NET 6 Runtime及ASP.NET Core 6 Runtime；
- 首次安装需要管理员权限；
- Windows服务账户必须能读写发布目录、备份目录和ProgramData数据目录，并能启停所配置服务；
- 防火墙允许可信客户端访问TCP 5050。

> **安全重点：5050当前使用HTTP，服务端升级接口没有身份认证。必须用Windows防火墙或网络ACL限制来源IP，严禁向互联网开放5050。**

## 3. 编译解决方案

在解决方案根目录执行：

```powershell
dotnet restore KaiZhongReleaseTool.sln
dotnet build KaiZhongReleaseTool.sln -c Release --no-restore
```

客户端输出目录通常为：

```text
bin\Release\net6.0-windows
```

服务端不要从各项目`bin`目录手工拼装，必须使用统一打包脚本。

## 4. 服务端首次安装

### 4.1 生成安装目录和升级包

版本号应逐次递增：

```powershell
.\Scripts\Build-ServerPackage.ps1 -Version "1.0.3"
```

生成：

```text
artifacts\Server\KaiZhongReleaseToolServerFiles
artifacts\Server\KaiZhongReleaseTool.Server-1.0.3.zip
```

- `KaiZhongReleaseToolServerFiles`用于首次安装；
- `KaiZhongReleaseTool.Server-版本号.zip`用于后续自动升级。

### 4.2 复制并安装服务

1. 把整个`KaiZhongReleaseToolServerFiles`复制到服务器固定目录，例如：

   ```text
   D:\KaiZhongReleaseToolServerFiles
   ```

2. **不要只复制EXE或DLL**，目录内运行库、更新器、BAT和`server-version.txt`都要保留。
3. 右键以管理员身份运行：

   ```text
   01-Install-Service.bat
   ```

4. 脚本会安装并启动`KaiZhongReleaseToolServer`，启动类型为自动，并配置故障重启。
5. 在服务器浏览器访问：

   ```text
   http://127.0.0.1:5050/
   ```

6. 返回包含`running`的JSON表示本机服务正常。
7. 再从客户端电脑访问`http://服务器IP:5050/`，确认网络和防火墙正常。

> **安装目录不要放在用户桌面、下载目录、`%TEMP%`或远程会话临时目录。后续自动升级会覆盖该固定目录。**

### 4.3 服务管理脚本

| 脚本 | 功能 |
|---|---|
| `01-Install-Service.bat` | 安装并启动；同名服务存在时先停止、删除再安装 |
| `02-Start-Service.bat` | 启动服务 |
| `03-Restart-Service.bat` | 重启服务 |
| `04-Stop-Service.bat` | 停止服务 |
| `05-Uninstall-Service.bat` | 停止并卸载，保留ProgramData业务目录 |

BAT会自动申请管理员权限。更新器不是Windows服务，不需要单独安装。

## 5. 服务端数据目录

服务端公共数据根目录：

```text
%ProgramData%\KaiZhong\KaiZhongReleaseTool
```

通常对应：

```text
C:\ProgramData\KaiZhong\KaiZhongReleaseTool
```

| 目录 | 用途 |
|---|---|
| `SMOMDLL` | 本次上传并准备发布的应用文件 |
| `Temp` | HTTP上传缓冲、ZIP临时文件 |
| `Update` | 升级包、暂存文件和旧版本备份 |

服务端不创建`log.db`；业务日志只保存在客户端。

## 6. 客户端首次运行

1. 把客户端完整发布输出复制到固定目录；
2. 运行`凯中发布工具.exe`；
3. 程序按需在EXE同级创建：
   - `servers.db`：服务器、分组、梯队、路径、服务和远程桌面配置；
   - `log.db`：发布与回滚日志；
   - `GetDLL.txt`：指定DLL清单；
   - `SMOMDLL`及四个应用子目录：待发布文件；
4. 首次加载自动检测服务器状态，之后可右键“刷新状态”。

客户端采用单实例运行。主窗口不能直接关闭；退出时请右键系统托盘图标选择“退出”。

## 7. 配置数据库复制与共享

`servers.db`位于客户端EXE同级。复制它即可让其他用户复用已经配置的服务器列表。

> **复制前，发送方和接收方必须从系统托盘完全退出客户端，避免SQLite正在写入。**

程序目录没有数据库时，会尝试迁移旧位置：

```text
%LOCALAPPDATA%\KaiZhongReleaseTool\servers.db
```

> **远程桌面密码依当前需求以明文保存在`servers.db`。必须严格限制客户端目录和数据库文件权限，不得通过不可信渠道传播。**

## 8. 服务器列表与分组

列表显示“梯队、服务器、状态、服务器IP”。梯队为数字1或2。

- 在线：整行绿色；
- 离线：整行红色；
- 未检测/检测中：整行黄色；
- 鼠标经过：字体蓝色加粗；
- 双击：打开服务器编辑；
- 左侧栏支持拖动宽度。

分组通过复选框过滤。右键菜单包含远程服务器、新增、编辑、删除、刷新状态、分组管理、发布回滚和升级服务端。删除服务器必须输入：

```text
我确认删除
```

分组支持新增、重命名和删除。正在被服务器使用的分组不能删除。服务器配置时只能选择已有分组，分组为空不能保存。

## 9. 服务器配置

服务器配置窗口右上角为编辑开关：

- 灰色：只读；
- 深蓝色：允许编辑；
- 只读时仍可切换页签查看；
- 保存按钮只在编辑开启时可用；
- 每次重新打开默认只读。

### 9.1 基本信息

- 服务器名称；
- 服务器分组；
- IP地址或主机名；
- 服务端口，默认5050；
- 远程桌面账户、密码和端口；
- 发布梯队，只能选择1或2，默认2。

同组、同属地的双备服务器，应至少配置一台第1梯队，其余第2梯队。例如两台API选一台第1梯队，两台Web也选一台第1梯队。

### 9.2 发布前备份

以下四个应用分别配置“路径”和“服务”：

- `SIE.ScheduleServer`；
- `SIE.WebApiHost`；
- `WebClient`；
- `WpfClient`。

路径是服务端本机实际发布目录。可手工输入，也可点击“选择”浏览服务器目录。多个服务名使用中文或英文逗号分隔。“备份到”是服务端保存时间戳ZIP的目录。

- 路径为空：该服务器不上传、不备份、不发布该应用；
- 路径不为空：上传对应应用，发布前备份该路径，然后覆盖；
- `SIE.ScheduleServer`、`SIE.WebApiHost`、`WebClient`配置路径后必须配置有效服务；
- `WpfClient`允许配置路径但服务为空，此时不启停服务。

## 10. 远程桌面

右键“远程服务器”会删除Windows凭据管理器中与当前IP/端口匹配的旧凭据，保存当前配置，再调用系统`mstsc`。

用户名自动规范化：

- `kz.com/user1`→`kz.com\user1`；
- `服务器IP/administrator`→`服务器IP\administrator`；
- `./administrator`→`服务器IP\administrator`。

3389端口不附加端口号，非3389端口会附加。

> **远程桌面证书警告用于验证服务器身份，与保存密码不是同一功能；信任证书不会替代账号密码。域策略仍可能禁止使用已保存凭据。**

## 11. 选择SMOM项目

“SMOM项目”文本框支持输入和文件夹选择。程序从当前路径向上查找项目结构并识别：

- `SIE.ScheduleServer\bin\Debug\net6.0`；
- `SIE.WebApiHost\bin\Debug\net6.0`；
- `WebClient\bin\Debug\net6.0`；
- `WpfClient\bin\Debug\net6.0-windows`。

`SMOM.KAIZHONG-Prod\SMOM.KAIZHONG`识别为正式机DLL；`SMOM.KAIZHONG\SMOM.KAIZHONG`识别为测试机DLL。无法识别时显示“未能识别路径”。

## 12. 获取DLL

### 12.1 获取指定DLL

1. 读取EXE同级`GetDLL.txt`；
2. 文件不存在时自动创建并停止本次获取；
3. 每行一个DLL名；非`.dll`结尾自动补充，扩展名不区分大小写；
4. 自动去重；
5. 获取前清空四个目标目录；
6. 清空失败最多重试5次、间隔1秒；
7. 任一目录清空失败则不继续；
8. 从四个项目输出目录分别查找并复制。

### 12.2 获取全量DLL

从四个输出目录查找所有以`SIE`开头的DLL，统计预计数量，清空后复制。

目标目录：

```text
SMOMDLL\SIE.ScheduleServer
SMOMDLL\SIE.WebApiHost
SMOMDLL\WebClient
SMOMDLL\WpfClient
```

步骤标题蓝色加粗、数量橙色加粗、阻断原因红色。DLL获取结果只显示在前端，不写`log.db`。

## 13. 上传并发布

点击“上传并发布”后，“获取DLL”和“上传并发布”同时禁用，流程结束后恢复。

选择服务器窗口支持全选、分组选择、全部显示、仅显示勾选和仅显示未勾选。显示过滤不会清除勾选。

### 第1步：上传文件到服务器

- 根据每台服务器已填写的应用路径，只上传相应应用；
- 未填写路径的应用不上传；
- 四个路径全为空时跳过该服务器网络上传；
- 相同应用组合复用一个临时ZIP；
- 不同服务器并发上传，按实际完成顺序显示；
- 日志显示实际文件数量；
- 失败重试5次、间隔3秒；
- 任意服务器失败，整个批次不进入第2步。

### 第2步：检查服务器配置

| 项目 | ✔ | ○ | × |
|---|---|---|---|
| 文件 | 服务端对应应用目录有文件 | 无文件或目录不存在 | 不使用× |
| 备份路径 | 已填写且目录存在 | 未填写 | 已填写但不存在/无效 |
| 服务 | 已填写且所有服务存在 | 未填写 | 至少一个服务不存在 |

阻断条件：

1. 配置了路径，但服务端目录不存在；
2. 配置了服务名，但至少一个服务不存在；
3. 非WpfClient的三个应用配置了路径却没有配置服务；
4. 服务端离线、请求失败或未返回有效结果。

`文件✔、备份路径○、服务○`不会阻断，该应用后续跳过。WpfClient路径有效而服务为空也允许通过。所有勾选服务器必须全部通过才进入第3步；失败原因会在本步骤最后以红色集中显示。

### 第3步：备份文件

- 只备份本次有上传文件并配置有效路径的应用；
- ZIP名称：`yyyyMMdd-HHmmss.zip`；
- ZIP内部按应用名保存目录结构；
- 任意服务器备份失败，整个批次不发布。

### 第4步：发布应用程序

按梯队执行：

```text
第1梯队：并发停服 → 并发覆盖 → 并发启服
第2梯队：并发停服 → 并发覆盖 → 并发启服
```

- 停服失败最多尝试5次；
- 停服失败不覆盖，并尝试恢复服务；
- 覆盖失败最多尝试10次，每次间隔3秒；
- 覆盖无论成功或失败都启服；
- 启服失败最多尝试10次，每次间隔3秒；
- 第1梯队任何停服、覆盖、启服或请求失败都会阻断第2梯队；
- 第1梯队完全成功后才开始第2梯队。

普通应用递归覆盖目标目录。WpfClient更新发布目录中的`Plugins.zip`，并把`Manifest.xml`的`Plugins`模块版本号最后一段加1。

> **第1梯队是第2梯队的发布闸门。出现红色失败后必须先处理，系统不会继续第2梯队。**

## 14. 日志

发布日志集：

```text
PushyyyyMMdd-HHmmss
```

回滚日志集：

```text
RollBackyyyyMMdd-HHmmss
```

每个步骤标题带实际时间。发布和回滚日志实时显示并写入客户端`log.db`。本次发布的DLL清单只写数据库，不显示在当前结果区；点击“查看日志”可以查看。DLL获取不写数据库。

## 15. 发布回滚

右键“发布回滚”可勾选多台服务器，并为每台服务器选择不同备份ZIP。任何勾选服务器离线或未选版本时，所有服务器都不回滚。

回滚前**不创建新备份**：

1. 停止所选备份涉及应用的服务；
2. 还原备份集；
3. 无论还原成功或失败都启动服务。

文件还原失败最多重试10次，启服失败最多重试10次。启动失败会在完成信息后红色提醒。日志开头逐台列出回滚备份集。

## 16. 代码修改后的服务端升级

仅修改客户端XAML、客户端数据库或客户端逻辑时，只需重新编译并替换客户端。修改以下内容必须升级服务端：

- `ServerHost.cs`；
- `Contracts.cs`中的共享协议；
- `AppPaths.cs`；
- `KaiZhongReleaseTool.Server`；
- `KaiZhongReleaseTool.Server.Updater`。

### 16.1 生成新版包

版本号必须递增：

```powershell
dotnet restore KaiZhongReleaseTool.sln
dotnet build KaiZhongReleaseTool.sln -c Release --no-restore
.\Scripts\Build-ServerPackage.ps1 -Version "1.0.4"
```

后续升级选择：

```text
artifacts\Server\KaiZhongReleaseTool.Server-1.0.4.zip
```

> **不要手工压缩外层`KaiZhongReleaseToolServerFiles`。正确ZIP打开后，根目录应直接看到服务端EXE、DLL、Updater和`server-version.txt`。**

### 16.2 客户端批量升级

1. 服务器列表右键“升级服务端”；
2. 选择新版本ZIP；
3. 选择服务器；
4. 客户端计算SHA-256并上传；
5. 服务端启动独立更新器并停止旧服务；
6. 更新器备份安装目录、覆盖新版、启动服务；
7. 最多等待90秒通过本机5050健康检查；
8. 失败时尝试恢复旧文件并重启旧服务。

> **升级期间不要结束Updater、删除ProgramData的Update目录或关闭服务器。建议先升级一台测试服务器，确认版本和发布规则正常后再批量升级。**

## 17. 常见问题

### 17.1 新规则没有生效

目标服务器通常仍是旧版服务端。生成更高版本升级包并升级，确认Windows服务已经重启。健康检查JSON中的`version`可用于确认版本。

### 17.2 无法访问5050

```powershell
sc.exe query KaiZhongReleaseToolServer
netstat -ano | findstr :5050
```

检查服务状态、防火墙、IP、端口和路由。

### 17.3 `App.baml`或DLL访问被拒绝

从系统托盘退出客户端、停止Visual Studio调试，确认旧程序和`dotnet`进程没有占用输出文件，再清理并生成。**不要在程序运行时覆盖其DLL。**

### 17.4 上传出现用户Temp会话目录错误

新版服务端固定使用：

```text
%ProgramData%\KaiZhong\KaiZhongReleaseTool\Temp
```

确认服务端已升级，并确认Windows服务账户对该目录有权限。

### 17.5 备份失败

检查源路径、备份到路径、磁盘空间、服务账户权限，以及备份到目录是否位于被备份目录内部。

### 17.6 服务启停失败

配置Windows“服务名称”而不是显示名称；检查拼写、服务依赖、权限和Windows事件查看器。

## 18. 发布前检查清单

- [ ] 客户端与服务端版本匹配；
- [ ] 所选服务器全部在线；
- [ ] 测试机/正式机SMOM路径正确；
- [ ] `GetDLL.txt`或全量DLL范围正确；
- [ ] `SMOMDLL`四个应用文件已核对；
- [ ] 每台服务器路径、服务名和备份到目录正确；
- [ ] 备份磁盘空间充足；
- [ ] 双备服务器梯队配置正确；
- [ ] 已确认可用的回滚备份；
- [ ] 已先在测试服务器完成验证。
