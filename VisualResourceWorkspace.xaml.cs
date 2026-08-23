using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UniFiDnsManager.Models;
using UniFiDnsManager.Services;

namespace UniFiDnsManager;

public partial class VisualResourceWorkspace : UserControl
{
    private readonly ObservableCollection<OfficialApiOperation> _listOperations = [];
    private readonly ObservableCollection<OfficialApiOperation> _otherOperations = [];
    private readonly ObservableCollection<OfficialResourceItem> _visibleItems = [];
    private readonly ObservableCollection<OfficialResourceDistributionItem> _distribution = [];
    private IReadOnlyList<OfficialResourceItem> _allItems = [];
    private IUniFiClient? _client;
    private string _moduleId = "devices";
    private OfficialApiOperation? _primaryOperation;
    private OfficialResourceItem? _selectedItem;
    private bool _changingModule;

    public event EventHandler<string>? StatusChanged;

    public VisualResourceWorkspace()
    {
        InitializeComponent();
        ResourceTabList.ItemsSource = _listOperations;
        OperationComboBox.ItemsSource = _otherOperations;
        ResourceGrid.ItemsSource = _visibleItems;
        TypeDistributionList.ItemsSource = _distribution;
    }

    public void SetClient(IUniFiClient? client)
    {
        _client = client;
        UpdateActionState();
    }

    public async void ShowModule(string moduleId)
    {
        if (string.Equals(_moduleId, moduleId, StringComparison.OrdinalIgnoreCase) && _listOperations.Count > 0)
        {
            if (_client is not null && _allItems.Count == 0) await LoadSelectedListAsync();
            return;
        }

        _changingModule = true;
        _moduleId = moduleId;
        _listOperations.Clear();
        _otherOperations.Clear();
        _allItems = [];
        _visibleItems.Clear();
        _distribution.Clear();
        DrawerLayer.Visibility = Visibility.Collapsed;

        var operations = OfficialApiCatalog.ForModule(moduleId).ToList();
        foreach (var operation in operations.Where(IsAutomaticListOperation)) _listOperations.Add(operation);
        if (_listOperations.Count == 0)
        {
            var firstGet = operations.FirstOrDefault(operation => operation.Method == "GET");
            if (firstGet is not null) _listOperations.Add(firstGet);
        }

        _primaryOperation = operations.FirstOrDefault(IsPrimaryCreateOperation);
        foreach (var operation in operations.Where(operation => !_listOperations.Contains(operation) && operation != _primaryOperation))
            _otherOperations.Add(operation);
        OperationComboBox.SelectedIndex = _otherOperations.Count > 0 ? 0 : -1;
        PrimaryActionButton.Visibility = _primaryOperation is null ? Visibility.Collapsed : Visibility.Visible;
        if (_primaryOperation is not null) PrimaryActionButton.Content = _primaryOperation.Title;
        RunOperationButton.IsEnabled = _otherOperations.Count > 0;
        OperationComboBox.Visibility = _otherOperations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunOperationButton.Visibility = _otherOperations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TotalGlyphText.Text = GlyphForModule(moduleId);
        ResourceSearchTextBox.Text = "";
        ResourceTabList.SelectedIndex = _listOperations.Count > 0 ? 0 : -1;
        _changingModule = false;
        UpdateActionState();
        await LoadSelectedListAsync();
    }

    private static bool IsAutomaticListOperation(OfficialApiOperation operation)
    {
        if (operation.Method != "GET" || operation.PathParameters.Count > 0) return false;
        var defaults = ParseQuery(operation.DefaultQuery);
        return operation.RequiredQueries.All(name => defaults.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value));
    }

    private static bool IsPrimaryCreateOperation(OfficialApiOperation operation)
    {
        if (!operation.IsWrite || operation.PathParameters.Count > 0) return false;
        return operation.Id.StartsWith("create", StringComparison.OrdinalIgnoreCase)
            || operation.Id.Equals("adoptDevice", StringComparison.OrdinalIgnoreCase);
    }

    private async void ResourceTabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingModule || ResourceTabList.SelectedItem is not OfficialApiOperation) return;
        await LoadSelectedListAsync();
    }

    private async Task LoadSelectedListAsync()
    {
        if (_client is null || ResourceTabList.SelectedItem is not OfficialApiOperation operation) return;
        try
        {
            SetLoading(true, $"正在读取{operation.Title.Replace("列出", "", StringComparison.Ordinal)}…");
            var path = operation.ResolvePath(_client.SiteId, new Dictionary<string, string>());
            if (!string.IsNullOrWhiteSpace(operation.DefaultQuery)) path += $"?{operation.DefaultQuery.Trim().TrimStart('?')}";
            var response = await _client.ExecuteOfficialApiAsync("GET", path);
            var snapshot = OfficialResourcePresentationService.Parse(response, _moduleId);
            _allItems = snapshot.Items;
            UpdateSnapshot(snapshot);
            ApplyFilter();
            StatusChanged?.Invoke(this, $"已读取 {snapshot.Items.Count} 条{operation.Title.Replace("列出", "", StringComparison.Ordinal)}。" );
        }
        catch (Exception ex)
        {
            _allItems = [];
            _visibleItems.Clear();
            UpdateSnapshot(new OfficialResourceSnapshot());
            EmptyTitleText.Text = "资源读取失败";
            EmptyDescriptionText.Text = ex.Message;
            EmptyState.Visibility = Visibility.Visible;
            StatusChanged?.Invoke(this, $"读取失败：{ex.Message}");
        }
        finally
        {
            SetLoading(false, "");
        }
    }

    private void UpdateSnapshot(OfficialResourceSnapshot snapshot)
    {
        TotalCountText.Text = snapshot.TotalCount.ToString();
        HealthyCountText.Text = snapshot.HealthyCount.ToString();
        AttentionCountText.Text = snapshot.AttentionCount.ToString();
        TypeCountText.Text = snapshot.TypeCount.ToString();
        HealthyNoteText.Text = snapshot.TotalCount == 0 ? "0%" : $"{snapshot.HealthyCount * 100 / Math.Max(1, snapshot.TotalCount)}% 可用";
        var unknown = Math.Max(0, snapshot.TotalCount - snapshot.HealthyCount - snapshot.AttentionCount);
        foreach (var progress in new[] { HealthyProgress, AttentionProgress, UnknownProgress }) progress.Maximum = Math.Max(1, snapshot.TotalCount);
        HealthyProgress.Value = snapshot.HealthyCount;
        AttentionProgress.Value = snapshot.AttentionCount;
        UnknownProgress.Value = unknown;
        HealthyProgressText.Text = snapshot.HealthyCount.ToString();
        AttentionProgressText.Text = snapshot.AttentionCount.ToString();
        UnknownProgressText.Text = unknown.ToString();
        _distribution.Clear();
        foreach (var item in snapshot.TypeDistribution) _distribution.Add(item);
    }

    private void ResourceSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var term = ResourceSearchTextBox.Text.Trim();
        var matches = string.IsNullOrWhiteSpace(term)
            ? _allItems
            : _allItems.Where(item => new[] { item.Name, item.Subtitle, item.State, item.Type, item.Address, item.Detail, item.Id }
                .Any(value => value.Contains(term, StringComparison.CurrentCultureIgnoreCase))).ToList();
        _visibleItems.Clear();
        foreach (var item in matches) _visibleItems.Add(item);
        ResultCountText.Text = $"显示 {_visibleItems.Count} / {_allItems.Count} 条";
        EmptyTitleText.Text = string.IsNullOrWhiteSpace(term) ? "暂无资源" : "没有匹配的资源";
        EmptyDescriptionText.Text = string.IsNullOrWhiteSpace(term)
            ? "当前官方接口没有返回资源。"
            : "请调整搜索条件后重试。";
        EmptyState.Visibility = _visibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResourceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResourceGrid.SelectedItem is not OfficialResourceItem item) return;
        ShowDrawer(item);
    }

    private void ShowDrawer(OfficialResourceItem item)
    {
        _selectedItem = item;
        DrawerGlyphText.Text = item.Glyph;
        DrawerNameText.Text = item.Name;
        DrawerStateText.Text = item.State;
        var stateBrush = (Brush)new BrushConverter().ConvertFromString(item.StateColor)!;
        DrawerStateText.Foreground = stateBrush;
        DrawerStateDot.Fill = stateBrush;
        DrawerSubtitleText.Text = $"{item.Type} · {item.Address}";
        DrawerFieldsList.ItemsSource = item.Fields;
        OpenDetailsButton.IsEnabled = FindDetailOperation() is not null;
        DrawerLayer.Visibility = Visibility.Visible;
    }

    private void CloseDrawerButton_Click(object sender, RoutedEventArgs e) => DrawerLayer.Visibility = Visibility.Collapsed;

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadSelectedListAsync();

    private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_primaryOperation is not null) await ExecuteOperationAsync(_primaryOperation, null);
    }

    private async void RunOperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (OperationComboBox.SelectedItem is OfficialApiOperation operation)
            await ExecuteOperationAsync(operation, _selectedItem);
    }

    private async void OpenDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        var operation = FindDetailOperation();
        if (operation is not null) await ExecuteOperationAsync(operation, _selectedItem);
    }

    private OfficialApiOperation? FindDetailOperation() => OfficialApiCatalog.ForModule(_moduleId)
        .FirstOrDefault(operation => operation.Method == "GET" && operation.PathParameters.Count > 0
            && (operation.Id.Contains("Details", StringComparison.OrdinalIgnoreCase)
                || operation.Id.StartsWith("get", StringComparison.OrdinalIgnoreCase)));

    private async Task ExecuteOperationAsync(OfficialApiOperation operation, OfficialResourceItem? selectedItem)
    {
        if (_client is null) return;
        OfficialOperationRequest? request;
        if (CanResolveDirectly(operation, selectedItem))
        {
            var values = operation.PathParameters.ToDictionary(parameter => parameter, _ => selectedItem!.Id, StringComparer.OrdinalIgnoreCase);
            request = new OfficialOperationRequest(operation.ResolvePath(_client.SiteId, values), null);
        }
        else
        {
            var dialog = new OfficialOperationDialog(operation, _client.SiteId, selectedItem) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Request is null) return;
            request = dialog.Request;
        }

        if (operation.IsWrite)
        {
            var warning = operation.IsDestructive
                ? $"确认执行“{operation.Title}”？此操作会立即修改 UniFi，且可能难以恢复。"
                : $"确认执行“{operation.Title}”？此操作会直接修改 UniFi。";
            if (MessageBox.Show(Window.GetWindow(this), warning, "确认官方 API 写操作", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        try
        {
            SetLoading(true, $"正在执行{operation.Title}…");
            var response = await _client.ExecuteOfficialApiAsync(operation.Method, request.RelativePath, request.RequestJson);
            if (operation.Method == "GET")
            {
                var detail = OfficialResourcePresentationService.ParseSingle(response, _moduleId);
                ShowDrawer(detail);
                StatusChanged?.Invoke(this, $"{operation.Title}完成。" );
            }
            else
            {
                StatusChanged?.Invoke(this, $"{operation.Title}完成，正在刷新资源。" );
                await LoadSelectedListAsync();
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"{operation.Title}失败：{ex.Message}");
            MessageBox.Show(Window.GetWindow(this), ex.Message, operation.Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetLoading(false, "");
        }
    }

    private static bool CanResolveDirectly(OfficialApiOperation operation, OfficialResourceItem? selectedItem) =>
        selectedItem is not null && !operation.HasBody && string.IsNullOrWhiteSpace(operation.DefaultQuery)
        && operation.RequiredQueries.Count == 0 && operation.PathParameters.Count > 0
        && operation.PathParameters.All(parameter => parameter.EndsWith("Id", StringComparison.OrdinalIgnoreCase));

    private void CopyIdButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null || string.IsNullOrWhiteSpace(_selectedItem.Id)) return;
        Clipboard.SetText(_selectedItem.Id);
        StatusChanged?.Invoke(this, "已复制资源 ID。" );
    }

    private void SetLoading(bool loading, string message)
    {
        LoadingText.Text = message;
        LoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ContentGrid.IsEnabled = !loading;
    }

    private void UpdateActionState()
    {
        var connected = _client is not null;
        PrimaryActionButton.IsEnabled = connected && _primaryOperation is not null;
        RunOperationButton.IsEnabled = connected && _otherOperations.Count > 0;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Trim().TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator >= 0) result[part[..separator]] = part[(separator + 1)..];
        }
        return result;
    }

    private static string GlyphForModule(string moduleId) => moduleId switch
    {
        "devices" => "▤", "clients" => "◉", "networks" => "⌘", "wifi" => "◖", "hotspot" => "◇",
        "firewall" => "⬡", "traffic" => "≋", "switching" => "⇆", "resources" => "◌", "application" => "ⓘ", _ => "◫"
    };
}
