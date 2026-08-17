using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace KaiZhongReleaseTool;

/// <summary>发布前选择一台或多台目标服务器的窗口。</summary>
public partial class PublishServerSelectionWindow : Window
{
    private readonly List<ServerSelectionItem> _items;
    private readonly Dictionary<string, System.Windows.Controls.CheckBox> _groupCheckBoxes = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingCheckBoxes;
    /// <summary>用户确认选择的服务器。</summary>
    public IReadOnlyList<ServerProfile> SelectedServers { get; private set; } = Array.Empty<ServerProfile>();

    public PublishServerSelectionWindow(IEnumerable<ServerProfile> servers)
    {
        InitializeComponent();
        _items = servers.Select(server => new ServerSelectionItem(server, OnSelectionChanged)).ToList();
        ServerSelectionDataGrid.ItemsSource = _items;
        CreateGroupCheckBoxes();
        UpdateSelectionCount();
    }

    /// <summary>勾选状态改变后，同时刷新数量和当前显示过滤结果。</summary>
    private void OnSelectionChanged()
    {
        UpdateSelectionCount();
        Dispatcher.BeginInvoke(new Action(() => ServerSelectionDataGrid?.Items.Refresh()));
    }

    /// <summary>三个显示复选框按单选方式工作，并过滤服务器行但不改变勾选状态。</summary>
    private void DisplayFilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox selected) return;
        ShowAllCheckBox.IsChecked = selected == ShowAllCheckBox;
        ShowSelectedCheckBox.IsChecked = selected == ShowSelectedCheckBox;
        ShowUnselectedCheckBox.IsChecked = selected == ShowUnselectedCheckBox;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_items);
        view.Filter = item => item is ServerSelectionItem option &&
            (ShowAllCheckBox.IsChecked == true || ShowSelectedCheckBox.IsChecked == true && option.IsSelected || ShowUnselectedCheckBox.IsChecked == true && !option.IsSelected);
        view.Refresh();
    }

    /// <summary>根据当前服务器分组动态创建支持自动换行的分组复选框。</summary>
    private void CreateGroupCheckBoxes()
    {
        foreach (var groupName in _items.Select(item => item.Server.GroupName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name))
        {
            var checkBox = new System.Windows.Controls.CheckBox
            {
                Content = groupName,
                Tag = groupName,
                IsThreeState = true,
                FontSize = 14,
                Padding = new Thickness(5),
                Margin = new Thickness(0, 2, 14, 2),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            checkBox.Click += GroupCheckBox_Click;
            _groupCheckBoxes[groupName] = checkBox;
            GroupCheckBoxPanel.Children.Add(checkBox);
        }
    }

    /// <summary>全选或取消选择列表中的全部服务器。</summary>
    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingCheckBoxes) return;
        var selected = SelectAllCheckBox.IsChecked == true;
        foreach (var item in _items) item.IsSelected = selected;
        UpdateSelectionCount();
    }

    /// <summary>勾选或取消某个分组中的全部服务器。</summary>
    private void GroupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingCheckBoxes || sender is not System.Windows.Controls.CheckBox checkBox || checkBox.Tag is not string groupName) return;
        var selected = checkBox.IsChecked == true;
        foreach (var item in _items.Where(item => string.Equals(item.Server.GroupName, groupName, StringComparison.OrdinalIgnoreCase)))
            item.IsSelected = selected;
        UpdateSelectionCount();
    }

    /// <summary>刷新已选择服务器数量以及全选框状态。</summary>
    private void UpdateSelectionCount()
    {
        var count = _items.Count(item => item.IsSelected);
        SelectionCountTextBlock.Text = $"已选择 {count} / {_items.Count} 台";
        _updatingCheckBoxes = true;
        try
        {
            SelectAllCheckBox.IsChecked = count == 0 ? false : count == _items.Count ? true : null;
            foreach (var (groupName, checkBox) in _groupCheckBoxes)
            {
                var groupItems = _items.Where(item => string.Equals(item.Server.GroupName, groupName, StringComparison.OrdinalIgnoreCase)).ToArray();
                var selectedCount = groupItems.Count(item => item.IsSelected);
                checkBox.IsChecked = selectedCount == 0 ? false : selectedCount == groupItems.Length ? true : null;
            }
        }
        finally { _updatingCheckBoxes = false; }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        SelectedServers = _items.Where(item => item.IsSelected).Select(item => item.Server).ToArray();
        if (SelectedServers.Count == 0)
        {
            System.Windows.MessageBox.Show("请至少选择一台服务器。", "发布服务器", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>给服务器增加界面专用的勾选状态。</summary>
    private sealed class ServerSelectionItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private readonly Action _changed;
        public ServerProfile Server { get; }
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); _changed(); } }
        public ServerSelectionItem(ServerProfile server, Action changed) { Server = server; _changed = changed; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
