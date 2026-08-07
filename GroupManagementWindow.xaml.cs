using Microsoft.Data.Sqlite;
using System.Windows;

namespace KaiZhongReleaseTool;

/// <summary>独立管理 SQLite 服务器分组的窗口。</summary>
public partial class GroupManagementWindow : Window
{
    private readonly ServerRepository _repository;
    public GroupManagementWindow(ServerRepository repository) { InitializeComponent(); _repository = repository; Reload(); }
    private void Reload() { GroupListBox.ItemsSource = null; GroupListBox.ItemsSource = _repository.GetGroups(); }
    private string InputName => GroupNameTextBox.Text.Trim();
    private void Add_Click(object sender, RoutedEventArgs e) { if (!ValidateName()) return; TryChange(() => _repository.AddGroup(InputName)); }
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (GroupListBox.SelectedItem is not string oldName) { ShowTip("请先选择需要重命名的分组。"); return; }
        if (!ValidateName()) return;
        TryChange(() => _repository.RenameGroup(oldName, InputName));
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (GroupListBox.SelectedItem is not string name) { ShowTip("请先选择需要删除的分组。"); return; }
        if (System.Windows.MessageBox.Show($"确定删除分组“{name}”吗？", "删除分组", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        TryChange(() => _repository.DeleteGroup(name));
    }
    private bool ValidateName() { if (!string.IsNullOrWhiteSpace(InputName)) return true; ShowTip("分组名称不能为空。"); return false; }
    private void TryChange(Action action) { try { action(); GroupNameTextBox.Clear(); Reload(); } catch (SqliteException) { ShowTip("分组名称已经存在。"); } catch (Exception ex) { ShowTip(ex.Message); } }
    private void GroupListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (GroupListBox.SelectedItem is string name) GroupNameTextBox.Text = name; }
    private static void ShowTip(string message) => System.Windows.MessageBox.Show(message, "分组管理", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
