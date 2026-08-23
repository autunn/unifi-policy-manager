using System.Text.RegularExpressions;

namespace UniFiDnsManager.Models;

public sealed record OfficialApiModule(string Id, string Title, string Description);

public sealed record OfficialApiOperation(
    string Id,
    string ModuleId,
    string Title,
    string Method,
    string PathTemplate,
    string Description,
    string DefaultBody = "",
    string DefaultQuery = "",
    IReadOnlyList<string>? RequiredQueryParameters = null)
{
    public bool IsWrite => Method != "GET";
    public bool HasBody => Method is "POST" or "PUT" or "PATCH";
    public bool IsDestructive => Method == "DELETE" || Id.Contains("Action", StringComparison.OrdinalIgnoreCase)
        || Id is "adoptDevice" or "createVouchers";
    public IReadOnlyList<string> PathParameters => Regex.Matches(PathTemplate, "\\{([^}]+)\\}")
        .Select(match => match.Groups[1].Value)
        .Where(name => name != "siteId")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<string> RequiredQueries => RequiredQueryParameters ?? [];

    public string ResolvePath(string siteId, IReadOnlyDictionary<string, string> parameters)
    {
        var path = PathTemplate.Replace("{siteId}", Uri.EscapeDataString(siteId), StringComparison.OrdinalIgnoreCase);
        foreach (var name in PathParameters)
        {
            if (!parameters.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"请填写路径参数 {name}。");
            path = path.Replace($"{{{name}}}", Uri.EscapeDataString(value.Trim()), StringComparison.OrdinalIgnoreCase);
        }
        return path;
    }

    public void ValidateQuery(string query)
    {
        if (RequiredQueries.Count == 0) return;
        var values = query.Trim().TrimStart('?')
            .Replace("\r\n", "&", StringComparison.Ordinal)
            .Replace('\r', '&')
            .Replace('\n', '&')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var separator = part.IndexOf('=');
                return separator < 0 ? [] : new[] { part[..separator], part[(separator + 1)..] };
            })
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace("+", " ", StringComparison.Ordinal)).Trim(),
                parts => Uri.UnescapeDataString(parts[1].Replace("+", " ", StringComparison.Ordinal)).Trim(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var name in RequiredQueries)
        {
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"请填写必需查询参数 {name}。");
        }
    }
}

public static class OfficialApiCatalog
{
    public static readonly IReadOnlyList<OfficialApiModule> Modules =
    [
        new("application", "应用与站点", "Network 应用信息和本地站点。"),
        new("devices", "UniFi 设备", "待采用设备、已采用设备、设备动作、端口动作和最新统计。"),
        new("clients", "在线客户端", "已连接客户端详情和访客授权动作。"),
        new("networks", "网络", "网络的列表、创建、详情、更新、删除和引用。"),
        new("wifi", "WiFi 广播", "无线广播的完整生命周期管理。"),
        new("hotspot", "Hotspot 凭证", "访客凭证生成、查询和删除。"),
        new("firewall", "防火墙", "防火墙策略、排序和自定义区域。"),
        new("acl", "ACL", "访问控制规则及用户规则排序。"),
        new("switching", "交换与聚合", "LAG、MC-LAG Domain 和 Switch Stack。"),
        new("dns", "DNS Policy", "官方 DNS Policy 的完整 CRUD。"),
        new("traffic", "流量匹配列表", "IPv4、IPv6 和端口匹配列表。"),
        new("resources", "支持资源", "国家、DPI、标签、RADIUS、VPN 和 WAN。")
    ];

    public static readonly IReadOnlyList<OfficialApiOperation> Operations =
    [
        // Application Info / Sites
        Op("getInfo", "application", "获取应用信息", "GET", "/v1/info", "读取 Network 版本与应用信息。"),
        Op("getSiteOverviewPage", "application", "列出本地站点", "GET", "/v1/sites", "列出 API Key 可访问的本地站点。", query: PageQuery),

        // UniFi Devices
        Op("getPendingDevicePage", "devices", "列出待采用设备", "GET", "/v1/pending-devices", "发现尚未被采用的 UniFi 设备。", query: PageQuery),
        Op("getAdoptedDeviceOverviewPage", "devices", "列出已采用设备", "GET", "/v1/sites/{siteId}/devices", "列出站点内已采用设备。", query: PageQuery),
        Op("adoptDevice", "devices", "采用设备", "POST", "/v1/sites/{siteId}/devices", "使用 MAC 地址采用待处理设备。", "{\n  \"ignoreDeviceLimit\": false,\n  \"macAddress\": \"\"\n}"),
        Op("removeDevice", "devices", "移除（取消采用）设备", "DELETE", "/v1/sites/{siteId}/devices/{deviceId}", "从站点移除已采用设备。"),
        Op("getAdoptedDeviceDetails", "devices", "获取设备详情", "GET", "/v1/sites/{siteId}/devices/{deviceId}", "读取单个已采用设备详情。"),
        Op("executeAdoptedDeviceAction", "devices", "执行设备动作", "POST", "/v1/sites/{siteId}/devices/{deviceId}/actions", "执行官方支持的设备动作，例如重启。", "{\n  \"action\": \"RESTART\"\n}"),
        Op("executePortAction", "devices", "执行端口动作", "POST", "/v1/sites/{siteId}/devices/{deviceId}/interfaces/ports/{portIdx}/actions", "执行端口动作，例如 PoE 电源循环。", "{\n  \"action\": \"POWER_CYCLE\"\n}"),
        Op("getAdoptedDeviceLatestStatistics", "devices", "获取设备最新统计", "GET", "/v1/sites/{siteId}/devices/{deviceId}/statistics/latest", "读取设备最新统计信息。"),

        // Clients
        Op("getConnectedClientOverviewPage", "clients", "列出在线客户端", "GET", "/v1/sites/{siteId}/clients", "列出当前连接客户端。", query: PageQuery),
        Op("getConnectedClientDetails", "clients", "获取客户端详情", "GET", "/v1/sites/{siteId}/clients/{clientId}", "读取单个在线客户端详情。"),
        Op("executeConnectedClientAction", "clients", "执行客户端动作", "POST", "/v1/sites/{siteId}/clients/{clientId}/actions", "授权或取消访客客户端访问。", "{\n  \"action\": \"AUTHORIZE_GUEST_ACCESS\",\n  \"timeLimitMinutes\": 60\n}"),

        // Networks
        Op("getNetworksOverviewPage", "networks", "列出网络", "GET", "/v1/sites/{siteId}/networks", "列出站点网络。", query: PageQuery),
        Op("createNetwork", "networks", "创建网络", "POST", "/v1/sites/{siteId}/networks", "创建 Gateway、Switch 或 Unmanaged 网络。", "{\n  \"enabled\": true,\n  \"management\": \"UNMANAGED\",\n  \"name\": \"New Network\",\n  \"vlanId\": 2\n}"),
        Op("deleteNetwork", "networks", "删除网络", "DELETE", "/v1/sites/{siteId}/networks/{networkId}", "删除指定网络。"),
        Op("getNetworkDetails", "networks", "获取网络详情", "GET", "/v1/sites/{siteId}/networks/{networkId}", "读取网络完整配置。"),
        Op("updateNetwork", "networks", "更新网络", "PUT", "/v1/sites/{siteId}/networks/{networkId}", "使用完整请求体更新网络。", "{\n  \"enabled\": true,\n  \"management\": \"UNMANAGED\",\n  \"name\": \"Network\",\n  \"vlanId\": 2\n}"),
        Op("getNetworkReferences", "networks", "获取网络引用", "GET", "/v1/sites/{siteId}/networks/{networkId}/references", "查看阻止网络删除或依赖该网络的对象。"),

        // WiFi Broadcasts
        Op("getWifiBroadcastPage", "wifi", "列出 WiFi 广播", "GET", "/v1/sites/{siteId}/wifi/broadcasts", "列出无线广播。", query: PageQuery),
        Op("createWifiBroadcast", "wifi", "创建 WiFi 广播", "POST", "/v1/sites/{siteId}/wifi/broadcasts", "创建 STANDARD 或 IOT_OPTIMIZED WiFi 广播。", WifiTemplate),
        Op("deleteWifiBroadcast", "wifi", "删除 WiFi 广播", "DELETE", "/v1/sites/{siteId}/wifi/broadcasts/{wifiBroadcastId}", "删除无线广播。"),
        Op("getWifiBroadcastDetails", "wifi", "获取 WiFi 广播详情", "GET", "/v1/sites/{siteId}/wifi/broadcasts/{wifiBroadcastId}", "读取无线广播完整配置。"),
        Op("updateWifiBroadcast", "wifi", "更新 WiFi 广播", "PUT", "/v1/sites/{siteId}/wifi/broadcasts/{wifiBroadcastId}", "使用完整请求体更新无线广播。", WifiTemplate),

        // Hotspot
        Op("deleteVouchers", "hotspot", "批量删除凭证", "DELETE", "/v1/sites/{siteId}/hotspot/vouchers", "按官方 filter 查询条件批量删除 Hotspot 凭证。", query: "filter=", requiredQuery: ["filter"]),
        Op("getVouchers", "hotspot", "列出凭证", "GET", "/v1/sites/{siteId}/hotspot/vouchers", "列出 Hotspot 凭证。", query: PageQuery),
        Op("createVouchers", "hotspot", "生成凭证", "POST", "/v1/sites/{siteId}/hotspot/vouchers", "一次生成最多 1000 个 Hotspot 凭证。", "{\n  \"name\": \"Guest Voucher\",\n  \"timeLimitMinutes\": 60,\n  \"count\": 1,\n  \"authorizedGuestLimit\": 1\n}"),
        Op("deleteVoucher", "hotspot", "删除单个凭证", "DELETE", "/v1/sites/{siteId}/hotspot/vouchers/{voucherId}", "删除指定 Hotspot 凭证。"),
        Op("getVoucher", "hotspot", "获取凭证详情", "GET", "/v1/sites/{siteId}/hotspot/vouchers/{voucherId}", "读取指定凭证详情。"),

        // Firewall Policies / Zones
        Op("getFirewallPolicies", "firewall", "列出防火墙策略", "GET", "/v1/sites/{siteId}/firewall/policies", "列出用户、系统和派生防火墙策略。", query: PageQuery),
        Op("createFirewallPolicy", "firewall", "创建防火墙策略", "POST", "/v1/sites/{siteId}/firewall/policies", "创建用户定义防火墙策略；请将模板中的区域 UUID 替换为当前站点值。", FirewallTemplate),
        Op("getFirewallPolicyOrdering", "firewall", "获取防火墙排序", "GET", "/v1/sites/{siteId}/firewall/policies/ordering", "按来源区域与目标区域读取用户定义防火墙策略顺序。", query: FirewallOrderingQuery, requiredQuery: ["sourceFirewallZoneId", "destinationFirewallZoneId"]),
        Op("updateFirewallPolicyOrdering", "firewall", "更新防火墙排序", "PUT", "/v1/sites/{siteId}/firewall/policies/ordering", "按来源区域与目标区域更新用户定义防火墙策略顺序。", "{\n  \"orderedFirewallPolicyIds\": {\n    \"beforeSystemDefined\": [],\n    \"afterSystemDefined\": []\n  }\n}", FirewallOrderingQuery, ["sourceFirewallZoneId", "destinationFirewallZoneId"]),
        Op("deleteFirewallPolicy", "firewall", "删除防火墙策略", "DELETE", "/v1/sites/{siteId}/firewall/policies/{firewallPolicyId}", "删除用户定义防火墙策略。"),
        Op("getFirewallPolicy", "firewall", "获取防火墙策略", "GET", "/v1/sites/{siteId}/firewall/policies/{firewallPolicyId}", "读取单个防火墙策略。"),
        Op("patchFirewallPolicy", "firewall", "Patch 防火墙日志", "PATCH", "/v1/sites/{siteId}/firewall/policies/{firewallPolicyId}", "局部修改防火墙策略 loggingEnabled。", "{\n  \"loggingEnabled\": true\n}"),
        Op("updateFirewallPolicy", "firewall", "更新防火墙策略", "PUT", "/v1/sites/{siteId}/firewall/policies/{firewallPolicyId}", "使用完整请求体更新用户定义防火墙策略。", FirewallTemplate),
        Op("getFirewallZones", "firewall", "列出防火墙区域", "GET", "/v1/sites/{siteId}/firewall/zones", "列出系统和自定义防火墙区域。", query: PageQuery),
        Op("createFirewallZone", "firewall", "创建自定义区域", "POST", "/v1/sites/{siteId}/firewall/zones", "创建自定义防火墙区域。", "{\n  \"name\": \"Custom Zone\",\n  \"networkIds\": []\n}"),
        Op("deleteFirewallZone", "firewall", "删除自定义区域", "DELETE", "/v1/sites/{siteId}/firewall/zones/{firewallZoneId}", "删除自定义防火墙区域。"),
        Op("getFirewallZone", "firewall", "获取防火墙区域", "GET", "/v1/sites/{siteId}/firewall/zones/{firewallZoneId}", "读取单个防火墙区域。"),
        Op("updateFirewallZone", "firewall", "更新防火墙区域", "PUT", "/v1/sites/{siteId}/firewall/zones/{firewallZoneId}", "更新自定义防火墙区域。", "{\n  \"name\": \"Custom Zone\",\n  \"networkIds\": []\n}"),

        // ACL
        Op("getAclRulePage", "acl", "列出 ACL 规则", "GET", "/v1/sites/{siteId}/acl-rules", "列出 ACL 规则。", query: PageQuery),
        Op("createAclRule", "acl", "创建 ACL 规则", "POST", "/v1/sites/{siteId}/acl-rules", "创建 IPv4 ACL 规则；MAC 类型可在专用 ACL 编辑器中生成。", AclTemplate),
        Op("getAclRuleOrdering", "acl", "获取 ACL 排序", "GET", "/v1/sites/{siteId}/acl-rules/ordering", "读取用户定义 ACL 顺序。"),
        Op("updateAclRuleOrdering", "acl", "更新 ACL 排序", "PUT", "/v1/sites/{siteId}/acl-rules/ordering", "更新用户定义 ACL 顺序。", "{\n  \"orderedAclRuleIds\": []\n}"),
        Op("deleteAclRule", "acl", "删除 ACL 规则", "DELETE", "/v1/sites/{siteId}/acl-rules/{aclRuleId}", "删除用户定义 ACL 规则。"),
        Op("getAclRule", "acl", "获取 ACL 规则", "GET", "/v1/sites/{siteId}/acl-rules/{aclRuleId}", "读取单个 ACL 规则。"),
        Op("updateAclRule", "acl", "更新 ACL 规则", "PUT", "/v1/sites/{siteId}/acl-rules/{aclRuleId}", "使用完整请求体更新 ACL 规则。", AclTemplate),

        // Switching
        Op("getLagPage", "switching", "列出 LAG", "GET", "/v1/sites/{siteId}/switching/lags", "列出链路聚合组。", query: PageQuery),
        Op("getLag", "switching", "获取 LAG 详情", "GET", "/v1/sites/{siteId}/switching/lags/{lagId}", "读取链路聚合组详情。"),
        Op("getMcLagDomainPage", "switching", "列出 MC-LAG Domain", "GET", "/v1/sites/{siteId}/switching/mc-lag-domains", "列出 MC-LAG Domain。", query: PageQuery),
        Op("getMcLagDomain", "switching", "获取 MC-LAG Domain", "GET", "/v1/sites/{siteId}/switching/mc-lag-domains/{mcLagDomainId}", "读取 MC-LAG Domain 详情。"),
        Op("getSwitchStackPage", "switching", "列出 Switch Stack", "GET", "/v1/sites/{siteId}/switching/switch-stacks", "列出交换机堆叠。", query: PageQuery),
        Op("getSwitchStack", "switching", "获取 Switch Stack", "GET", "/v1/sites/{siteId}/switching/switch-stacks/{switchStackId}", "读取交换机堆叠详情。"),

        // DNS Policies
        Op("getDnsPolicyPage", "dns", "列出 DNS Policy", "GET", "/v1/sites/{siteId}/dns/policies", "列出全部 DNS Policy。", query: PageQuery),
        Op("createDnsPolicy", "dns", "创建 DNS Policy", "POST", "/v1/sites/{siteId}/dns/policies", "创建 DNS Policy；推荐使用专用 DNS 页面。", "{\n  \"enabled\": true,\n  \"type\": \"A_RECORD\",\n  \"domain\": \"example.com\",\n  \"ipv4Address\": \"192.0.2.10\",\n  \"ttlSeconds\": 0\n}"),
        Op("deleteDnsPolicy", "dns", "删除 DNS Policy", "DELETE", "/v1/sites/{siteId}/dns/policies/{dnsPolicyId}", "删除指定 DNS Policy。"),
        Op("getDnsPolicy", "dns", "获取 DNS Policy", "GET", "/v1/sites/{siteId}/dns/policies/{dnsPolicyId}", "读取单个 DNS Policy。"),
        Op("updateDnsPolicy", "dns", "更新 DNS Policy", "PUT", "/v1/sites/{siteId}/dns/policies/{dnsPolicyId}", "使用完整请求体更新 DNS Policy。", DnsTemplate),

        // Traffic Matching Lists
        Op("getTrafficMatchingLists", "traffic", "列出流量匹配列表", "GET", "/v1/sites/{siteId}/traffic-matching-lists", "列出 IPv4、IPv6 和端口匹配列表。", query: PageQuery),
        Op("createTrafficMatchingList", "traffic", "创建流量匹配列表", "POST", "/v1/sites/{siteId}/traffic-matching-lists", "创建流量匹配列表。", "{\n  \"name\": \"Protected IPs\",\n  \"type\": \"IPV4_ADDRESSES\",\n  \"items\": []\n}"),
        Op("deleteTrafficMatchingList", "traffic", "删除流量匹配列表", "DELETE", "/v1/sites/{siteId}/traffic-matching-lists/{trafficMatchingListId}", "删除流量匹配列表。"),
        Op("getTrafficMatchingList", "traffic", "获取流量匹配列表", "GET", "/v1/sites/{siteId}/traffic-matching-lists/{trafficMatchingListId}", "读取流量匹配列表。"),
        Op("updateTrafficMatchingList", "traffic", "更新流量匹配列表", "PUT", "/v1/sites/{siteId}/traffic-matching-lists/{trafficMatchingListId}", "使用完整请求体更新流量匹配列表。", TrafficTemplate),

        // Supporting Resources
        Op("getCountries", "resources", "列出国家/地区", "GET", "/v1/countries", "列出官方国家/地区资源。", query: PageQuery),
        Op("getDpiApplications", "resources", "列出 DPI 应用", "GET", "/v1/dpi/applications", "列出 DPI 应用。", query: PageQuery),
        Op("getDpiApplicationCategories", "resources", "列出 DPI 分类", "GET", "/v1/dpi/categories", "列出 DPI 应用分类。", query: PageQuery),
        Op("getDeviceTagPage", "resources", "列出设备标签", "GET", "/v1/sites/{siteId}/device-tags", "列出设备标签。", query: PageQuery),
        Op("getRadiusProfileOverviewPage", "resources", "列出 RADIUS Profiles", "GET", "/v1/sites/{siteId}/radius/profiles", "列出 RADIUS Profiles。", query: PageQuery),
        Op("getVpnServerPage", "resources", "列出 VPN Servers", "GET", "/v1/sites/{siteId}/vpn/servers", "列出 VPN Servers。", query: PageQuery),
        Op("getSiteToSiteVpnTunnelPage", "resources", "列出 Site-to-Site VPN", "GET", "/v1/sites/{siteId}/vpn/site-to-site-tunnels", "列出站点到站点 VPN Tunnels。", query: PageQuery),
        Op("getWansOverviewPage", "resources", "列出 WAN Interfaces", "GET", "/v1/sites/{siteId}/wans", "列出 WAN Interfaces。", query: PageQuery)
    ];

    public static OfficialApiModule GetModule(string id) => Modules.First(module => module.Id == id);
    public static IReadOnlyList<OfficialApiOperation> ForModule(string id) =>
        id == "all" ? Operations : Operations.Where(operation => operation.ModuleId == id).ToList();

    private const string PageQuery = "offset=0&limit=50";
    private const string FirewallOrderingQuery = "sourceFirewallZoneId=&destinationFirewallZoneId=";
    private const string AclTemplate = "{\n  \"type\": \"IPV4\",\n  \"name\": \"New IPv4 ACL\",\n  \"description\": \"\",\n  \"enabled\": false,\n  \"action\": \"BLOCK\"\n}";
    private const string FirewallTemplate = "{\n  \"name\": \"New Firewall Policy\",\n  \"description\": \"\",\n  \"enabled\": false,\n  \"loggingEnabled\": false,\n  \"action\": { \"type\": \"BLOCK\" },\n  \"source\": { \"zoneId\": \"<SOURCE_ZONE_UUID>\" },\n  \"destination\": { \"zoneId\": \"<DESTINATION_ZONE_UUID>\" },\n  \"ipProtocolScope\": { \"ipVersion\": \"IPV4\" }\n}";
    private const string DnsTemplate = "{\n  \"enabled\": true,\n  \"type\": \"A_RECORD\",\n  \"domain\": \"example.com\",\n  \"ipv4Address\": \"192.0.2.10\",\n  \"ttlSeconds\": 0\n}";
    private const string TrafficTemplate = "{\n  \"name\": \"Protected IPs\",\n  \"type\": \"IPV4_ADDRESSES\",\n  \"items\": []\n}";
    private const string WifiTemplate = "{\n  \"type\": \"STANDARD\",\n  \"name\": \"New WiFi\",\n  \"enabled\": true,\n  \"hideName\": false,\n  \"clientIsolationEnabled\": false,\n  \"multicastToUnicastConversionEnabled\": false,\n  \"channel2gLockedTo6\": false,\n  \"dtimPeriod2gLockedTo3\": false,\n  \"uapsdEnabled\": true,\n  \"securityConfiguration\": { \"type\": \"OPEN\" },\n  \"advertiseDeviceName\": false,\n  \"arpProxyEnabled\": false,\n  \"broadcastingFrequenciesGHz\": [2.4, 5],\n  \"bssTransitionEnabled\": true\n}";

    private static OfficialApiOperation Op(
        string id,
        string module,
        string title,
        string method,
        string path,
        string description,
        string body = "",
        string query = "",
        IReadOnlyList<string>? requiredQuery = null) => new(id, module, title, method, path, description, body, query, requiredQuery);
}
