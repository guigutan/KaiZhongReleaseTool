using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.ComponentModel;
using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Documents;

namespace KaiZhongReleaseTool;

/// <summary>
/// 客户端主窗口，负责收集用户输入并通过 HTTP/JSON 向服务端发送指令。
/// </summary>
public partial class MainWindow : Window
{
    // 所有请求复用同一个 HttpClient，避免频繁创建网络连接。
    // 文件夹上传可能耗时较长，因此统一使用三十分钟请求超时。
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    // 状态检测应快速返回，单独使用三秒超时的客户端。
    private readonly HttpClient _statusHttpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ServerRepository _serverRepository = new();
    private readonly List<ServerProfile> _allServers = new();
    private readonly ObservableCollection<ServerProfile> _servers = new();
    private ServerProfile? _selectedServer;
    private CancellationTokenSource? _pathDetectionCts;
    private string[]? _resolvedOutputPaths;
    private static readonly string[] DllProjectNames = { "SIE.ScheduleServer", "SIE.WebApiHost", "WebClient", "WpfClient" };

    /// <summary>初始化指令列表，并默认选择文件复制操作。</summary>
    public MainWindow()
    {
        InitializeComponent();
        // 下拉框显示中文名称，但发送请求时仍使用稳定的枚举值。
        CommandTypeComboBox.DisplayMemberPath = nameof(CommandOption.Name);
        CommandTypeComboBox.SelectedValuePath = nameof(CommandOption.Type);
        CommandTypeComboBox.ItemsSource = CommandOptions;
        CommandTypeComboBox.SelectedValue = CommandType.FileCopy;
        ServerDataGrid.ItemsSource = _servers;
        Loaded += MainWindow_Loaded;
    }

    /// <summary>客户端首次显示时读取 SQLite 配置，并立即检测全部服务器状态。</summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        LoadServers();
        await RefreshAllServerStatusAsync();
    }

    /// <summary>从 SQLite 重新加载服务器列表。</summary>
    private void LoadServers(long? selectedId = null)
    {
        _allServers.Clear();
        _allServers.AddRange(_serverRepository.GetAll());
        var selectedServer = selectedId.HasValue ? _allServers.FirstOrDefault(item => item.Id == selectedId.Value) : null;
        var groups = new[] { "全部分组" }.Concat(_serverRepository.GetGroups()).ToArray();
        GroupFilterComboBox.ItemsSource = groups;
        GroupFilterComboBox.SelectedItem = selectedServer?.GroupName ?? "全部分组";
        ApplyGroupFilter();
        if (selectedId.HasValue)
            ServerDataGrid.SelectedItem = _servers.FirstOrDefault(item => item.Id == selectedId.Value);
        else if (_servers.Count > 0)
            ServerDataGrid.SelectedIndex = 0;
    }

    /// <summary>根据下拉框选中的分组刷新左侧可见服务器。</summary>
    private void ApplyGroupFilter()
    {
        var group = GroupFilterComboBox.SelectedItem as string ?? "全部分组";
        var visibleServers = group == "全部分组"
            ? _allServers
            : _allServers.Where(item => string.Equals(item.GroupName, group, StringComparison.OrdinalIgnoreCase));
        _servers.Clear();
        foreach (var server in visibleServers) _servers.Add(server);
        ServerCountTextBlock.Text = group == "全部分组"
            ? $"共 {_allServers.Count} 台服务器"
            : $"当前分组 {_servers.Count} 台，共 {_allServers.Count} 台";
    }

    /// <summary>切换分组时只筛选列表，不重新访问数据库和服务器。</summary>
    private void GroupFilterComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyGroupFilter();
        if (_servers.Count > 0) ServerDataGrid.SelectedIndex = 0;
    }

    /// <summary>
    /// 拦截标题栏关闭按钮：普通关闭只隐藏窗口，只有托盘“退出”才真正关闭。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if ((System.Windows.Application.Current as App)?.IsExiting != true)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    /// <summary>把界面参数组装成请求，发送到服务端并显示结构化执行结果。</summary>
    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedServer is null)
        {
            ResultTextBox.Text = "请先在服务器列表中选择一台服务器。";
            return;
        }
        ResultTextBox.Text = "正在执行...";
        try
        {
            var command = new CommandRequest
            {
                Type = (CommandType)CommandTypeComboBox.SelectedValue,
                SourcePath = SourcePathTextBox.Text,
                DestinationPath = DestinationPathTextBox.Text,
                Content = ContentTextBox.Text,
                ServiceName = ServiceNameTextBox.Text
            };
            // 统一补上末尾斜杠，避免用户输入不同格式时拼接出错误地址。
            var baseUrl = _selectedServer.BaseUrl;
            if (command.Type == CommandType.FolderUpload)
            {
                await UploadFolderAsync(baseUrl, command);
                return;
            }
            using var response = await _httpClient.PostAsJsonAsync(baseUrl + "api/command", command);
            var result = await response.Content.ReadFromJsonAsync<CommandResponse>();
            // 读取文件时直接展示正文，其他指令继续显示完整 JSON 结果。
            if (command.Type == CommandType.FileRead &&
                result?.Success == true &&
                result.Data is JsonElement data &&
                data.TryGetProperty("content", out var content))
            {
                ResultTextBox.Text = $"{result.Message}{Environment.NewLine}{Environment.NewLine}{content.GetString()}";
            }
            else
            {
                ResultTextBox.Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            }
        }
        catch (Exception ex) { ResultTextBox.Text = $"请求失败：{ex.Message}"; }
    }

    /// <summary>根据指令类型启用所需输入项，减少无效参数输入。</summary>
    private void CommandTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CommandTypeComboBox.SelectedValue is not CommandType type) return;
        var isService = type.ToString().StartsWith("Service", StringComparison.Ordinal);
        ServiceNameTextBox.IsEnabled = isService && type != CommandType.ServiceList;
        SourcePathTextBox.IsEnabled = !isService;
        // 这些操作同时需要源路径和目标路径。
        DestinationPathTextBox.IsEnabled = !isService && type is
            CommandType.FileMove or
            CommandType.FileCopy or
            CommandType.FilePaste or
            CommandType.FileBackup or
            CommandType.DirectoryCompressFiles or
            CommandType.DirectoryCompressAll or
            CommandType.FileExtract or
            CommandType.FolderUpload;
        ContentTextBox.IsEnabled = type == CommandType.FileModify;
        BrowseFolderButton.IsEnabled = type == CommandType.FolderUpload;
    }

    /// <summary>打开系统文件夹选择窗口，把结果填入源路径输入框。</summary>
    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "请选择需要上传的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SourcePathTextBox.Text = dialog.SelectedPath;
    }

    /// <summary>
    /// 把客户端文件夹临时压缩后以表单文件上传，完成后无论成功失败都删除临时 ZIP。
    /// </summary>
    private async Task UploadFolderAsync(string baseUrl, CommandRequest command)
    {
        if (string.IsNullOrWhiteSpace(command.SourcePath) || !Directory.Exists(command.SourcePath))
            throw new DirectoryNotFoundException("请选择一个存在的客户端文件夹。");
        if (string.IsNullOrWhiteSpace(command.DestinationPath))
            throw new ArgumentException("服务端保存路径不能为空。");

        var tempZip = Path.Combine(Path.GetTempPath(), $"KaiZhongUpload_{Guid.NewGuid():N}.zip");
        try
        {
            ResultTextBox.Text = "正在压缩并上传文件夹...";
            ZipFile.CreateFromDirectory(command.SourcePath, tempZip, CompressionLevel.Optimal, includeBaseDirectory: false);
            await using var fileStream = File.OpenRead(tempZip);
            using var form = new MultipartFormDataContent();
            using var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            form.Add(fileContent, "folderArchive", Path.GetFileName(tempZip));
            form.Add(new StringContent(command.DestinationPath), "destinationPath");
            using var response = await _httpClient.PostAsync(baseUrl + "api/upload-folder", form);
            var result = await response.Content.ReadFromJsonAsync<CommandResponse>();
            ResultTextBox.Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    /// <summary>提供给指令下拉框的中文名称和枚举值映射。</summary>
    private static readonly CommandOption[] CommandOptions =
    {
        new(CommandType.FileMove, "文件 - 移动"),
        new(CommandType.FileCopy, "文件 - 复制"),
        new(CommandType.FilePaste, "文件 - 粘贴并覆盖"),
        new(CommandType.FileModify, "文件 - 修改内容"),
        new(CommandType.FileDelete, "文件/文件夹 - 删除"),
        new(CommandType.FileBackup, "文件 - 备份"),
        new(CommandType.DirectoryCreate, "文件夹 - 创建"),
        new(CommandType.DirectoryCompressFiles, "文件夹 - 仅压缩第一层文件"),
        new(CommandType.DirectoryCompressAll, "文件夹 - 压缩全部内容"),
        new(CommandType.FileExtract, "ZIP - 解压文件"),
        new(CommandType.FolderUpload, "文件夹 - 上传到服务端"),
        new(CommandType.FileRead, "文件 - 读取服务端文件内容"),
        new(CommandType.ServiceList, "服务 - 获取服务列表"),
        new(CommandType.ServiceStatus, "服务 - 获取指定服务状态"),
        new(CommandType.ServiceStop, "服务 - 停止"),
        new(CommandType.ServiceStart, "服务 - 启动"),
        new(CommandType.ServiceRestart, "服务 - 重启")
    };

    /// <summary>表示下拉框中的一项指令。</summary>
    private sealed record CommandOption(CommandType Type, string Name);

    /// <summary>选择服务器后，把对应地址设为当前指令目标。</summary>
    private void ServerDataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedServer = ServerDataGrid.SelectedItem as ServerProfile;
    }

    /// <summary>右键某一行时先选中该行，确保菜单操作对应用户点击的服务器。</summary>
    private void ServerDataGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not System.Windows.Controls.DataGridRow)
            element = VisualTreeHelper.GetParent(element);
        if (element is System.Windows.Controls.DataGridRow row)
            ServerDataGrid.SelectedItem = row.Item;
    }

    /// <summary>鼠标左键双击服务器行时，直接打开该服务器的编辑窗口。</summary>
    private void ServerDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not System.Windows.Controls.DataGridRow)
            element = VisualTreeHelper.GetParent(element);
        if (element is not System.Windows.Controls.DataGridRow row) return;
        ServerDataGrid.SelectedItem = row.Item;
        EditServer_Click(sender, e);
        e.Handled = true;
    }

    /// <summary>打开新增窗口，保存成功后刷新列表并检测新服务器。</summary>
    private async void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ServerEditWindow(_serverRepository.GetGroups()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        try
        {
            var id = _serverRepository.Add(dialog.Result);
            LoadServers(id);
            if (_selectedServer is not null) await CheckServerStatusAsync(_selectedServer);
        }
        catch (SqliteException ex) { ShowDatabaseError(ex); }
    }

    /// <summary>修改当前选中的服务器配置。</summary>
    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedServer is null) { ShowSelectServerTip(); return; }
        var dialog = new ServerEditWindow(_serverRepository.GetGroups(), _selectedServer) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        try
        {
            _serverRepository.Update(dialog.Result);
            LoadServers(dialog.Result.Id);
            if (_selectedServer is not null) await CheckServerStatusAsync(_selectedServer);
        }
        catch (SqliteException ex) { ShowDatabaseError(ex); }
    }

    /// <summary>打开独立分组管理窗口，关闭后重新加载服务器与分组筛选项。</summary>
    private async void ManageGroups_Click(object sender, RoutedEventArgs e)
    {
        var selectedId = _selectedServer?.Id;
        var dialog = new GroupManagementWindow(_serverRepository) { Owner = this };
        dialog.ShowDialog();
        LoadServers(selectedId);
        await RefreshAllServerStatusAsync();
    }

    /// <summary>确认后删除当前服务器配置，只删除数据库记录，不操作远程服务器。</summary>
    private void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedServer is null) { ShowSelectServerTip(); return; }
        var answer = System.Windows.MessageBox.Show($"确定删除服务器“{_selectedServer.Name}”吗？\n此操作不会删除远程服务器上的任何内容。",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _serverRepository.Delete(_selectedServer.Id);
        LoadServers();
    }

    /// <summary>响应“刷新状态”按钮，并发检测列表中的全部服务器。</summary>
    private async void RefreshServers_Click(object sender, RoutedEventArgs e) => await RefreshAllServerStatusAsync();

    /// <summary>并发检测所有服务器，避免二十多台服务器串行检测造成长时间等待。</summary>
    private async Task RefreshAllServerStatusAsync()
    {
        RefreshServersMenuItem.IsEnabled = false;
        try
        {
            foreach (var server in _allServers) { server.Status = "检测中"; server.StatusDetail = "正在连接..."; }
            await Task.WhenAll(_allServers.Select(CheckServerStatusAsync));
        }
        finally { RefreshServersMenuItem.IsEnabled = true; }
    }

    /// <summary>访问服务端健康检查地址，根据 HTTP 结果更新在线状态。</summary>
    private async Task CheckServerStatusAsync(ServerProfile server)
    {
        try
        {
            using var response = await _statusHttpClient.GetAsync(server.BaseUrl);
            server.Status = response.IsSuccessStatusCode ? "在线" : "离线";
            server.StatusDetail = response.IsSuccessStatusCode
                ? $"正常 · {DateTime.Now:HH:mm:ss}"
                : $"HTTP {(int)response.StatusCode} · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            server.Status = "离线";
            server.StatusDetail = $"{GetShortError(ex.Message)} · {DateTime.Now:HH:mm:ss}";
        }
    }

    /// <summary>缩短网络异常文字，避免状态列被很长的系统信息撑开。</summary>
    private static string GetShortError(string message) => message.Length <= 45 ? message : message[..45] + "...";

    private static void ShowSelectServerTip() => System.Windows.MessageBox.Show("请先选择一台服务器。", "操作提示", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>把 SQLite 约束错误转换成用户容易理解的提示。</summary>
    private static void ShowDatabaseError(SqliteException ex)
    {
        var message = ex.SqliteErrorCode == 19 ? "相同主机和端口的服务器已经存在。" : $"保存服务器失败：{ex.Message}";
        System.Windows.MessageBox.Show(message, "服务器配置", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 删除旧凭据并重新写入当前账户密码，然后使用免提示配置启动系统远程桌面。
    /// </summary>
    private void RemoteServer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedServer is null) { ShowSelectServerTip(); return; }
        try
        {
            if (string.IsNullOrWhiteSpace(_selectedServer.Username) || string.IsNullOrEmpty(_selectedServer.Password))
            {
                System.Windows.MessageBox.Show("请先编辑服务器，填写远程桌面账户和密码。", "远程服务器", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targets = new[]
            {
                $"TERMSRV/{_selectedServer.Host}",
                $"TERMSRV/{_selectedServer.Host}:{_selectedServer.RemoteDesktopPort}"
            };
            foreach (var target in targets)
            {
                // 删除命令在凭据不存在时会返回非零退出码，此处可以忽略。
                RunCmdKey($"/delete:{target}", null, null, requireSuccess: false);
                RunCmdKey($"/add:{target}", _selectedServer.Username, _selectedServer.Password, requireSuccess: true);
            }

            // RDP 文件不保存密码，密码由上面写入的 Windows 凭据管理器提供。
            var rdpFile = Path.Combine(Path.GetTempPath(), $"KaiZhongRdp_{Guid.NewGuid():N}.rdp");
            var address = $"{_selectedServer.Host}:{_selectedServer.RemoteDesktopPort}";
            File.WriteAllText(rdpFile,
                $"full address:s:{address}{Environment.NewLine}" +
                $"username:s:{_selectedServer.Username}{Environment.NewLine}" +
                "prompt for credentials:i:0\r\n" +
                "promptcredentialonce:i:1\r\n" +
                "authentication level:i:2\r\n" +
                "enablecredsspsupport:i:1\r\n");

            var remoteDesktop = new ProcessStartInfo("mstsc.exe") { UseShellExecute = true };
            remoteDesktop.ArgumentList.Add(rdpFile);
            var remoteProcess = Process.Start(remoteDesktop) ?? throw new InvalidOperationException("无法启动远程桌面程序。");
            remoteProcess.EnableRaisingEvents = true;
            remoteProcess.Exited += (_, _) => { try { File.Delete(rdpFile); } catch { } };
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"无法打开远程服务器：{ex.Message}", "远程服务器", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>执行 cmdkey，新增凭据时自动传入账户和明文密码。</summary>
    private static void RunCmdKey(string targetArgument, string? username, string? password, bool requireSuccess)
    {
        var command = new ProcessStartInfo("cmdkey.exe") { UseShellExecute = false, CreateNoWindow = true };
        command.ArgumentList.Add(targetArgument);
        if (username is not null) command.ArgumentList.Add($"/user:{username}");
        if (password is not null) command.ArgumentList.Add($"/pass:{password}");
        using var process = Process.Start(command) ?? throw new InvalidOperationException("无法运行 Windows 凭据管理工具。");
        process.WaitForExit();
        if (requireSuccess && process.ExitCode != 0)
            throw new InvalidOperationException("新增或修改 Windows 远程桌面凭据失败。");
    }

    /// <summary>项目路径变化后延迟识别，避免用户每输入一个字符就扫描磁盘。</summary>
    private async void SmomProjectPathTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _pathDetectionCts?.Cancel();
        _pathDetectionCts?.Dispose();
        _pathDetectionCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, _pathDetectionCts.Token);
            await ResolveSmomPathsAsync(_pathDetectionCts.Token);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>允许用户通过系统文件夹选择器填写 SMOM 项目路径。</summary>
    private void SelectSmomFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "请选择 SMOM 项目目录或其相关项目目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(SmomProjectPathTextBox.Text) ? SmomProjectPathTextBox.Text : string.Empty
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SmomProjectPathTextBox.Text = dialog.SelectedPath;
    }

    /// <summary>双击顶部提示文字时，用资源管理器打开当前程序所在文件夹。</summary>
    private void OpenProgramFolder_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        explorer.ArgumentList.Add(AppContext.BaseDirectory);
        Process.Start(explorer);
        e.Handled = true;
    }

    /// <summary>校验 SMOMDLL 中存在 DLL 后，打开支持全选和多选的服务器选择窗口。</summary>
    private void PublishToServerButton_Click(object sender, RoutedEventArgs e)
    {
        var dllRoot = Path.Combine(AppContext.BaseDirectory, "SMOMDLL");
        string[] dllFiles;
        try
        {
            dllFiles = Directory.Exists(dllRoot)
                ? Directory.GetFiles(dllRoot, "*.dll", SearchOption.AllDirectories)
                : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"读取 SMOMDLL 文件夹失败：{ex.Message}", "发布到服务器", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (dllFiles.Length == 0)
        {
            System.Windows.MessageBox.Show("SMOMDLL 文件夹及其子文件夹中没有 DLL 文件，请先获取 DLL。", "发布到服务器", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_allServers.Count == 0)
        {
            System.Windows.MessageBox.Show("服务器列表为空，请先新增服务器。", "发布到服务器", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new PublishServerSelectionWindow(_allServers) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        ResultTextBox.Text = $"已准备 {dllFiles.Length} 个 DLL。{Environment.NewLine}" +
            $"已选择 {dialog.SelectedServers.Count} 台服务器：{Environment.NewLine}" +
            string.Join(Environment.NewLine, dialog.SelectedServers.Select(server => $"- {server.Name}（{server.Host}）")) +
            $"{Environment.NewLine}{Environment.NewLine}服务器选择已完成，实际发布功能将在后续步骤中接入。";
    }

    /// <summary>根据所选模式收集 DLL，并复制到程序目录下对应的 SMOMDLL 子目录。</summary>
    private async void GetDllButton_Click(object sender, RoutedEventArgs e)
    {
        _pathDetectionCts?.Cancel();
        _pathDetectionCts = new CancellationTokenSource();
        try
        {
            await ResolveSmomPathsAsync(_pathDetectionCts.Token);
            if (_resolvedOutputPaths is null)
            {
                ResultTextBox.Text = "未能识别 SMOM 项目路径，无法获取 DLL。";
                return;
            }

            var specifiedMode = SpecifiedDllRadioButton.IsChecked == true;
            var specifiedNames = specifiedMode ? ReadSpecifiedDllNames(createWhenMissing: true) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (specifiedMode && specifiedNames.Count == 0)
            {
                DllCountTextBlock.Text = "待获取：0 个";
                ResultTextBox.Text = $"GetDLL.txt 内容为空，请先填写需要获取的 DLL 文件名。{Environment.NewLine}{GetDllListFilePath()}";
                return;
            }

            var targetDirectories = PrepareDllTargetDirectories();
            var requestedCount = specifiedMode ? specifiedNames.Count : 0;
            var copiedCount = 0;
            var missingCount = 0;
            var summary = new List<string>();
            for (var index = 0; index < _resolvedOutputPaths.Length; index++)
            {
                var sourceDirectory = _resolvedOutputPaths[index];
                var projectName = DllProjectNames[index];
                if (!Directory.Exists(sourceDirectory))
                {
                    summary.Add($"【{projectName}】源目录不存在，复制 0 个");
                    if (specifiedMode) missingCount += specifiedNames.Count;
                    continue;
                }

                var sourceFiles = Directory.GetFiles(sourceDirectory, "*.dll", SearchOption.TopDirectoryOnly);
                var filesToCopy = specifiedMode
                    ? sourceFiles.Where(file => specifiedNames.Contains(Path.GetFileName(file))).ToArray()
                    : sourceFiles.Where(file => Path.GetFileName(file).StartsWith("SIE", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (!specifiedMode) requestedCount += filesToCopy.Length;
                foreach (var sourceFile in filesToCopy)
                {
                    File.Copy(sourceFile, Path.Combine(targetDirectories[index], Path.GetFileName(sourceFile)), overwrite: true);
                    copiedCount++;
                }
                if (specifiedMode) missingCount += specifiedNames.Count - filesToCopy.Length;
                summary.Add($"【{projectName}】复制 {filesToCopy.Length} 个 DLL 到 {targetDirectories[index]}");
            }
            DllCountTextBlock.Text = $"汇总：{copiedCount} 个";
            summary.Insert(0, specifiedMode
                ? $"指定 DLL：{specifiedNames.Count} 种；实际复制：{copiedCount} 个；四个项目累计缺失：{missingCount} 个"
                : $"全量 SIE DLL：共复制 {copiedCount} 个");
            ResultTextBox.Text = string.Join(Environment.NewLine, summary);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ResultTextBox.Text = $"获取 DLL 失败：{ex.Message}"; }
    }

    /// <summary>切换获取模式时立即更新预计 DLL 数量。</summary>
    private void DllModeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateDllCountPreview();
    }

    /// <summary>根据当前模式计算待获取数量，指定模式按去重文件名计数，全量模式按实际文件计数。</summary>
    private void UpdateDllCountPreview()
    {
        try
        {
            int count;
            if (SpecifiedDllRadioButton.IsChecked == true)
                count = ReadSpecifiedDllNames(createWhenMissing: false).Count;
            else if (_resolvedOutputPaths is null)
                count = 0;
            else
                count = _resolvedOutputPaths.Where(Directory.Exists).Sum(path =>
                    Directory.GetFiles(path, "*.dll", SearchOption.TopDirectoryOnly)
                        .Count(file => Path.GetFileName(file).StartsWith("SIE", StringComparison.OrdinalIgnoreCase)));
            DllCountTextBlock.Text = $"待获取：{count} 个";
        }
        catch { DllCountTextBlock.Text = "待获取：无法统计"; }
    }

    /// <summary>读取 GetDLL.txt，自动补全扩展名并按不区分大小写方式去重。</summary>
    private static HashSet<string> ReadSpecifiedDllNames(bool createWhenMissing)
    {
        var filePath = GetDllListFilePath();
        if (!File.Exists(filePath))
        {
            if (createWhenMissing) File.WriteAllText(filePath, string.Empty);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(filePath))
        {
            var name = Path.GetFileName(rawLine.Trim());
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name += ".dll";
            result.Add(name);
        }
        return result;
    }

    /// <summary>返回与当前程序同级的 DLL 清单文件路径。</summary>
    private static string GetDllListFilePath() => Path.Combine(AppContext.BaseDirectory, "GetDLL.txt");

    /// <summary>清空并重新创建四个 DLL 目标目录；目录被占用时抛出明确错误。</summary>
    private static string[] PrepareDllTargetDirectories()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "SMOMDLL");
        Directory.CreateDirectory(root);
        var targets = DllProjectNames.Select(name => Path.Combine(root, name)).ToArray();
        foreach (var target in targets)
        {
            if (Directory.Exists(target))
            {
                try { Directory.Delete(target, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"无法清空目录“{target}”，其中的文件可能正被占用，请关闭后重试。", ex);
                }
            }
            Directory.CreateDirectory(target);
        }
        return targets;
    }

    /// <summary>在后台定位 Projects\SMOM 目录，并在界面上显示四个完整输出路径。</summary>
    private async Task ResolveSmomPathsAsync(CancellationToken token)
    {
        var inputPath = SmomProjectPathTextBox.Text.Trim().Trim('"');
        var smomDirectory = await Task.Run(() => FindSmomDirectory(inputPath, token), token);
        if (smomDirectory is null)
        {
            _resolvedOutputPaths = null;
            SetUnrecognizedPaths();
            return;
        }

        _resolvedOutputPaths = new[]
        {
            Path.Combine(smomDirectory, "SIE.ScheduleServer", "bin", "Debug", "net6.0"),
            Path.Combine(smomDirectory, "SIE.WebApiHost", "bin", "Debug", "net6.0"),
            Path.Combine(smomDirectory, "WebClient", "bin", "Debug", "net6.0"),
            Path.Combine(smomDirectory, "WpfClient", "bin", "Debug", "net6.0-windows")
        };
        var outerRepositoryName = new DirectoryInfo(smomDirectory).Parent?.Parent?.Parent?.Name;
        System.Windows.Media.Brush? environmentBrush = null;
        if (string.Equals(outerRepositoryName, "SMOM.KAIZHONG-Prod", StringComparison.OrdinalIgnoreCase))
        {
            environmentBrush = System.Windows.Media.Brushes.Red;
            DllEnvironmentTextBlock.Text = "正式机DLL";
        }
        else if (string.Equals(outerRepositoryName, "SMOM.KAIZHONG", StringComparison.OrdinalIgnoreCase))
        {
            environmentBrush = System.Windows.Media.Brushes.Green;
            DllEnvironmentTextBlock.Text = "测试机DLL";
        }
        else DllEnvironmentTextBlock.Text = "未能识别DLL环境";

        SetColoredPath(ScheduleServerPathTextBlock, "SIE.ScheduleServer", _resolvedOutputPaths[0], outerRepositoryName, environmentBrush);
        SetColoredPath(WebApiHostPathTextBlock, "SIE.WebApiHost", _resolvedOutputPaths[1], outerRepositoryName, environmentBrush);
        SetColoredPath(WebClientPathTextBlock, "WebClient", _resolvedOutputPaths[2], outerRepositoryName, environmentBrush);
        SetColoredPath(WpfClientPathTextBlock, "WpfClient", _resolvedOutputPaths[3], outerRepositoryName, environmentBrush);
        UpdateDllCountPreview();
    }

    /// <summary>从输入目录向上回溯并向下有限搜索名称严格匹配的 Projects\SMOM 结构。</summary>
    private static string? FindSmomDirectory(string inputPath, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(inputPath)) return null;
        string fullPath;
        try { fullPath = Path.GetFullPath(inputPath); }
        catch { return null; }
        // 输入即使比 SMOM 更深，也可以直接从文本中的 Projects\SMOM 片段截取项目目录。
        var marker = $"{Path.DirectorySeparatorChar}Projects{Path.DirectorySeparatorChar}SMOM";
        var markerIndex = fullPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var embeddedSmom = fullPath[..(markerIndex + marker.Length)];
            if (Directory.Exists(embeddedSmom)) return embeddedSmom;
        }
        var start = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : File.Exists(fullPath) ? new FileInfo(fullPath).Directory : null;
        if (start is null) return null;

        for (var current = start; current is not null; current = current.Parent)
        {
            token.ThrowIfCancellationRequested();
            if (IsSmomDirectory(current)) return current.FullName;

            // 两种仓库都采用“外层环境目录\SMOM.KAIZHONG\Projects\SMOM”的固定结构。
            // 因此即使用户选择的是外层目录下的 CrossPlatformCJ.V10.3，也可以先向上找到
            // SMOM.KAIZHONG-Prod 或 SMOM.KAIZHONG，再转入旁边的 SMOM.KAIZHONG 仓库。
            if (current.Name.Equals("SMOM.KAIZHONG-Prod", StringComparison.OrdinalIgnoreCase) ||
                current.Name.Equals("SMOM.KAIZHONG", StringComparison.OrdinalIgnoreCase))
            {
                var nestedRepository = Path.Combine(current.FullName, "SMOM.KAIZHONG", "Projects", "SMOM");
                if (Directory.Exists(nestedRepository)) return nestedRepository;
            }

            var direct = Path.Combine(current.FullName, "Projects", "SMOM");
            if (Directory.Exists(direct)) return direct;
        }

        var pending = new Queue<(DirectoryInfo Directory, int Depth)>();
        pending.Enqueue((start, 0));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Dequeue();
            if (depth >= 4) continue;
            DirectoryInfo[] children;
            try { children = directory.GetDirectories(); }
            catch { continue; }
            foreach (var child in children)
            {
                if (IsSmomDirectory(child)) return child.FullName;
                if (child.Name is not ("bin" or "obj" or ".git" or ".vs")) pending.Enqueue((child, depth + 1));
            }
        }
        return null;
    }

    /// <summary>判断目录是否正好是 Projects 文件夹下名为 SMOM 的目录。</summary>
    private static bool IsSmomDirectory(DirectoryInfo directory) =>
        directory.Name.Equals("SMOM", StringComparison.OrdinalIgnoreCase) &&
        directory.Parent?.Name.Equals("Projects", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>显示完整路径，并单独给外层环境目录名称设置红色或绿色。</summary>
    private static void SetColoredPath(System.Windows.Controls.TextBlock target, string label, string path, string? highlightedName, System.Windows.Media.Brush? brush)
    {
        target.Inlines.Clear();
        target.Inlines.Add(new Run($"{label}："));
        if (string.IsNullOrWhiteSpace(highlightedName) || brush is null)
        {
            target.Inlines.Add(new Run(path));
            return;
        }

        var marker = $"{Path.DirectorySeparatorChar}{highlightedName}{Path.DirectorySeparatorChar}";
        var markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            target.Inlines.Add(new Run(path));
            return;
        }
        var nameIndex = markerIndex + 1;
        target.Inlines.Add(new Run(path[..nameIndex]));
        target.Inlines.Add(new Run(path.Substring(nameIndex, highlightedName.Length)) { Foreground = brush, FontWeight = FontWeights.Bold });
        target.Inlines.Add(new Run(path[(nameIndex + highlightedName.Length)..]));
    }

    /// <summary>无法识别项目结构时统一重置四行提示。</summary>
    private void SetUnrecognizedPaths()
    {
        ScheduleServerPathTextBlock.Text = "SIE.ScheduleServer：未能识别路径";
        WebApiHostPathTextBlock.Text = "SIE.WebApiHost：未能识别路径";
        WebClientPathTextBlock.Text = "WebClient：未能识别路径";
        WpfClientPathTextBlock.Text = "WpfClient：未能识别路径";
        DllEnvironmentTextBlock.Text = "未能识别DLL环境";
        UpdateDllCountPreview();
    }
}
