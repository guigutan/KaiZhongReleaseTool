using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace KaiZhongReleaseTool;

/// <summary>批量选择回滚服务器，并分别加载和选择各服务器的历史备份。</summary>
public partial class RollbackSelectionWindow : Window
{
    private readonly Func<ServerProfile, Task<(bool Success, string Message, string[] Files)>> _loadBackups;
    private readonly Dictionary<string, System.Windows.Controls.CheckBox> _groupCheckBoxes = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingCheckBoxes;
    public ObservableCollection<RollbackServerOption> Options { get; } = new();
    public IReadOnlyList<RollbackServerOption> SelectedOptions => Options.Where(item => item.IsSelected).ToArray();

    public RollbackSelectionWindow(IEnumerable<ServerProfile> servers, Func<ServerProfile, Task<(bool Success, string Message, string[] Files)>> loadBackups)
    {
        InitializeComponent(); _loadBackups = loadBackups;
        foreach (var server in servers) { var option = new RollbackServerOption(server); option.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(RollbackServerOption.IsSelected)) { UpdateSelectionCount(); Dispatcher.BeginInvoke(new Action(() => ServerDataGrid?.Items.Refresh())); } }; Options.Add(option); }
        ServerDataGrid.ItemsSource = Options; Loaded += async (_, _) => await LoadAllAsync();
        CreateGroupCheckBoxes(); UpdateSelectionCount();
    }

    /// <summary>过滤显示全部、已勾选或未勾选服务器，不影响各行原有选择状态。</summary>
    private void DisplayFilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox selected) return;
        ShowAllCheckBox.IsChecked = selected == ShowAllCheckBox;
        ShowSelectedCheckBox.IsChecked = selected == ShowSelectedCheckBox;
        ShowUnselectedCheckBox.IsChecked = selected == ShowUnselectedCheckBox;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Options);
        view.Filter = item => item is RollbackServerOption option &&
            (ShowAllCheckBox.IsChecked == true || ShowSelectedCheckBox.IsChecked == true && option.IsSelected || ShowUnselectedCheckBox.IsChecked == true && !option.IsSelected);
        view.Refresh();
    }

    private async Task LoadAllAsync()
    {
        var pending = Options.Select(LoadOneAsync).ToList();
        await Task.WhenAll(pending);
    }

    private async Task LoadOneAsync(RollbackServerOption option)
    {
        var result = await _loadBackups(option.Server);
        option.BackupFiles.Clear(); foreach (var file in result.Files) option.BackupFiles.Add(file);
        option.SelectedBackupFile = null; option.CanSelectBackup = result.Success && option.BackupFiles.Count > 0;
        option.Status = result.Success ? option.BackupFiles.Count > 0 ? $"共 {option.BackupFiles.Count} 个备份" : "没有备份文件" : result.Message;
    }

    /// <summary>根据服务器配置中的分组动态创建可自动换行的分组复选框。</summary>
    private void CreateGroupCheckBoxes()
    {
        foreach (var groupName in Options.Select(item => item.Server.GroupName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name))
        {
            var checkBox = new System.Windows.Controls.CheckBox { Content = groupName, Tag = groupName, IsThreeState = true, FontSize = 14, Padding = new Thickness(5), Margin = new Thickness(0, 2, 14, 2), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            checkBox.Click += GroupCheckBox_Click; _groupCheckBoxes[groupName] = checkBox; GroupCheckBoxPanel.Children.Add(checkBox);
        }
    }

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingCheckBoxes) return; var selected = SelectAllCheckBox.IsChecked == true; foreach (var item in Options) item.IsSelected = selected; UpdateSelectionCount();
    }

    private void GroupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingCheckBoxes || sender is not System.Windows.Controls.CheckBox checkBox || checkBox.Tag is not string groupName) return;
        var selected = checkBox.IsChecked == true; foreach (var item in Options.Where(item => string.Equals(item.Server.GroupName, groupName, StringComparison.OrdinalIgnoreCase))) item.IsSelected = selected; UpdateSelectionCount();
    }
    private void ServerCheckBox_Click(object sender, RoutedEventArgs e) { ServerDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true); UpdateSelectionCount(); }
    private void UpdateSelectionCount()
    {
        if (SelectionCountTextBlock is null) return;
        var count = Options.Count(item => item.IsSelected); SelectionCountTextBlock.Text = $"已选择 {count} / {Options.Count} 台";
        _updatingCheckBoxes = true;
        try
        {
            SelectAllCheckBox.IsChecked = count == 0 ? false : count == Options.Count ? true : null;
            foreach (var pair in _groupCheckBoxes)
            {
                var groupItems = Options.Where(item => string.Equals(item.Server.GroupName, pair.Key, StringComparison.OrdinalIgnoreCase)).ToArray(); var selectedCount = groupItems.Count(item => item.IsSelected);
                pair.Value.IsChecked = selectedCount == 0 ? false : selectedCount == groupItems.Length ? true : null;
            }
        }
        finally { _updatingCheckBoxes = false; }
    }
    private void SelectVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: RollbackServerOption option }) return;
        if (option.BackupFiles.Count == 0) { System.Windows.MessageBox.Show($"{option.Server.Name} 没有可选择的备份版本。{Environment.NewLine}{option.Status}", "选择版本", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var dialog = new BackupVersionSelectionWindow(option.Server.Name, option.BackupFiles, option.SelectedBackupFile) { Owner = this };
        if (dialog.ShowDialog() == true) option.SelectedBackupFile = dialog.SelectedVersion;
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        ServerDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true); ServerDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        var selected = Options.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0) { System.Windows.MessageBox.Show("请至少选择一台服务器。", "发布回滚", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var missingVersions = selected.Where(item => string.IsNullOrWhiteSpace(item.SelectedBackupFile)).Select(item => item.Server.Name).ToArray();
        if (missingVersions.Length > 0) { System.Windows.MessageBox.Show("以下服务器尚未选择回滚备份版本，所有服务器都不会执行回滚：" + Environment.NewLine + string.Join(Environment.NewLine, missingVersions), "发布回滚", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var offlineServers = selected.Where(item => !string.Equals(item.Server.Status, "在线", StringComparison.Ordinal)).Select(item => item.Server.Name).ToArray();
        if (offlineServers.Length > 0) { System.Windows.MessageBox.Show("以下服务器当前不在线，所有服务器都不会执行回滚：" + Environment.NewLine + string.Join(Environment.NewLine, offlineServers), "发布回滚", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class RollbackServerOption : INotifyPropertyChanged
{
    private bool _isSelected; private bool _canSelectBackup; private string? _selectedBackupFile; private string _status = "正在读取...";
    public ServerProfile Server { get; }
    public ObservableCollection<string> BackupFiles { get; } = new();
    public bool IsSelected { get => _isSelected; set { _isSelected = value; Notify(); } }
    public bool CanSelectBackup { get => _canSelectBackup; set { _canSelectBackup = value; Notify(); } }
    public string? SelectedBackupFile { get => _selectedBackupFile; set { _selectedBackupFile = value; Notify(); } }
    public string Status { get => _status; set { _status = value; Notify(); } }
    public RollbackServerOption(ServerProfile server) => Server = server;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
