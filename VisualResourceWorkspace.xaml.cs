using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UniFiDnsManager.Models;
using UniFiDnsManager.Services;

namespace UniFiDnsManager;

public partial class VisualResourceWorkspace : UserControl
{
    private readonly ObservableCollection<OfficialApiOperation> _listOperations = [];
    private readonly ObservableCollection<OfficialApiOperation> _otherOperations = [];
    private readonly ObservableCollection<OfficialApiOperation> _contextOperations = [];
    private readonly ObservableCollection<OfficialResourceItem> _visibleItems = [];
    private readonly ObservableCollection<OfficialResourceDistributionItem> _distribution = [];
    private IReadOnlyList<OfficialResourceItem> _allItems = [];
    private IReadOnlyList<OfficialApiOperation> _moduleOperations = [];
    private IUniFiClient? _client;
    private string _moduleId = "devices";
    private OfficialApiOperation? _primaryOperation;
    private OfficialResourceItem? _selectedItem;
    private bool _changingModule;
    private int _loadVersion;

    public event EventHandler<string>? StatusChanged;

    public VisualResourceWorkspace()
    {
        InitializeComponent();
        ResourceTabList.ItemsSource = _listOperations;
        OperationComboBox.ItemsSource = _otherOperations;
        ResourceGrid.ItemsSource = _visibleItems;
        TypeDistributionList.ItemsSource = _distribution;
        DrawerActionsList.ItemsSource = _contextOperations;
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
            if (_client is not null) await LoadSelectedListAsync();
            return;
        }

        _changingModule = true;
        _moduleId = moduleId;
        _listOperations.Clear();
        _otherOperations.Clear();
        _allItems = [];
        _visibleItems.Clear();
        _distribution.Clear();
        _contextOperations.Clear();
        DrawerLayer.Visibility = Visibility.Collapsed;

        _moduleOperations = OfficialApiCatalog.ForModule(moduleId).ToList();
        foreach (var operation in _moduleOperations.Where(IsAutomaticListOperation)) _listOperations.Add(operation);
        if (_listOperations.Count == 0)
        {
            var firstGet = _moduleOperations.FirstOrDefault(operation => operation.Method == "GET");
            if (firstGet is not null) _listOperations.Add(firstGet);
        }

        TotalGlyphText.Text = GlyphForModule(moduleId);
        ResourceSearchTextBox.Text = "";
        ResourceTabList.SelectedIndex = _listOperations.Count > 0 ? 0 : -1;
        _changingModule = false;
        ConfigureSelectedListActions();
        await LoadSelectedListAsync();
    }

    private static bool IsAutomaticListOperation(OfficialApiOperation operation)
    {
        if (operation.Method != "GET" || operation.PathParameters.Count > 0) return false;
        var defaults = ParseQuery(operation.DefaultQuery);
        return operation.RequiredQueries.All(name => defaults.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value));
    }

    private async void ResourceTabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingModule || ResourceTabList.SelectedItem is not OfficialApiOperation) return;
        ConfigureSelectedListActions();
        await LoadSelectedListAsync();
    }

    private async void ResourceTabList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_changingModule || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(ResourceTabList, source) is not ListBoxItem tab
            || tab.Content is not OfficialApiOperation operation
            || ResourceTabList.SelectedItem is not OfficialApiOperation selected
            || !operation.Id.Equals(selected.Id, StringComparison.Ordinal)) return;
        await LoadSelectedListAsync();
    }

    private void ConfigureSelectedListActions()
    {
        var listOperation = ResourceTabList.SelectedItem as OfficialApiOperation;
        _primaryOperation = OfficialResourceInteractionService.FindPrimaryOperation(listOperation, _moduleOperations);
        _otherOperations.Clear();
        foreach (var operation in OfficialResourceInteractionService.FindCollectionOperations(listOperation, _moduleOperations, _primaryOperation))
            _otherOperations.Add(operation);
        OperationComboBox.SelectedIndex = _otherOperations.Count > 0 ? 0 : -1;
        PrimaryActionButton.Visibility = _primaryOperation is null ? Visibility.Collapsed : Visibility.Visible;
        if (_primaryOperation is not null) PrimaryActionButton.Content = _primaryOperation.Title;
        OperationComboBox.Visibility = _otherOperations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunOperationButton.Visibility = _otherOperations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateActionState();
    }

    private async Task LoadSelectedListAsync()
    {
        if (_client is null || ResourceTabList.SelectedItem is not OfficialApiOperation operation) return;
        var loadVersion = ++_loadVersion;
        try
        {
            SetLoading(true, $"正在读取{operation.Title.Replace("列出", "", StringComparison.Ordinal)}…");
            var snapshot = await LoadAllPagesAsync(operation, loadVersion);
            if (loadVersion != _loadVersion) return;
            _allItems = snapshot.Items;
            _selectedItem = null;
            ResourceGrid.SelectedItem = null;
            DrawerLayer.Visibility = Visibility.Collapsed;
            UpdateSnapshot(snapshot);
            ApplyFilter();
            StatusChanged?.Invoke(this, $"已读取 {snapshot.Items.Count} 条{operation.Title.Replace("列出", "", StringComparison.Ordinal)}。" );
        }
        catch (Exception ex)
        {
            if (loadVersion != _loadVersion) return;
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
            if (loadVersion == _loadVersion) SetLoading(false, "");
        }
    }

    private async Task<OfficialResourceSnapshot> LoadAllPagesAsync(OfficialApiOperation operation, int loadVersion)
    {
        var basePath = operation.ResolvePath(_client!.SiteId, new Dictionary<string, string>());
        if (!OfficialResourceInteractionService.SupportsPaging(operation))
        {
            var path = AppendQuery(basePath, operation.DefaultQuery);
            var response = await _client.ExecuteOfficialApiAsync("GET", path);
            return OfficialResourcePresentationService.Parse(response, _moduleId);
        }

        var query = ParseQuery(operation.DefaultQuery);
        var offset = query.TryGetValue("offset", out var offsetValue) && int.TryParse(offsetValue, out var parsedOffset)
            ? Math.Max(0, parsedOffset)
            : 0;
        var limit = query.TryGetValue("limit", out var limitValue) && int.TryParse(limitValue, out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, 200)
            : 50;
        var pages = new List<OfficialResourceSnapshot>();
        var loadedResponseItems = 0;

        for (var pageNumber = 0; pageNumber < 500; pageNumber++)
        {
            var pageQuery = OfficialResourceInteractionService.BuildPageQuery(operation.DefaultQuery, offset, limit);
            var response = await _client.ExecuteOfficialApiAsync("GET", AppendQuery(basePath, pageQuery));
            if (loadVersion != _loadVersion) return new OfficialResourceSnapshot();
            var page = OfficialResourcePresentationService.Parse(response, _moduleId);
            pages.Add(page);
            loadedResponseItems += page.Items.Count;
            if (page.HasReportedTotalCount)
                LoadingText.Text = $"正在读取{operation.Title.Replace("列出", "", StringComparison.Ordinal)}… {Math.Min(loadedResponseItems, page.TotalCount)} / {page.TotalCount}";

            if (page.Items.Count == 0) break;
            if (page.HasReportedTotalCount && loadedResponseItems >= page.TotalCount) break;
            if (!page.HasReportedTotalCount && page.Items.Count < limit) break;
            offset += page.Items.Count;
        }

        return OfficialResourceInteractionService.MergePages(pages);
    }

    private void UpdateSnapshot(OfficialResourceSnapshot snapshot)
    {
        TotalCountText.Text = snapshot.TotalCount.ToString();
        HealthyCountText.Text = snapshot.HasHealthData ? snapshot.HealthyCount.ToString() : "—";
        AttentionCountText.Text = snapshot.HasHealthData ? snapshot.AttentionCount.ToString() : "—";
        TypeCountText.Text = snapshot.TypeCount.ToString();
        HealthyNoteText.Text = snapshot.HasHealthData
            ? snapshot.TotalCount == 0 ? "0%" : $"{snapshot.HealthyCount * 100 / Math.Max(1, snapshot.Items.Count)}% 可用"
            : "接口未返回状态";
        foreach (var progress in new[] { HealthyProgress, AttentionProgress, UnknownProgress }) progress.Maximum = Math.Max(1, snapshot.Items.Count);
        HealthyProgress.Value = snapshot.HasHealthData ? snapshot.HealthyCount : 0;
        AttentionProgress.Value = snapshot.HasHealthData ? snapshot.AttentionCount : 0;
        UnknownProgress.Value = snapshot.HasHealthData ? snapshot.UnknownCount : 0;
        HealthyProgressText.Text = snapshot.HasHealthData ? snapshot.HealthyCount.ToString() : "—";
        AttentionProgressText.Text = snapshot.HasHealthData ? snapshot.AttentionCount.ToString() : "—";
        UnknownProgressText.Text = snapshot.HasHealthData ? snapshot.UnknownCount.ToString() : "—";
        HealthPanelNoteText.Text = snapshot.HasHealthData ? "实时快照" : "接口未返回状态字段";
        HealthyLabelText.Text = snapshot.HasHealthData ? "正常" : "正常";
        AttentionLabelText.Text = snapshot.HasHealthData ? "需要关注" : "需关注";
        UnknownLabelText.Text = snapshot.HasHealthData ? "状态未知" : "无状态字段";
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

    private void ResourceGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var row = FindVisualParent<DataGridRow>(source);
        if (row?.Item is OfficialResourceItem item) ShowDrawer(item);
    }

    private void ShowDrawer(OfficialResourceItem item)
    {
        var isOpening = DrawerLayer.Visibility != Visibility.Visible;
        _selectedItem = item;
        DrawerGlyphText.Text = item.Glyph;
        DrawerNameText.Text = item.Name;
        DrawerStateText.Text = item.State;
        var stateBrush = (Brush)new BrushConverter().ConvertFromString(item.StateColor)!;
        DrawerStateText.Foreground = stateBrush;
        DrawerStateDot.Fill = stateBrush;
        DrawerSubtitleText.Text = $"{item.Type} · {item.Address}";
        DrawerFieldsList.ItemsSource = item.Fields;
        _contextOperations.Clear();
        var itemOperations = OfficialResourceInteractionService.FindItemOperations(
            ResourceTabList.SelectedItem as OfficialApiOperation,
            _moduleOperations);
        var detailOperation = OfficialResourceInteractionService.FindDetailOperation(
            ResourceTabList.SelectedItem as OfficialApiOperation,
            _moduleOperations);
        foreach (var operation in itemOperations.Where(operation => operation != detailOperation))
            _contextOperations.Add(operation);
        OpenDetailsButton.IsEnabled = detailOperation is not null;
        OpenDetailsButton.Visibility = detailOperation is null ? Visibility.Collapsed : Visibility.Visible;
        DrawerActionsTitleText.Visibility = _contextOperations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DrawerActionsList.Visibility = _contextOperations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var hasWrite = itemOperations.Any(operation => operation.IsWrite);
        DrawerCapabilityText.Text = hasWrite
            ? "写操作使用官方 API，并在执行前要求再次确认。"
            : "官方 API 对此资源仅提供查询能力。";
        DrawerLayer.Visibility = Visibility.Visible;
        if (isOpening) AnimateDrawerEntrance();
    }

    private void AnimateDrawerEntrance()
    {
        var transform = DrawerPanel.RenderTransform as TranslateTransform ?? new TranslateTransform();
        DrawerPanel.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = 14,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        DrawerPanel.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.94,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(120)
        });
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
            await ExecuteOperationAsync(operation, null);
    }

    private async void OpenDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        var operation = FindDetailOperation();
        if (operation is not null) await ExecuteOperationAsync(operation, _selectedItem);
    }

    private async void DrawerActionButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is OfficialApiOperation operation)
            await ExecuteOperationAsync(operation, _selectedItem);
    }

    private OfficialApiOperation? FindDetailOperation() => OfficialResourceInteractionService.FindDetailOperation(
        ResourceTabList.SelectedItem as OfficialApiOperation,
        _moduleOperations);

    private async Task ExecuteOperationAsync(OfficialApiOperation operation, OfficialResourceItem? selectedItem)
    {
        if (_client is null) return;
        if (operation.Method == "PUT" && selectedItem is not null)
        {
            try
            {
                selectedItem = await LoadFreshDetailsForEditAsync(selectedItem);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"无法读取最新配置：{ex.Message}");
                MessageBox.Show(Window.GetWindow(this),
                    $"为避免用不完整数据覆盖配置，更新前必须先读取完整详情。\n\n{ex.Message}",
                    operation.Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }
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

    private async Task<OfficialResourceItem> LoadFreshDetailsForEditAsync(OfficialResourceItem selectedItem)
    {
        var detailOperation = FindDetailOperation()
            ?? throw new InvalidOperationException("官方 API 未提供该资源的详情端点。");
        if (!CanResolveDirectly(detailOperation, selectedItem))
            throw new InvalidOperationException("无法自动解析该资源的详情参数。");

        var values = detailOperation.PathParameters.ToDictionary(
            parameter => parameter,
            _ => selectedItem.Id,
            StringComparer.OrdinalIgnoreCase);
        SetLoading(true, "正在读取最新完整配置…");
        try
        {
            var path = detailOperation.ResolvePath(_client!.SiteId, values);
            var response = await _client.ExecuteOfficialApiAsync("GET", path);
            return OfficialResourcePresentationService.ParseSingle(response, _moduleId);
        }
        finally
        {
            SetLoading(false, "");
        }
    }

    private static string AppendQuery(string path, string query)
    {
        var normalized = query.Trim().TrimStart('?');
        return string.IsNullOrWhiteSpace(normalized) ? path : $"{path}?{normalized}";
    }

    private static T? FindVisualParent<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match) return match;
        }
        return null;
    }

    private static string GlyphForModule(string moduleId) => moduleId switch
    {
        "devices" => "▤", "clients" => "◉", "networks" => "⌘", "wifi" => "◖", "hotspot" => "◇",
        "firewall" => "⬡", "traffic" => "≋", "switching" => "⇆", "resources" => "◌", "application" => "ⓘ", _ => "◫"
    };
}
