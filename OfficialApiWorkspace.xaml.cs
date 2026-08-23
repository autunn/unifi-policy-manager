using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using UniFiDnsManager.Models;
using UniFiDnsManager.Services;

namespace UniFiDnsManager;

public partial class OfficialApiWorkspace : UserControl
{
    private readonly ObservableCollection<OfficialApiOperation> _visibleOperations = [];
    private readonly BackupService _auditService = new();
    private IReadOnlyList<OfficialApiOperation> _moduleOperations = [];
    private IUniFiClient? _client;
    private OfficialApiOperation? _selectedOperation;

    public event EventHandler<string>? StatusChanged;

    public OfficialApiWorkspace()
    {
        InitializeComponent();
        OperationListBox.ItemsSource = _visibleOperations;
    }

    public void SetClient(IUniFiClient? client)
    {
        _client = client;
        UpdateExecuteState();
    }

    public void ShowModule(string moduleId)
    {
        var showAll = moduleId == "all";
        var module = showAll ? null : OfficialApiCatalog.GetModule(moduleId);
        ModuleTitleText.Text = showAll ? "全部官方端点" : module!.Title;
        ModuleDescriptionText.Text = showAll
            ? "覆盖 UniFi Network v10.4.57 OpenAPI 中的 44 条路径、73 个操作。"
            : module!.Description;
        _moduleOperations = OfficialApiCatalog.ForModule(moduleId);
        OperationCountText.Text = $"{_moduleOperations.Count} 个官方操作";
        OperationSearchTextBox.Clear();
        RefreshOperationList();
        OperationListBox.SelectedIndex = _visibleOperations.Count > 0 ? 0 : -1;
    }

    private void RefreshOperationList()
    {
        var query = OperationSearchTextBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _moduleOperations
            : _moduleOperations.Where(operation =>
                new[] { operation.Title, operation.Method, operation.PathTemplate, operation.Description, operation.Id }
                    .Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        _visibleOperations.Clear();
        foreach (var operation in filtered) _visibleOperations.Add(operation);
    }

    private void OperationSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshOperationList();

    private void OperationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedOperation = OperationListBox.SelectedItem as OfficialApiOperation;
        if (_selectedOperation is null)
        {
            ExecuteButton.IsEnabled = false;
            return;
        }

        MethodText.Text = _selectedOperation.Method;
        OperationTitleText.Text = _selectedOperation.Title;
        OperationDescriptionText.Text = _selectedOperation.Description;
        PathParametersTextBox.Text = string.Join(Environment.NewLine, _selectedOperation.PathParameters.Select(name => $"{name}="));
        QueryTextBox.Text = _selectedOperation.DefaultQuery;
        QueryLabelText.Text = _selectedOperation.RequiredQueries.Count == 0
            ? "查询参数（不含 ?）"
            : $"查询参数（必填：{string.Join("、", _selectedOperation.RequiredQueries)}）";
        RequestBodyTextBox.Text = _selectedOperation.DefaultBody;
        RequestBodyHeader.Visibility = _selectedOperation.HasBody ? Visibility.Visible : Visibility.Collapsed;
        RequestBodyTextBox.Visibility = _selectedOperation.HasBody ? Visibility.Visible : Visibility.Collapsed;
        RequestHeaderRow.Height = _selectedOperation.HasBody ? GridLength.Auto : new GridLength(0);
        RequestBodyRow.Height = _selectedOperation.HasBody ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ExecutionHintText.Text = _selectedOperation.IsWrite
            ? "这是写操作：程序会显示最终端点和请求体，并要求再次确认。"
            : "这是只读操作；Site ID 会由当前站点自动填写。";
        ResponseTextBox.Clear();
        UpdatePathPreview();
        UpdateExecuteState();
    }

    private void RequestInput_Changed(object sender, TextChangedEventArgs e) => UpdatePathPreview();

    private void UpdatePathPreview()
    {
        if (_selectedOperation is null) return;
        try
        {
            var path = _selectedOperation.ResolvePath(_client?.SiteId ?? "{currentSiteId}", ParsePathParameters(requireValues: false));
            var query = NormalizeQuery(QueryTextBox.Text);
            PathPreviewText.Text = path + (query.Length > 0 ? $"?{query}" : string.Empty);
        }
        catch
        {
            PathPreviewText.Text = _selectedOperation.PathTemplate;
        }
    }

    private void UpdateExecuteState() => ExecuteButton.IsEnabled = _client is not null && _selectedOperation is not null;

    private async void ExecuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || _selectedOperation is null) return;
        try
        {
            var path = _selectedOperation.ResolvePath(_client.SiteId, ParsePathParameters(requireValues: true));
            var query = NormalizeQuery(QueryTextBox.Text);
            _selectedOperation.ValidateQuery(query);
            if (query.Length > 0) path += $"?{query}";
            var body = _selectedOperation.HasBody ? NormalizeJson(RequestBodyTextBox.Text) : null;

            if (_selectedOperation.IsWrite)
            {
                var confirmation = $"即将调用官方端点：\n\n{_selectedOperation.Method} {path}\n\n" +
                    (body is null ? string.Empty : $"请求体：\n{body}\n\n") +
                    "此操作会直接修改 UniFi。是否继续？";
                if (MessageBox.Show(Window.GetWindow(this), confirmation, "确认执行官方写操作", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            SetBusy(true);
            StatusChanged?.Invoke(this, $"正在执行 {_selectedOperation.Title}…");
            ResponseTextBox.Text = await _client.ExecuteOfficialApiAsync(_selectedOperation.Method, path, body);
            if (_selectedOperation.IsWrite)
            {
                await _auditService.LogOperationAsync(new
                {
                    kind = "official_api",
                    operationId = _selectedOperation.Id,
                    method = _selectedOperation.Method
                });
            }
            StatusChanged?.Invoke(this, $"{_selectedOperation.Title}执行成功。");
        }
        catch (Exception ex)
        {
            ResponseTextBox.Text = ex is UniFiApiException api && !string.IsNullOrWhiteSpace(api.Details)
                ? $"{ex.Message}\n\n{api.Details}"
                : ex.Message;
            StatusChanged?.Invoke(this, $"{_selectedOperation.Title}执行失败：{ex.Message}");
            MessageBox.Show(Window.GetWindow(this), ex.Message, "官方 API 请求失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    private Dictionary<string, string> ParsePathParameters(bool requireValues)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in PathParametersTextBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = rawLine.IndexOf('=');
            if (separator < 1) throw new InvalidOperationException($"路径参数格式不正确：{rawLine}");
            var name = rawLine[..separator].Trim();
            var value = rawLine[(separator + 1)..].Trim();
            if (requireValues && value.Length == 0) throw new InvalidOperationException($"请填写路径参数 {name}。");
            result[name] = value;
        }
        return result;
    }

    private static string NormalizeQuery(string query) => query.Trim().TrimStart('?')
        .Replace("\r\n", "&", StringComparison.Ordinal)
        .Replace('\r', '&')
        .Replace('\n', '&');

    private static string? NormalizeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("请填写请求体 JSON。");
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private void FormatRequestButton_Click(object sender, RoutedEventArgs e)
    {
        try { RequestBodyTextBox.Text = NormalizeJson(RequestBodyTextBox.Text); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "JSON 格式错误", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CopyResponseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ResponseTextBox.Text)) Clipboard.SetText(ResponseTextBox.Text);
    }

    private void SetBusy(bool busy)
    {
        ExecuteButton.IsEnabled = !busy && _client is not null && _selectedOperation is not null;
        ExecuteButton.Content = busy ? "正在执行…" : "执行官方请求";
        OperationListBox.IsEnabled = !busy;
    }
}
