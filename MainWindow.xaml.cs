using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Text.Json;
using Microsoft.Win32;
using UniFiDnsManager.Models;
using UniFiDnsManager.Services;

namespace UniFiDnsManager;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DnsRecord> _records = [];
    private readonly ObservableCollection<OfficialPolicyRule> _aclRules = [];
    private readonly ObservableCollection<OfficialPolicyRule> _firewallRules = [];
    private readonly ObservableCollection<PolicyChangeItem> _changePlanItems = [];
    private readonly BackupService _backupService = new();
    private readonly SecureSettingsService _secureSettingsService = new();
    private readonly bool _demoMode;
    private ICollectionView? _view;
    private ICollectionView? _aclView;
    private ICollectionView? _firewallView;
    private IUniFiClient? _client;
    private IReadOnlyList<PolicyReferenceItem> _policyReferences = [];
    private OperationSnapshot? _lastOperation;
    private PolicyBundle? _loadedBundle;
    private string? _loadedBundlePath;
    private PolicyChangePlan? _changePlan;
    private string? _lastChangePlanBackupPath;
    private bool _uiReady;

    private string SelectedDnsType => (DnsTypeTabs?.SelectedItem as TabItem)?.Tag?.ToString() ?? "NS";

    public MainWindow(bool demoMode = false)
    {
        InitializeComponent();
        OfficialApiPage.StatusChanged += (_, message) => SetStatus(message, message.Contains("失败", StringComparison.Ordinal));
        _uiReady = true;
        _demoMode = demoMode;
        if (!_demoMode) LoadConnectionSettings();
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => _client?.Dispose();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _view = CollectionViewSource.GetDefaultView(_records);
        _view.Filter = FilterRecord;
        RecordsGrid.ItemsSource = _view;
        _aclView = CollectionViewSource.GetDefaultView(_aclRules);
        _aclView.Filter = item => FilterPolicy(item, OfficialPolicyKind.Acl);
        AclGrid.ItemsSource = _aclView;
        _firewallView = CollectionViewSource.GetDefaultView(_firewallRules);
        _firewallView.Filter = item => FilterPolicy(item, OfficialPolicyKind.Firewall);
        FirewallGrid.ItemsSource = _firewallView;
        ChangePlanGrid.ItemsSource = _changePlanItems;
        if (_demoMode)
        {
            _client = new DemoUniFiClient();
            ShowWorkspace();
            await RefreshAllAsync();
            SetStatus("模拟模式：不会连接或修改真实 UCG。");
        }
    }

    private void NavigationRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || (sender as FrameworkElement)?.Tag is not string page) return;
        NavigateTo(page);
    }

    private void NavigateTo(string page)
    {
        if (OverviewPage is null || ChangeCenterPage is null || DnsPage is null || AclPage is null || FirewallPage is null || OfficialApiPage is null) return;
        var apiModuleId = ApiModuleForPage(page);
        OverviewPage.Visibility = page == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        ChangeCenterPage.Visibility = page == "ChangeCenter" ? Visibility.Visible : Visibility.Collapsed;
        DnsPage.Visibility = page == "Dns" ? Visibility.Visible : Visibility.Collapsed;
        AclPage.Visibility = page == "Acl" ? Visibility.Visible : Visibility.Collapsed;
        FirewallPage.Visibility = page == "Firewall" ? Visibility.Visible : Visibility.Collapsed;
        OfficialApiPage.Visibility = apiModuleId is not null ? Visibility.Visible : Visibility.Collapsed;
        if (apiModuleId is not null) OfficialApiPage.ShowModule(apiModuleId);
        (PageTitleText.Text, PageSubtitleText.Text) = page switch
        {
            "ChangeCenter" => ("策略变更中心", "导入策略基线，预览并执行 DNS、ACL 与防火墙差异。"),
            "Dns" => ("DNS 记录", "管理转发域名、A、AAAA、CNAME、MX、TXT 与 SRV。"),
            "Acl" => ("ACL 规则", "管理官方 API 支持的 IPv4 与 MAC 访问控制规则。"),
            "Firewall" => ("防火墙策略", "管理用户定义防火墙策略及执行顺序。"),
            "ApiDevices" => ("UniFi 设备", "采用、移除、查看设备，执行设备与端口动作并读取最新统计。"),
            "ApiClients" => ("在线客户端", "查看在线客户端并执行官方访客授权动作。"),
            "ApiNetworks" => ("网络", "通过官方 API 创建、查看、更新、删除网络并检查引用。"),
            "ApiWifi" => ("WiFi 广播", "管理 STANDARD 与 IOT_OPTIMIZED WiFi 广播。"),
            "ApiHotspot" => ("Hotspot 凭证", "生成、查询与删除访客网络凭证。"),
            "ApiFirewall" => ("防火墙区域与官方端点", "覆盖防火墙策略、Patch、排序和自定义区域端点。"),
            "ApiTraffic" => ("流量匹配列表", "管理 IPv4、IPv6 和端口匹配列表。"),
            "ApiSwitching" => ("交换与聚合", "读取 LAG、MC-LAG Domain 和 Switch Stack。"),
            "ApiResources" => ("WAN / VPN / 支持资源", "读取 WAN、VPN、RADIUS、设备标签、DPI 与国家资源。"),
            "ApiApplication" => ("应用与站点", "读取 Network 应用信息和本地站点。"),
            "ApiAll" => ("全部官方端点", "UniFi Network v10.4.57：44 条路径、73 个操作。"),
            _ => ("策略概览", "查看当前站点的策略状态与安全操作入口。")
        };
    }

    private static string? ApiModuleForPage(string page) => page switch
    {
        "ApiDevices" => "devices",
        "ApiClients" => "clients",
        "ApiNetworks" => "networks",
        "ApiWifi" => "wifi",
        "ApiHotspot" => "hotspot",
        "ApiFirewall" => "firewall",
        "ApiTraffic" => "traffic",
        "ApiSwitching" => "switching",
        "ApiResources" => "resources",
        "ApiApplication" => "application",
        "ApiAll" => "all",
        _ => null
    };

    private void OpenChangeCenterButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeCenterNavRadio.IsChecked = true;
        NavigateTo("ChangeCenter");
    }

    private void OpenDnsButton_Click(object sender, RoutedEventArgs e)
    {
        DnsNavRadio.IsChecked = true;
        NavigateTo("Dns");
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("正在验证官方 API 并读取站点…", async () =>
        {
            _client?.Dispose();
            _client = null;
            var host = HostTextBox.Text.Trim();
            var apiKey = ApiKeyInput.Password;
            var verifyTls = VerifyTlsCheckBox.IsChecked == true;
            var rememberApiKey = RememberApiKeyCheckBox.IsChecked == true;
            var client = await UniFiClient.ConnectAsync(
                host,
                apiKey,
                verifyTls);

            UniFiSite? selectedSite;
            if (client.Sites.Count == 1)
            {
                selectedSite = client.Sites[0];
            }
            else
            {
                var selector = new SiteSelectionWindow(client.Sites) { Owner = this };
                if (selector.ShowDialog() != true || selector.SelectedSite is null)
                {
                    client.Dispose();
                    SetStatus("已取消站点选择。");
                    return;
                }
                selectedSite = selector.SelectedSite;
            }

            client.SelectSite(selectedSite);
            _client = client;
            SaveConnectionSettings(host, verifyTls, rememberApiKey, apiKey);
            ApiKeyInput.Clear();
            _lastOperation = null;
            ShowWorkspace();
            await RefreshAllAsync();
            SetStatus($"已读取 DNS {_records.Count} 条、ACL {_aclRules.Count} 条、防火墙 {_firewallRules.Count} 条。");
        });
    }

    private void ShowWorkspace()
    {
        if (_client is null) return;
        OfficialApiPage.SetClient(_client);
        LoginPanel.Visibility = Visibility.Collapsed;
        WorkspacePanel.Visibility = Visibility.Visible;
        ConnectionDot.Fill = new SolidColorBrush(Color.FromRgb(30, 184, 117));
        ConnectionText.Text = _demoMode ? "模拟模式" : "官方 API";
        TargetText.Text = _demoMode ? "模拟数据（不会连接路由器）" : $"已连接 {_client.Target}";
        SiteInfoText.Text = $"Site: {_client.Site} · UUID: {_client.SiteId} · Network {_client.ApplicationVersion}";
        SidebarSiteText.Text = $"{_client.Site} · {_client.ApplicationVersion}";
        DashboardNavRadio.IsChecked = true;
        NavigateTo("Overview");
    }

    private void ShowLogin()
    {
        OfficialApiPage.SetClient(null);
        WorkspacePanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        ConnectionDot.Fill = new SolidColorBrush(Color.FromRgb(152, 164, 176));
        ConnectionText.Text = "未连接";
        _records.Clear();
        _aclRules.Clear();
        _firewallRules.Clear();
        _policyReferences = [];
        _lastOperation = null;
        ClearLoadedPlan();
        UpdateUndoState();
        if (!_demoMode) LoadConnectionSettings();
    }

    private void LoadConnectionSettings()
    {
        var settings = _secureSettingsService.Load();
        HostTextBox.Text = settings.Host;
        VerifyTlsCheckBox.IsChecked = settings.VerifyTls;
        RememberApiKeyCheckBox.IsChecked = settings.RememberApiKey;
        ApiKeyInput.Password = settings.ApiKey;
        ForgetApiKeyButton.IsEnabled = settings.RememberApiKey && settings.ApiKey.Length > 0;
    }

    private void SaveConnectionSettings(string host, bool verifyTls, bool rememberApiKey, string apiKey)
    {
        try
        {
            _secureSettingsService.Save(host, verifyTls, rememberApiKey, apiKey);
            ForgetApiKeyButton.IsEnabled = rememberApiKey;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"已连接 UCG，但保存连接信息失败：\n\n{ex.Message}", "无法保存 API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ForgetApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _secureSettingsService.ForgetApiKey(HostTextBox.Text, VerifyTlsCheckBox.IsChecked == true);
            ApiKeyInput.Clear();
            RememberApiKeyCheckBox.IsChecked = false;
            ForgetApiKeyButton.IsEnabled = false;
            SetStatus("已删除本机保存的 API Key。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法清除 API Key", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshRecordsAsync()
    {
        var client = RequireClient();
        var records = await client.ListRecordsAsync();
        var order = DnsTypes.All.Select((type, index) => (type, index)).ToDictionary(item => item.type, item => item.index);
        var sorted = records.OrderBy(record => order.GetValueOrDefault(record.RecordType, 99)).ThenBy(record => record.Key, StringComparer.OrdinalIgnoreCase).ThenBy(record => record.Value, StringComparer.OrdinalIgnoreCase).ToList();
        _records.Clear();
        foreach (var record in sorted)
        {
            record.PopulateSrvParts();
            _records.Add(record);
        }
        if (string.IsNullOrWhiteSpace(BatchDnsServerTextBox.Text))
        {
            var existingServer = sorted.FirstOrDefault(record => record.IsForwardDomain && !string.IsNullOrWhiteSpace(record.Value))?.Value;
            if (!string.IsNullOrWhiteSpace(existingServer)) BatchDnsServerTextBox.Text = existingServer;
        }
        _view?.Refresh();
        UpdateStatistics();
        UpdateBatchSelectionState();
        UpdateUndoState();
    }

    private async Task RefreshPoliciesAsync(OfficialPolicyKind kind)
    {
        var items = await RequireClient().ListPoliciesAsync(kind);
        var target = kind == OfficialPolicyKind.Acl ? _aclRules : _firewallRules;
        target.Clear();
        foreach (var item in items.OrderBy(item => item.Index)) target.Add(item);
        if (kind == OfficialPolicyKind.Acl) _aclView?.Refresh(); else _firewallView?.Refresh();
        UpdatePolicyStatistics();
    }

    private async Task RefreshAllAsync()
    {
        await RefreshRecordsAsync();
        await RefreshPoliciesAsync(OfficialPolicyKind.Acl);
        await RefreshPoliciesAsync(OfficialPolicyKind.Firewall);
        _policyReferences = await RequireClient().ListPolicyReferencesAsync();
        if (_loadedBundle is not null) RebuildChangePlan();
    }

    private bool FilterRecord(object item)
    {
        if (item is not DnsRecord record) return false;
        if (!string.Equals(record.RecordType, SelectedDnsType, StringComparison.OrdinalIgnoreCase)) return false;
        var search = SearchTextBox?.Text.Trim() ?? string.Empty;
        if (search.Length == 0) return true;
        return new[] { record.Id, record.RecordType, record.Key, record.Value, record.Service, record.Protocol }
            .Any(value => (value ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void DnsTypeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || e.Source != DnsTypeTabs) return;
        var isForwardDomain = SelectedDnsType == "NS";
        BatchSelectColumn.Visibility = isForwardDomain ? Visibility.Visible : Visibility.Collapsed;
        BatchDeleteButton.Visibility = isForwardDomain ? Visibility.Visible : Visibility.Collapsed;
        _view?.Refresh();
        UpdateStatistics();
        UpdateBatchSelectionState();
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (!_uiReady) return;
        _view?.Refresh();
        UpdateStatistics();
        UpdateBatchSelectionState();
    }

    private bool FilterPolicy(object item, OfficialPolicyKind kind)
    {
        if (item is not OfficialPolicyRule rule) return false;
        var origin = SelectedPolicyOrigin(kind);
        if (origin == "USER" && !rule.CanModify) return false;
        if (origin == "SYSTEM" && rule.CanModify) return false;
        var search = kind == OfficialPolicyKind.Acl ? AclSearchTextBox?.Text.Trim() : FirewallSearchTextBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(search)) return true;
        return new[] { rule.Id, rule.Name, rule.Type, rule.Action, rule.Origin, rule.Description }
            .Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private string SelectedPolicyOrigin(OfficialPolicyKind kind)
    {
        if (kind == OfficialPolicyKind.Acl)
        {
            if (AclUserSegment?.IsChecked == true) return "USER";
            if (AclSystemSegment?.IsChecked == true) return "SYSTEM";
            return "ALL";
        }
        if (FirewallUserSegment?.IsChecked == true) return "USER";
        if (FirewallSystemSegment?.IsChecked == true) return "SYSTEM";
        return "ALL";
    }

    private static string PolicyOriginLabel(string origin) => origin switch
    {
        "USER" => "用户定义",
        "SYSTEM" => "系统与派生",
        _ => "全部来源"
    };

    private void PolicyFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady) return;
        _aclView?.Refresh();
        _firewallView?.Refresh();
        UpdatePolicyStatistics();
    }

    private void PolicyOriginFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _aclView?.Refresh();
        _firewallView?.Refresh();
        UpdatePolicyStatistics();
    }

    private void UpdateStatistics()
    {
        NsCount.Text = _records.Count(record => record.RecordType == "NS").ToString();
        ACount.Text = _records.Count(record => record.RecordType == "A").ToString();
        AaaaCount.Text = _records.Count(record => record.RecordType == "AAAA").ToString();
        CnameCount.Text = _records.Count(record => record.RecordType == "CNAME").ToString();
        MxCount.Text = _records.Count(record => record.RecordType == "MX").ToString();
        TxtCount.Text = _records.Count(record => record.RecordType == "TXT").ToString();
        SrvCount.Text = _records.Count(record => record.RecordType == "SRV").ToString();
        var selectedType = SelectedDnsType;
        var typeTitle = DnsTypeTitle(selectedType);
        var typeCount = _records.Count(record => record.RecordType == selectedType);
        var displayed = _view?.Cast<DnsRecord>().Count() ?? _records.Count;
        CurrentDnsTypeTitle.Text = typeTitle;
        DnsTypeDescriptionText.Text = DnsTypeDescription(selectedType);
        DnsTotalCountText.Text = $"共 {_records.Count} 条记录";
        RecordSummaryText.Text = $"{typeTitle}共 {typeCount} 条，当前显示 {displayed} 条。";
        AddDnsRecordButton.Content = $"新增 {typeTitle}";
        var hasSearch = !string.IsNullOrWhiteSpace(SearchTextBox?.Text);
        DnsEmptyStateTitle.Text = hasSearch ? $"未找到匹配的{typeTitle}" : $"还没有{typeTitle}";
        DnsEmptyStateDescription.Text = hasSearch
            ? "请调整搜索条件后重试。"
            : selectedType == "NS"
                ? "可新增一条记录，或使用上方批量操作面板导入规则。"
                : $"点击“新增 {typeTitle}”创建第一条记录。";
        DnsEmptyState.Visibility = displayed == 0 ? Visibility.Visible : Visibility.Collapsed;
        DnsTabHeaderText.Text = $"DNS 记录 ({_records.Count})";
        UpdateDashboardStatistics();
    }

    private static string DnsTypeTitle(string type) => type == "NS" ? "转发域名" : $"{type} 记录";

    private static string DnsTypeDescription(string type) => type switch
    {
        "NS" => "把匹配域名的 DNS 查询转发到指定上游 DNS 服务器；支持批量新增与批量删除。",
        "A" => "将域名解析到 IPv4 地址。",
        "AAAA" => "将域名解析到 IPv6 地址。",
        "CNAME" => "将一个别名域名指向另一个规范域名。",
        "MX" => "指定负责接收该域名邮件的服务器及优先级。",
        "TXT" => "保存域名验证、邮件策略或其他文本信息。",
        "SRV" => "为指定服务声明服务器、端口、优先级与权重。",
        _ => "管理 DNS 记录。"
    };

    private void UpdatePolicyStatistics()
    {
        var aclDisplayed = _aclView?.Cast<OfficialPolicyRule>().Count() ?? _aclRules.Count;
        var firewallDisplayed = _firewallView?.Cast<OfficialPolicyRule>().Count() ?? _firewallRules.Count;
        var aclUserCount = _aclRules.Count(rule => rule.CanModify);
        var firewallUserCount = _firewallRules.Count(rule => rule.CanModify);
        AclAllSegment.Content = $"全部 {_aclRules.Count}";
        AclUserSegment.Content = $"用户定义 {aclUserCount}";
        AclSystemSegment.Content = $"系统与派生 {_aclRules.Count - aclUserCount}";
        FirewallAllSegment.Content = $"全部 {_firewallRules.Count}";
        FirewallUserSegment.Content = $"用户定义 {firewallUserCount}";
        FirewallSystemSegment.Content = $"系统与派生 {_firewallRules.Count - firewallUserCount}";
        AclSummaryText.Text = $"{PolicyOriginLabel(SelectedPolicyOrigin(OfficialPolicyKind.Acl))}显示 {aclDisplayed} 条；系统/派生规则只读。";
        FirewallSummaryText.Text = $"{PolicyOriginLabel(SelectedPolicyOrigin(OfficialPolicyKind.Firewall))}显示 {firewallDisplayed} 条；系统/派生规则只读。";
        AclEmptyState.Visibility = aclDisplayed == 0 ? Visibility.Visible : Visibility.Collapsed;
        FirewallEmptyState.Visibility = firewallDisplayed == 0 ? Visibility.Visible : Visibility.Collapsed;
        AclTabHeaderText.Text = $"ACL 规则 ({_aclRules.Count})";
        FirewallTabHeaderText.Text = $"防火墙 ({_firewallRules.Count})";
        UpdateDashboardStatistics();
    }

    private void UpdateDashboardStatistics()
    {
        if (!_uiReady || DashboardTotalCount is null) return;
        DashboardDnsCount.Text = _records.Count.ToString();
        DashboardAclCount.Text = _aclRules.Count.ToString();
        DashboardFirewallCount.Text = _firewallRules.Count.ToString();
        DashboardTotalCount.Text = (_records.Count + _aclRules.Count + _firewallRules.Count).ToString();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RunBusyAsync("正在刷新全部策略…", async () => { await RefreshAllAsync(); SetStatus("ACL、DNS 和防火墙策略已刷新。"); });

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var editor = new RecordEditorWindow(initialType: SelectedDnsType) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null) _ = CreateRecordAsync(editor.Result);
    }

    private async Task CreateRecordAsync(DnsRecord record)
    {
        await RunBusyAsync("正在创建记录…", async () =>
        {
            var backup = await _backupService.SaveSnapshotAsync("before-create", Snapshot());
            var created = await RequireClient().CreateRecordAsync(record) ?? record.Clone();
            await RefreshRecordsAsync();
            if (string.IsNullOrWhiteSpace(created.Id)) created = FindMatchingRecord(record) ?? created;
            _lastOperation = new OperationSnapshot { Kind = OperationKind.Create, Records = [created.Clone()] };
            await _backupService.LogOperationAsync(new { kind = "create", record = created, backup });
            UpdateUndoState();
            SetStatus("DNS 记录已创建，操作前快照已保存。");
        });
    }

    private void EditRowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DnsRecord record) OpenEditor(record);
    }

    private void RecordsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecordsGrid.SelectedItem is DnsRecord record) OpenEditor(record);
    }

    private void OpenEditor(DnsRecord record)
    {
        var editor = new RecordEditorWindow(record) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null) _ = UpdateRecordAsync(record, editor.Result);
    }

    private async Task UpdateRecordAsync(DnsRecord before, DnsRecord after)
    {
        await RunBusyAsync("正在更新记录…", async () =>
        {
            var backup = await _backupService.SaveSnapshotAsync("before-update", Snapshot());
            await RequireClient().UpdateRecordAsync(before.Id ?? throw new UniFiApiException("记录 ID 缺失。"), after);
            _lastOperation = new OperationSnapshot { Kind = OperationKind.Update, RecordId = before.Id, Records = [before.Clone()] };
            await _backupService.LogOperationAsync(new { kind = "update", before, after, backup });
            await RefreshRecordsAsync();
            SetStatus("DNS 记录已更新，可撤销本次修改。");
        });
    }

    private async void ToggleRowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DnsRecord record) return;
        var updated = record.Clone();
        updated.Enabled = !record.Enabled;
        await UpdateRecordAsync(record, updated);
    }

    private async void DeleteRowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DnsRecord record) return;
        if (MessageBox.Show(this, $"确定删除 {record.TypeLabel}“{record.Key}”吗？\n\n删除前会自动保存完整 DNS 快照。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunBusyAsync("正在删除记录…", async () =>
        {
            var backup = await _backupService.SaveSnapshotAsync("before-delete", Snapshot());
            await RequireClient().DeleteRecordAsync(record.Id ?? throw new UniFiApiException("记录 ID 缺失。"));
            _lastOperation = new OperationSnapshot { Kind = OperationKind.Delete, Records = [record.Clone()] };
            await _backupService.LogOperationAsync(new { kind = "delete", record, backup });
            await RefreshRecordsAsync();
            SetStatus("记录已删除，可使用“撤销上一次操作”恢复。");
        });
    }

    private async void AddAclButton_Click(object sender, RoutedEventArgs e) =>
        await OpenPolicyEditorAsync(OfficialPolicyKind.Acl, null);

    private async void AddFirewallButton_Click(object sender, RoutedEventArgs e) =>
        await OpenPolicyEditorAsync(OfficialPolicyKind.Firewall, null);

    private async void EditPolicy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is OfficialPolicyRule rule) await OpenPolicyEditorAsync(rule.Kind, rule);
    }

    private async void AclGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AclGrid.SelectedItem is OfficialPolicyRule rule) await OpenPolicyEditorAsync(rule.Kind, rule);
    }

    private async void FirewallGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FirewallGrid.SelectedItem is OfficialPolicyRule rule) await OpenPolicyEditorAsync(rule.Kind, rule);
    }

    private async Task OpenPolicyEditorAsync(OfficialPolicyKind kind, OfficialPolicyRule? existing)
    {
        var readOnly = existing is { CanModify: false };
        var editor = new PolicyJsonEditorWindow(kind, _policyReferences, existing, readOnly) { Owner = this };
        if (editor.ShowDialog() != true || editor.ResultJson is null) return;
        await RunBusyAsync(existing is null ? "正在创建策略…" : "正在更新策略…", async () =>
        {
            var backup = await _backupService.SaveObjectSnapshotAsync(existing is null ? "before-policy-create" : "before-policy-update", await CaptureBundleAsync());
            if (existing is null) await RequireClient().CreatePolicyAsync(kind, editor.ResultJson);
            else await RequireClient().UpdatePolicyAsync(kind, existing.Id, editor.ResultJson);
            await _backupService.LogOperationAsync(new { kind = existing is null ? "policy_create" : "policy_update", policyKind = kind.ToString(), id = existing?.Id, backup });
            await RefreshPoliciesAsync(kind);
            SetStatus(existing is null ? "策略已创建。" : "策略已更新。");
        });
    }

    private async void TogglePolicy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not OfficialPolicyRule { CanModify: true } rule) return;
        await RunBusyAsync("正在切换策略状态…", async () =>
        {
            var backup = await _backupService.SaveObjectSnapshotAsync("before-policy-toggle", await CaptureBundleAsync());
            var requestJson = OfficialPolicyJson.WithEnabled(rule.Kind, rule.ToEditableJson(), !rule.Enabled);
            await RequireClient().UpdatePolicyAsync(rule.Kind, rule.Id, requestJson);
            await _backupService.LogOperationAsync(new { kind = "policy_toggle", policyKind = rule.Kind.ToString(), rule.Id, enabled = !rule.Enabled, backup });
            await RefreshPoliciesAsync(rule.Kind);
            SetStatus($"策略“{rule.Name}”已{(rule.Enabled ? "停用" : "启用")}。");
        });
    }

    private async void DeletePolicy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not OfficialPolicyRule { CanModify: true } rule) return;
        if (MessageBox.Show(this, $"确定删除策略“{rule.Name}”吗？\n\n删除前会保存 ACL、DNS 和防火墙完整快照。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunBusyAsync("正在删除策略…", async () =>
        {
            var backup = await _backupService.SaveObjectSnapshotAsync("before-policy-delete", await CaptureBundleAsync());
            await RequireClient().DeletePolicyAsync(rule.Kind, rule.Id);
            await _backupService.LogOperationAsync(new { kind = "policy_delete", policyKind = rule.Kind.ToString(), rule = JsonSerializer.Deserialize<JsonElement>(rule.RawResponseJson), backup });
            await RefreshPoliciesAsync(rule.Kind);
            SetStatus($"策略“{rule.Name}”已删除。");
        });
    }

    private async void MovePolicyUp_Click(object sender, RoutedEventArgs e) => await MovePolicyAsync(sender, -1);

    private async void MovePolicyDown_Click(object sender, RoutedEventArgs e) => await MovePolicyAsync(sender, 1);

    private async Task MovePolicyAsync(object sender, int direction)
    {
        if ((sender as FrameworkElement)?.Tag is not OfficialPolicyRule { CanModify: true } rule) return;
        await RunBusyAsync("正在调整策略顺序…", async () =>
        {
            var backup = await _backupService.SaveObjectSnapshotAsync("before-policy-reorder", await CaptureBundleAsync());
            await RequireClient().MovePolicyAsync(rule.Kind, rule.Id, direction);
            await _backupService.LogOperationAsync(new { kind = "policy_reorder", policyKind = rule.Kind.ToString(), rule.Id, direction, backup });
            await RefreshPoliciesAsync(rule.Kind);
            SetStatus($"策略“{rule.Name}”顺序已更新。");
        });
    }

    private async void LoadPolicyBundleButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "载入 UniFi 策略基线",
            Filter = "UniFi 策略 JSON|*.json|所有文件|*.*",
            InitialDirectory = _backupService.BackupDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunBusyAsync("正在读取策略基线并计算差异…", async () =>
        {
            _loadedBundle = await PolicyChangeService.LoadBundleAsync(dialog.FileName);
            _loadedBundlePath = dialog.FileName;
            RebuildChangePlan();
            PlanSourceText.Text = $"{Path.GetFileName(dialog.FileName)} · Site {_loadedBundle.Site ?? "未知"} · {_loadedBundle.CreatedAt:yyyy-MM-dd HH:mm:ss}";
            if (!string.IsNullOrWhiteSpace(_loadedBundle.SiteId) && !string.Equals(_loadedBundle.SiteId, RequireClient().SiteId, StringComparison.OrdinalIgnoreCase))
                MessageBox.Show(this, "该基线来自另一个 Site。程序会按策略名称和内容匹配，但网络、区域等 UUID 必须在当前站点中有效。", "跨站点基线", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus($"已载入策略基线：DNS {_loadedBundle.DnsRecords.Count}、ACL {_loadedBundle.AclRules.Count}、防火墙 {_loadedBundle.FirewallPolicies.Count}。" );
        });
    }

    private async void RestoreLastPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastChangePlanBackupPath) || !File.Exists(_lastChangePlanBackupPath)) return;
        await RunBusyAsync("正在载入上次执行前快照…", async () =>
        {
            _loadedBundle = await PolicyChangeService.LoadBundleAsync(_lastChangePlanBackupPath);
            _loadedBundlePath = _lastChangePlanBackupPath;
            SynchronizeDeleteCheckBox.IsChecked = true;
            RebuildChangePlan();
            PlanSourceText.Text = $"恢复快照 · {Path.GetFileName(_lastChangePlanBackupPath)}";
            SetStatus("已载入上次执行前快照，请检查删除项后再执行恢复计划。" );
        });
    }

    private void PlanMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _loadedBundle is null) return;
        RebuildChangePlan();
    }

    private void RebuildChangePlan()
    {
        if (_loadedBundle is null || string.IsNullOrWhiteSpace(_loadedBundlePath))
        {
            ClearLoadedPlan();
            return;
        }

        foreach (var old in _changePlanItems) old.PropertyChanged -= ChangePlanItem_PropertyChanged;
        _changePlan = PolicyChangeService.BuildPlan(
            _loadedBundle,
            _loadedBundlePath,
            _records.ToList(),
            _aclRules.ToList(),
            _firewallRules.ToList(),
            SynchronizeDeleteCheckBox.IsChecked == true);
        _changePlanItems.Clear();
        foreach (var item in _changePlan.Items)
        {
            item.PropertyChanged += ChangePlanItem_PropertyChanged;
            _changePlanItems.Add(item);
        }
        UpdateChangePlanState();
    }

    private void ClearLoadedPlan()
    {
        foreach (var item in _changePlanItems) item.PropertyChanged -= ChangePlanItem_PropertyChanged;
        _changePlanItems.Clear();
        _loadedBundle = null;
        _loadedBundlePath = null;
        _changePlan = null;
        if (!_uiReady || PlanSourceText is null) return;
        PlanSourceText.Text = "尚未载入。可导入本程序导出的 JSON 基线或自动备份。";
        PlanSummaryText.Text = "载入基线后显示差异。";
        PlanAddCount.Text = PlanUpdateCount.Text = PlanDeleteCount.Text = PlanUnchangedCount.Text = PlanInvalidCount.Text = "0";
        PlanSelectedText.Text = "已选择 0 项。";
        ExecutePlanButton.IsEnabled = false;
        ApplyOrderingButton.IsEnabled = false;
    }

    private void ChangePlanItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PolicyChangeItem.IsSelected)) UpdateChangePlanState();
    }

    private void UpdateChangePlanState()
    {
        if (_changePlan is null) return;
        PlanAddCount.Text = _changePlan.AddCount.ToString();
        PlanUpdateCount.Text = _changePlan.UpdateCount.ToString();
        PlanDeleteCount.Text = _changePlan.DeleteCount.ToString();
        PlanUnchangedCount.Text = _changePlan.UnchangedCount.ToString();
        PlanInvalidCount.Text = _changePlan.InvalidCount.ToString();
        var selected = _changePlan.SelectedCount;
        var selectedDeletes = _changePlan.Items.Count(item => item.IsSelected && item.Action == PolicyChangeAction.Delete);
        PlanSummaryText.Text = $"共 {_changePlan.Items.Count} 项差异；默认只选择新增和更新。{(_changePlan.SynchronizeDeletes ? "严格同步已开启，将恢复用户策略排序；删除项仍需手动选择。" : "当前不会删除现有策略，也不会改动排序。")}{(_changePlan.InvalidCount > 0 ? " 存在无效项的策略范围已自动禁止删除。" : string.Empty)}";
        PlanSelectedText.Text = $"已选择 {selected} 项{(selectedDeletes > 0 ? $"，其中删除 {selectedDeletes} 项" : string.Empty)}。";
        ExecutePlanButton.IsEnabled = selected > 0;
        var hasOrdering = _changePlan.Bundle.AclOrdering is { OrderedAclRuleIds.Count: > 0 } ||
                          _changePlan.Bundle.FirewallOrdering is { BeforeSystemDefined.Count: > 0 } ||
                          _changePlan.Bundle.FirewallOrdering is { AfterSystemDefined.Count: > 0 };
        var policyOrderBlocked = _changePlan.Items.Any(item =>
            item.Scope is PolicyChangeScope.Acl or PolicyChangeScope.Firewall &&
            item.Action is PolicyChangeAction.Add or PolicyChangeAction.Invalid);
        ApplyOrderingButton.IsEnabled = _changePlan.SynchronizeDeletes && hasOrdering && !policyOrderBlocked;
    }

    private void SelectSafePlanItemsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _changePlanItems) item.IsSelected = item.Action is PolicyChangeAction.Add or PolicyChangeAction.Update;
        UpdateChangePlanState();
    }

    private void SelectAllPlanItemsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _changePlanItems) item.IsSelected = item.IsActionable;
        UpdateChangePlanState();
    }

    private void ClearPlanSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _changePlanItems) item.IsSelected = false;
        UpdateChangePlanState();
    }

    private async void ApplyOrderingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_changePlan is null || !ApplyOrderingButton.IsEnabled) return;
        var plan = _changePlan;
        if (MessageBox.Show(this, "确定按导入基线恢复 ACL 和防火墙用户策略顺序吗？\n\n执行前会保存完整快照。", "恢复策略排序", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunBusyAsync("正在恢复用户策略排序…", async () =>
        {
            var before = await CaptureBundleAsync();
            var backup = await _backupService.SaveObjectSnapshotAsync("before-order-restore", before);
            _lastChangePlanBackupPath = backup;
            RestoreLastPlanButton.IsEnabled = true;
            ResolvePlanActualIds(plan);
            await PolicyChangeExecutor.RestoreOrderingAsync(RequireClient(), plan);
            await RefreshPoliciesAsync(OfficialPolicyKind.Acl);
            await RefreshPoliciesAsync(OfficialPolicyKind.Firewall);
            await _backupService.LogOperationAsync(new { kind = "restore_policy_ordering", source = plan.SourcePath, backup });
            SetStatus("ACL 与防火墙用户策略排序已按基线恢复。" );
        });
    }

    private async void ExecutePlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_changePlan is null) return;
        var plan = _changePlan;
        var selected = plan.Items.Where(item => item.IsSelected && item.IsActionable).ToList();
        if (selected.Count == 0) return;
        var deletes = selected.Count(item => item.Action == PolicyChangeAction.Delete);
        var message = $"将执行 {selected.Count} 项策略变更：\n\n新增 {selected.Count(item => item.Action == PolicyChangeAction.Add)}\n更新 {selected.Count(item => item.Action == PolicyChangeAction.Update)}\n删除 {deletes}\n\n执行前会保存包含排序信息的完整基线。";
        if (deletes > 0) message += "\n\n警告：所选删除项会立即从 UCG 移除。";
        if (MessageBox.Show(this, message, "确认执行策略变更", MessageBoxButton.YesNo, deletes > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        await RunBusyAsync($"正在执行 {selected.Count} 项策略变更…", async () =>
        {
            var before = await CaptureBundleAsync();
            var backup = await _backupService.SaveObjectSnapshotAsync("before-change-plan", before);
            _lastChangePlanBackupPath = backup;
            RestoreLastPlanButton.IsEnabled = true;
            var failures = new List<string>();
            var client = RequireClient();
            foreach (var item in selected.OrderBy(ChangeExecutionOrder))
            {
                item.Status = "执行中";
                try
                {
                    await PolicyChangeExecutor.ExecuteItemAsync(client, item);
                    item.Status = "已完成";
                }
                catch (Exception exception)
                {
                    item.Status = "失败";
                    failures.Add($"{item.ScopeLabel} · {item.Name}：{exception.Message}");
                }
                await Task.Delay(60);
            }

            await RefreshAllAsync();
            if (failures.Count == 0 && plan.SynchronizeDeletes)
            {
                ResolvePlanActualIds(plan);
                await PolicyChangeExecutor.RestoreOrderingAsync(client, plan);
                await RefreshPoliciesAsync(OfficialPolicyKind.Acl);
                await RefreshPoliciesAsync(OfficialPolicyKind.Firewall);
            }
            await _backupService.LogOperationAsync(new
            {
                kind = "change_plan",
                source = plan.SourcePath,
                synchronizeDeletes = plan.SynchronizeDeletes,
                selected = selected.Select(item => new { scope = item.ScopeLabel, action = item.ActionLabel, item.Name, item.Status }),
                failures,
                backup
            });
            RebuildChangePlan();
            SetStatus($"策略变更完成：成功 {selected.Count - failures.Count} 项，失败 {failures.Count} 项。", failures.Count > 0);
            if (failures.Count > 0)
                MessageBox.Show(this, string.Join(Environment.NewLine, failures.Take(25)), "部分变更失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void ResolvePlanActualIds(PolicyChangePlan plan)
    {
        foreach (var item in plan.Items.Where(item => item.Action != PolicyChangeAction.Delete && string.IsNullOrWhiteSpace(item.ActualId)))
        {
            item.ActualId = item.Scope switch
            {
                PolicyChangeScope.Dns when item.DesiredDns is not null => FindMatchingRecord(item.DesiredDns)?.Id,
                PolicyChangeScope.Acl => _aclRules.FirstOrDefault(rule => string.Equals(rule.Name, item.Name, StringComparison.OrdinalIgnoreCase))?.Id,
                PolicyChangeScope.Firewall => _firewallRules.FirstOrDefault(rule => string.Equals(rule.Name, item.Name, StringComparison.OrdinalIgnoreCase))?.Id,
                _ => item.ActualId
            };
        }
    }

    private static int ChangeExecutionOrder(PolicyChangeItem item) => item.Action switch
    {
        PolicyChangeAction.Update => 0,
        PolicyChangeAction.Add => 1,
        PolicyChangeAction.Delete => 2,
        _ => 9
    };

    private async Task<PolicyBundle> CaptureBundleAsync()
    {
        var client = RequireClient();
        var dnsTask = client.ListRecordsAsync();
        var aclTask = client.ListPoliciesAsync(OfficialPolicyKind.Acl);
        var firewallTask = client.ListPoliciesAsync(OfficialPolicyKind.Firewall);
        await Task.WhenAll(dnsTask, aclTask, firewallTask);
        PolicyOrderingSnapshot? aclOrdering = null;
        PolicyOrderingSnapshot? firewallOrdering = null;
        try { aclOrdering = await client.GetPolicyOrderingAsync(OfficialPolicyKind.Acl); }
        catch (UniFiApiException exception) when (exception.StatusCode is 403 or 404) { }
        try { firewallOrdering = await client.GetPolicyOrderingAsync(OfficialPolicyKind.Firewall); }
        catch (UniFiApiException exception) when (exception.StatusCode is 403 or 404) { }
        var dns = await dnsTask;
        var acl = await aclTask;
        var firewall = await firewallTask;
        return PolicyChangeService.CaptureBundle(client, dns, acl, firewall, aclOrdering, firewallOrdering);
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 DNS 规则",
            Filter = "支持的文件|*.txt;*.csv;*.xlsx|文本文件|*.txt|CSV 文件|*.csv|Excel 工作簿|*.xlsx"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var result = ImportService.ImportFile(dialog.FileName, BatchDnsServerTextBox.Text.Trim());
            ShowImportedRules(result, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LoadBundledRulesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaultDnsServer = BatchDnsServerTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(defaultDnsServer))
            {
                BatchDnsServerTextBox.Focus();
                throw new ValidationException("请先填写转发域默认 DNS 服务器，再载入内置规则。");
            }
            ShowImportedRules(ImportService.ImportBundledForwardDomains(defaultDnsServer), "EXE 内置转发域规则");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowImportedRules(ImportResult result, string sourceName)
    {
        BatchRulesTextBox.Text = ImportService.FormatRecordsForEditor(result.Records);
        var types = string.Join("、", result.Records.GroupBy(record => record.RecordType).Select(group => $"{group.Key} {group.Count()}"));
        ImportSummaryText.Text = $"有效 {result.Records.Count} 条；文件内重复 {result.DuplicateInput.Count} 条；无效 {result.Invalid.Count} 条。{(types.Length > 0 ? $" 类型：{types}。" : string.Empty)}";
        SetStatus($"已从 {sourceName} 整理出 {result.Records.Count} 条 DNS 规则。");
    }

    private void SaveCsvTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存 DNS 全部类型 CSV 模板",
            Filter = "CSV 文件|*.csv",
            FileName = "unifi-dns-rules-template.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ImportService.SaveDnsRulesCsvTemplate(dialog.FileName);
            SetStatus($"CSV 模板已保存：{dialog.FileName}");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void PreviewBatchAddButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = ImportService.ParseText(BatchRulesTextBox.Text, BatchDnsServerTextBox.Text.Trim());
            var existingKeys = _records.Select(ImportService.IdentityKey).ToHashSet(ImportService.IdentityComparer);
            var existing = input.Records.Where(record => existingKeys.Contains(ImportService.IdentityKey(record))).ToList();
            var pending = input.Records.Where(record => !existingKeys.Contains(ImportService.IdentityKey(record))).ToList();
            var preview = new BatchPreview(pending, existing, input.DuplicateInput, input.Invalid);
            var window = new BatchPreviewWindow(preview) { Owner = this };
            if (window.ShowDialog() == true) await ApplyBatchAddAsync(preview);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ApplyBatchAddAsync(BatchPreview preview)
    {
        await RunBusyAsync($"正在新增 {preview.Pending.Count} 条 DNS 规则…", async () =>
        {
            var backup = await _backupService.SaveSnapshotAsync("before-dns-batch-add", Snapshot());
            var client = RequireClient();
            var currentKeys = _records.Select(ImportService.IdentityKey).ToHashSet(ImportService.IdentityComparer);
            var created = new List<DnsRecord>();
            var failed = new List<string>();
            foreach (var record in preview.Pending)
            {
                var identity = ImportService.IdentityKey(record);
                if (currentKeys.Contains(identity)) continue;
                try
                {
                    var input = DnsValidator.Normalize(record);
                    var result = await client.CreateRecordAsync(input) ?? input;
                    created.Add(result);
                    currentKeys.Add(identity);
                }
                catch (Exception ex) { failed.Add($"{ImportService.Describe(record)}：{ex.Message}"); }
                await Task.Delay(80);
            }
            await RefreshRecordsAsync();
            foreach (var item in created.Where(item => string.IsNullOrWhiteSpace(item.Id)).ToList())
            {
                var match = FindMatchingRecord(item);
                if (match is not null) { item.Id = match.Id; }
            }
            _lastOperation = created.Count > 0 ? new OperationSnapshot { Kind = OperationKind.BatchCreate, Records = created.Select(item => item.Clone()).ToList() } : null;
            await _backupService.LogOperationAsync(new { kind = "batch_dns_create", created, failed, backup });
            UpdateUndoState();
            SetStatus($"批量新增完成：成功 {created.Count} 条，失败 {failed.Count} 条。", failed.Count > 0);
            if (failed.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, failed.Take(20)), "部分记录创建失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void BatchSelectionCheckBox_Click(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(new Action(UpdateBatchSelectionState));

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var check = SelectAllCheckBox.IsChecked == true;
        foreach (var record in _view?.Cast<DnsRecord>().Where(record => record.IsForwardDomain) ?? []) record.IsSelectedForBatch = check;
        UpdateBatchSelectionState();
    }

    private void SelectVisibleForwardDomainsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var record in _records.Where(record => record.IsForwardDomain)) record.IsSelectedForBatch = true;
        UpdateBatchSelectionState();
    }

    private void ClearBatchSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var record in _records.Where(record => record.IsForwardDomain)) record.IsSelectedForBatch = false;
        UpdateBatchSelectionState();
    }

    private void UpdateBatchSelectionState()
    {
        if (!_uiReady || BatchDeleteButton is null || BatchDeletePanelButton is null || BatchDeleteSelectionText is null || SelectAllCheckBox is null) return;
        var selected = _records.Count(record => record.IsForwardDomain && record.IsSelectedForBatch);
        BatchDeleteButton.Content = $"批量删除转发域名 ({selected})";
        BatchDeleteButton.IsEnabled = selected > 0 && SelectedDnsType == "NS";
        BatchDeletePanelButton.Content = $"批量删除转发域名 ({selected})";
        BatchDeletePanelButton.IsEnabled = selected > 0;
        var visible = _view?.Cast<DnsRecord>().Where(record => record.IsForwardDomain).ToList() ?? [];
        var total = _records.Count(record => record.IsForwardDomain);
        BatchDeleteSelectionText.Text = $"共有 {total} 条转发域名，当前列表显示 {visible.Count} 条，已选择 {selected} 条。批量删除会先预览并保存完整 DNS 快照。";
        SelectAllCheckBox.IsChecked = visible.Count > 0 && visible.All(record => record.IsSelectedForBatch);
    }

    private async void BatchDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _records.Where(record => record.IsForwardDomain && record.IsSelectedForBatch).Select(record => record.Clone()).ToList();
        if (selected.Count == 0) return;
        var preview = new BatchPreviewWindow(selected) { Owner = this };
        if (preview.ShowDialog() != true) return;
        await RunBusyAsync($"正在删除 {selected.Count} 条转发域名…", async () =>
        {
            var backup = await _backupService.SaveSnapshotAsync("before-forward-batch-delete", Snapshot());
            var deleted = new List<DnsRecord>();
            var failed = new List<string>();
            foreach (var record in selected)
            {
                try
                {
                    await RequireClient().DeleteRecordAsync(record.Id ?? throw new UniFiApiException("记录 ID 缺失。"));
                    deleted.Add(record);
                }
                catch (Exception ex) { failed.Add($"{record.Key}: {ex.Message}"); }
                await Task.Delay(80);
            }
            _lastOperation = deleted.Count > 0 ? new OperationSnapshot { Kind = OperationKind.BatchDelete, Records = deleted } : null;
            await _backupService.LogOperationAsync(new { kind = "batch_delete", deleted, failed, backup });
            await RefreshRecordsAsync();
            SetStatus($"批量删除完成：成功 {deleted.Count} 条，失败 {failed.Count} 条。", failed.Count > 0);
            if (failed.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, failed.Take(20)), "部分记录删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOperation is null) return;
        if (MessageBox.Show(this, "确定撤销上一次由本程序完成的操作吗？\n\n撤销前也会保存完整快照。", "确认撤销", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunBusyAsync("正在撤销…", async () =>
        {
            var operation = _lastOperation;
            var backup = await _backupService.SaveSnapshotAsync("before-undo", Snapshot());
            var client = RequireClient();
            switch (operation.Kind)
            {
                case OperationKind.Create:
                case OperationKind.BatchCreate:
                    foreach (var created in operation.Records)
                    {
                        var id = created.Id ?? FindMatchingRecord(created)?.Id;
                        if (!string.IsNullOrWhiteSpace(id)) await client.DeleteRecordAsync(id);
                    }
                    break;
                case OperationKind.Delete:
                case OperationKind.BatchDelete:
                    foreach (var deleted in operation.Records) await client.CreateRecordAsync(deleted);
                    break;
                case OperationKind.Update:
                    await client.UpdateRecordAsync(operation.RecordId ?? throw new UniFiApiException("撤销记录 ID 缺失。"), operation.Records[0]);
                    break;
            }
            await _backupService.LogOperationAsync(new { kind = "undo", operation = operation.Kind.ToString(), backup });
            _lastOperation = null;
            await RefreshRecordsAsync();
            SetStatus("上一次操作已撤销。");
        });
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出完整策略基线",
            Filter = "JSON 文件|*.json",
            FileName = $"unifi-policy-baseline-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunBusyAsync("正在导出备份…", async () =>
        {
            var bundle = await CaptureBundleAsync();
            await PolicyChangeService.SaveBundleAsync(dialog.FileName, bundle);
            SetStatus($"已导出 DNS {_records.Count}、ACL {_aclRules.Count}、防火墙 {_firewallRules.Count} 条策略：{dialog.FileName}");
        });
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        _client?.Dispose();
        _client = null;
        ShowLogin();
        SetStatus("已断开连接。");
    }

    private IReadOnlyList<DnsRecord> Snapshot() => _records.Select(record => record.Clone()).ToList();

    private DnsRecord? FindMatchingRecord(DnsRecord target)
    {
        var identity = ImportService.IdentityKey(target);
        return _records.LastOrDefault(record => string.Equals(ImportService.IdentityKey(record), identity, StringComparison.Ordinal));
    }

    private IUniFiClient RequireClient() => _client ?? throw new UniFiApiException("请先连接 UCG。");

    private void UpdateUndoState() => UndoButton.IsEnabled = _lastOperation is not null;

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        BusyText.Text = message;
        BusyOverlay.Visibility = Visibility.Visible;
        IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            IsEnabled = true;
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(Exception exception)
    {
        var message = exception switch
        {
            UniFiApiException api when api.StatusCode is 401 or 403 => "API Key 无效、已撤销或没有当前 Console 的访问权限。请在 unifi.ui.com → Settings → API Keys 检查。",
            UniFiApiException api when api.StatusCode == 404 => "此 Console 未提供请求的官方 Integration API，或所选站点/记录不存在。",
            _ => exception.Message
        };
        SetStatus(message, true);
        MessageBox.Show(this, message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(194, 54, 54)) : (Brush)FindResource("MutedBrush");
    }
}
