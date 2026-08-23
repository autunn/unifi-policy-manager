import AppKit
import Foundation
import SwiftUI
import UniformTypeIdentifiers

enum WorkspacePage: String, CaseIterable, Identifiable {
    case overview, changes
    case apiDevices, apiClients, apiNetworks, apiWifi, apiHotspot
    case dns, acl, firewall, apiFirewall, apiTraffic
    case apiSwitching, apiResources, apiApplication, apiAll

    var id: String { rawValue }
    var title: String {
        switch self {
        case .overview: return "概览"
        case .changes: return "策略变更中心"
        case .apiDevices: return "UniFi 设备"
        case .apiClients: return "在线客户端"
        case .apiNetworks: return "网络"
        case .apiWifi: return "WiFi 广播"
        case .apiHotspot: return "Hotspot 凭证"
        case .dns: return "DNS 记录"
        case .acl: return "ACL 规则"
        case .firewall: return "防火墙策略"
        case .apiFirewall: return "防火墙区域/API"
        case .apiTraffic: return "流量匹配列表"
        case .apiSwitching: return "交换与聚合"
        case .apiResources: return "WAN / VPN / 资源"
        case .apiApplication: return "应用与站点"
        case .apiAll: return "全部官方端点"
        }
    }
    var symbol: String {
        switch self {
        case .overview: return "square.grid.2x2"
        case .changes: return "arrow.triangle.2.circlepath"
        case .apiDevices: return "externaldrive.connected.to.line.below"
        case .apiClients: return "person.2"
        case .apiNetworks: return "point.3.connected.trianglepath.dotted"
        case .apiWifi: return "wifi"
        case .apiHotspot: return "ticket"
        case .dns: return "network"
        case .acl: return "checklist.checked"
        case .firewall: return "shield.lefthalf.filled"
        case .apiFirewall: return "hexagon"
        case .apiTraffic: return "list.bullet.rectangle"
        case .apiSwitching: return "arrow.triangle.swap"
        case .apiResources: return "server.rack"
        case .apiApplication: return "info.circle"
        case .apiAll: return "chevron.left.forwardslash.chevron.right"
        }
    }

    var section: WorkspaceSection {
        switch self {
        case .overview, .changes: return .workspace
        case .apiDevices, .apiClients, .apiNetworks, .apiWifi, .apiHotspot: return .network
        case .dns, .acl, .firewall, .apiFirewall, .apiTraffic: return .policy
        case .apiSwitching, .apiResources, .apiApplication, .apiAll: return .infrastructure
        }
    }

    var apiModuleID: String? {
        switch self {
        case .apiDevices: return "devices"
        case .apiClients: return "clients"
        case .apiNetworks: return "networks"
        case .apiWifi: return "wifi"
        case .apiHotspot: return "hotspot"
        case .apiFirewall: return "firewall"
        case .apiTraffic: return "traffic"
        case .apiSwitching: return "switching"
        case .apiResources: return "resources"
        case .apiApplication: return "application"
        case .apiAll: return "all"
        default: return nil
        }
    }
}

enum WorkspaceSection: String, CaseIterable, Identifiable, Hashable {
    case workspace, network, policy, infrastructure
    var id: String { rawValue }
    var title: String {
        switch self {
        case .workspace: return "工作台"
        case .network: return "网络资源"
        case .policy: return "策略与安全"
        case .infrastructure: return "基础设施与开发"
        }
    }
    var pages: [WorkspacePage] { WorkspacePage.allCases.filter { $0.section == self } }
}

struct SidebarExpansionState: Equatable {
    private var collapsedSections: Set<WorkspaceSection> = []

    func isExpanded(_ section: WorkspaceSection) -> Bool {
        !collapsedSections.contains(section)
    }

    mutating func setExpanded(_ expanded: Bool, for section: WorkspaceSection) {
        if expanded {
            collapsedSections.remove(section)
        } else {
            collapsedSections.insert(section)
        }
    }

    mutating func reveal(_ page: WorkspacePage) {
        collapsedSections.remove(page.section)
    }
}

@MainActor
final class AppModel: ObservableObject {
    @Published var host = ConnectionPreferences.host
    @Published var apiKey = ConnectionPreferences.rememberKey ? KeychainService.load() : ""
    @Published var verifyTLS = ConnectionPreferences.verifyTLS
    @Published var rememberKey = ConnectionPreferences.rememberKey
    @Published var connected = false
    @Published var demoMode = false
    @Published var busy = false
    @Published var status = "准备连接"
    @Published var errorMessage: String?
    @Published var selectedPage: WorkspacePage? = .overview
    @Published var sites: [UniFiSite] = []
    @Published var selectedSite: UniFiSite?
    @Published var showSitePicker = false
    @Published var dnsRecords: [DNSRecord] = []
    @Published var aclRules: [PolicyRule] = []
    @Published var firewallRules: [PolicyRule] = []
    @Published var references: [PolicyReference] = []
    @Published var writeReady = false
    @Published var search = ""
    @Published var selectedDNSType: DNSRecordTypeTab = .forwardDomain
    @Published var lastBackupURL: URL?
    @Published var batchDNSServer = ""
    @Published var batchEditorText = DNSImportService.editorHeader
    @Published var importSummary = "可加载应用内置的 212 条转发域规则，或导入 TXT、CSV、XLSX。"
    @Published var dnsBatchPreview: DNSBatchPreview?
    @Published var changePlan: PolicyChangePlan?
    @Published var synchronizeDeletes = false
    @Published var officialAPIResponse = "选择左侧操作后执行官方请求。"

    private var api: UniFiAPI?
    private var loadedBundle: PolicyBundle?
    private var loadedBundleURL: URL?
    private var aclOrdering: PolicyOrderingSnapshot?
    private var firewallOrdering: PolicyOrderingSnapshot?

    private struct CorePolicyState {
        let dns: [DNSRecord]
        let acl: [PolicyRule]
        let firewall: [PolicyRule]
        let aclOrdering: PolicyOrderingSnapshot?
        let firewallOrdering: PolicyOrderingSnapshot?
    }

    var targetLabel: String { demoMode ? "演示环境" : (api?.target ?? host) }
    var versionLabel: String { demoMode ? "Demo 10.4.57" : (api?.applicationVersion ?? "未知") }
    var totalCount: Int { dnsRecords.count + aclRules.count + firewallRules.count }

    var filteredDNS: [DNSRecord] {
        dnsRecords.filter { record in
            let matchesType = record.recordType == selectedDNSType.rawValue
            let query = search.trimmingCharacters(in: .whitespacesAndNewlines)
            return matchesType && (query.isEmpty || [record.key, record.value, record.recordType].contains { $0.localizedCaseInsensitiveContains(query) })
        }
    }

    func dnsCount(for type: DNSRecordTypeTab) -> Int {
        dnsRecords.filter { $0.recordType == type.rawValue }.count
    }

    func filteredPolicies(_ kind: PolicyKind) -> [PolicyRule] {
        let source = kind == .acl ? aclRules : firewallRules
        let query = search.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return source }
        return source.filter { [$0.name, $0.type, $0.action, $0.origin, $0.description].contains { $0.localizedCaseInsensitiveContains(query) } }
    }

    func connect() {
        Task {
            await perform("正在验证官方 API…") {
                let client = try UniFiAPI(host: self.host, apiKey: self.apiKey, verifyTLS: self.verifyTLS)
                try await client.connect()
                self.api = client
                self.sites = client.sites
                if client.sites.count == 1 {
                    await self.finishConnection(site: client.sites[0])
                } else {
                    self.showSitePicker = true
                    self.status = "请选择要管理的站点"
                }
            }
        }
    }

    func chooseSite(_ site: UniFiSite) {
        showSitePicker = false
        Task {
            await perform("正在读取站点策略…") {
                await self.finishConnection(site: site)
            }
        }
    }

    private func finishConnection(site: UniFiSite) async {
        guard let api else { return }
        api.selectSite(site)
        selectedSite = site
        ConnectionPreferences.host = host
        ConnectionPreferences.verifyTLS = verifyTLS
        ConnectionPreferences.rememberKey = rememberKey
        do {
            if rememberKey { try KeychainService.save(apiKey) } else { KeychainService.forget() }
        } catch { errorMessage = error.localizedDescription }
        connected = true
        demoMode = false
        writeReady = false
        selectedPage = .overview
        await refreshAllBody()
        apiKey = ""
    }

    func startDemo() {
        demoMode = true
        connected = true
        selectedSite = UniFiSite(id: "cf95a3c0-21f4-48a8-a028-6ad714b9689e", internalReference: "default", name: "家庭网络")
        dnsRecords = DemoData.dns
        aclRules = DemoData.acl
        firewallRules = DemoData.firewall
        references = DemoData.references
        aclOrdering = PolicyOrderingSnapshot(kind: .acl, orderedACLRuleIDs: aclRules.filter(\.canModify).map(\.id))
        firewallOrdering = PolicyOrderingSnapshot(kind: .firewall, beforeSystemDefined: firewallRules.filter(\.canModify).map(\.id))
        batchDNSServer = dnsRecords.first(where: \.isForwardDomain)?.value ?? "192.168.1.10"
        writeReady = true
        selectedPage = .overview
        status = "演示模式：不会连接或修改真实 UCG"
    }

    func disconnect() {
        api = nil
        connected = false
        demoMode = false
        selectedSite = nil
        dnsRecords = []
        aclRules = []
        firewallRules = []
        references = []
        aclOrdering = nil
        firewallOrdering = nil
        loadedBundle = nil
        loadedBundleURL = nil
        changePlan = nil
        dnsBatchPreview = nil
        writeReady = false
        search = ""
        officialAPIResponse = "选择左侧操作后执行官方请求。"
        apiKey = rememberKey ? KeychainService.load() : ""
        status = "已断开连接"
    }

    func forgetKey() {
        KeychainService.forget()
        apiKey = ""
        rememberKey = false
        ConnectionPreferences.rememberKey = false
        status = "已从 macOS 钥匙串删除 API Key"
    }

    func refreshAll() { Task { await perform("正在刷新全部策略…") { await self.refreshAllBody() } } }

    func executeOfficialOperation(
        _ operation: OfficialAPIOperation,
        parameters: [String: String],
        query: String,
        requestBody: String
    ) {
        Task {
            await perform("正在执行 \(operation.title)…") {
                guard let siteID = self.selectedSite?.id else { throw UniFiError.api("请先选择 UniFi 站点。") }
                var path = try operation.resolvedPath(siteID: siteID, parameters: parameters)
                let normalizedQuery = query.trimmingCharacters(in: .whitespacesAndNewlines)
                    .trimmingCharacters(in: CharacterSet(charactersIn: "?"))
                    .replacingOccurrences(of: "\r\n", with: "&")
                    .replacingOccurrences(of: "\r", with: "&")
                    .replacingOccurrences(of: "\n", with: "&")
                try operation.validateQuery(normalizedQuery)
                if !normalizedQuery.isEmpty { path += "?\(normalizedQuery)" }
                let body = operation.hasBody ? requestBody : nil
                if operation.hasBody {
                    _ = try JSONSerialization.jsonObject(with: Data(requestBody.utf8), options: [.fragmentsAllowed])
                }
                if self.demoMode {
                    let response: [String: Any] = operation.method == "GET"
                        ? ["data": [["id": "demo-resource", "name": "演示资源", "endpoint": path]], "count": 1, "totalCount": 1, "demo": true]
                        : ["success": true, "method": operation.method, "endpoint": path, "demo": true]
                    let data = try JSONSerialization.data(withJSONObject: response, options: [.prettyPrinted, .sortedKeys])
                    self.officialAPIResponse = String(decoding: data, as: UTF8.self)
                } else {
                    self.officialAPIResponse = try await self.requireAPI().executeOfficial(
                        method: operation.method,
                        relativePath: path,
                        requestJSON: body
                    )
                }
                if operation.isWrite { BackupService.log("official api \(operation.method) \(operation.id)") }
                self.status = "\(operation.title)执行成功"
            }
        }
    }

    func requestOfficialResource(
        _ operation: OfficialAPIOperation,
        parameters: [String: String] = [:],
        query: String = "",
        requestBody: String? = nil
    ) async throws -> String {
        guard let siteID = selectedSite?.id else { throw UniFiError.api("请先选择 UniFi 站点。") }
        var path = try operation.resolvedPath(siteID: siteID, parameters: parameters)
        let normalizedQuery = query.trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "?"))
            .replacingOccurrences(of: "\r\n", with: "&")
            .replacingOccurrences(of: "\r", with: "&")
            .replacingOccurrences(of: "\n", with: "&")
        try operation.validateQuery(normalizedQuery)
        if !normalizedQuery.isEmpty { path += "?\(normalizedQuery)" }

        let body = operation.hasBody ? requestBody : nil
        if let body, !body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            _ = try JSONSerialization.jsonObject(with: Data(body.utf8), options: [.fragmentsAllowed])
        }

        let response: String
        if demoMode {
            response = try OfficialResourceDemo.response(for: operation, path: path)
        } else {
            response = try await requireAPI().executeOfficial(
                method: operation.method,
                relativePath: path,
                requestJSON: body
            )
        }
        if operation.isWrite { BackupService.log("official visual action \(operation.method) \(operation.id)") }
        status = "\(operation.title)执行成功"
        return response
    }

    private func refreshAllBody() async {
        guard !demoMode else { writeReady = true; status = "演示数据已刷新"; return }
        guard let api else { writeReady = false; status = "尚未连接 UniFi Console"; return }
        do {
            let core = try await fetchCorePolicies()
            apply(core)
            references = await api.listReferences()
            writeReady = true
            if loadedBundle != nil { rebuildChangePlan() }
            status = "已读取 DNS \(dnsRecords.count) 条、ACL \(aclRules.count) 条、防火墙 \(firewallRules.count) 条"
        } catch {
            writeReady = false
            errorMessage = error.localizedDescription
            status = "策略读取不完整，写入已禁用"
        }
    }

    func saveDNS(_ record: DNSRecord) {
        Task {
            await perform(record.id == nil ? "正在新增 DNS 记录…" : "正在更新 DNS 记录…") {
                try await self.backup(reason: record.id == nil ? "before-create-dns" : "before-update-dns")
                if self.demoMode {
                    var saved = record
                    if saved.id == nil { saved.id = UUID().uuidString; self.dnsRecords.append(saved) }
                    else if let index = self.dnsRecords.firstIndex(where: { $0.id == saved.id }) { self.dnsRecords[index] = saved }
                } else if let api = self.api {
                    if record.id == nil { _ = try await api.createDNS(record) } else { _ = try await api.updateDNS(record) }
                    await self.refreshAllBody()
                }
                BackupService.log(record.id == nil ? "create dns \(record.key)" : "update dns \(record.key)")
                self.status = record.id == nil ? "DNS 记录已新增" : "DNS 记录已更新"
            }
        }
    }

    func deleteDNS(_ record: DNSRecord) {
        Task {
            await perform("正在删除 DNS 记录…") {
                try await self.backup(reason: "before-delete-dns")
                if self.demoMode { self.dnsRecords.removeAll { $0.stableID == record.stableID } }
                else if let api = self.api { try await api.deleteDNS(record); await self.refreshAllBody() }
                BackupService.log("delete dns \(record.key)")
                self.status = "DNS 记录已删除"
            }
        }
    }

    func toggleDNS(_ record: DNSRecord) { var updated = record; updated.enabled.toggle(); saveDNS(updated) }

    func loadBundledDNSRules() {
        do {
            guard !batchDNSServer.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw UniFiError.api("请先填写转发域默认 DNS 服务器。")
            }
            showImportedRules(try DNSImportService.loadBundled(defaultDNSServer: batchDNSServer), source: "应用内置规则")
        } catch { errorMessage = error.localizedDescription }
    }

    func chooseDNSImportFile() {
        let panel = NSOpenPanel()
        var contentTypes: [UTType] = [.plainText, .commaSeparatedText]
        if let listType = UTType(filenameExtension: "list") { contentTypes.append(listType) }
        if let xlsxType = UTType(filenameExtension: "xlsx") { contentTypes.append(xlsxType) }
        panel.allowedContentTypes = contentTypes
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            showImportedRules(try DNSImportService.importFile(url, defaultDNSServer: batchDNSServer), source: url.lastPathComponent)
        } catch { errorMessage = error.localizedDescription }
    }

    func saveDNSTemplate() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.commaSeparatedText]
        panel.nameFieldStringValue = "unifi-dns-rules-template.csv"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try ("\u{feff}" + DNSImportService.csvTemplate).write(to: url, atomically: true, encoding: .utf8)
            status = "CSV 模板已保存到 \(url.lastPathComponent)"
        } catch { errorMessage = error.localizedDescription }
    }

    func previewDNSBatchAdd() {
        do {
            let result = try DNSImportService.parseText(batchEditorText, defaultDNSServer: batchDNSServer)
            let existingKeys = Set(dnsRecords.map(DNSImportService.identity))
            let existing = result.records.filter { existingKeys.contains(DNSImportService.identity($0)) }
            let pending = result.records.filter { !existingKeys.contains(DNSImportService.identity($0)) }
            dnsBatchPreview = DNSBatchPreview(pending: pending, existing: existing, duplicateInput: result.duplicateInput, invalid: result.invalid)
        } catch { errorMessage = error.localizedDescription }
    }

    func applyDNSBatchAdd(_ preview: DNSBatchPreview) {
        dnsBatchPreview = nil
        Task {
            await perform("正在批量新增 DNS 规则…") {
                guard !preview.pending.isEmpty else { self.status = "没有需要新增的 DNS 规则"; return }
                try await self.backup(reason: "before-dns-batch-add")
                var created = 0
                var failures: [String] = []
                var currentKeys = Set(self.dnsRecords.map(DNSImportService.identity))
                for source in preview.pending {
                    do {
                        let record = try UniFiPayloadValidator.normalizeDNS(source)
                        let key = DNSImportService.identity(record)
                        if currentKeys.contains(key) { continue }
                        if self.demoMode {
                            var saved = record
                            saved.id = UUID().uuidString
                            self.dnsRecords.append(saved)
                        } else if let api = self.api { _ = try await api.createDNS(record) }
                        currentKeys.insert(key)
                        created += 1
                    } catch { failures.append("\(DNSImportService.describe(source))：\(error.localizedDescription)") }
                }
                if !self.demoMode { await self.refreshAllBody() }
                BackupService.log("batch create dns \(created), failed \(failures.count)")
                self.status = "批量新增完成：成功 \(created) 条，失败 \(failures.count) 条"
                if !failures.isEmpty { self.errorMessage = "部分规则新增失败：\n" + failures.prefix(20).joined(separator: "\n") }
            }
        }
    }

    func batchDeleteForwardDomains(_ records: [DNSRecord]) {
        let selected = records.filter(\.isForwardDomain)
        guard !selected.isEmpty else { return }
        Task {
            await perform("正在批量删除转发域名…") {
                try await self.backup(reason: "before-dns-batch-delete")
                var deleted = 0
                var failures: [String] = []
                for record in selected {
                    do {
                        if self.demoMode { self.dnsRecords.removeAll { $0.stableID == record.stableID } }
                        else if let api = self.api { try await api.deleteDNS(record) }
                        deleted += 1
                    } catch { failures.append("\(record.key)：\(error.localizedDescription)") }
                }
                if !self.demoMode { await self.refreshAllBody() }
                BackupService.log("batch delete forward domains \(deleted), failed \(failures.count)")
                self.status = "批量删除完成：成功 \(deleted) 条，失败 \(failures.count) 条"
                if !failures.isEmpty { self.errorMessage = "部分转发域删除失败：\n" + failures.prefix(20).joined(separator: "\n") }
            }
        }
    }

    private func showImportedRules(_ result: DNSImportResult, source: String) {
        batchEditorText = DNSImportService.formatRecords(result.records)
        let types = Dictionary(grouping: result.records, by: \.recordType).map { "\($0.key) \($0.value.count)" }.sorted().joined(separator: "、")
        importSummary = "\(source)：有效 \(result.records.count) 条；文件内重复 \(result.duplicateInput.count) 条；无效 \(result.invalid.count) 条。\(types.isEmpty ? "" : " 类型：\(types)。")"
        if !result.invalid.isEmpty { errorMessage = "发现无效行：\n" + result.invalid.prefix(20).joined(separator: "\n") }
    }

    func savePolicy(kind: PolicyKind, existing: PolicyRule?, json: String) {
        Task {
            await perform(existing == nil ? "正在新增策略…" : "正在更新策略…") {
                try await self.backup(reason: existing == nil ? "before-create-policy" : "before-update-policy")
                if self.demoMode {
                    let object = try JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any] ?? [:]
                    let raw = String(decoding: try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]), as: UTF8.self)
                    let rule = PolicyRule(
                        id: existing?.id ?? UUID().uuidString, kind: kind, name: object["name"] as? String ?? "未命名",
                        enabled: object["enabled"] as? Bool ?? false, index: existing?.index ?? 999,
                        type: object["type"] as? String ?? "IPV4", action: (object["action"] as? String) ?? ((object["action"] as? [String: Any])?["type"] as? String ?? "BLOCK"),
                        origin: "USER_DEFINED", description: object["description"] as? String ?? "", rawJSON: raw
                    )
                    if kind == .acl { self.upsert(rule, in: &self.aclRules) } else { self.upsert(rule, in: &self.firewallRules) }
                } else if let api = self.api {
                    if let existing { _ = try await api.updatePolicy(existing, json: json) } else { _ = try await api.createPolicy(kind, json: json) }
                    await self.refreshAllBody()
                }
                BackupService.log("save \(kind.rawValue) policy")
                self.status = existing == nil ? "策略已新增" : "策略已更新"
            }
        }
    }

    func deletePolicy(_ rule: PolicyRule) {
        Task {
            await perform("正在删除策略…") {
                try await self.backup(reason: "before-delete-policy")
                if self.demoMode {
                    if rule.kind == .acl { self.aclRules.removeAll { $0.id == rule.id } } else { self.firewallRules.removeAll { $0.id == rule.id } }
                } else if let api = self.api { try await api.deletePolicy(rule); await self.refreshAllBody() }
                BackupService.log("delete \(rule.kind.rawValue) policy \(rule.name)")
                self.status = "策略已删除"
            }
        }
    }

    func togglePolicy(_ rule: PolicyRule) {
        guard rule.canModify,
              var object = try? JSONSerialization.jsonObject(with: Data(rule.editableJSON.utf8)) as? [String: Any] else { return }
        object["enabled"] = !rule.enabled
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]) else { return }
        savePolicy(kind: rule.kind, existing: rule, json: String(decoding: data, as: UTF8.self))
    }

    func movePolicy(_ rule: PolicyRule, direction: Int) {
        guard rule.canModify else { return }
        Task {
            await perform(direction < 0 ? "正在上移策略…" : "正在下移策略…") {
                try await self.backup(reason: "before-policy-reorder")
                if self.demoMode {
                    var list = rule.kind == .acl ? self.aclRules : self.firewallRules
                    guard let index = list.firstIndex(where: { $0.id == rule.id }) else { return }
                    let target = index + direction
                    if list.indices.contains(target), list[target].canModify { list.swapAt(index, target) }
                    if rule.kind == .acl { self.aclRules = list } else { self.firewallRules = list }
                } else if let api = self.api {
                    try await api.movePolicy(rule, direction: direction)
                    await self.refreshAllBody()
                }
                BackupService.log("move \(rule.kind.rawValue) policy \(rule.name) \(direction)")
                self.status = direction < 0 ? "策略已上移" : "策略已下移"
            }
        }
    }

    func exportBaseline() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "unifi-policy-baseline-\(dateStamp()).json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try BackupService.encoder.encode(bundle()).write(to: url, options: .atomic)
            status = "基线已导出到 \(url.lastPathComponent)"
        } catch { errorMessage = error.localizedDescription }
    }

    func revealBackups() {
        try? FileManager.default.createDirectory(at: BackupService.root.appendingPathComponent("backups"), withIntermediateDirectories: true)
        NSWorkspace.shared.activateFileViewerSelecting([BackupService.root.appendingPathComponent("backups")])
    }

    func loadPolicyBaseline() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            loadedBundle = try PolicyBundleCodec.load(url)
            loadedBundleURL = url
            rebuildChangePlan()
            selectedPage = .changes
            status = "已加载策略基线 \(url.lastPathComponent)"
        } catch { errorMessage = error.localizedDescription }
    }

    func loadLastBackupAsPlan() {
        guard let url = lastBackupURL else { errorMessage = "当前会话还没有写入前快照。"; return }
        do {
            loadedBundle = try PolicyBundleCodec.load(url)
            loadedBundleURL = url
            synchronizeDeletes = true
            rebuildChangePlan()
            selectedPage = .changes
            status = "已加载上次写入前快照"
        } catch { errorMessage = error.localizedDescription }
    }

    func setSynchronizeDeletes(_ enabled: Bool) {
        synchronizeDeletes = enabled
        rebuildChangePlan()
    }

    func rebuildChangePlan() {
        guard let bundle = loadedBundle, let url = loadedBundleURL else { changePlan = nil; return }
        changePlan = PolicyChangeService.buildPlan(
            bundle: bundle, sourceURL: url, currentDNS: dnsRecords, currentACL: aclRules,
            currentFirewall: firewallRules, synchronizeDeletes: synchronizeDeletes
        )
    }

    func togglePlanItem(_ id: UUID, selected: Bool) {
        guard var plan = changePlan, let index = plan.items.firstIndex(where: { $0.id == id }), plan.items[index].action.actionable else { return }
        plan.items[index].isSelected = selected
        changePlan = plan
    }

    func selectSafePlanItems() { selectPlanItems { $0.action == .add || $0.action == .update } }
    func selectAllPlanItems() { selectPlanItems { $0.action.actionable } }
    func clearPlanSelection() { selectPlanItems { _ in false } }

    private func selectPlanItems(_ predicate: (PolicyChangeItem) -> Bool) {
        guard var plan = changePlan else { return }
        for index in plan.items.indices { plan.items[index].isSelected = predicate(plan.items[index]) }
        changePlan = plan
    }

    func executeSelectedPlanChanges() {
        Task {
            await perform("正在执行策略变更计划…") {
                guard var plan = self.changePlan else { throw UniFiError.api("请先加载策略基线。") }
                let selected = plan.items.indices.filter { plan.items[$0].isSelected && plan.items[$0].action.actionable }
                guard !selected.isEmpty else { throw UniFiError.api("请至少选择一项可执行变更。") }
                try await self.backup(reason: "before-change-plan")
                var failures: [String] = []
                for index in selected.sorted(by: { self.changeExecutionOrder(plan.items[$0]) < self.changeExecutionOrder(plan.items[$1]) }) {
                    do {
                        try await self.executePlanItem(&plan.items[index])
                        plan.items[index].status = "已完成"
                    } catch {
                        plan.items[index].status = "失败：\(error.localizedDescription)"
                        failures.append("\(plan.items[index].scope.label) / \(plan.items[index].name)：\(error.localizedDescription)")
                    }
                    self.changePlan = plan
                }
                if !self.demoMode { await self.refreshAllBody() }
                self.resolveActualIDs(&plan)
                if failures.isEmpty && plan.synchronizeDeletes { try await self.restoreOrdering(for: &plan) }
                self.changePlan = plan
                BackupService.log("execute change plan selected \(selected.count), failed \(failures.count)")
                self.status = "变更计划完成：成功 \(selected.count - failures.count) 项，失败 \(failures.count) 项"
                if !failures.isEmpty { self.errorMessage = "部分变更失败：\n" + failures.prefix(20).joined(separator: "\n") }
            }
        }
    }

    func applyLoadedOrdering() {
        Task {
            await perform("正在恢复基线策略排序…") {
                guard var plan = self.changePlan, plan.synchronizeDeletes else { throw UniFiError.api("请先加载基线并开启严格同步。") }
                try await self.backup(reason: "before-order-restore")
                self.resolveActualIDs(&plan)
                try await self.restoreOrdering(for: &plan)
                self.changePlan = plan
                if !self.demoMode { await self.refreshAllBody() }
                self.status = "基线策略排序已恢复"
            }
        }
    }

    private func executePlanItem(_ item: inout PolicyChangeItem) async throws {
        if item.scope == .dns {
            switch item.action {
            case .add:
                guard var record = item.desiredDNS else { throw UniFiError.api("缺少目标 DNS 记录。") }
                if demoMode { record.id = UUID().uuidString; dnsRecords.append(record); item.actualID = record.id }
                else if let api { item.actualID = try await api.createDNS(record)?.id }
            case .update:
                guard var record = item.desiredDNS, let id = item.currentID else { throw UniFiError.api("DNS 记录 ID 或目标内容缺失。") }
                record.id = id
                if demoMode, let index = dnsRecords.firstIndex(where: { $0.id == id }) { dnsRecords[index] = record }
                else if let api { _ = try await api.updateDNS(record) }
                item.actualID = id
            case .delete:
                guard let id = item.currentID else { throw UniFiError.api("DNS 记录 ID 缺失。") }
                if demoMode { dnsRecords.removeAll { $0.id == id } }
                else if let api, let record = dnsRecords.first(where: { $0.id == id }) { try await api.deleteDNS(record) }
                else { throw UniFiError.api("找不到要删除的 DNS 记录。") }
                item.actualID = nil
            default: break
            }
            return
        }

        let kind: PolicyKind = item.scope == .acl ? .acl : .firewall
        switch item.action {
        case .add:
            guard let json = item.desiredPolicyJSON else { throw UniFiError.api("缺少目标策略 JSON。") }
            if demoMode {
                let object = try JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any] ?? [:]
                let id = UUID().uuidString
                let rawObject = object.merging(["id": id, "index": item.desiredIndex, "metadata": ["origin": "USER_DEFINED"]]) { current, _ in current }
                let raw = String(decoding: try JSONSerialization.data(withJSONObject: rawObject, options: [.prettyPrinted, .sortedKeys]), as: UTF8.self)
                let rule = PolicyRule(
                    id: id, kind: kind, name: object["name"] as? String ?? item.name,
                    enabled: object["enabled"] as? Bool ?? false, index: item.desiredIndex,
                    type: object["type"] as? String ?? ((object["ipProtocolScope"] as? [String: Any])?["ipVersion"] as? String ?? "IPV4"),
                    action: object["action"] as? String ?? ((object["action"] as? [String: Any])?["type"] as? String ?? "BLOCK"),
                    origin: "USER_DEFINED", description: object["description"] as? String ?? "", rawJSON: raw
                )
                if kind == .acl { aclRules.append(rule) } else { firewallRules.append(rule) }
                item.actualID = id
            } else if let api { item.actualID = try await api.createPolicy(kind, json: json)?.id }
        case .update:
            guard let id = item.currentID, let json = item.desiredPolicyJSON else { throw UniFiError.api("策略 ID 或目标 JSON 缺失。") }
            let list = kind == .acl ? aclRules : firewallRules
            guard let rule = list.first(where: { $0.id == id }) else { throw UniFiError.api("找不到要更新的策略。") }
            if demoMode {
                let object = try JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any] ?? [:]
                let rawObject = object.merging(["id": id, "index": rule.index, "metadata": ["origin": "USER_DEFINED"]]) { current, _ in current }
                let raw = String(decoding: try JSONSerialization.data(withJSONObject: rawObject, options: [.prettyPrinted, .sortedKeys]), as: UTF8.self)
                let updated = PolicyRule(id: id, kind: kind, name: object["name"] as? String ?? rule.name, enabled: object["enabled"] as? Bool ?? false, index: rule.index, type: object["type"] as? String ?? rule.type, action: object["action"] as? String ?? ((object["action"] as? [String: Any])?["type"] as? String ?? rule.action), origin: "USER_DEFINED", description: object["description"] as? String ?? "", rawJSON: raw)
                if kind == .acl { upsert(updated, in: &aclRules) } else { upsert(updated, in: &firewallRules) }
            } else if let api { _ = try await api.updatePolicy(rule, json: json) }
            item.actualID = id
        case .delete:
            guard let id = item.currentID else { throw UniFiError.api("策略 ID 缺失。") }
            let list = kind == .acl ? aclRules : firewallRules
            guard let rule = list.first(where: { $0.id == id && $0.canModify }) else { throw UniFiError.api("找不到可删除的用户策略。") }
            if demoMode {
                if kind == .acl { aclRules.removeAll { $0.id == id } } else { firewallRules.removeAll { $0.id == id } }
            } else if let api { try await api.deletePolicy(rule) }
            item.actualID = nil
        default: break
        }
    }

    private func restoreOrdering(for plan: inout PolicyChangePlan) async throws {
        var idMap: [String: String] = [:]
        for item in plan.items {
            guard (item.scope == .acl || item.scope == .firewall), let source = item.desiredSourceID, let actual = item.actualID else { continue }
            idMap[source.lowercased()] = actual
        }
        if !plan.items.contains(where: { $0.scope == .acl && $0.action == .invalid }),
           let source = plan.bundle.aclOrdering, !source.orderedACLRuleIDs.isEmpty {
            var current = demoMode ? (aclOrdering ?? PolicyOrderingSnapshot(kind: .acl, orderedACLRuleIDs: aclRules.filter(\.canModify).map(\.id))) : try await requireAPI().getPolicyOrdering(.acl)
            var target = source.orderedACLRuleIDs.compactMap { idMap[$0.lowercased()] }
            target.append(contentsOf: current.orderedACLRuleIDs.filter { !target.containsCaseInsensitive($0) })
            current.orderedACLRuleIDs = target
            if demoMode { aclOrdering = current } else { try await requireAPI().setPolicyOrdering(current) }
        }
        if !plan.items.contains(where: { $0.scope == .firewall && $0.action == .invalid }),
           let source = plan.bundle.firewallOrdering,
           !source.beforeSystemDefined.isEmpty || !source.afterSystemDefined.isEmpty {
            var current = demoMode ? (firewallOrdering ?? PolicyOrderingSnapshot(kind: .firewall, beforeSystemDefined: firewallRules.filter(\.canModify).map(\.id))) : try await requireAPI().getPolicyOrdering(.firewall)
            var before = source.beforeSystemDefined.compactMap { idMap[$0.lowercased()] }
            var after = source.afterSystemDefined.compactMap { idMap[$0.lowercased()] }
            var mapped = Set((before + after).map { $0.lowercased() })
            before.append(contentsOf: current.beforeSystemDefined.filter { mapped.insert($0.lowercased()).inserted })
            after.append(contentsOf: current.afterSystemDefined.filter { mapped.insert($0.lowercased()).inserted })
            current.beforeSystemDefined = before
            current.afterSystemDefined = after
            if demoMode { firewallOrdering = current } else { try await requireAPI().setPolicyOrdering(current) }
        }
    }

    private func resolveActualIDs(_ plan: inout PolicyChangePlan) {
        for index in plan.items.indices where plan.items[index].action != .delete && plan.items[index].actualID == nil {
            switch plan.items[index].scope {
            case .dns:
                if let desired = plan.items[index].desiredDNS {
                    plan.items[index].actualID = dnsRecords.first(where: { DNSImportService.identity($0) == DNSImportService.identity(desired) })?.id
                }
            case .acl:
                plan.items[index].actualID = aclRules.first(where: { $0.name.caseInsensitiveCompare(plan.items[index].name) == .orderedSame })?.id
            case .firewall:
                plan.items[index].actualID = firewallRules.first(where: { $0.name.caseInsensitiveCompare(plan.items[index].name) == .orderedSame })?.id
            }
        }
    }

    private func changeExecutionOrder(_ item: PolicyChangeItem) -> Int {
        switch item.action { case .add: return 0; case .update: return 1; case .delete: return 2; default: return 9 }
    }

    private func requireAPI() throws -> UniFiAPI {
        guard let api else { throw UniFiError.api("尚未连接 UniFi Console。") }
        return api
    }

    private func fetchCorePolicies() async throws -> CorePolicyState {
        guard let api else { throw UniFiError.api("尚未连接 UniFi Console。") }
        async let dnsTask = api.listDNSRecords()
        async let aclTask = api.listPolicies(.acl)
        async let firewallTask = api.listPolicies(.firewall)
        let (dns, acl, firewall) = try await (dnsTask, aclTask, firewallTask)
        async let aclOrderingTask: PolicyOrderingSnapshot? = try? api.getPolicyOrdering(.acl)
        async let firewallOrderingTask: PolicyOrderingSnapshot? = try? api.getPolicyOrdering(.firewall)
        let (aclOrder, firewallOrder) = await (aclOrderingTask, firewallOrderingTask)
        return CorePolicyState(
            dns: dns.sorted { $0.key.localizedCaseInsensitiveCompare($1.key) == .orderedAscending },
            acl: acl,
            firewall: firewall,
            aclOrdering: aclOrder,
            firewallOrdering: firewallOrder
        )
    }

    private func apply(_ core: CorePolicyState) {
        dnsRecords = core.dns
        aclRules = core.acl
        firewallRules = core.firewall
        aclOrdering = core.aclOrdering
        firewallOrdering = core.firewallOrdering
        if batchDNSServer.isEmpty, let server = core.dns.first(where: \.isForwardDomain)?.value { batchDNSServer = server }
    }

    private func backup(reason: String) async throws {
        if demoMode {
            lastBackupURL = try BackupService.saveSnapshot(reason: reason, bundle: bundle())
            return
        }
        do {
            let core = try await fetchCorePolicies()
            apply(core)
            writeReady = true
            lastBackupURL = try BackupService.saveSnapshot(reason: reason, bundle: bundle(core: core))
        } catch {
            writeReady = false
            throw UniFiError.api("无法读取 DNS、ACL 和防火墙的完整实时基线，写入已取消：\(error.localizedDescription)")
        }
    }

    private func bundle(core: CorePolicyState? = nil) -> PolicyBundle {
        let currentDNS = core?.dns ?? dnsRecords
        let currentACL = core?.acl ?? aclRules
        let currentFirewall = core?.firewall ?? firewallRules
        return PolicyBundle(
            schemaVersion: 2, createdAt: Date(), target: targetLabel, site: selectedSite?.displayName ?? "",
            siteID: selectedSite?.id ?? "", networkVersion: versionLabel, dnsRecords: currentDNS,
            aclRules: currentACL.map(jsonValue), firewallPolicies: currentFirewall.map(jsonValue),
            aclOrdering: core?.aclOrdering ?? aclOrdering,
            firewallOrdering: core?.firewallOrdering ?? firewallOrdering
        )
    }

    private func jsonValue(_ rule: PolicyRule) -> JSONValue {
        guard let object = try? JSONSerialization.jsonObject(with: Data(rule.rawJSON.utf8)) else { return .object([:]) }
        return .from(object)
    }

    private func upsert(_ rule: PolicyRule, in array: inout [PolicyRule]) {
        if let index = array.firstIndex(where: { $0.id == rule.id }) { array[index] = rule } else { array.append(rule) }
    }

    private func perform(_ message: String, operation: @escaping () async throws -> Void) async {
        guard !busy else { return }
        busy = true
        status = message
        do { try await operation() } catch { errorMessage = error.localizedDescription; status = "操作失败" }
        busy = false
    }

    private func dateStamp() -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        return formatter.string(from: Date())
    }
}

private extension Array where Element == String {
    func containsCaseInsensitive(_ value: String) -> Bool {
        contains { $0.caseInsensitiveCompare(value) == .orderedSame }
    }
}

enum DemoData {
    static let dns = [
        DNSRecord(id: UUID().uuidString, recordType: "NS", key: "openai.com", value: "192.168.1.10"),
        DNSRecord(id: UUID().uuidString, recordType: "A", key: "nas.home.arpa", value: "192.168.1.20", ttl: 300),
        DNSRecord(id: UUID().uuidString, recordType: "CNAME", key: "media.home.arpa", value: "nas.home.arpa", ttl: 300),
        DNSRecord(id: UUID().uuidString, recordType: "TXT", key: "_policy.home.arpa", value: "managed-by=unifi-policy-manager")
    ]
    static let acl = [
        policy(.acl, "允许管理网段", true, 1, "IPV4", "ALLOW", "USER_DEFINED"),
        policy(.acl, "访客隔离", true, 2, "IPV4", "BLOCK", "USER_DEFINED"),
        policy(.acl, "System default", true, 3, "IPV4", "ALLOW", "SYSTEM_DEFINED")
    ]
    static let firewall = [
        policy(.firewall, "允许 DNS 到基础设施", true, 10, "IPV4", "ALLOW", "USER_DEFINED"),
        policy(.firewall, "阻止 IoT 访问管理网", true, 20, "IPV4_AND_IPV6", "BLOCK", "USER_DEFINED")
    ]
    static let references = [
        PolicyReference(id: UUID().uuidString, name: "Default", kind: "网络"),
        PolicyReference(id: UUID().uuidString, name: "Internal", kind: "防火墙区域")
    ]

    private static func policy(_ kind: PolicyKind, _ name: String, _ enabled: Bool, _ index: Int, _ type: String, _ action: String, _ origin: String) -> PolicyRule {
        let id = UUID().uuidString
        let object: [String: Any] = kind == .acl
            ? ["id": id, "name": name, "enabled": enabled, "type": type, "action": action, "description": "", "metadata": ["origin": origin]]
            : ["id": id, "name": name, "enabled": enabled, "loggingEnabled": false, "description": "", "action": ["type": action], "source": ["zoneId": UUID().uuidString], "destination": ["zoneId": UUID().uuidString], "ipProtocolScope": ["ipVersion": type], "metadata": ["origin": origin]]
        let raw = String(decoding: try! JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]), as: UTF8.self)
        return PolicyRule(id: id, kind: kind, name: name, enabled: enabled, index: index, type: type, action: action, origin: origin, description: "", rawJSON: raw)
    }
}
