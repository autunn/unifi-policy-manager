using UniFiDnsManager.Models;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;

namespace UniFiDnsManager.Services;

public sealed class DemoUniFiClient : IUniFiClient
{
    private static readonly JsonSerializerOptions DisplayJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly UniFiSite DemoSite = new("00000000-0000-0000-0000-000000000001", "default", "Default");
    private readonly List<DnsRecord> _records =
    [
        new() { Id = "demo-ns-1", RecordType = "NS", Key = "example.com", Value = "192.168.1.10", Enabled = true },
        new() { Id = "demo-a-1", RecordType = "A", Key = "nas.example.com", Value = "192.168.1.9", Ttl = 0, Enabled = true },
        new() { Id = "demo-aaaa-1", RecordType = "AAAA", Key = "v6.example.com", Value = "2001:db8::10", Ttl = 3600, Enabled = true },
        new() { Id = "demo-cname-1", RecordType = "CNAME", Key = "www.example.com", Value = "nas.example.com", Ttl = 300, Enabled = true },
        new() { Id = "demo-mx-1", RecordType = "MX", Key = "example.com", Value = "mail.example.com", Priority = 10, Enabled = true },
        new() { Id = "demo-txt-1", RecordType = "TXT", Key = "_dmarc.example.com", Value = "v=DMARC1; p=none", Enabled = true },
        new() { Id = "demo-srv-1", RecordType = "SRV", Key = "_sip._tcp.example.com", Value = "sip.example.com", Port = 5060, Priority = 10, Weight = 5, Enabled = false }
    ];
    private readonly List<OfficialPolicyRule> _aclRules = [];
    private readonly List<OfficialPolicyRule> _firewallRules = [];

    public string Target => "模拟数据";
    public string Site => DemoSite.DisplayName;
    public string SiteId => DemoSite.Id;
    public string ApplicationVersion => "10.5.67-demo";
    public IReadOnlyList<UniFiSite> Sites { get; } = [DemoSite];

    public DemoUniFiClient()
    {
        foreach (var record in _records) record.PopulateSrvParts();
        _aclRules.Add(ParseDemoRule(OfficialPolicyKind.Acl, """
        {
          "id":"10000000-0000-0000-0000-000000000001","index":0,"metadata":{"origin":"USER_DEFINED"},
          "type":"IPV4","name":"阻止访客访问管理网","description":"演示 ACL","enabled":true,"action":"BLOCK"
        }
        """));
        _firewallRules.Add(ParseDemoRule(OfficialPolicyKind.Firewall, """
        {
          "id":"20000000-0000-0000-0000-000000000001","index":0,"metadata":{"origin":"USER_DEFINED"},
          "name":"允许可信网络访问服务器","description":"演示防火墙策略","enabled":true,"loggingEnabled":false,
          "action":{"type":"ALLOW","allowReturnTraffic":true},
          "source":{"zoneId":"30000000-0000-0000-0000-000000000001"},
          "destination":{"zoneId":"30000000-0000-0000-0000-000000000002"},
          "ipProtocolScope":{"ipVersion":"IPV4"}
        }
        """));
    }

    public void SelectSite(UniFiSite site)
    {
        if (site.Id != DemoSite.Id) throw new UniFiApiException("模拟站点不存在。", 404);
    }

    public Task<IReadOnlyList<UniFiSite>> ListSitesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Sites);

    public Task<IReadOnlyList<DnsRecord>> ListRecordsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DnsRecord>>(_records.Select(item => item.Clone()).ToList());

    public Task<DnsRecord?> CreateRecordAsync(DnsRecord record, CancellationToken cancellationToken = default)
    {
        var normalized = DnsValidator.Normalize(record);
        normalized.Id = "demo-" + Guid.NewGuid().ToString("N")[..12];
        normalized.PopulateSrvParts();
        _records.Add(normalized);
        return Task.FromResult<DnsRecord?>(normalized.Clone());
    }

    public Task<DnsRecord?> UpdateRecordAsync(string id, DnsRecord record, CancellationToken cancellationToken = default)
    {
        var index = _records.FindIndex(item => item.Id == id);
        if (index < 0) throw new UniFiApiException("记录不存在。", 404);
        var normalized = DnsValidator.Normalize(record);
        normalized.Id = id;
        normalized.PopulateSrvParts();
        _records[index] = normalized;
        return Task.FromResult<DnsRecord?>(normalized.Clone());
    }

    public Task DeleteRecordAsync(string id, CancellationToken cancellationToken = default)
    {
        var removed = _records.RemoveAll(item => item.Id == id);
        if (removed == 0) throw new UniFiApiException("记录不存在。", 404);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OfficialPolicyRule>> ListPoliciesAsync(OfficialPolicyKind kind, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OfficialPolicyRule>>(GetPolicyList(kind).OrderBy(item => item.Index).ToList());

    public Task<OfficialPolicyRule?> CreatePolicyAsync(OfficialPolicyKind kind, string requestJson, CancellationToken cancellationToken = default)
    {
        var normalized = OfficialPolicyJson.NormalizeAndValidate(kind, requestJson);
        var list = GetPolicyList(kind);
        var created = BuildDemoResponse(kind, normalized, Guid.NewGuid().ToString(), list.Count);
        list.Add(created);
        return Task.FromResult<OfficialPolicyRule?>(created);
    }

    public Task<OfficialPolicyRule?> UpdatePolicyAsync(OfficialPolicyKind kind, string id, string requestJson, CancellationToken cancellationToken = default)
    {
        var list = GetPolicyList(kind);
        var index = list.FindIndex(item => item.Id == id);
        if (index < 0) throw new UniFiApiException("策略不存在。", 404);
        var normalized = OfficialPolicyJson.NormalizeAndValidate(kind, requestJson);
        var updated = BuildDemoResponse(kind, normalized, id, list[index].Index);
        list[index] = updated;
        return Task.FromResult<OfficialPolicyRule?>(updated);
    }

    public Task DeletePolicyAsync(OfficialPolicyKind kind, string id, CancellationToken cancellationToken = default)
    {
        if (GetPolicyList(kind).RemoveAll(item => item.Id == id) == 0) throw new UniFiApiException("策略不存在。", 404);
        Reindex(GetPolicyList(kind));
        return Task.CompletedTask;
    }

    public Task MovePolicyAsync(OfficialPolicyKind kind, string id, int direction, CancellationToken cancellationToken = default)
    {
        var list = GetPolicyList(kind);
        var index = list.FindIndex(item => item.Id == id);
        var target = index + direction;
        if (index < 0) throw new UniFiApiException("策略不存在。", 404);
        if (target >= 0 && target < list.Count) (list[index], list[target]) = (list[target], list[index]);
        Reindex(list);
        return Task.CompletedTask;
    }

    public Task<PolicyOrderingSnapshot> GetPolicyOrderingAsync(OfficialPolicyKind kind, CancellationToken cancellationToken = default)
    {
        var ids = GetPolicyList(kind).OrderBy(item => item.Index).Select(item => item.Id).ToList();
        return Task.FromResult(kind == OfficialPolicyKind.Acl
            ? new PolicyOrderingSnapshot { Kind = kind, OrderedAclRuleIds = ids }
            : new PolicyOrderingSnapshot { Kind = kind, BeforeSystemDefined = ids });
    }

    public Task SetPolicyOrderingAsync(OfficialPolicyKind kind, PolicyOrderingSnapshot ordering, CancellationToken cancellationToken = default)
    {
        var ids = kind == OfficialPolicyKind.Acl
            ? ordering.OrderedAclRuleIds
            : ordering.BeforeSystemDefined.Concat(ordering.AfterSystemDefined).ToList();
        var list = GetPolicyList(kind);
        var byId = list.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var reordered = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        reordered.AddRange(list.Where(item => !ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase)));
        list.Clear();
        list.AddRange(reordered);
        Reindex(list);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PolicyReferenceItem>> ListPolicyReferencesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PolicyReferenceItem>>([
            new("40000000-0000-0000-0000-000000000001", "Default", "网络"),
            new("30000000-0000-0000-0000-000000000001", "Internal", "防火墙区域"),
            new("30000000-0000-0000-0000-000000000002", "Server", "防火墙区域")
        ]);

    public Task<string> ExecuteOfficialApiAsync(
        string method,
        string relativePath,
        string? requestJson = null,
        CancellationToken cancellationToken = default)
    {
        var demoData = BuildOfficialDemoData(relativePath);
        var response = method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            ? (object)new { data = demoData, count = demoData.Length, totalCount = demoData.Length, demo = true }
            : new { success = true, method = method.ToUpperInvariant(), endpoint = relativePath, demo = true };
        return Task.FromResult(JsonSerializer.Serialize(response, DisplayJsonOptions));
    }

    private static object[] BuildOfficialDemoData(string relativePath)
    {
        if (relativePath.Contains("/devices", StringComparison.OrdinalIgnoreCase))
            return
            [
                new { id = "demo-gateway", name = "Cloud Gateway Fiber", state = "ONLINE", model = "UCG-Fiber", ipAddress = "192.168.1.1", firmwareVersion = "4.2.11" },
                new { id = "demo-switch", name = "Core Switch", state = "ONLINE", model = "Pro Max 24", ipAddress = "192.168.1.2", firmwareVersion = "7.1.26" },
                new { id = "demo-ap-1", name = "Living Room", state = "ONLINE", model = "U7 Pro", ipAddress = "192.168.1.21", firmwareVersion = "8.0.17" },
                new { id = "demo-ap-2", name = "Office AP", state = "UPDATING", model = "U6 Plus", ipAddress = "192.168.1.22", firmwareVersion = "7.0.66" }
            ];
        if (relativePath.Contains("/clients", StringComparison.OrdinalIgnoreCase))
            return
            [
                new { id = "client-1", name = "MacBook Pro", state = "CONNECTED", type = "WIRELESS", ipAddress = "192.168.1.101" },
                new { id = "client-2", name = "NAS", state = "CONNECTED", type = "WIRED", ipAddress = "192.168.1.30" }
            ];
        if (relativePath.Contains("/networks", StringComparison.OrdinalIgnoreCase))
            return
            [
                new { id = "network-1", name = "Default", enabled = true, management = "GATEWAY", vlanId = 1 },
                new { id = "network-2", name = "IoT", enabled = true, management = "GATEWAY", vlanId = 20 }
            ];
        if (relativePath.Contains("/wifi/broadcasts", StringComparison.OrdinalIgnoreCase))
            return [new { id = "wifi-1", name = "Home WiFi", enabled = true, type = "STANDARD", securityConfiguration = new { type = "WPA2_PERSONAL" } }];
        return [new { id = "demo-resource", name = "演示资源", status = "ACTIVE", type = "OFFICIAL", description = "官方 API 演示数据" }];
    }

    private List<OfficialPolicyRule> GetPolicyList(OfficialPolicyKind kind) => kind == OfficialPolicyKind.Acl ? _aclRules : _firewallRules;

    private static OfficialPolicyRule BuildDemoResponse(OfficialPolicyKind kind, string requestJson, string id, int index)
    {
        var node = JsonNode.Parse(requestJson)!.AsObject();
        node["id"] = id;
        node["index"] = index;
        node["metadata"] = new JsonObject { ["origin"] = "USER_DEFINED" };
        return ParseDemoRule(kind, node.ToJsonString());
    }

    private static OfficialPolicyRule ParseDemoRule(OfficialPolicyKind kind, string json)
    {
        using var document = JsonDocument.Parse(json);
        return OfficialPolicyRule.Parse(kind, document.RootElement);
    }

    private static void Reindex(List<OfficialPolicyRule> list)
    {
        for (var index = 0; index < list.Count; index++)
        {
            var node = JsonNode.Parse(list[index].RawResponseJson)!.AsObject();
            node["index"] = index;
            list[index] = ParseDemoRule(list[index].Kind, node.ToJsonString());
        }
    }

    public void Dispose() { }
}
