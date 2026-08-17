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
using System.Security.Cryptography;
using Brushes = System.Windows.Media.Brushes;

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
    private readonly LogRepository _logRepository = new();
    private string? _currentLogSetName;
    private readonly List<ServerProfile> _allServers = new();
    private readonly ObservableCollection<ServerProfile> _servers = new();
    private readonly Dictionary<string, System.Windows.Controls.CheckBox> _serverGroupFilterCheckBoxes = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingServerGroupFilters;
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
        RebuildServerGroupFilters();
        ApplyGroupFilter();
        if (selectedId.HasValue)
            ServerDataGrid.SelectedItem = _servers.FirstOrDefault(item => item.Id == selectedId.Value);
        else if (_servers.Count > 0)
            ServerDataGrid.SelectedIndex = 0;
    }

    /// <summary>根据勾选的一个或多个分组刷新左侧可见服务器。</summary>
    private void ApplyGroupFilter()
    {
        var selectedGroups = _serverGroupFilterCheckBoxes.Where(item => item.Value.IsChecked == true).Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var showAll = AllServersFilterCheckBox.IsChecked == true || selectedGroups.Count == 0;
        var visibleServers = showAll ? _allServers : _allServers.Where(item => selectedGroups.Contains(item.GroupName));
        _servers.Clear();
        foreach (var server in visibleServers) _servers.Add(server);
        ServerCountTextBlock.Text = showAll ? $"共 {_allServers.Count} 台服务器" : $"当前筛选 {_servers.Count} 台，共 {_allServers.Count} 台";
    }

    /// <summary>根据数据库分组重建自动换行的筛选复选框。</summary>
    private void RebuildServerGroupFilters()
    {
        _updatingServerGroupFilters = true;
        try
        {
            ServerGroupFilterPanel.Children.Clear(); ServerGroupFilterPanel.Children.Add(AllServersFilterCheckBox); _serverGroupFilterCheckBoxes.Clear(); AllServersFilterCheckBox.IsChecked = true;
            foreach (var groupName in _serverRepository.GetGroups())
            {
                var checkBox = new System.Windows.Controls.CheckBox { Content = groupName, Tag = groupName, FontSize = 14, Padding = new Thickness(5), Margin = new Thickness(0, 2, 12, 2), Cursor = System.Windows.Input.Cursors.Hand };
                checkBox.Click += ServerGroupFilterCheckBox_Click; _serverGroupFilterCheckBoxes[groupName] = checkBox; ServerGroupFilterPanel.Children.Add(checkBox);
            }
        }
        finally { _updatingServerGroupFilters = false; }
    }

    private void AllServersFilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingServerGroupFilters) return;
        _updatingServerGroupFilters = true;
        try { AllServersFilterCheckBox.IsChecked = true; foreach (var checkBox in _serverGroupFilterCheckBoxes.Values) checkBox.IsChecked = false; }
        finally { _updatingServerGroupFilters = false; }
        ApplyGroupFilter(); if (_servers.Count > 0) ServerDataGrid.SelectedIndex = 0;
    }

    private void ServerGroupFilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingServerGroupFilters) return;
        _updatingServerGroupFilters = true;
        try
        {
            var anySelected = _serverGroupFilterCheckBoxes.Values.Any(item => item.IsChecked == true);
            AllServersFilterCheckBox.IsChecked = !anySelected;
        }
        finally { _updatingServerGroupFilters = false; }
        ApplyGroupFilter(); if (_servers.Count > 0) ServerDataGrid.SelectedIndex = 0;
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
        var confirmation = new DeleteServerConfirmationWindow(_selectedServer.Name) { Owner = this };
        if (confirmation.ShowDialog() != true) return;
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
        if (string.IsNullOrWhiteSpace(_selectedServer.Username) || string.IsNullOrEmpty(_selectedServer.Password))
        {
            System.Windows.MessageBox.Show("请先编辑服务器，填写远程桌面账户和密码。", "远程服务器", MessageBoxButton.OK, MessageBoxImage.Information); return;
        }
        OpenSystemRemoteDesktop(_selectedServer);
    }

    /// <summary>备用方式：写入 Windows 凭据管理器并启动系统 mstsc。</summary>
    public static void OpenSystemRemoteDesktop(ServerProfile server)
    {
        try
        {
            // 3389 是远程桌面的默认端口，此时地址只写主机名；非默认端口才追加端口号。
            var address = server.RemoteDesktopPort == 3389
                ? server.Host
                : $"{server.Host}:{server.RemoteDesktopPort}";
            var credentialTarget = $"TERMSRV/{address}";
            var remoteUserName = NormalizeRemoteDesktopUserName(server.Host, server.Username);

            // 先删除通用凭据和 Windows 凭据中属于当前服务器的旧条目。
            WindowsCredentialHelper.DeleteServerCredentials(server.Host, server.RemoteDesktopPort);

            // “.\账户”表示远程服务器的本地账户。cmdkey 写入时改成“服务器\账户”，
            // RDP 文件仍保留原始写法，使远程登录界面继续按本机账户处理。
            WindowsCredentialHelper.SaveRemoteDesktopCredential(
                credentialTarget, remoteUserName, server.Password);
                // 删除命令在凭据不存在时会返回非零退出码，此处可以忽略。

            // RDP 文件不保存密码，密码由上面写入的 Windows 凭据管理器提供。
            var rdpFile = Path.Combine(Path.GetTempPath(), $"KaiZhongRdp_{Guid.NewGuid():N}.rdp");
            File.WriteAllText(rdpFile,
                $"full address:s:{address}{Environment.NewLine}" +
                $"username:s:{remoteUserName}{Environment.NewLine}" +
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

    /// <summary>
    /// 规范远程桌面账户：服务器IP\用户表示服务器本地账户，域名/用户表示 AD 域账户。
    /// </summary>
    private static string NormalizeRemoteDesktopUserName(string host, string userName)
    {
        // 先统一分隔符，避免用户混用“/”和“\”导致凭据账户不一致。
        var normalized = userName.Trim().Replace('/', '\\');
        if (normalized.StartsWith(@".\", StringComparison.Ordinal))
            return $@"{host}\{normalized[2..]}";

        // 用户允许按“kz.com/user1”录入，RDP 使用标准的“kz.com\user1”格式。
        var slashIndex = normalized.IndexOf('/');
        if (slashIndex > 0 && slashIndex < normalized.Length - 1)
            return normalized[..slashIndex] + "\\" + normalized[(slashIndex + 1)..];

        return normalized;
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

    /// <summary>校验本地 DLL、选择服务器，并把完整 SMOMDLL 并发同步到所有目标服务器。</summary>
    private async void PublishToServerButton_Click(object sender, RoutedEventArgs e)
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

        var tempZip = Path.Combine(Path.GetTempPath(), $"KaiZhongPublish_{Guid.NewGuid():N}.zip");
        PublishToServerButton.IsEnabled = false;
        GetDllButton.IsEnabled = false;
        ResultTextBox.Visibility = Visibility.Collapsed;
        PublishLogRichTextBox.Visibility = Visibility.Visible;
        PublishLogRichTextBox.Document.Blocks.Clear();
        try
        {
            var backupTimestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _currentLogSetName = "Push" + backupTimestamp;
            _logRepository.CreateSet(_currentLogSetName, "Push", backupTimestamp + ".zip");
            AppendPublishLine($"当前发布日志：{_currentLogSetName}", Brushes.SteelBlue, true);
            AppendPublishStepHeader("第1步：上传文件到服务器");
            await Task.Run(() => ZipFile.CreateFromDirectory(dllRoot, tempZip, CompressionLevel.Optimal, includeBaseDirectory: false));
            var uploadFileCount = dllFiles.Length;
            var uploads = await RunServerStageAsync(dialog.SelectedServers, server => UploadSmomDllAsync(server, tempZip, uploadFileCount), result => result.Success ? $"【{result.Server.Name}】文件上传 ✔ {result.FileCount}个" : $"【{result.Server.Name}】文件上传 ×（已尝试5次）", result => result.Success ? Brushes.SeaGreen : Brushes.Crimson);
            if (uploads.Any(item => !item.Success)) { AppendPublishAbort("存在文件上传失败的服务器，已终止发布。"); return; }

            AppendPublishStepHeader("第2步：检查服务器配置");
            var checks = await RunDeploymentStageAsync(dialog.SelectedServers, "api/deploy/check", serverResult =>
            {
                foreach (var item in serverResult.Response?.Items ?? new())
                {
                    var fileMark = item.HasFiles ? "✔" : "○";
                    var backupMark = item.BackupPathConfigured ? item.BackupPathExists ? "✔" : "×" : "○";
                    var serviceMark = item.ServicesConfigured ? item.ServicesExist ? "✔" : "×" : "○";
                    var text = $"【{serverResult.Server.Name}】{item.ApplicationName}[文件{fileMark}，备份路径{backupMark}，服务{serviceMark}]";
                    AppendPublishLine(text, item.Success ? Brushes.SeaGreen : Brushes.Crimson);
                }
                AppendPublishLine(string.Empty, Brushes.Black);
            });
            WriteConfigurationFailureReasons(checks);
            if (checks.Any(result => result.Response is null || !result.Response.Success)) { AppendPublishAbort("存在不满足发布条件的服务器配置，已终止发布。"); return; }

            AppendPublishStepHeader("第3步：备份文件");
            var backups = await RunServerStageAsync(dialog.SelectedServers, server => BackupServerAsync(server, backupTimestamp), result => $"【{result.Server.Name}】{backupTimestamp}.zip 备份{(result.Success ? "✔" : "×")}", result => result.Success ? Brushes.SeaGreen : Brushes.Crimson);
            if (backups.Any(item => !item.Success)) { AppendPublishAbort("存在备份失败的服务器，已终止发布。"); return; }

            AppendPublishStepHeader("第4步：发布应用程序");
            void WritePublishResult(ServerStageResult result)
            {
                foreach (var item in result.Response?.Items ?? new())
                    AppendPublishLine($"【{result.Server.Name}】{item.ApplicationName} 发布{(item.Success ? "✔" : $"×（已尝试{item.Attempts}次）")}{(item.ApplicationName == "WpfClient" && item.Success ? $" 版本修改✔ 当前版本{item.Version}" : string.Empty)}", item.Success ? Brushes.SeaGreen : Brushes.Crimson);
            }

            var publishes = new List<ServerStageResult>();
            var starts = new List<ServerStageResult>();

            async Task<(bool StopSucceeded, bool AllSucceeded)> PublishTierAsync(IEnumerable<ServerProfile> servers)
            {
                var tierServers = servers.ToArray();
                var tierStops = await RunDeploymentStageAsync(tierServers, "api/deploy/stop", result => WriteServiceStage(new[] { result }, "停止"));
                if (tierStops.Any(result => result.Response is null || !result.Response.Success))
                {
                    AppendPublishAbort("本梯队存在服务停止失败的服务器，不执行文件覆盖；正在恢复已经停止的服务。");
                    var recoveryStarts = await RunDeploymentStageAsync(tierServers, "api/deploy/start", result => WriteServiceStage(new[] { result }, "恢复启动"));
                    starts.AddRange(recoveryStarts);
                    return (false, false);
                }

                // 文件覆盖无论成功或失败，都必须继续启动本梯队服务。
                var tierPublishes = await RunDeploymentStageAsync(tierServers, "api/deploy/publish", WritePublishResult);
                publishes.AddRange(tierPublishes);
                var tierStarts = await RunDeploymentStageAsync(tierServers, "api/deploy/start", result => WriteServiceStage(new[] { result }, "启动"));
                starts.AddRange(tierStarts);
                var allSucceeded = tierPublishes.All(result => result.Response?.Success == true)
                    && tierStarts.All(result => result.Response?.Success == true);
                return (true, allSucceeded);
            }

            AppendPublishLine("即将发布第1梯队的服务器。", Brushes.SteelBlue, true);
            var tierOneResult = await PublishTierAsync(dialog.SelectedServers.Where(server => server.ReleaseTier == 1));
            if (!tierOneResult.AllSucceeded)
            {
                foreach (var failed in starts.SelectMany(result => (result.Response?.Items ?? new List<DeploymentStageItem>())
                    .Where(item => !item.Success)
                    .Select(item => (result.Server.Name, Item: item))))
                    AppendPublishLine($"【{failed.Name}】服务：{failed.Item.ApplicationName}${failed.Item.ServiceName} 启动失败，请尽快处理。", Brushes.Crimson, true, failed.Name);
                AppendPublishLine("第1梯队存在执行失败的服务器，已阻断第2梯队发布。", Brushes.Crimson, true);
                return;
            }

            AppendPublishLine("第1梯队已发布完成，即将发布第2梯队的服务器。", Brushes.SteelBlue, true);
            var tierTwoResult = await PublishTierAsync(dialog.SelectedServers.Where(server => server.ReleaseTier != 1));
            if (!tierTwoResult.StopSucceeded) return;

            var allSucceeded = starts.All(item => item.Response?.Success == true) && publishes.All(item => item.Response?.Success == true);
            var completedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            AppendPublishLine(allSucceeded ? $"发布流程执行完成 {completedAt}" : $"发布流程执行完成，但存在失败项，请查看红色日志。 {completedAt}", allSucceeded ? Brushes.SeaGreen : Brushes.Crimson, true);
            var failedServices = starts.SelectMany(result => (result.Response?.Items ?? new List<DeploymentStageItem>())
                .Where(item => !item.Success)
                .Select(item => (result.Server.Name, Item: item))).ToArray();
            foreach (var failed in failedServices)
                AppendPublishLine($"【{failed.Name}】服务：{failed.Item.ApplicationName}${failed.Item.ServiceName} 启动失败，请尽快处理。", Brushes.Crimson, true, failed.Name);
            SavePublishedFileListToDatabase(dllRoot);
        }
        catch (Exception ex)
        {
            AppendPublishLine($"发布流程异常：{ex.Message}", Brushes.Crimson, true);
        }
        finally
        {
            PublishToServerButton.IsEnabled = true;
            GetDllButton.IsEnabled = true;
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    /// <summary>向一台服务器上传 SMOMDLL 压缩包，并返回该服务器的独立执行结果。</summary>
    private async Task<SmomDllPublishResult> UploadSmomDllAsync(ServerProfile server, string zipPath, int fileCount = 0)
    {
        string lastMessage = string.Empty;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await using var fileStream = File.OpenRead(zipPath);
                using var form = new MultipartFormDataContent();
                using var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
                form.Add(fileContent, "smomDllArchive", Path.GetFileName(zipPath));
                form.Add(new StringContent(_currentLogSetName ?? string.Empty), "logSetName");
                using var response = await _httpClient.PostAsync(server.BaseUrl + "api/deploy/smomdll", form);
                var parsed = await ReadCommandResponseAsync(response, "上传 SMOMDLL");
                var commandResult = parsed.Result;
                lastMessage = parsed.Message;
                if (response.IsSuccessStatusCode && commandResult?.Success == true)
                    return new SmomDllPublishResult(server, true, $"{lastMessage}（第 {attempt} 次成功）", fileCount);
            }
            catch (Exception ex) { lastMessage = ex.Message; }
            if (attempt < 5) await Task.Delay(TimeSpan.FromSeconds(3));
        }
        return new SmomDllPublishResult(server, false, $"上传失败，已尝试 5 次：{lastMessage}");
    }

    /// <summary>备份单台服务器本次确实存在待发布文件的应用目录。</summary>
    private async Task<SmomDllPublishResult> BackupServerAsync(ServerProfile server, string timestamp)
    {
        try
        {
            var request = new ServerBackupRequest { LogSetName = _currentLogSetName, Timestamp = timestamp, ScheduleServerPath = server.ScheduleServerBackupPath, WebApiHostPath = server.WebApiHostBackupPath, WebClientPath = server.WebClientBackupPath, WpfClientPath = server.WpfClientBackupPath, BackupDestinationPath = server.BackupDestinationPath };
            using var response = await _httpClient.PostAsJsonAsync(server.BaseUrl + "api/deploy/backup", request);
            var parsed = await ReadCommandResponseAsync(response, "发布前备份");
            return new SmomDllPublishResult(server, response.IsSuccessStatusCode && parsed.Result?.Success == true, parsed.Message);
        }
        catch (Exception ex) { return new SmomDllPublishResult(server, false, ex.Message); }
    }

    /// <summary>并发执行服务器任务，并按实际完成顺序即时写入日志。</summary>
    private async Task<List<SmomDllPublishResult>> RunServerStageAsync(IEnumerable<ServerProfile> servers, Func<ServerProfile, Task<SmomDllPublishResult>> operation, Func<SmomDllPublishResult, string> format, Func<SmomDllPublishResult, System.Windows.Media.Brush> color)
    {
        var pending = servers.Select(operation).ToList();
        var results = new List<SmomDllPublishResult>();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending); pending.Remove(completed);
            var result = await completed; results.Add(result); AppendPublishLine(format(result), color(result));
        }
        return results;
    }

    /// <summary>调用服务端分阶段接口并返回每台服务器的执行明细。</summary>
    private async Task<List<ServerStageResult>> RunDeploymentStageAsync(IEnumerable<ServerProfile> servers, string endpoint, Action<ServerStageResult>? completedCallback = null)
    {
        async Task<ServerStageResult> ExecuteAsync(ServerProfile server)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(server.BaseUrl + endpoint, CreateCurrentDeploymentRequest(server));
                var content = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(content)) return new(server, null, $"HTTP {(int)response.StatusCode}，服务端未返回内容，请更新并重启服务端。");
                var result = JsonSerializer.Deserialize<DeploymentStageResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return new(server, result, result?.Message ?? "服务端返回空结果。");
            }
            catch (Exception ex) { return new(server, null, ex.Message); }
        }
        var pending = servers.Select(ExecuteAsync).ToList();
        var results = new List<ServerStageResult>();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending); pending.Remove(completed); var result = await completed; results.Add(result);
            if (result.Response is null) AppendPublishLine($"【{result.Server.Name}】阶段请求失败：{result.Message}", Brushes.Crimson, serverName: result.Server.Name);
            else completedCallback?.Invoke(result);
        }
        return results;
    }

    private void WriteServiceStage(IEnumerable<ServerStageResult> results, string action)
    {
        foreach (var result in results)
            foreach (var item in result.Response?.Items ?? new())
                AppendPublishLine($"【{result.Server.Name}】{item.ApplicationName}${item.ServiceName} {action}{(item.Success ? "✔" : $"×（已尝试{item.Attempts}次）")}", item.Success ? Brushes.SeaGreen : Brushes.Crimson);
    }

    private void AppendPublishHeader(string text) => AppendPublishLine(text, Brushes.SteelBlue, true);
    /// <summary>发布步骤开始时追加该步骤自己的当前时间。</summary>
    private void AppendPublishStepHeader(string text) => AppendPublishHeader($"{text} {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    private void AppendPublishAbort(string text) => AppendPublishLine(text, Brushes.Crimson, true);

    /// <summary>在配置检查结果末尾，按服务器和应用集中显示所有红色失败原因。</summary>
    private void WriteConfigurationFailureReasons(IEnumerable<ServerStageResult> checks)
    {
        var failedResults = checks.Where(result => result.Response is null || !result.Response.Success).ToArray();
        if (failedResults.Length == 0) return;

        AppendPublishLine("配置检查失败原因：", Brushes.Crimson, true);
        foreach (var serverResult in failedResults)
        {
            if (serverResult.Response is null)
            {
                AppendPublishLine($"【{serverResult.Server.Name}】>>原因：无法取得服务器配置检查结果：{serverResult.Message}", Brushes.Crimson, true, serverResult.Server.Name);
                AppendPublishLine(string.Empty, Brushes.Crimson);
                continue;
            }

            foreach (var item in serverResult.Response.Items.Where(item => !item.Success))
            {
                var fileMark = item.HasFiles ? "✔" : "○";
                var backupMark = item.BackupPathConfigured ? item.BackupPathExists ? "✔" : "×" : "○";
                var serviceMark = item.ServicesConfigured ? item.ServicesExist ? "✔" : "×" : "○";
                var reasons = GetConfigurationFailureReasons(item);
                if (reasons.Count == 0) reasons.Add("服务器配置不满足发布条件。");
                AppendPublishLine($"【{serverResult.Server.Name}】{item.ApplicationName}[文件{fileMark}，备份路径{backupMark}，服务{serviceMark}] >>原因：{reasons[0]}", Brushes.Crimson, true, serverResult.Server.Name);
                foreach (var reason in reasons.Skip(1))
                    AppendPublishLine($"    原因：{reason}", Brushes.Crimson, true, serverResult.Server.Name);
            }
            // 不同服务器的原因之间留一行，便于批量发布时识别。
            AppendPublishLine(string.Empty, Brushes.Crimson);
        }
    }

    /// <summary>根据服务端返回的检查状态生成一项或多项明确原因。</summary>
    private static List<string> GetConfigurationFailureReasons(DeploymentStageItem item)
    {
        var reasons = new List<string>();
        if (item.BackupPathConfigured && !item.BackupPathExists)
            reasons.Add("配置了备份路径，但该路径在服务端不存在。");
        if (item.ServicesConfigured && !item.ServicesExist)
            reasons.Add("配置了服务名，但其中至少一个服务在服务端不存在。");
        if (!string.Equals(item.ApplicationName, "WpfClient", StringComparison.OrdinalIgnoreCase)
            && item.BackupPathConfigured && !item.ServicesConfigured)
            reasons.Add("配置了备份路径，但没有配置对应服务名。");
        return reasons;
    }

    /// <summary>把本次发布的 DLL 清单仅写入数据库，不追加到当前执行结果界面。</summary>
    private void SavePublishedFileListToDatabase(string dllRoot)
    {
        if (string.IsNullOrWhiteSpace(_currentLogSetName)) return;
        _logRepository.Append(_currentLogSetName, "本次发布的文件", "Header");
        foreach (var applicationName in DllProjectNames)
        {
            _logRepository.Append(_currentLogSetName, $"【{applicationName}】", "Header");
            var applicationDirectory = Path.Combine(dllRoot, applicationName);
            if (Directory.Exists(applicationDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(applicationDirectory, "*.dll", SearchOption.AllDirectories).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    _logRepository.Append(_currentLogSetName, $"\t{Path.GetFileName(file)}", "Success");
            }
            _logRepository.Append(_currentLogSetName, string.Empty, "Success");
        }
    }

    /// <summary>向发布日志追加一行带颜色的文字，并立即滚动到最新结果。</summary>
    private void AppendPublishLine(string text, System.Windows.Media.Brush color, bool bold = false, string serverName = "")
    {
        var paragraph = new Paragraph(new Run(text) { Foreground = color, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal }) { Margin = new Thickness(0, 2, 0, 2) };
        PublishLogRichTextBox.Document.Blocks.Add(paragraph);
        PublishLogRichTextBox.ScrollToEnd();
        if (!string.IsNullOrWhiteSpace(_currentLogSetName))
        {
            var level = color == Brushes.Crimson ? "Error" : color == Brushes.DarkGoldenrod ? "Warning" : bold ? "Header" : "Success";
            _logRepository.Append(_currentLogSetName, text, level, serverName);
        }
    }

    private void ViewLogs_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => new LogViewerWindow(_logRepository) { Owner = this }.ShowDialog();

    private sealed record ServerStageResult(ServerProfile Server, DeploymentStageResponse? Response, string Message);

    /// <summary>按顺序执行单台服务器的 SMOMDLL 同步和发布前备份。</summary>
    private async Task<SmomDllPublishResult> PublishAndBackupAsync(ServerProfile server, string zipPath, string backupTimestamp)
    {
        var syncResult = await UploadSmomDllAsync(server, zipPath);
        if (!syncResult.Success) return syncResult;

        var backupRequest = new ServerBackupRequest
        {
            Timestamp = backupTimestamp,
            ScheduleServerPath = server.ScheduleServerBackupPath,
            WebApiHostPath = server.WebApiHostBackupPath,
            WebClientPath = server.WebClientBackupPath,
            WpfClientPath = server.WpfClientBackupPath,
            BackupDestinationPath = server.BackupDestinationPath
        };
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(server.BaseUrl + "api/deploy/backup", backupRequest);
            var backupParsed = await ReadCommandResponseAsync(response, "发布前备份");
            var backupResult = backupParsed.Result;
            var backupMessage = backupParsed.Message;
            var backupSuccess = response.IsSuccessStatusCode && backupResult?.Success == true;
            if (!backupSuccess) return new SmomDllPublishResult(server, false, $"上传：{syncResult.Message}；备份失败：{backupMessage}。未执行文件覆盖。");
            using var applyResponse = await _httpClient.PostAsJsonAsync(server.BaseUrl + "api/deploy/apply", CreateDeploymentRequest(server));
            var applyParsed = await ReadCommandResponseAsync(applyResponse, "执行发布");
            var applyResult = applyParsed.Result;
            var applyMessage = applyParsed.Message;
            var success = applyResponse.IsSuccessStatusCode && applyResult?.Success == true;
            return new SmomDllPublishResult(server, success, $"上传：{syncResult.Message}；备份：{backupMessage}；{applyMessage}");
        }
        catch (Exception ex)
        {
            return new SmomDllPublishResult(server, false, $"第1步：{syncResult.Message} 第2步备份失败：{ex.Message}");
        }
    }

    /// <summary>把服务器路径和对应服务配置转换成发布、回滚接口使用的请求。</summary>
    private static DeploymentRollbackRequest CreateDeploymentRequest(ServerProfile server) => new()
    {
        ScheduleServerPath = server.ScheduleServerBackupPath, WebApiHostPath = server.WebApiHostBackupPath, WebClientPath = server.WebClientBackupPath, WpfClientPath = server.WpfClientBackupPath,
        ScheduleServerServices = server.ScheduleServerServiceName, WebApiHostServices = server.WebApiHostServiceName, WebClientServices = server.WebClientServiceName, WpfClientServices = server.WpfClientServiceName,
        BackupDestinationPath = server.BackupDestinationPath
    };

    private DeploymentRollbackRequest CreateCurrentDeploymentRequest(ServerProfile server)
    {
        var request = CreateDeploymentRequest(server); request.LogSetName = _currentLogSetName; return request;
    }

    /// <summary>读取所选服务器备份列表，让用户选择版本后执行服务端回滚。</summary>
    private async void RollbackServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 打开批量回滚窗口前刷新一次全部服务器状态，确认时据此执行严格的在线校验。
            await Task.WhenAll(_allServers.Select(CheckServerStatusAsync));
            async Task<(bool Success, string Message, string[] Files)> LoadBackupsAsync(ServerProfile server)
            {
                try
                {
                    using var response = await _httpClient.PostAsJsonAsync(server.BaseUrl + "api/deploy/backups", CreateDeploymentRequest(server));
                    var parsed = await ReadCommandResponseAsync(response, "读取备份列表");
                    var files = parsed.Result?.Data is JsonElement element && element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray() : Array.Empty<string>();
                    return (response.IsSuccessStatusCode && parsed.Result?.Success == true, parsed.Message, files);
                }
                catch (Exception ex) { return (false, ex.Message, Array.Empty<string>()); }
            }
            var dialog = new RollbackSelectionWindow(_allServers, LoadBackupsAsync) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var rollbackTimestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _currentLogSetName = "RollBack" + rollbackTimestamp;
            var backupSummary = string.Join("，", dialog.SelectedOptions.Select(item => $"{item.Server.Name}:{item.SelectedBackupFile}"));
            _logRepository.CreateSet(_currentLogSetName, "RollBack", backupSummary);
            ResultTextBox.Visibility = Visibility.Collapsed; PublishLogRichTextBox.Visibility = Visibility.Visible; PublishLogRichTextBox.Document.Blocks.Clear();
            AppendPublishLine($"当前回滚日志：{_currentLogSetName}", Brushes.SteelBlue, true);
            AppendPublishLine("回滚备份集：", Brushes.SteelBlue, true);
            foreach (var option in dialog.SelectedOptions)
                AppendPublishLine($"{option.Server.Name}:{option.SelectedBackupFile}", Brushes.SteelBlue);

            AppendPublishHeader("第1步：停止服务");
            var stops = await RunRollbackStageAsync(dialog.SelectedOptions, "api/deploy/rollback-stop", result => WriteServiceStage(new[] { result }, "停止"));
            if (stops.Any(result => result.Response is null || !result.Response.Success))
            {
                AppendPublishAbort("存在服务停止失败，不执行回滚备份集；正在恢复已停止的服务。");
                await RunRollbackStageAsync(dialog.SelectedOptions, "api/deploy/rollback-start", result => WriteServiceStage(new[] { result }, "恢复启动"));
                return;
            }

            AppendPublishHeader("第2步：回滚备份集");
            var rollbacks = await RunRollbackStageAsync(dialog.SelectedOptions, "api/deploy/rollback-files", result =>
            {
                foreach (var item in result.Response?.Items ?? new())
                    AppendPublishLine($"【{result.Server.Name}】{item.ApplicationName} 回滚{(item.Success ? "✔" : $"×（已尝试{item.Attempts}次）")}{(item.ApplicationName == "WpfClient" && item.Success && item.Version is not null ? $" 当前版本{item.Version}" : string.Empty)}", item.Success ? Brushes.SeaGreen : Brushes.Crimson);
            });

            AppendPublishHeader("第3步：启动服务");
            var starts = await RunRollbackStageAsync(dialog.SelectedOptions, "api/deploy/rollback-start", result => WriteServiceStage(new[] { result }, "启动"));
            var success = rollbacks.All(item => item.Response?.Success == true) && starts.All(item => item.Response?.Success == true);
            AppendPublishLine(success ? "回滚流程执行完成。" : "回滚流程执行完成，但存在失败项，请查看红色日志。", success ? Brushes.SeaGreen : Brushes.Crimson, true);
            var failedServices = starts.SelectMany(result => (result.Response?.Items ?? new List<DeploymentStageItem>())
                .Where(item => !item.Success)
                .Select(item => (result.Server.Name, Item: item))).ToArray();
            foreach (var failed in failedServices)
                AppendPublishLine($"【{failed.Name}】服务：{failed.Item.ApplicationName}${failed.Item.ServiceName} 启动失败，请尽快处理。", Brushes.Crimson, true, failed.Name);
        }
        catch (Exception ex) { AppendPublishLine($"发布回滚失败：{ex.Message}", Brushes.Crimson, true); }
    }

    /// <summary>选择服务升级包和多台服务器，并发触发无界面 Windows 服务自动更新。</summary>
    private async void UpdateServerService_Click(object sender, RoutedEventArgs e)
    {
        var fileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择服务端自动升级包",
            Filter = "服务端升级包 (*.zip)|*.zip"
        };
        if (fileDialog.ShowDialog(this) != true) return;

        var selection = new PublishServerSelectionWindow(_allServers)
        {
            Owner = this,
            Title = "选择需要升级的服务器"
        };
        if (selection.ShowDialog() != true || selection.SelectedServers.Count == 0) return;

        byte[] hash;
        await using (var packageStream = File.OpenRead(fileDialog.FileName))
        {
            using var sha256 = SHA256.Create();
            hash = await sha256.ComputeHashAsync(packageStream);
        }
        var hashText = Convert.ToHexString(hash);

        ResultTextBox.Visibility = Visibility.Visible;
        PublishLogRichTextBox.Visibility = Visibility.Collapsed;
        ResultTextBox.Clear();
        ResultTextBox.AppendText("批量升级 Windows 服务\r\n\r\n");

        async Task<(ServerProfile Server, bool Success, string Message)> UpdateOneAsync(ServerProfile server)
        {
            try
            {
                await using var stream = File.OpenRead(fileDialog.FileName);
                using var form = new MultipartFormDataContent();
                form.Add(new StreamContent(stream), "updatePackage", Path.GetFileName(fileDialog.FileName));
                form.Add(new StringContent(hashText), "sha256");
                using var request = new HttpRequestMessage(HttpMethod.Post, server.BaseUrl + "api/system/update") { Content = form };
                using var response = await _httpClient.SendAsync(request);
                var responseText = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) return (server, false, responseText);

                // 服务退出、覆盖并重新启动需要时间；轮询健康接口确认恢复在线。
                await Task.Delay(3000);
                for (var attempt = 1; attempt <= 30; attempt++)
                {
                    try
                    {
                        using var health = await _statusHttpClient.GetAsync(server.BaseUrl);
                        if (health.IsSuccessStatusCode) return (server, true, "升级完成并已重新在线");
                    }
                    catch { }
                    await Task.Delay(3000);
                }
                return (server, false, "升级包已接收，但90秒内未重新上线");
            }
            catch (Exception ex) { return (server, false, ex.Message); }
        }

        var pending = selection.SelectedServers.Select(UpdateOneAsync).ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var result = await completed;
            ResultTextBox.AppendText($"【{result.Server.Name}】{(result.Success ? "升级✔" : "升级×")} {result.Message}\r\n");
            ResultTextBox.ScrollToEnd();
        }
    }

    /// <summary>多台服务器按各自选择的备份版本并发执行回滚阶段，并按完成顺序实时显示。</summary>
    private async Task<List<ServerStageResult>> RunRollbackStageAsync(IEnumerable<RollbackServerOption> options, string endpoint, Action<ServerStageResult> completedCallback)
    {
        async Task<ServerStageResult> ExecuteAsync(RollbackServerOption option)
        {
            try
            {
                var request = CreateCurrentDeploymentRequest(option.Server); request.BackupFileName = option.SelectedBackupFile;
                using var response = await _httpClient.PostAsJsonAsync(option.Server.BaseUrl + endpoint, request);
                var content = await response.Content.ReadAsStringAsync();
                var result = string.IsNullOrWhiteSpace(content) ? null : JsonSerializer.Deserialize<DeploymentStageResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return new(option.Server, result, result?.Message ?? $"HTTP {(int)response.StatusCode}，服务端未返回有效结果。");
            }
            catch (Exception ex) { return new(option.Server, null, ex.Message); }
        }
        var pending = options.Select(ExecuteAsync).ToList(); var results = new List<ServerStageResult>();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending); pending.Remove(completed); var result = await completed; results.Add(result);
            if (result.Response is null) AppendPublishLine($"【{result.Server.Name}】阶段请求失败：{result.Message}", Brushes.Crimson, serverName: result.Server.Name); else completedCallback(result);
        }
        return results;
    }

    /// <summary>安全读取服务端响应，避免空响应或旧版接口返回非 JSON 时显示难懂的解析异常。</summary>
    private static async Task<(CommandResponse? Result, string Message)> ReadCommandResponseAsync(HttpResponseMessage response, string operationName)
    {
        var content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(content))
        {
            var hint = response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "当前服务端不支持此接口，请把服务端更新为最新版本并重新启动。"
                : "服务端没有返回内容，请检查服务端运行日志。";
            return (null, $"{operationName}失败：HTTP {(int)response.StatusCode}，{hint}");
        }
        try
        {
            var result = JsonSerializer.Deserialize<CommandResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return result is null
                ? (null, $"{operationName}失败：服务端返回了空的 JSON 结果。")
                : (result, result.Message);
        }
        catch (JsonException)
        {
            var safeContent = content.Length > 500 ? content[..500] + "..." : content;
            var hint = response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "当前服务端可能仍是旧版本，请更新并重新启动服务端。"
                : "服务端返回格式不是有效的 JSON，请查看服务端日志。";
            return (null, $"{operationName}失败：HTTP {(int)response.StatusCode}，{hint}{Environment.NewLine}服务端响应：{safeContent}");
        }
    }

    /// <summary>一台服务器的 SMOMDLL 同步结果。</summary>
    private sealed record SmomDllPublishResult(ServerProfile Server, bool Success, string Message, int FileCount = 0);

    /// <summary>根据所选模式收集 DLL，并复制到程序目录下对应的 SMOMDLL 子目录。</summary>
    private async void GetDllButton_Click(object sender, RoutedEventArgs e)
    {
        _pathDetectionCts?.Cancel();
        _pathDetectionCts = new CancellationTokenSource();
        GetDllButton.IsEnabled = false;
        PublishToServerButton.IsEnabled = false;
        ResultTextBox.Visibility = Visibility.Collapsed;
        PublishLogRichTextBox.Visibility = Visibility.Visible;
        PublishLogRichTextBox.Document.Blocks.Clear();
        var output = new List<string>();
        void ShowOutput() => RenderDllOutput(output);
        try
        {
            await ResolveSmomPathsAsync(_pathDetectionCts.Token);
            if (_resolvedOutputPaths is null)
            {
                output.Add("未能识别 SMOM 项目路径，无法获取 DLL。"); ShowOutput();
                return;
            }

            var specifiedMode = SpecifiedDllRadioButton.IsChecked == true;
            var listFilePath = GetDllListFilePath();
            HashSet<string> specifiedNames;
            if (specifiedMode)
            {
                output.Add("获取指定DLL"); output.Add(string.Empty); output.Add("第1步：读取GetDLL.txt信息");
                if (!File.Exists(listFilePath))
                {
                    File.WriteAllText(listFilePath, string.Empty);
                    output.Add("GetDLL.txt文件不存在，已经自动为您创建，请先填写指定DLL名"); ShowOutput(); DllCountTextBlock.Text = "待获取：0 个"; return;
                }
                specifiedNames = ReadSpecifiedDllNames(createWhenMissing: false);
                if (specifiedNames.Count == 0)
                {
                    output.Add("请先填写指定DLL名"); ShowOutput(); DllCountTextBlock.Text = "待获取：0 个"; return;
                }
                output.Add("即将要获取的DLL："); output.AddRange(specifiedNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)); output.Add(string.Empty);
            }
            else specifiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 清空目标目录前先扫描源文件，确保全量模式能显示准确的预计数量。
            var sourceFilesByProject = new string[DllProjectNames.Length][];
            for (var index = 0; index < _resolvedOutputPaths.Length; index++)
            {
                var sourceDirectory = _resolvedOutputPaths[index];
                var sourceFiles = Directory.Exists(sourceDirectory) ? Directory.GetFiles(sourceDirectory, "*.dll", SearchOption.TopDirectoryOnly) : Array.Empty<string>();
                sourceFilesByProject[index] = specifiedMode
                    ? sourceFiles.Where(file => specifiedNames.Contains(Path.GetFileName(file))).ToArray()
                    : sourceFiles.Where(file => Path.GetFileName(file).StartsWith("SIE", StringComparison.OrdinalIgnoreCase)).ToArray();
            }

            if (!specifiedMode)
            {
                output.Add("获取全量DLL"); output.Add(string.Empty); output.Add("第1步：全量DLL数量");
                for (var index = 0; index < DllProjectNames.Length; index++) output.Add($"{DllProjectNames[index]}  预计{sourceFilesByProject[index].Length}个DLL");
                output.Add(string.Empty);
            }

            output.Add("第2步：清空原目录文件"); ShowOutput();
            var prepareResult = await PrepareDllTargetDirectoriesAsync(_pathDetectionCts.Token);
            output.AddRange(prepareResult.Messages); ShowOutput();
            if (!prepareResult.Success)
            {
                output.Add("存在目录未能清空，不执行下一步操作。"); ShowOutput(); return;
            }

            output.Add(string.Empty); output.Add("第3步：获取DLL"); ShowOutput();
            var totalCopied = 0;
            for (var index = 0; index < DllProjectNames.Length; index++)
            {
                var copied = 0;
                foreach (var sourceFile in sourceFilesByProject[index])
                {
                    File.Copy(sourceFile, Path.Combine(prepareResult.Directories[index], Path.GetFileName(sourceFile)), overwrite: true);
                    copied++; totalCopied++;
                }
                output.Add($"目录：...\\SMOMDLL\\{DllProjectNames[index]}  已获取到{copied}个DLL"); ShowOutput();
            }
            DllCountTextBlock.Text = $"汇总：{totalCopied} 个";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { output.Add($"获取 DLL 失败：{ex.Message}"); ShowOutput(); }
        finally { GetDllButton.IsEnabled = true; PublishToServerButton.IsEnabled = true; }
    }

    /// <summary>以蓝色步骤标题和橙色数量渲染 DLL 获取结果；此方法只更新界面，不写日志数据库。</summary>
    private void RenderDllOutput(IEnumerable<string> lines)
    {
        PublishLogRichTextBox.Document.Blocks.Clear();
        foreach (var line in lines)
        {
            var isHeader = line is "获取指定DLL" or "获取全量DLL" || line.StartsWith("第1步：", StringComparison.Ordinal) || line.StartsWith("第2步：", StringComparison.Ordinal) || line.StartsWith("第3步：", StringComparison.Ordinal);
            var isBlockingReason = line.Contains("请先填写指定DLL名", StringComparison.Ordinal)
                || line.Contains("未能识别", StringComparison.Ordinal)
                || line.Contains("不执行下一步", StringComparison.Ordinal)
                || line.Contains("清空×", StringComparison.Ordinal)
                || line.Contains("获取 DLL 失败", StringComparison.Ordinal);
            var paragraph = new Paragraph { Margin = new Thickness(0, isHeader ? 4 : 2, 0, isHeader ? 5 : 2) };
            if (isHeader)
            {
                paragraph.Inlines.Add(new Run(line) { Foreground = Brushes.SteelBlue, FontWeight = FontWeights.Bold });
            }
            else if (isBlockingReason)
            {
                paragraph.Inlines.Add(new Run(line) { Foreground = Brushes.Crimson, FontWeight = FontWeights.Bold });
            }
            else
            {
                // 只突出“预计N个DLL”和“已获取到N个DLL”中的数字。
                var matches = System.Text.RegularExpressions.Regex.Matches(line, @"(?<=预计)\d+(?=个DLL)|(?<=已获取到)\d+(?=个DLL)");
                var position = 0;
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Index > position) paragraph.Inlines.Add(new Run(line[position..match.Index]));
                    paragraph.Inlines.Add(new Run(match.Value) { Foreground = Brushes.DarkOrange, FontWeight = FontWeights.Bold });
                    position = match.Index + match.Length;
                }
                if (position < line.Length) paragraph.Inlines.Add(new Run(line[position..]));
                if (line.Length == 0) paragraph.Inlines.Add(new Run(" "));
            }
            PublishLogRichTextBox.Document.Blocks.Add(paragraph);
        }
        PublishLogRichTextBox.ScrollToEnd();
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
    private static async Task<(bool Success, string[] Directories, List<string> Messages)> PrepareDllTargetDirectoriesAsync(CancellationToken token)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "SMOMDLL");
        var rootExisted = Directory.Exists(root);
        Directory.CreateDirectory(root);
        var targets = DllProjectNames.Select(name => Path.Combine(root, name)).ToArray();
        var anyTargetExisted = targets.Any(Directory.Exists);
        var messages = new List<string>();
        var allSucceeded = true;
        foreach (var target in targets)
        {
            var success = false;
            string? lastError = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                    Directory.CreateDirectory(target);
                    // 删除并重建后再次确认其中没有任何文件和子目录。
                    if (Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("目录清空后仍存在内容。");
                    success = true; break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lastError = ex.Message; if (attempt < 5) await Task.Delay(TimeSpan.FromSeconds(1), token); }
            }
            allSucceeded &= success;
            messages.Add(success ? $"目录：...\\SMOMDLL\\{Path.GetFileName(target)}  清空✔" : $"目录：...\\SMOMDLL\\{Path.GetFileName(target)}  清空×（已尝试5次）：{lastError}");
        }
        if (!rootExisted || !anyTargetExisted) messages.Insert(0, "SMOMDLL相关目录不存在，已经自动为您创建。");
        return (allSucceeded, targets, messages);
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
