using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniFiDnsManager.Models;

namespace UniFiDnsManager.Services;

public static class OfficialResourcePresentationService
{
    private static readonly IReadOnlyDictionary<string, string> FieldLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "资源 ID", ["name"] = "名称", ["displayName"] = "显示名称", ["hostname"] = "主机名",
        ["status"] = "状态", ["state"] = "状态", ["enabled"] = "已启用", ["type"] = "类型",
        ["model"] = "型号", ["modelName"] = "型号", ["deviceType"] = "设备类型", ["ipAddress"] = "IP 地址",
        ["ipv4Address"] = "IPv4 地址", ["ipv6Address"] = "IPv6 地址", ["macAddress"] = "MAC 地址",
        ["vlanId"] = "VLAN ID", ["firmwareVersion"] = "固件版本", ["version"] = "版本",
        ["description"] = "说明", ["securityConfiguration.type"] = "安全类型", ["management"] = "管理方式",
        ["channel"] = "信道", ["connectedAt"] = "连接时间", ["lastSeenAt"] = "最后在线",
        ["createdAt"] = "创建时间", ["updatedAt"] = "更新时间", ["internalReference"] = "内部标识"
    };

    public static OfficialResourceSnapshot Parse(string json, string moduleId)
    {
        using var document = JsonDocument.Parse(json);
        var elements = ExtractResourceElements(document.RootElement);
        var items = elements
            .Where(element => element.ValueKind == JsonValueKind.Object)
            .Select(element => BuildItem(element, moduleId))
            .ToList();

        var reportedTotal = ReadTotalCount(document.RootElement);
        return CreateSnapshot(items, Math.Max(reportedTotal ?? items.Count, items.Count), reportedTotal.HasValue);
    }

    public static OfficialResourceSnapshot CreateSnapshot(
        IReadOnlyList<OfficialResourceItem> items,
        int? totalCount = null,
        bool hasReportedTotalCount = false)
    {
        var hasExplicitHealth = items.Any(item => item.HasHealthData);
        var healthy = items.Count(item => item.HasHealthData && item.IsHealthy);
        var attention = items.Count(item => item.HasHealthData && item.NeedsAttention);
        var grouped = items
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Type) ? "其他" : item.Type)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        return new OfficialResourceSnapshot
        {
            Items = items,
            TotalCount = Math.Max(totalCount ?? items.Count, items.Count),
            HealthyCount = healthy,
            AttentionCount = attention,
            TypeCount = grouped.Count,
            HasHealthData = hasExplicitHealth,
            HasReportedTotalCount = hasReportedTotalCount,
            TypeDistribution = grouped.Select(group => new OfficialResourceDistributionItem(
                group.Key,
                group.Count(),
                items.Count == 0 ? 0 : group.Count() * 100d / items.Count)).ToList()
        };
    }

    public static OfficialResourceItem ParseSingle(string json, string moduleId)
    {
        var snapshot = Parse(json, moduleId);
        return snapshot.Items.FirstOrDefault() ?? new OfficialResourceItem
        {
            Name = "操作结果",
            State = "已完成",
            IsHealthy = true,
            HasHealthData = true,
            Glyph = GlyphFor(moduleId, ""),
            RawJson = json,
            Fields = [new OfficialResourceField("结果", "请求已成功完成")]
        };
    }

    private static IReadOnlyList<JsonElement> ExtractResourceElements(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().Select(item => item.Clone()).ToList();
        if (root.ValueKind != JsonValueKind.Object) return [];

        foreach (var preferred in new[] { "data", "items", "results", "devices", "clients", "networks", "broadcasts", "vouchers", "policies", "zones" })
        {
            if (TryGetProperty(root, preferred, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray().Select(item => item.Clone()).ToList();
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0
                && property.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object))
                return property.Value.EnumerateArray().Select(item => item.Clone()).ToList();
        }

        return [root.Clone()];
    }

    private static OfficialResourceItem BuildItem(JsonElement element, string moduleId)
    {
        var flattened = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Flatten(element, "", flattened, 0);
        var id = Candidate(flattened, "id", "deviceId", "clientId", "networkId", "wifiBroadcastId", "voucherId", "macAddress",
                "code", "slug", "internalReference", "name", "displayName")
            ?? ContentFingerprint(element);
        var name = Candidate(flattened, "name", "displayName", "hostname", "deviceName", "ssid", "code", "internalReference")
            ?? id;
        var stateRaw = Candidate(flattened, "status", "state", "connectionState", "adoptionState", "enabled");
        var state = FriendlyState(stateRaw);
        var type = Candidate(flattened, "modelName", "model", "deviceType", "type", "management", "securityConfiguration.type") ?? "其他";
        var address = Candidate(flattened, "ipAddress", "ipv4Address", "ip", "gatewayIpAddress", "macAddress", "vlanId") ?? "—";
        var detail = Candidate(flattened, "description", "firmwareVersion", "version", "manufacturerName", "internalReference") ?? "—";
        var subtitle = Candidate(flattened, "location", "siteName", "manufacturerName", "internalReference", "macAddress") ?? type;
        var normalizedState = stateRaw?.Trim().ToUpperInvariant() ?? "";
        var attention = normalizedState.Contains("OFFLINE", StringComparison.Ordinal)
            || normalizedState.Contains("DISABLED", StringComparison.Ordinal)
            || normalizedState.Contains("FAILED", StringComparison.Ordinal)
            || normalizedState.Contains("ERROR", StringComparison.Ordinal)
            || normalizedState.Contains("PENDING", StringComparison.Ordinal)
            || normalizedState.Contains("UPDATING", StringComparison.Ordinal)
            || normalizedState.Contains("PROVISIONING", StringComparison.Ordinal)
            || normalizedState == "FALSE";
        var healthy = !attention && (!string.IsNullOrWhiteSpace(normalizedState));
        var fields = flattened
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .OrderBy(pair => FieldPriority(pair.Key))
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(18)
            .Select(pair => new OfficialResourceField(FriendlyFieldLabel(pair.Key), pair.Value))
            .ToList();

        return new OfficialResourceItem
        {
            Id = id,
            Name = name,
            Subtitle = subtitle,
            State = state,
            Type = type,
            Address = address,
            Detail = detail,
            Glyph = GlyphFor(moduleId, type),
            StateColor = attention ? "#D97706" : healthy ? "#37BE5F" : "#98A2B3",
            IsHealthy = healthy,
            NeedsAttention = attention,
            HasHealthData = !string.IsNullOrWhiteSpace(stateRaw),
            RawJson = element.GetRawText(),
            Fields = fields
        };
    }

    private static string ContentFingerprint(JsonElement element)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(element.GetRawText()));
        return $"resource-{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static void Flatten(JsonElement element, string prefix, IDictionary<string, string> output, int depth)
    {
        if (depth > 3) return;
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            var path = string.IsNullOrWhiteSpace(prefix) ? property.Name : $"{prefix}.{property.Name}";
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(property.Value, path, output, depth + 1);
                    break;
                case JsonValueKind.Array:
                    output[path] = FormatArray(property.Value);
                    break;
                case JsonValueKind.String:
                    output[path] = FormatString(property.Value.GetString() ?? "");
                    break;
                case JsonValueKind.Number:
                    output[path] = property.Value.GetRawText();
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    output[path] = property.Value.GetBoolean() ? "true" : "false";
                    break;
            }
        }
    }

    private static string FormatArray(JsonElement array)
    {
        var values = array.EnumerateArray().Take(6).Select(value => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object when TryGetProperty(value, "name", out var name) => name.GetString() ?? value.GetRawText(),
            _ => value.GetRawText()
        }).ToList();
        if (array.GetArrayLength() > values.Count) values.Add($"…共 {array.GetArrayLength()} 项");
        return values.Count == 0 ? "空" : string.Join(", ", values);
    }

    private static string FormatString(string value)
    {
        if (value.Contains("T", StringComparison.Ordinal)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        return value;
    }

    private static int? ReadTotalCount(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "totalCount", "count" })
        {
            if (TryGetProperty(root, key, out var value) && value.TryGetInt32(out var count)) return count;
        }
        return null;
    }

    private static string? Candidate(IReadOnlyDictionary<string, string> values, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = values.FirstOrDefault(pair => pair.Key.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value;
            match = values.FirstOrDefault(pair => pair.Key.EndsWith($".{candidate}", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value;
        }
        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string FriendlyState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未知";
        return value.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "ENABLED" or "ACTIVE" => "已启用",
            "FALSE" or "DISABLED" => "已停用",
            "ONLINE" or "CONNECTED" or "ADOPTED" or "READY" or "OK" => "在线",
            "OFFLINE" or "DISCONNECTED" => "离线",
            "PENDING" or "PENDING_ADOPTION" => "待处理",
            "UPDATING" or "PROVISIONING" => "处理中",
            var raw => raw.Replace('_', ' ')
        };
    }

    private static string FriendlyFieldLabel(string path)
    {
        if (FieldLabels.TryGetValue(path, out var direct)) return direct;
        var leaf = path.Split('.').Last();
        if (FieldLabels.TryGetValue(leaf, out var leafLabel)) return leafLabel;
        return string.Concat(leaf.Select((character, index) => index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
    }

    private static int FieldPriority(string path)
    {
        var leaf = path.Split('.').Last();
        return leaf.ToLowerInvariant() switch
        {
            "name" or "displayname" => 0,
            "status" or "state" or "enabled" => 1,
            "type" or "model" or "modelname" => 2,
            "ipaddress" or "ipv4address" or "macaddress" => 3,
            "id" => 4,
            _ => 10
        };
    }

    private static string GlyphFor(string moduleId, string type) => moduleId switch
    {
        "devices" when type.Contains("AP", StringComparison.OrdinalIgnoreCase) => "⌁",
        "devices" => "▤",
        "clients" => "◉",
        "networks" => "⌘",
        "wifi" => "◖",
        "hotspot" => "◇",
        "firewall" => "⬡",
        "traffic" => "≋",
        "switching" => "⇆",
        "resources" => "◌",
        "application" => "ⓘ",
        _ => "◫"
    };
}
