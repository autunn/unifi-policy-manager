using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using UniFiDnsManager.Models;

namespace UniFiDnsManager;

public partial class OfficialOperationDialog : Window
{
    private readonly OfficialApiOperation _operation;
    private readonly string _siteId;
    private readonly ObservableCollection<OperationFieldRow> _fields = [];

    public OfficialOperationRequest? Request { get; private set; }

    public OfficialOperationDialog(OfficialApiOperation operation, string siteId, OfficialResourceItem? selectedItem)
    {
        InitializeComponent();
        _operation = operation;
        _siteId = siteId;
        OperationTitleText.Text = operation.Title;
        OperationDescriptionText.Text = operation.Description;
        ExecuteButton.Content = operation.IsWrite ? "检查并执行" : "读取";
        WriteWarning.Visibility = operation.IsWrite ? Visibility.Visible : Visibility.Collapsed;
        FieldsGrid.ItemsSource = _fields;
        BuildFields(selectedItem);
    }

    private void BuildFields(OfficialResourceItem? selectedItem)
    {
        JsonElement? selectedRoot = null;
        if (selectedItem is not null)
        {
            try
            {
                using var selectedDocument = JsonDocument.Parse(selectedItem.RawJson);
                selectedRoot = selectedDocument.RootElement.Clone();
            }
            catch (JsonException)
            {
                selectedRoot = null;
            }
        }

        foreach (var parameter in _operation.PathParameters)
        {
            var useSelection = selectedItem is not null && parameter.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                && !parameter.Equals("portIdx", StringComparison.OrdinalIgnoreCase);
            _fields.Add(new OperationFieldRow(
                "对象", parameter, FriendlyLabel(parameter), useSelection ? selectedItem!.Id : "", true, "必填", "String"));
        }

        foreach (var pair in ParseQuery(_operation.DefaultQuery))
        {
            var required = _operation.RequiredQueries.Contains(pair.Key, StringComparer.OrdinalIgnoreCase);
            _fields.Add(new OperationFieldRow(
                "选项", pair.Key, FriendlyLabel(pair.Key), pair.Value, required,
                required ? "必填" : "可选", "String"));
        }

        if (_operation.HasBody && !string.IsNullOrWhiteSpace(_operation.DefaultBody))
        {
            using var document = JsonDocument.Parse(_operation.DefaultBody);
            FlattenBody(document.RootElement, "", _fields, selectedRoot);
        }

        if (_fields.Count == 0)
            _fields.Add(new OperationFieldRow("信息", "none", "无需额外参数", "", false, "直接执行", "String", true));
    }

    private void ExecuteButton_Click(object sender, RoutedEventArgs e)
    {
        FieldsGrid.CommitEdit();
        FieldsGrid.CommitEdit();
        try
        {
            var pathValues = _fields
                .Where(field => field.Category == "对象")
                .ToDictionary(field => field.Key, field => field.Value.Trim(), StringComparer.OrdinalIgnoreCase);
            foreach (var field in _fields.Where(field => field.Required && !field.IsReadOnly))
            {
                if (string.IsNullOrWhiteSpace(field.Value)) throw new InvalidOperationException($"请填写“{field.Label}”。");
            }

            var path = _operation.ResolvePath(_siteId, pathValues);
            var queryPairs = _fields
                .Where(field => field.Category == "选项" && !string.IsNullOrWhiteSpace(field.Value))
                .Select(field => $"{Uri.EscapeDataString(field.Key)}={Uri.EscapeDataString(field.Value.Trim())}")
                .ToList();
            if (queryPairs.Count > 0) path += $"?{string.Join("&", queryPairs)}";
            _operation.ValidateQuery(string.Join("&", queryPairs));

            string? body = null;
            if (_operation.HasBody)
            {
                var root = JsonNode.Parse(_operation.DefaultBody) ?? new JsonObject();
                foreach (var field in _fields.Where(field => field.Category == "参数"))
                    SetNodeValue(root, field.Key, ParseNodeValue(field));
                body = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }

            Request = new OfficialOperationRequest(path, body);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ValidationText.Text = ex.Message;
        }
    }

    private static void FlattenBody(
        JsonElement element,
        string prefix,
        ICollection<OperationFieldRow> fields,
        JsonElement? selectedRoot)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            var path = string.IsNullOrWhiteSpace(prefix) ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                FlattenBody(property.Value, path, fields, selectedRoot);
                continue;
            }
            var kind = property.Value.ValueKind.ToString();
            var source = selectedRoot is JsonElement root && TryGetPathValue(root, path, out var selectedValue)
                ? selectedValue
                : property.Value;
            var value = source.ValueKind switch
            {
                JsonValueKind.String => source.GetString() ?? "",
                JsonValueKind.Number => source.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Array => FormatArrayValue(source),
                _ => ""
            };
            fields.Add(new OperationFieldRow("参数", path, FriendlyLabel(path), value, false, HintForKind(kind), kind));
        }
    }

    private static bool TryGetPathValue(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object) return false;
            var found = false;
            foreach (var property in value.EnumerateObject())
            {
                if (!property.Name.Equals(segment, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                found = true;
                break;
            }
            if (!found) return false;
        }
        return value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object);
    }

    private static JsonNode? ParseNodeValue(OperationFieldRow field)
    {
        var value = field.Value.Trim();
        return field.Kind switch
        {
            "True" or "False" => bool.TryParse(value, out var boolean)
                ? JsonValue.Create(boolean)
                : throw new InvalidOperationException($"“{field.Label}”只能填写 true 或 false。"),
            "Number" => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? JsonValue.Create(number)
                : throw new InvalidOperationException($"“{field.Label}”必须是数字。"),
            "Array" => ParseArray(value),
            _ => JsonValue.Create(value)
        };
    }

    private static JsonArray ParseArray(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            if (JsonNode.Parse(value) is JsonArray jsonArray) return jsonArray;
            throw new InvalidOperationException("数组字段格式不正确。");
        }
        var array = new JsonArray();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (decimal.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) array.Add(number);
            else if (bool.TryParse(item, out var boolean)) array.Add(boolean);
            else array.Add(item);
        }
        return array;
    }

    private static void SetNodeValue(JsonNode root, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        JsonNode current = root;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (current is not JsonObject currentObject) throw new InvalidOperationException($"无法设置字段 {path}。");
            current = currentObject[parts[index]] ??= new JsonObject();
        }
        if (current is not JsonObject target) throw new InvalidOperationException($"无法设置字段 {path}。");
        target[parts[^1]] = value;
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Trim().TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0) continue;
            result[Uri.UnescapeDataString(part[..separator])] = Uri.UnescapeDataString(part[(separator + 1)..]);
        }
        return result;
    }

    private static string FormatArrayValue(JsonElement array)
    {
        var builder = new StringBuilder();
        foreach (var value in array.EnumerateArray())
        {
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
        }
        return builder.ToString();
    }

    private static string FriendlyLabel(string path)
    {
        var leaf = path.Split('.').Last();
        var spaced = string.Concat(leaf.Select((character, index) => index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
        return spaced switch
        {
            "device Id" => "设备 ID", "client Id" => "客户端 ID", "network Id" => "网络 ID",
            "wifi Broadcast Id" => "WiFi 广播 ID", "voucher Id" => "凭证 ID", "firewall Policy Id" => "防火墙策略 ID",
            "firewall Zone Id" => "防火墙区域 ID", "port Idx" => "端口编号", "mac Address" => "MAC 地址",
            "time Limit Minutes" => "有效分钟数", "ignore Device Limit" => "忽略设备数量限制",
            "offset" => "起始位置", "limit" => "读取数量", "filter" => "筛选条件",
            _ => spaced
        };
    }

    private static string HintForKind(string kind) => kind switch
    {
        "True" or "False" => "true / false",
        "Number" => "数字",
        "Array" => "逗号分隔",
        _ => "文本"
    };
}

public sealed class OperationFieldRow
{
    public OperationFieldRow(string category, string key, string label, string value, bool required, string hint, string kind, bool isReadOnly = false)
    {
        Category = category;
        Key = key;
        Label = label;
        Value = value;
        Required = required;
        Hint = hint;
        Kind = kind;
        IsReadOnly = isReadOnly;
    }

    public string Category { get; }
    public string Key { get; }
    public string Label { get; }
    public string Value { get; set; }
    public bool Required { get; }
    public string Hint { get; }
    public string Kind { get; }
    public bool IsReadOnly { get; }
}
