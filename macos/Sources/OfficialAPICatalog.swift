import Foundation

struct OfficialAPIModule: Identifiable, Hashable {
    let id: String
    let title: String
    let description: String
}

struct OfficialAPIOperation: Identifiable, Hashable {
    let id: String
    let moduleID: String
    let title: String
    let method: String
    let pathTemplate: String
    let description: String
    let defaultBody: String
    let defaultQuery: String
    let requiredQueryParameters: [String]

    var isWrite: Bool { method != "GET" }
    var hasBody: Bool { ["POST", "PUT", "PATCH"].contains(method) }
    var isDestructive: Bool {
        method == "DELETE" || id.localizedCaseInsensitiveContains("Action") || id == "adoptDevice" || id == "createVouchers"
    }
    var pathParameters: [String] {
        let expression = try! NSRegularExpression(pattern: #"\{([^}]+)\}"#)
        let range = NSRange(pathTemplate.startIndex..., in: pathTemplate)
        var output: [String] = []
        for match in expression.matches(in: pathTemplate, range: range) {
            guard let swiftRange = Range(match.range(at: 1), in: pathTemplate) else { continue }
            let name = String(pathTemplate[swiftRange])
            if name != "siteId", !output.contains(name) { output.append(name) }
        }
        return output
    }

    func resolvedPath(siteID: String, parameters: [String: String]) throws -> String {
        var path = pathTemplate.replacingOccurrences(of: "{siteId}", with: Self.escape(siteID))
        for name in pathParameters {
            guard let value = parameters[name]?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty else {
                throw UniFiError.api("请填写路径参数 \(name)。")
            }
            path = path.replacingOccurrences(of: "{\(name)}", with: Self.escape(value))
        }
        return path
    }

    func validateQuery(_ query: String) throws {
        guard !requiredQueryParameters.isEmpty else { return }
        var values: [String: String] = [:]
        let normalized = query.trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "?"))
            .replacingOccurrences(of: "\r\n", with: "&")
            .replacingOccurrences(of: "\r", with: "&")
            .replacingOccurrences(of: "\n", with: "&")
        for part in normalized.split(separator: "&") {
            let pair = part.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard pair.count == 2 else { continue }
            let name = String(pair[0]).removingPercentEncoding ?? String(pair[0])
            let value = String(pair[1]).removingPercentEncoding ?? String(pair[1])
            values[name.trimmingCharacters(in: .whitespaces)] = value.trimmingCharacters(in: .whitespaces)
        }
        for name in requiredQueryParameters where values[name]?.isEmpty != false {
            throw UniFiError.api("请填写必需查询参数 \(name)。")
        }
    }

    private static func escape(_ value: String) -> String {
        value.addingPercentEncoding(withAllowedCharacters: CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))) ?? value
    }
}

enum OfficialAPICatalog {
    static let modules: [OfficialAPIModule] = [
        .init(id: "application", title: "应用与站点", description: "Network 应用信息和本地站点。"),
        .init(id: "devices", title: "UniFi 设备", description: "待采用设备、已采用设备、设备动作、端口动作和最新统计。"),
        .init(id: "clients", title: "在线客户端", description: "已连接客户端详情和访客授权动作。"),
        .init(id: "networks", title: "网络", description: "网络的列表、创建、详情、更新、删除和引用。"),
        .init(id: "wifi", title: "WiFi 广播", description: "无线广播的完整生命周期管理。"),
        .init(id: "hotspot", title: "Hotspot 凭证", description: "访客凭证生成、查询和删除。"),
        .init(id: "firewall", title: "防火墙", description: "防火墙策略、排序和自定义区域。"),
        .init(id: "acl", title: "ACL", description: "访问控制规则及用户规则排序。"),
        .init(id: "switching", title: "交换与聚合", description: "LAG、MC-LAG Domain 和 Switch Stack。"),
        .init(id: "dns", title: "DNS Policy", description: "官方 DNS Policy 的完整 CRUD。"),
        .init(id: "traffic", title: "流量匹配列表", description: "IPv4、IPv6 和端口匹配列表。"),
        .init(id: "resources", title: "支持资源", description: "国家、DPI、标签、RADIUS、VPN 和 WAN。")
    ]

    static let operations: [OfficialAPIOperation] = [
        op("getInfo", "application", "获取应用信息", "GET", "/v1/info"),
        op("getSiteOverviewPage", "application", "列出本地站点", "GET", "/v1/sites", query: pageQuery),

        op("getPendingDevicePage", "devices", "列出待采用设备", "GET", "/v1/pending-devices", query: pageQuery),
        op("getAdoptedDeviceOverviewPage", "devices", "列出已采用设备", "GET", "/v1/sites/{siteId}/devices", query: pageQuery),
        op("adoptDevice", "devices", "采用设备", "POST", "/v1/sites/{siteId}/devices", body: "{\n  \"ignoreDeviceLimit\": false,\n  \"macAddress\": \"\"\n}"),
        op("removeDevice", "devices", "移除（取消采用）设备", "DELETE", "/v1/sites/{siteId}/devices/{deviceId}"),
        op("getAdoptedDeviceDetails", "devices", "获取设备详情", "GET", "/v1/sites/{siteId}/devices/{deviceId}"),
        op("executeAdoptedDeviceAction", "devices", "执行设备动作", "POST", "/v1/sites/{siteId}/devices/{deviceId}/actions", body: "{\n  \"action\": \"RESTART\"\n}"),
        op("executePortAction", "devices", "执行端口动作", "POST", "/v1/sites/{siteId}/devices/{deviceId}/interfaces/ports/{portIdx}/actions", body: "{\n  \"action\": \"POWER_CYCLE\"\n}"),
        op("getAdoptedDeviceLatestStatistics", "devices", "获取设备最新统计", "GET", "/v1/sites/{siteId}/devices/{deviceId}/statistics/latest"),

        op("getConnectedClientOverviewPage", "clients", "列出在线客户端", "GET", "/v1/sites/{siteId}/clients", query: pageQuery),
        op("getConnectedClientDetails", "clients", "获取客户端详情", "GET", "/v1/sites/{siteId}/clients/{clientId}"),
        op("executeConnectedClientAction", "clients", "执行客户端动作", "POST", "/v1/sites/{siteId}/clients/{clientId}/actions", body: "{\n  \"action\": \"AUTHORIZE_GUEST_ACCESS\",\n  \"timeLimitMinutes\": 60\n}"),

        op("getNetworksOverviewPage", "networks", "列出网络", "GET", "/v1/sites/{siteId}/networks", query: pageQuery),
        op("createNetwork", "networks", "创建网络", "POST", "/v1/sites/{siteId}/networks", body: networkTemplate),
        op("deleteNetwork", "networks", "删除网络", "DELETE", "/v1/sites/{siteId}/networks/{networkId}"),
        op("getNetworkDetails", "networks", "获取网络详情", "GET", "/v1/sites/{siteId}/networks/{networkId}"),
        op("updateNetwork", "networks", "更新网络", "PUT", "/v1/sites/{siteId}/networks/{networkId}", body: networkTemplate),
        op("getNetworkReferences", "networks", "获取网络引用", "GET", "/v1/sites/{siteId}/networks/{networkId}/references"),

        op("getWifiBroadcastPage", "wifi", "列出 WiFi 广播", "GET", "/v1/sites/{siteId}/wifi/broadcasts", query: pageQuery),
        op("createWifiBroadcast", "wifi", "创建 WiFi 广播", "POST", "/v1/sites/{siteId}/wifi/broadcasts", body: wifiTemplate),
        op("deleteWifiBroadcast", "wifi", "删除 WiFi 广播", "DELETE", "/v1/sites/{siteId}/wifi/broadcasts/{wifiBroadcastId}"),
        op("getWifiBroadcastDetails", "wifi", "获取 WiFi 广播详情", "GET", "/v1/sites/{siteId}/wifi/broadcasts/{wifiBroadcastId}"),
        op("updateWifiBroadcast", "wifi", "更新 WiFi 广播", "PUT", "/v1/sites/{siteId}/wifi/broadcasts/{wifiBroadcastId}", body: wifiTemplate),

        op("deleteVouchers", "hotspot", "批量删除凭证", "DELETE", "/v1/sites/{siteId}/hotspot/vouchers", query: "filter=", requiredQuery: ["filter"]),
        op("getVouchers", "hotspot", "列出凭证", "GET", "/v1/sites/{siteId}/hotspot/vouchers", query: pageQuery),
        op("createVouchers", "hotspot", "生成凭证", "POST", "/v1/sites/{siteId}/hotspot/vouchers", body: "{\n  \"name\": \"Guest Voucher\",\n  \"timeLimitMinutes\": 60,\n  \"count\": 1,\n  \"authorizedGuestLimit\": 1\n}"),
        op("deleteVoucher", "hotspot", "删除单个凭证", "DELETE", "/v1/sites/{siteId}/hotspot/vouchers/{voucherId}"),
        op("getVoucher", "hotspot", "获取凭证详情", "GET", "/v1/sites/{siteId}/hotspot/vouchers/{voucherId}"),

        op("getFirewallPolicies", "firewall", "列出防火墙策略", "GET", "/v1/sites/{siteId}/firewall/policies", query: pageQuery),
        op("createFirewallPolicy", "firewall", "创建防火墙策略", "POST", "/v1/sites/{siteId}/firewall/policies", body: firewallTemplate),
        op("getFirewallPolicyOrdering", "firewall", "获取防火墙排序", "GET", "/v1/sites/{siteId}/firewall/policies/ordering", query: firewallOrderingQuery, requiredQuery: ["sourceFirewallZoneId", "destinationFirewallZoneId"]),
        op("updateFirewallPolicyOrdering", "firewall", "更新防火墙排序", "PUT", "/v1/sites/{siteId}/firewall/policies/ordering", body: "{\n  \"orderedFirewallPolicyIds\": {\n    \"beforeSystemDefined\": [],\n    \"afterSystemDefined\": []\n  }\n}", query: firewallOrderingQuery, requiredQuery: ["sourceFirewallZoneId", "destinationFirewallZoneId"]),
        op("deleteFirewallPolicy", "firewall", "删除防火墙策略", "DELETE", "/v1/sites/{siteId}/firewall/policies/{firewallPolicyId}"),
        op("getFirewallPolicy", "firewall", "获取防火墙策略", "GET", "/v1/sites/{siteId}/firewall/policies/{firewallPolicyId}"),
        op("patchFirewallPolicy", "firewall", "Patch 防火墙日志", "PATCH", "/v1/sites/{siteId}/firewall/policies/{firewallPolicyId}", body: "{\n  \"loggingEnabled\": true\n}"),
        op("updateFirewallPolicy", "firewall", "更新防火墙策略", "PUT", "/v1/sites/{siteId}/firewall/policies/{firewallPolicyId}", body: firewallTemplate),
        op("getFirewallZones", "firewall", "列出防火墙区域", "GET", "/v1/sites/{siteId}/firewall/zones", query: pageQuery),
        op("createFirewallZone", "firewall", "创建自定义区域", "POST", "/v1/sites/{siteId}/firewall/zones", body: zoneTemplate),
        op("deleteFirewallZone", "firewall", "删除自定义区域", "DELETE", "/v1/sites/{siteId}/firewall/zones/{firewallZoneId}"),
        op("getFirewallZone", "firewall", "获取防火墙区域", "GET", "/v1/sites/{siteId}/firewall/zones/{firewallZoneId}"),
        op("updateFirewallZone", "firewall", "更新防火墙区域", "PUT", "/v1/sites/{siteId}/firewall/zones/{firewallZoneId}", body: zoneTemplate),

        op("getAclRulePage", "acl", "列出 ACL 规则", "GET", "/v1/sites/{siteId}/acl-rules", query: pageQuery),
        op("createAclRule", "acl", "创建 ACL 规则", "POST", "/v1/sites/{siteId}/acl-rules", body: aclTemplate),
        op("getAclRuleOrdering", "acl", "获取 ACL 排序", "GET", "/v1/sites/{siteId}/acl-rules/ordering"),
        op("updateAclRuleOrdering", "acl", "更新 ACL 排序", "PUT", "/v1/sites/{siteId}/acl-rules/ordering", body: "{\n  \"orderedAclRuleIds\": []\n}"),
        op("deleteAclRule", "acl", "删除 ACL 规则", "DELETE", "/v1/sites/{siteId}/acl-rules/{aclRuleId}"),
        op("getAclRule", "acl", "获取 ACL 规则", "GET", "/v1/sites/{siteId}/acl-rules/{aclRuleId}"),
        op("updateAclRule", "acl", "更新 ACL 规则", "PUT", "/v1/sites/{siteId}/acl-rules/{aclRuleId}", body: aclTemplate),

        op("getLagPage", "switching", "列出 LAG", "GET", "/v1/sites/{siteId}/switching/lags", query: pageQuery),
        op("getLag", "switching", "获取 LAG 详情", "GET", "/v1/sites/{siteId}/switching/lags/{lagId}"),
        op("getMcLagDomainPage", "switching", "列出 MC-LAG Domain", "GET", "/v1/sites/{siteId}/switching/mc-lag-domains", query: pageQuery),
        op("getMcLagDomain", "switching", "获取 MC-LAG Domain", "GET", "/v1/sites/{siteId}/switching/mc-lag-domains/{mcLagDomainId}"),
        op("getSwitchStackPage", "switching", "列出 Switch Stack", "GET", "/v1/sites/{siteId}/switching/switch-stacks", query: pageQuery),
        op("getSwitchStack", "switching", "获取 Switch Stack", "GET", "/v1/sites/{siteId}/switching/switch-stacks/{switchStackId}"),

        op("getDnsPolicyPage", "dns", "列出 DNS Policy", "GET", "/v1/sites/{siteId}/dns/policies", query: pageQuery),
        op("createDnsPolicy", "dns", "创建 DNS Policy", "POST", "/v1/sites/{siteId}/dns/policies", body: dnsTemplate),
        op("deleteDnsPolicy", "dns", "删除 DNS Policy", "DELETE", "/v1/sites/{siteId}/dns/policies/{dnsPolicyId}"),
        op("getDnsPolicy", "dns", "获取 DNS Policy", "GET", "/v1/sites/{siteId}/dns/policies/{dnsPolicyId}"),
        op("updateDnsPolicy", "dns", "更新 DNS Policy", "PUT", "/v1/sites/{siteId}/dns/policies/{dnsPolicyId}", body: dnsTemplate),

        op("getTrafficMatchingLists", "traffic", "列出流量匹配列表", "GET", "/v1/sites/{siteId}/traffic-matching-lists", query: pageQuery),
        op("createTrafficMatchingList", "traffic", "创建流量匹配列表", "POST", "/v1/sites/{siteId}/traffic-matching-lists", body: trafficTemplate),
        op("deleteTrafficMatchingList", "traffic", "删除流量匹配列表", "DELETE", "/v1/sites/{siteId}/traffic-matching-lists/{trafficMatchingListId}"),
        op("getTrafficMatchingList", "traffic", "获取流量匹配列表", "GET", "/v1/sites/{siteId}/traffic-matching-lists/{trafficMatchingListId}"),
        op("updateTrafficMatchingList", "traffic", "更新流量匹配列表", "PUT", "/v1/sites/{siteId}/traffic-matching-lists/{trafficMatchingListId}", body: trafficTemplate),

        op("getCountries", "resources", "列出国家/地区", "GET", "/v1/countries", query: pageQuery),
        op("getDpiApplications", "resources", "列出 DPI 应用", "GET", "/v1/dpi/applications", query: pageQuery),
        op("getDpiApplicationCategories", "resources", "列出 DPI 分类", "GET", "/v1/dpi/categories", query: pageQuery),
        op("getDeviceTagPage", "resources", "列出设备标签", "GET", "/v1/sites/{siteId}/device-tags", query: pageQuery),
        op("getRadiusProfileOverviewPage", "resources", "列出 RADIUS Profiles", "GET", "/v1/sites/{siteId}/radius/profiles", query: pageQuery),
        op("getVpnServerPage", "resources", "列出 VPN Servers", "GET", "/v1/sites/{siteId}/vpn/servers", query: pageQuery),
        op("getSiteToSiteVpnTunnelPage", "resources", "列出 Site-to-Site VPN", "GET", "/v1/sites/{siteId}/vpn/site-to-site-tunnels", query: pageQuery),
        op("getWansOverviewPage", "resources", "列出 WAN Interfaces", "GET", "/v1/sites/{siteId}/wans", query: pageQuery)
    ]

    static func module(_ id: String) -> OfficialAPIModule? { modules.first { $0.id == id } }
    static func operations(for moduleID: String) -> [OfficialAPIOperation] {
        moduleID == "all" ? operations : operations.filter { $0.moduleID == moduleID }
    }

    private static let pageQuery = "offset=0&limit=50"
    private static let firewallOrderingQuery = "sourceFirewallZoneId=&destinationFirewallZoneId="
    private static let aclTemplate = "{\n  \"type\": \"IPV4\",\n  \"name\": \"New IPv4 ACL\",\n  \"description\": \"\",\n  \"enabled\": false,\n  \"action\": \"BLOCK\"\n}"
    private static let firewallTemplate = "{\n  \"name\": \"New Firewall Policy\",\n  \"description\": \"\",\n  \"enabled\": false,\n  \"loggingEnabled\": false,\n  \"action\": { \"type\": \"BLOCK\" },\n  \"source\": { \"zoneId\": \"<SOURCE_ZONE_UUID>\" },\n  \"destination\": { \"zoneId\": \"<DESTINATION_ZONE_UUID>\" },\n  \"ipProtocolScope\": { \"ipVersion\": \"IPV4\" }\n}"
    private static let networkTemplate = "{\n  \"enabled\": true,\n  \"management\": \"UNMANAGED\",\n  \"name\": \"New Network\",\n  \"vlanId\": 2\n}"
    private static let zoneTemplate = "{\n  \"name\": \"Custom Zone\",\n  \"networkIds\": []\n}"
    private static let trafficTemplate = "{\n  \"name\": \"Protected IPs\",\n  \"type\": \"IPV4_ADDRESSES\",\n  \"items\": []\n}"
    private static let dnsTemplate = "{\n  \"enabled\": true,\n  \"type\": \"A_RECORD\",\n  \"domain\": \"example.com\",\n  \"ipv4Address\": \"192.0.2.10\",\n  \"ttlSeconds\": 0\n}"
    private static let wifiTemplate = "{\n  \"type\": \"STANDARD\",\n  \"name\": \"New WiFi\",\n  \"enabled\": true,\n  \"hideName\": false,\n  \"clientIsolationEnabled\": false,\n  \"multicastToUnicastConversionEnabled\": false,\n  \"channel2gLockedTo6\": false,\n  \"dtimPeriod2gLockedTo3\": false,\n  \"uapsdEnabled\": true,\n  \"securityConfiguration\": { \"type\": \"OPEN\" },\n  \"advertiseDeviceName\": false,\n  \"arpProxyEnabled\": false,\n  \"broadcastingFrequenciesGHz\": [2.4, 5],\n  \"bssTransitionEnabled\": true\n}"

    private static func op(
        _ id: String, _ moduleID: String, _ title: String, _ method: String, _ path: String,
        body: String = "", query: String = "", requiredQuery: [String] = []
    ) -> OfficialAPIOperation {
        .init(
            id: id, moduleID: moduleID, title: title, method: method, pathTemplate: path,
            description: "\(title)；使用 UniFi Network v10.4.57 官方端点。",
            defaultBody: body, defaultQuery: query, requiredQueryParameters: requiredQuery
        )
    }
}
