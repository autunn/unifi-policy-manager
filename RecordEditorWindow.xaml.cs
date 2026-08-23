using System.Windows;
using System.Windows.Controls;
using UniFiDnsManager.Models;
using UniFiDnsManager.Services;

namespace UniFiDnsManager;

public partial class RecordEditorWindow : Window
{
    private readonly DnsRecord? _original;
    private bool _initialized;
    public DnsRecord? Result { get; private set; }

    public RecordEditorWindow(DnsRecord? record = null, string initialType = "NS")
    {
        InitializeComponent();
        _original = record?.Clone();
        HeadingText.Text = record is null ? "新增 DNS 记录" : "编辑 DNS 记录";
        Title = HeadingText.Text;
        var requestedType = initialType.Trim().ToUpperInvariant();
        if (!DnsTypes.All.Contains(requestedType)) requestedType = "NS";
        var type = record?.RecordType ?? requestedType;
        TypeComboBox.SelectedItem = TypeComboBox.Items.Cast<ComboBoxItem>().First(item => Equals(item.Tag, type));
        TypeComboBox.IsEnabled = record is null;
        if (record is not null) Populate(record);
        _initialized = true;
        UpdateFieldVisibility();
    }

    private string SelectedType => (TypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "NS";

    private void Populate(DnsRecord record)
    {
        EnabledCheckBox.IsChecked = record.Enabled;
        KeyTextBox.Text = record.Key;
        ValueTextBox.Text = record.Value;
        TxtKeyTextBox.Text = record.Key;
        TxtValueTextBox.Text = record.Value;
        PriorityTextBox.Text = record.Priority.GetValueOrDefault().ToString();
        if (record.Ttl.GetValueOrDefault() > 0)
        {
            ManualTtlRadio.IsChecked = true;
            TtlTextBox.Text = record.Ttl.ToString();
        }
        else AutoTtlRadio.IsChecked = true;
        record.PopulateSrvParts();
        SrvDomainTextBox.Text = record.Domain;
        SrvServerTextBox.Text = record.Value;
        SrvServiceTextBox.Text = record.Service;
        SrvProtocolTextBox.Text = record.Protocol;
        SrvPortTextBox.Text = record.Port?.ToString() ?? string.Empty;
        SrvPriorityTextBox.Text = record.Priority.GetValueOrDefault().ToString();
        SrvWeightTextBox.Text = record.Weight.GetValueOrDefault().ToString();
    }

    private void TypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) UpdateFieldVisibility();
    }

    private void UpdateFieldVisibility()
    {
        var type = SelectedType;
        StandardFieldsPanel.Visibility = type is not ("TXT" or "SRV") ? Visibility.Visible : Visibility.Collapsed;
        TxtFieldsPanel.Visibility = type == "TXT" ? Visibility.Visible : Visibility.Collapsed;
        SrvFieldsPanel.Visibility = type == "SRV" ? Visibility.Visible : Visibility.Collapsed;
        TtlPanel.Visibility = DnsTypes.TtlTypes.Contains(type) ? Visibility.Visible : Visibility.Collapsed;
        PriorityPanel.Visibility = type == "MX" ? Visibility.Visible : Visibility.Collapsed;
        KeyLabel.Text = type switch { "CNAME" => "别名域名", _ => "域名" };
        ValueLabel.Text = type switch
        {
            "NS" => "DNS 服务器",
            "A" or "AAAA" => "IP 地址",
            "CNAME" => "目标域名",
            "MX" => "电子邮件服务器",
            _ => "值"
        };
        if (_original is null)
        {
            KeyTextBox.Text = string.Empty;
            ValueTextBox.Text = type == "NS" ? "192.168.1.10" : string.Empty;
        }
    }

    private void TtlRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (TtlTextBox is not null) TtlTextBox.IsEnabled = ManualTtlRadio?.IsChecked == true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var type = SelectedType;
            var record = new DnsRecord
            {
                Id = _original?.Id,
                RecordType = type,
                Enabled = EnabledCheckBox.IsChecked == true
            };
            if (type == "TXT")
            {
                record.Key = TxtKeyTextBox.Text;
                record.Value = TxtValueTextBox.Text;
            }
            else if (type == "SRV")
            {
                record.Domain = SrvDomainTextBox.Text;
                record.Value = SrvServerTextBox.Text;
                record.Service = SrvServiceTextBox.Text;
                record.Protocol = SrvProtocolTextBox.Text;
                record.Port = ParseInt(SrvPortTextBox.Text, "端口");
                record.Priority = ParseInt(SrvPriorityTextBox.Text, "优先级");
                record.Weight = ParseInt(SrvWeightTextBox.Text, "权重");
            }
            else
            {
                record.Key = KeyTextBox.Text;
                record.Value = ValueTextBox.Text;
                if (DnsTypes.TtlTypes.Contains(type)) record.Ttl = ManualTtlRadio.IsChecked == true ? ParseInt(TtlTextBox.Text, "TTL") : 0;
                if (type == "MX") record.Priority = ParseInt(PriorityTextBox.Text, "优先级");
            }
            Result = DnsValidator.Normalize(record);
            Result.Id = _original?.Id;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ValidationException or FormatException)
        {
            MessageBox.Show(this, ex.Message, "记录内容有误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static int ParseInt(string value, string label) =>
        int.TryParse(value.Trim(), out var number) ? number : throw new FormatException($"{label}必须是整数。");

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
