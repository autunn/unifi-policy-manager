import Foundation

enum UniFiError: LocalizedError {
    case invalidHost
    case api(String)
    case invalidResponse(String)

    var errorDescription: String? {
        switch self {
        case .invalidHost: return "UCG 地址格式不正确，只填写 IP 或主机名。"
        case .api(let message), .invalidResponse(let message): return message
        }
    }
}

final class TLSDelegate: NSObject, URLSessionDelegate {
    let verifyTLS: Bool
    init(verifyTLS: Bool) { self.verifyTLS = verifyTLS }

    func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        guard !verifyTLS,
              challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust else {
            completionHandler(.performDefaultHandling, nil)
            return
        }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }
}

final class UniFiAPI: @unchecked Sendable {
    let target: String
    let apiKey: String
    private let session: URLSession
    private let delegate: TLSDelegate
    private let decoder = JSONDecoder()
    private let encoder = JSONEncoder()

    private(set) var applicationVersion = "未知"
    private(set) var sites: [UniFiSite] = []
    private(set) var selectedSite: UniFiSite?

    init(host: String, apiKey: String, verifyTLS: Bool) throws {
        let trimmed = host.trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let raw = trimmed.contains("://") ? trimmed : "https://\(trimmed)"
        guard let url = URL(string: raw), ["http", "https"].contains(url.scheme), url.host != nil, url.path.isEmpty || url.path == "/" else {
            throw UniFiError.invalidHost
        }
        guard !apiKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw UniFiError.api("请输入 UniFi API Key。")
        }
        target = raw.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        self.apiKey = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        delegate = TLSDelegate(verifyTLS: verifyTLS)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 30
        configuration.httpAdditionalHeaders = ["User-Agent": "UniFi-Policy-Manager-macOS/1.0"]
        session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
    }

    func connect() async throws {
        let info = try await request(path: "/proxy/network/integration/v1/info")
        applicationVersion = info["applicationVersion"] as? String ?? "未知"
        sites = try await paged(path: "/proxy/network/integration/v1/sites").map { object in
            UniFiSite(
                id: object["id"] as? String ?? "",
                internalReference: object["internalReference"] as? String ?? "",
                name: object["name"] as? String ?? ""
            )
        }.filter { !$0.id.isEmpty }
        if sites.isEmpty { throw UniFiError.invalidResponse("API Key 可用，但没有返回可管理站点。") }
    }

    func selectSite(_ site: UniFiSite) { selectedSite = site }

    func listDNSRecords() async throws -> [DNSRecord] {
        try await paged(path: sitePath("dns/policies")).map(parseDNS)
    }

    func createDNS(_ record: DNSRecord) async throws -> DNSRecord? {
        let object = try await request(path: sitePath("dns/policies"), method: "POST", body: try UniFiPayloadValidator.dnsPayload(record))
        return object.isEmpty ? nil : parseDNS(object)
    }

    func updateDNS(_ record: DNSRecord) async throws -> DNSRecord? {
        guard let id = record.id else { throw UniFiError.api("DNS 记录缺少 ID。") }
        let object = try await request(path: sitePath("dns/policies/\(id)"), method: "PUT", body: try UniFiPayloadValidator.dnsPayload(record))
        return object.isEmpty ? nil : parseDNS(object)
    }

    func deleteDNS(_ record: DNSRecord) async throws {
        guard let id = record.id else { throw UniFiError.api("DNS 记录缺少 ID。") }
        _ = try await request(path: sitePath("dns/policies/\(id)"), method: "DELETE")
    }

    func listPolicies(_ kind: PolicyKind) async throws -> [PolicyRule] {
        try await paged(path: sitePath(kind.apiPath)).map { parsePolicy($0, kind: kind) }.sorted { $0.index < $1.index }
    }

    func createPolicy(_ kind: PolicyKind, json: String) async throws -> PolicyRule? {
        let body = try UniFiPayloadValidator.policyPayload(kind, json: json)
        let object = try await request(path: sitePath(kind.apiPath), method: "POST", body: body)
        return object.isEmpty ? nil : parsePolicy(object, kind: kind)
    }

    func updatePolicy(_ rule: PolicyRule, json: String) async throws -> PolicyRule? {
        let body = try UniFiPayloadValidator.policyPayload(rule.kind, json: json)
        let object = try await request(path: sitePath("\(rule.kind.apiPath)/\(rule.id)"), method: "PUT", body: body)
        return object.isEmpty ? nil : parsePolicy(object, kind: rule.kind)
    }

    func deletePolicy(_ rule: PolicyRule) async throws {
        _ = try await request(path: sitePath("\(rule.kind.apiPath)/\(rule.id)"), method: "DELETE")
    }

    func getPolicyOrdering(_ kind: PolicyKind) async throws -> PolicyOrderingSnapshot {
        let object = try await request(path: sitePath("\(kind.apiPath)/ordering"))
        if kind == .acl {
            guard let ids = object["orderedAclRuleIds"] as? [String] else {
                throw UniFiError.invalidResponse("ACL 排序响应缺少 orderedAclRuleIds。")
            }
            return PolicyOrderingSnapshot(kind: .acl, orderedACLRuleIDs: ids)
        }
        guard let ordered = object["orderedFirewallPolicyIds"] as? [String: Any],
              let before = ordered["beforeSystemDefined"] as? [String],
              let after = ordered["afterSystemDefined"] as? [String] else {
            throw UniFiError.invalidResponse("防火墙排序响应格式不正确。")
        }
        return PolicyOrderingSnapshot(kind: .firewall, beforeSystemDefined: before, afterSystemDefined: after)
    }

    func setPolicyOrdering(_ ordering: PolicyOrderingSnapshot) async throws {
        let body: [String: Any]
        if ordering.kind == .acl {
            body = ["orderedAclRuleIds": ordering.orderedACLRuleIDs]
        } else {
            body = ["orderedFirewallPolicyIds": [
                "beforeSystemDefined": ordering.beforeSystemDefined,
                "afterSystemDefined": ordering.afterSystemDefined
            ]]
        }
        _ = try await request(path: sitePath("\(ordering.kind.apiPath)/ordering"), method: "PUT", body: body)
    }

    func movePolicy(_ rule: PolicyRule, direction: Int) async throws {
        guard direction == -1 || direction == 1 else { throw UniFiError.api("排序方向无效。") }
        var ordering = try await getPolicyOrdering(rule.kind)
        if rule.kind == .acl {
            try swap(rule.id, direction: direction, in: &ordering.orderedACLRuleIDs)
        } else if ordering.beforeSystemDefined.contains(where: { $0.caseInsensitiveCompare(rule.id) == .orderedSame }) {
            try swap(rule.id, direction: direction, in: &ordering.beforeSystemDefined)
        } else if ordering.afterSystemDefined.contains(where: { $0.caseInsensitiveCompare(rule.id) == .orderedSame }) {
            try swap(rule.id, direction: direction, in: &ordering.afterSystemDefined)
        } else {
            throw UniFiError.api("该防火墙策略不在可排序的用户策略列表中。")
        }
        try await setPolicyOrdering(ordering)
    }

    func listReferences() async -> [PolicyReference] {
        let endpoints = [
            ("networks", "网络"), ("firewall/zones", "防火墙区域"), ("devices", "设备"),
            ("traffic-matching-lists", "流量匹配列表"), ("vpn/servers", "VPN 服务器"),
            ("vpn/site-to-site-tunnels", "站点到站点 VPN"), ("device-tags", "设备标签")
        ]
        var output: [PolicyReference] = []
        for (path, kind) in endpoints {
            guard let rows = try? await paged(path: sitePath(path)) else { continue }
            output += rows.compactMap { row in
                guard let id = row["id"] as? String else { return nil }
                return PolicyReference(id: id, name: row["name"] as? String ?? id, kind: kind)
            }
        }
        return output
    }

    func executeOfficial(method: String, relativePath: String, requestJSON: String?) async throws -> String {
        let normalizedMethod = method.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        guard ["GET", "POST", "PUT", "PATCH", "DELETE"].contains(normalizedMethod) else {
            throw UniFiError.api("不支持的 HTTP 方法。")
        }
        guard relativePath.hasPrefix("/v1/"), !relativePath.contains(".."), !relativePath.contains("://") else {
            throw UniFiError.api("只允许调用官方 Network /v1 端点。")
        }
        let body: Any?
        if let requestJSON, !requestJSON.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            body = try JSONSerialization.jsonObject(with: Data(requestJSON.utf8), options: [.fragmentsAllowed])
        } else {
            body = nil
        }
        let object = try await rawRequest(
            path: "/proxy/network/integration\(relativePath)",
            method: normalizedMethod,
            body: body
        )
        let data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .fragmentsAllowed])
        return String(decoding: data, as: UTF8.self)
    }

    private func sitePath(_ suffix: String) -> String {
        let id = selectedSite?.id.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? ""
        return "/proxy/network/integration/v1/sites/\(id)/\(suffix)"
    }

    private func paged(path: String) async throws -> [[String: Any]] {
        var output: [[String: Any]] = []
        var offset = 0
        while true {
            let separator = path.contains("?") ? "&" : "?"
            let root = try await request(path: "\(path)\(separator)offset=\(offset)&limit=200")
            guard let data = root["data"] as? [[String: Any]] else { throw UniFiError.invalidResponse("UniFi 列表响应格式不正确。") }
            output += data
            let count = root["count"] as? Int ?? data.count
            let total = root["totalCount"] as? Int ?? output.count
            offset += count
            if count == 0 || offset >= total { break }
        }
        return output
    }

    private func request(path: String, method: String = "GET", body: [String: Any]? = nil) async throws -> [String: Any] {
        let object = try await rawRequest(path: path, method: method, body: body)
        if let dictionary = object as? [String: Any] { return dictionary }
        return [:]
    }

    private func rawRequest(path: String, method: String = "GET", body: Any? = nil) async throws -> Any {
        guard let url = URL(string: target + path) else { throw UniFiError.invalidHost }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(apiKey, forHTTPHeaderField: "X-API-Key")
        if let body {
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw UniFiError.invalidResponse("UCG 没有返回 HTTP 响应。") }
        if !(200..<300).contains(http.statusCode) {
            let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            var message = object?["message"] as? String ?? "UniFi 请求失败（HTTP \(http.statusCode)）。"
            if let code = object?["code"] as? String, !code.isEmpty { message += " [\(code)]" }
            if let requestID = object?["requestId"] as? String, !requestID.isEmpty { message += "；Request ID: \(requestID)" }
            if object == nil, !data.isEmpty { message += " 控制器返回了非 JSON 错误页面，请确认填写的是 Console 根地址。" }
            throw UniFiError.api(message)
        }
        if data.isEmpty { return [String: Any]() }
        return try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
    }

    private func parseDNS(_ item: [String: Any]) -> DNSRecord {
        let apiType = item["type"] as? String ?? ""
        let recordType = [
            "FORWARD_DOMAIN": "NS", "A_RECORD": "A", "AAAA_RECORD": "AAAA",
            "CNAME_RECORD": "CNAME", "MX_RECORD": "MX", "TXT_RECORD": "TXT", "SRV_RECORD": "SRV"
        ][apiType] ?? apiType
        let domain = item["domain"] as? String ?? ""
        var record = DNSRecord(
            id: item["id"] as? String, recordType: recordType, key: domain, value: "",
            enabled: item["enabled"] as? Bool ?? false, ttl: item["ttlSeconds"] as? Int,
            priority: item["priority"] as? Int, weight: item["weight"] as? Int, port: item["port"] as? Int,
            service: item["service"] as? String ?? "", protocolName: item["protocol"] as? String ?? "", domain: domain
        )
        switch recordType {
        case "NS": record.value = item["ipAddress"] as? String ?? ""
        case "A": record.value = item["ipv4Address"] as? String ?? ""
        case "AAAA": record.value = item["ipv6Address"] as? String ?? ""
        case "CNAME": record.value = item["targetDomain"] as? String ?? ""
        case "MX": record.value = item["mailServerDomain"] as? String ?? ""
        case "TXT": record.value = item["text"] as? String ?? ""
        case "SRV":
            record.value = item["serverDomain"] as? String ?? ""
            record.key = "\(record.service).\(record.protocolName).\(domain)"
        default: break
        }
        return record
    }

    private func parsePolicy(_ item: [String: Any], kind: PolicyKind) -> PolicyRule {
        let metadata = item["metadata"] as? [String: Any]
        let actionObject = item["action"] as? [String: Any]
        let scope = item["ipProtocolScope"] as? [String: Any]
        let raw = (try? JSONSerialization.data(withJSONObject: item, options: [.prettyPrinted, .sortedKeys])).map { String(decoding: $0, as: UTF8.self) } ?? "{}"
        return PolicyRule(
            id: item["id"] as? String ?? UUID().uuidString, kind: kind, name: item["name"] as? String ?? "未命名",
            enabled: item["enabled"] as? Bool ?? false, index: item["index"] as? Int ?? 0,
            type: kind == .acl ? (item["type"] as? String ?? "") : (scope?["ipVersion"] as? String ?? ""),
            action: kind == .acl ? (item["action"] as? String ?? "") : (actionObject?["type"] as? String ?? ""),
            origin: metadata?["origin"] as? String ?? "", description: item["description"] as? String ?? "", rawJSON: raw
        )
    }

    private func swap(_ id: String, direction: Int, in ids: inout [String]) throws {
        guard let index = ids.firstIndex(where: { $0.caseInsensitiveCompare(id) == .orderedSame }) else {
            throw UniFiError.api("该策略不在用户定义排序列表中。")
        }
        let target = index + direction
        guard ids.indices.contains(target) else { return }
        ids.swapAt(index, target)
    }

}
