import Foundation

struct UniFiSite: Identifiable, Codable, Hashable {
    let id: String
    let internalReference: String
    let name: String

    var displayName: String { name.isEmpty ? internalReference : name }
}

enum PolicyKind: String, Codable, CaseIterable, Identifiable {
    case acl
    case firewall

    var id: String { rawValue }
    var title: String { self == .acl ? "ACL" : "防火墙" }
    var apiPath: String { self == .acl ? "acl-rules" : "firewall/policies" }

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let number = try? container.decode(Int.self) {
            self = number == 0 ? .acl : .firewall
            return
        }
        let value = try container.decode(String.self).lowercased()
        switch value {
        case "acl": self = .acl
        case "firewall": self = .firewall
        default: throw DecodingError.dataCorruptedError(in: container, debugDescription: "未知策略类型：\(value)")
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(self == .acl ? "Acl" : "Firewall")
    }
}

enum DNSRecordTypeTab: String, CaseIterable, Identifiable {
    case forwardDomain = "NS"
    case a = "A"
    case aaaa = "AAAA"
    case cname = "CNAME"
    case mx = "MX"
    case txt = "TXT"
    case srv = "SRV"

    var id: String { rawValue }
    var title: String { self == .forwardDomain ? "转发域名" : "\(rawValue) 记录" }
    var shortTitle: String { self == .forwardDomain ? "转发域名" : rawValue }
    var symbol: String {
        switch self {
        case .forwardDomain: return "arrow.triangle.branch"
        case .a: return "4.circle"
        case .aaaa: return "6.circle"
        case .cname: return "link"
        case .mx: return "envelope"
        case .txt: return "text.quote"
        case .srv: return "server.rack"
        }
    }
    var description: String {
        switch self {
        case .forwardDomain: return "把匹配域名的 DNS 查询转发到指定上游 DNS 服务器；支持批量新增与批量删除。"
        case .a: return "将域名解析到 IPv4 地址。"
        case .aaaa: return "将域名解析到 IPv6 地址。"
        case .cname: return "将一个别名域名指向另一个规范域名。"
        case .mx: return "指定负责接收该域名邮件的服务器及优先级。"
        case .txt: return "保存域名验证、邮件策略或其他文本信息。"
        case .srv: return "为指定服务声明服务器、端口、优先级与权重。"
        }
    }

    func makeDraft() -> DNSRecord { DNSRecord(recordType: rawValue) }
}

struct DNSRecord: Identifiable, Codable, Hashable {
    var id: String?
    var recordType: String
    var key: String
    var value: String
    var enabled: Bool
    var ttl: Int?
    var priority: Int?
    var weight: Int?
    var port: Int?
    var service: String
    var protocolName: String
    var domain: String

    init(
        id: String? = nil, recordType: String = "NS", key: String = "", value: String = "",
        enabled: Bool = true, ttl: Int? = nil, priority: Int? = nil, weight: Int? = nil,
        port: Int? = nil, service: String = "", protocolName: String = "", domain: String = ""
    ) {
        self.id = id
        self.recordType = recordType
        self.key = key
        self.value = value
        self.enabled = enabled
        self.ttl = ttl
        self.priority = priority
        self.weight = weight
        self.port = port
        self.service = service
        self.protocolName = protocolName
        self.domain = domain
    }

    enum CodingKeys: String, CodingKey {
        case id = "_id"
        case legacyID = "id"
        case recordType = "record_type"
        case key, value, enabled, ttl, priority, weight, port, service, domain
        case protocolName = "protocol"
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decodeIfPresent(String.self, forKey: .id)
            ?? container.decodeIfPresent(String.self, forKey: .legacyID)
        recordType = try container.decodeIfPresent(String.self, forKey: .recordType) ?? "NS"
        key = try container.decodeIfPresent(String.self, forKey: .key) ?? ""
        value = try container.decodeIfPresent(String.self, forKey: .value) ?? ""
        enabled = try container.decodeIfPresent(Bool.self, forKey: .enabled) ?? true
        ttl = try container.decodeIfPresent(Int.self, forKey: .ttl)
        priority = try container.decodeIfPresent(Int.self, forKey: .priority)
        weight = try container.decodeIfPresent(Int.self, forKey: .weight)
        port = try container.decodeIfPresent(Int.self, forKey: .port)
        service = try container.decodeIfPresent(String.self, forKey: .service) ?? ""
        protocolName = try container.decodeIfPresent(String.self, forKey: .protocolName) ?? ""
        domain = try container.decodeIfPresent(String.self, forKey: .domain) ?? ""
        if recordType == "SRV", domain.isEmpty {
            let parts = key.split(separator: ".", omittingEmptySubsequences: true).map(String.init)
            if parts.count >= 3, parts[0].hasPrefix("_"), parts[1].hasPrefix("_") {
                service = parts[0]
                protocolName = parts[1]
                domain = parts.dropFirst(2).joined(separator: ".")
            }
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encodeIfPresent(id, forKey: .id)
        try container.encode(recordType, forKey: .recordType)
        try container.encode(key, forKey: .key)
        try container.encode(value, forKey: .value)
        try container.encode(enabled, forKey: .enabled)
        try container.encodeIfPresent(ttl, forKey: .ttl)
        try container.encodeIfPresent(priority, forKey: .priority)
        try container.encodeIfPresent(weight, forKey: .weight)
        try container.encodeIfPresent(port, forKey: .port)
        try container.encode(service, forKey: .service)
        try container.encode(protocolName, forKey: .protocolName)
        try container.encode(domain, forKey: .domain)
    }

    var stableID: String { id ?? "\(recordType)|\(key)|\(value)" }
    var isForwardDomain: Bool { recordType == "NS" }
    var typeLabel: String { recordType == "NS" ? "转发域名" : recordType }
    var stateLabel: String { enabled ? "已启用" : "已停用" }
    var extraLabel: String {
        switch recordType {
        case "A", "AAAA", "CNAME": return (ttl ?? 0) > 0 ? "TTL \(ttl!) 秒" : "TTL 自动"
        case "MX": return "优先级 \(priority ?? 0)"
        case "SRV": return "端口 \(port ?? 0) · 优先级 \(priority ?? 0) · 权重 \(weight ?? 0)"
        default: return ""
        }
    }
}

struct PolicyOrderingSnapshot: Codable, Hashable {
    var kind: PolicyKind
    var orderedACLRuleIDs: [String] = []
    var beforeSystemDefined: [String] = []
    var afterSystemDefined: [String] = []

    enum CodingKeys: String, CodingKey {
        case kind
        case orderedACLRuleIDs = "ordered_acl_rule_ids"
        case beforeSystemDefined = "before_system_defined"
        case afterSystemDefined = "after_system_defined"
    }
}

struct PolicyRule: Identifiable, Codable, Hashable {
    let id: String
    let kind: PolicyKind
    let name: String
    let enabled: Bool
    let index: Int
    let type: String
    let action: String
    let origin: String
    let description: String
    let rawJSON: String

    var canModify: Bool { origin.caseInsensitiveCompare("USER_DEFINED") == .orderedSame }
    var stateLabel: String { enabled ? "已启用" : "已停用" }
    var originLabel: String {
        switch origin {
        case "USER_DEFINED": return "用户定义"
        case "SYSTEM_DEFINED": return "系统定义"
        case "DERIVED": return "派生"
        case "ORCHESTRATED": return "编排"
        default: return origin
        }
    }

    var editableJSON: String {
        guard var object = try? JSONSerialization.jsonObject(with: Data(rawJSON.utf8)) as? [String: Any] else { return rawJSON }
        object.removeValue(forKey: "id")
        object.removeValue(forKey: "index")
        object.removeValue(forKey: "metadata")
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]) else { return rawJSON }
        return String(decoding: data, as: UTF8.self)
    }
}

struct PolicyReference: Identifiable, Hashable {
    let id: String
    let name: String
    let kind: String
}

struct PolicyBundle: Codable {
    var schemaVersion: Int = 2
    var createdAt: Date = Date()
    var target: String = ""
    var site: String = ""
    var siteID: String = ""
    var networkVersion: String = ""
    var dnsRecords: [DNSRecord] = []
    var aclRules: [JSONValue] = []
    var firewallPolicies: [JSONValue] = []
    var aclOrdering: PolicyOrderingSnapshot?
    var firewallOrdering: PolicyOrderingSnapshot?
    var hasDNSSection = true
    var hasACLSection = true
    var hasFirewallSection = true

    enum CodingKeys: String, CodingKey {
        case schemaVersion = "schema_version"
        case createdAt = "created_at"
        case target, site
        case siteID = "site_id"
        case networkVersion = "network_version"
        case dnsRecords = "dns_records"
        case aclRules = "acl_rules"
        case firewallPolicies = "firewall_policies"
        case aclOrdering = "acl_ordering"
        case firewallOrdering = "firewall_ordering"
        case legacyRecords = "records"
    }

    init(
        schemaVersion: Int = 2, createdAt: Date = Date(), target: String = "", site: String = "",
        siteID: String = "", networkVersion: String = "", dnsRecords: [DNSRecord] = [],
        aclRules: [JSONValue] = [], firewallPolicies: [JSONValue] = [],
        aclOrdering: PolicyOrderingSnapshot? = nil, firewallOrdering: PolicyOrderingSnapshot? = nil
    ) {
        self.schemaVersion = schemaVersion
        self.createdAt = createdAt
        self.target = target
        self.site = site
        self.siteID = siteID
        self.networkVersion = networkVersion
        self.dnsRecords = dnsRecords
        self.aclRules = aclRules
        self.firewallPolicies = firewallPolicies
        self.aclOrdering = aclOrdering
        self.firewallOrdering = firewallOrdering
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        schemaVersion = try container.decodeIfPresent(Int.self, forKey: .schemaVersion) ?? 1
        createdAt = try container.decodeIfPresent(Date.self, forKey: .createdAt) ?? Date()
        target = try container.decodeIfPresent(String.self, forKey: .target) ?? ""
        site = try container.decodeIfPresent(String.self, forKey: .site) ?? ""
        siteID = try container.decodeIfPresent(String.self, forKey: .siteID) ?? ""
        networkVersion = try container.decodeIfPresent(String.self, forKey: .networkVersion) ?? ""
        hasDNSSection = container.contains(.dnsRecords) || container.contains(.legacyRecords)
        hasACLSection = container.contains(.aclRules)
        hasFirewallSection = container.contains(.firewallPolicies)
        dnsRecords = try container.decodeIfPresent([DNSRecord].self, forKey: .dnsRecords)
            ?? container.decodeIfPresent([DNSRecord].self, forKey: .legacyRecords)
            ?? []
        aclRules = try container.decodeIfPresent([JSONValue].self, forKey: .aclRules) ?? []
        firewallPolicies = try container.decodeIfPresent([JSONValue].self, forKey: .firewallPolicies) ?? []
        aclOrdering = try container.decodeIfPresent(PolicyOrderingSnapshot.self, forKey: .aclOrdering)
        firewallOrdering = try container.decodeIfPresent(PolicyOrderingSnapshot.self, forKey: .firewallOrdering)
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(schemaVersion, forKey: .schemaVersion)
        try container.encode(createdAt, forKey: .createdAt)
        try container.encode(target, forKey: .target)
        try container.encode(site, forKey: .site)
        try container.encode(siteID, forKey: .siteID)
        try container.encode(networkVersion, forKey: .networkVersion)
        try container.encode(dnsRecords, forKey: .dnsRecords)
        try container.encode(aclRules, forKey: .aclRules)
        try container.encode(firewallPolicies, forKey: .firewallPolicies)
        try container.encodeIfPresent(aclOrdering, forKey: .aclOrdering)
        try container.encodeIfPresent(firewallOrdering, forKey: .firewallOrdering)
    }
}

enum JSONValue: Codable, Hashable {
    case string(String), number(Double), bool(Bool), object([String: JSONValue]), array([JSONValue]), null

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() { self = .null }
        else if let value = try? container.decode(Bool.self) { self = .bool(value) }
        else if let value = try? container.decode(Double.self) { self = .number(value) }
        else if let value = try? container.decode(String.self) { self = .string(value) }
        else if let value = try? container.decode([String: JSONValue].self) { self = .object(value) }
        else { self = .array(try container.decode([JSONValue].self)) }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .string(let value): try container.encode(value)
        case .number(let value): try container.encode(value)
        case .bool(let value): try container.encode(value)
        case .object(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .null: try container.encodeNil()
        }
    }
}

extension JSONValue {
    static func from(_ value: Any) -> JSONValue {
        switch value {
        case let value as String: return .string(value)
        case let value as Bool: return .bool(value)
        case let value as NSNumber: return .number(value.doubleValue)
        case let value as [String: Any]: return .object(value.mapValues(JSONValue.from))
        case let value as [Any]: return .array(value.map(JSONValue.from))
        default: return .null
        }
    }

    var foundationValue: Any {
        switch self {
        case .string(let value): return value
        case .number(let value): return value
        case .bool(let value): return value
        case .object(let value): return value.mapValues(\.foundationValue)
        case .array(let value): return value.map(\.foundationValue)
        case .null: return NSNull()
        }
    }
}
