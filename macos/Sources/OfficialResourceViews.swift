import AppKit
import Foundation
import SwiftUI

struct OfficialResourceField: Identifiable, Hashable {
    let label: String
    let value: String
    var id: String { "\(label)|\(value)" }
}

struct OfficialResourceItem: Identifiable, Hashable {
    let id: String
    let name: String
    let subtitle: String
    let state: String
    let type: String
    let address: String
    let detail: String
    let symbol: String
    let isHealthy: Bool
    let needsAttention: Bool
    let fields: [OfficialResourceField]
}

struct OfficialResourceDistribution: Identifiable, Hashable {
    let label: String
    let count: Int
    let percentage: Double
    var id: String { label }
}

struct OfficialResourceSnapshot {
    let items: [OfficialResourceItem]
    let totalCount: Int
    let healthyCount: Int
    let attentionCount: Int
    let distributions: [OfficialResourceDistribution]

    static let empty = OfficialResourceSnapshot(items: [], totalCount: 0, healthyCount: 0, attentionCount: 0, distributions: [])
    var unknownCount: Int { max(0, totalCount - healthyCount - attentionCount) }
}

enum OfficialResourcePresenter {
    static func parse(_ json: String, moduleID: String) throws -> OfficialResourceSnapshot {
        let object = try JSONSerialization.jsonObject(with: Data(json.utf8), options: [.fragmentsAllowed])
        let dictionaries = extractDictionaries(object)
        let items = dictionaries.enumerated().map { makeItem($0.element, moduleID: moduleID, index: $0.offset) }
        let root = object as? [String: Any]
        let total = (root?["totalCount"] as? NSNumber)?.intValue
            ?? (root?["count"] as? NSNumber)?.intValue
            ?? items.count
        let hasExplicitState = items.contains { $0.state != "未知" }
        let healthy = hasExplicitState ? items.filter(\.isHealthy).count : items.count
        let attention = items.filter(\.needsAttention).count
        let resourcesByType = Dictionary(grouping: items) { item in item.type }
        var grouped: [(label: String, count: Int)] = resourcesByType.map { key, value in
            (label: key.isEmpty ? "其他" : key, count: value.count)
        }
        grouped.sort { lhs, rhs in
            if lhs.count != rhs.count { return lhs.count > rhs.count }
            return lhs.label.localizedCaseInsensitiveCompare(rhs.label) == .orderedAscending
        }
        let distributions = grouped.prefix(6).map {
            OfficialResourceDistribution(label: $0.label, count: $0.count, percentage: items.isEmpty ? 0 : Double($0.count) / Double(items.count))
        }
        return OfficialResourceSnapshot(
            items: items,
            totalCount: max(total, items.count),
            healthyCount: healthy,
            attentionCount: attention,
            distributions: distributions
        )
    }

    private static func extractDictionaries(_ object: Any) -> [[String: Any]] {
        if let array = object as? [[String: Any]] { return array }
        guard let dictionary = object as? [String: Any] else { return [] }
        for key in ["data", "items", "results", "devices", "clients", "networks", "broadcasts", "vouchers", "policies", "zones"] {
            if let array = dictionary[key] as? [[String: Any]] { return array }
        }
        if let array = dictionary.values.compactMap({ $0 as? [[String: Any]] }).first { return array }
        return [dictionary]
    }

    private static func makeItem(_ dictionary: [String: Any], moduleID: String, index: Int) -> OfficialResourceItem {
        var flattened: [String: String] = [:]
        flatten(dictionary, prefix: "", output: &flattened, depth: 0)
        let id = candidate(flattened, keys: ["id", "deviceId", "clientId", "networkId", "wifiBroadcastId", "voucherId", "macAddress"])
            ?? "resource-\(index + 1)"
        let name = candidate(flattened, keys: ["name", "displayName", "hostname", "deviceName", "ssid", "code", "internalReference"]) ?? id
        let rawState = candidate(flattened, keys: ["status", "state", "connectionState", "adoptionState", "enabled"])
        let state = friendlyState(rawState)
        let type = candidate(flattened, keys: ["modelName", "model", "deviceType", "type", "management", "securityConfiguration.type"]) ?? "其他"
        let address = candidate(flattened, keys: ["ipAddress", "ipv4Address", "ip", "gatewayIpAddress", "macAddress", "vlanId"]) ?? "—"
        let detail = candidate(flattened, keys: ["description", "firmwareVersion", "version", "manufacturerName", "internalReference"]) ?? "—"
        let subtitle = candidate(flattened, keys: ["location", "siteName", "manufacturerName", "internalReference", "macAddress"]) ?? type
        let normalized = rawState?.uppercased() ?? ""
        let attention = ["OFFLINE", "DISABLED", "FAILED", "ERROR", "PENDING", "UPDATING", "PROVISIONING", "FALSE"].contains { normalized.contains($0) }
        let fields = flattened
            .filter { !$0.value.isEmpty }
            .sorted { fieldPriority($0.key) == fieldPriority($1.key) ? $0.key < $1.key : fieldPriority($0.key) < fieldPriority($1.key) }
            .prefix(18)
            .map { OfficialResourceField(label: friendlyLabel($0.key), value: $0.value) }
        return OfficialResourceItem(
            id: id, name: name, subtitle: subtitle, state: state, type: type, address: address, detail: detail,
            symbol: symbol(moduleID: moduleID, type: type), isHealthy: !attention && !normalized.isEmpty,
            needsAttention: attention, fields: Array(fields)
        )
    }

    private static func flatten(_ dictionary: [String: Any], prefix: String, output: inout [String: String], depth: Int) {
        guard depth <= 3 else { return }
        for (key, value) in dictionary {
            let path = prefix.isEmpty ? key : "\(prefix).\(key)"
            if let child = value as? [String: Any] {
                flatten(child, prefix: path, output: &output, depth: depth + 1)
            } else if let array = value as? [Any] {
                output[path] = array.prefix(6).map(displayValue).joined(separator: ", ")
            } else {
                output[path] = displayValue(value)
            }
        }
    }

    private static func displayValue(_ value: Any) -> String {
        if let boolean = value as? Bool { return boolean ? "true" : "false" }
        if let string = value as? String { return string }
        if let number = value as? NSNumber { return number.stringValue }
        if let dictionary = value as? [String: Any], let name = dictionary["name"] { return displayValue(name) }
        return String(describing: value)
    }

    private static func candidate(_ values: [String: String], keys: [String]) -> String? {
        for key in keys {
            if let direct = values.first(where: { $0.key.caseInsensitiveCompare(key) == .orderedSame })?.value, !direct.isEmpty { return direct }
            if let nested = values.first(where: { $0.key.lowercased().hasSuffix(".\(key.lowercased())") })?.value, !nested.isEmpty { return nested }
        }
        return nil
    }

    private static func friendlyState(_ value: String?) -> String {
        guard let value, !value.isEmpty else { return "未知" }
        switch value.uppercased() {
        case "TRUE", "ENABLED", "ACTIVE": return "已启用"
        case "FALSE", "DISABLED": return "已停用"
        case "ONLINE", "CONNECTED", "ADOPTED", "READY", "OK": return "在线"
        case "OFFLINE", "DISCONNECTED": return "离线"
        case "PENDING", "PENDING_ADOPTION": return "待处理"
        case "UPDATING", "PROVISIONING": return "处理中"
        default: return value.replacingOccurrences(of: "_", with: " ")
        }
    }

    private static func friendlyLabel(_ path: String) -> String {
        let known: [String: String] = [
            "id": "资源 ID", "name": "名称", "displayName": "显示名称", "hostname": "主机名", "status": "状态",
            "state": "状态", "enabled": "已启用", "type": "类型", "model": "型号", "modelName": "型号",
            "deviceType": "设备类型", "ipAddress": "IP 地址", "ipv4Address": "IPv4 地址", "macAddress": "MAC 地址",
            "vlanId": "VLAN ID", "firmwareVersion": "固件版本", "version": "版本", "description": "说明",
            "management": "管理方式", "internalReference": "内部标识"
        ]
        if let label = known[path] { return label }
        let leaf = path.split(separator: ".").last.map(String.init) ?? path
        if let label = known[leaf] { return label }
        return splitCamelCase(leaf)
    }

    private static func fieldPriority(_ path: String) -> Int {
        let leaf = path.split(separator: ".").last?.lowercased() ?? path.lowercased()
        if ["name", "displayname"].contains(leaf) { return 0 }
        if ["status", "state", "enabled"].contains(leaf) { return 1 }
        if ["type", "model", "modelname"].contains(leaf) { return 2 }
        if ["ipaddress", "ipv4address", "macaddress"].contains(leaf) { return 3 }
        if leaf == "id" { return 4 }
        return 10
    }

    private static func splitCamelCase(_ value: String) -> String {
        value.enumerated().reduce(into: "") { result, pair in
            if pair.offset > 0, pair.element.isUppercase { result.append(" ") }
            result.append(pair.element)
        }
    }

    private static func symbol(moduleID: String, type: String) -> String {
        switch moduleID {
        case "devices": return type.localizedCaseInsensitiveContains("AP") ? "wifi.router" : "externaldrive.connected.to.line.below"
        case "clients": return "laptopcomputer.and.iphone"
        case "networks": return "point.3.connected.trianglepath.dotted"
        case "wifi": return "wifi"
        case "hotspot": return "ticket"
        case "firewall": return "shield.lefthalf.filled"
        case "traffic": return "list.bullet.rectangle"
        case "switching": return "arrow.triangle.swap"
        case "resources": return "server.rack"
        case "application": return "info.circle"
        default: return "square.stack.3d.up"
        }
    }
}

enum OfficialResourceDemo {
    static func response(for operation: OfficialAPIOperation, path: String) throws -> String {
        let data: [[String: Any]]
        switch operation.moduleID {
        case "devices":
            data = [
                ["id": "demo-gateway", "name": "Cloud Gateway Fiber", "state": "ONLINE", "model": "UCG-Fiber", "ipAddress": "192.168.1.1", "firmwareVersion": "4.2.11"],
                ["id": "demo-switch", "name": "Core Switch", "state": "ONLINE", "model": "Pro Max 24", "ipAddress": "192.168.1.2"],
                ["id": "demo-ap-1", "name": "Living Room", "state": "ONLINE", "model": "U7 Pro", "ipAddress": "192.168.1.21"],
                ["id": "demo-ap-2", "name": "Office AP", "state": "UPDATING", "model": "U6 Plus", "ipAddress": "192.168.1.22"]
            ]
        case "clients":
            data = [
                ["id": "client-1", "name": "MacBook Pro", "state": "CONNECTED", "type": "WIRELESS", "ipAddress": "192.168.1.101"],
                ["id": "client-2", "name": "NAS", "state": "CONNECTED", "type": "WIRED", "ipAddress": "192.168.1.30"]
            ]
        case "networks":
            data = [["id": "network-1", "name": "Default", "enabled": true, "management": "GATEWAY", "vlanId": 1], ["id": "network-2", "name": "IoT", "enabled": true, "management": "GATEWAY", "vlanId": 20]]
        case "wifi":
            data = [["id": "wifi-1", "name": "Home WiFi", "enabled": true, "type": "STANDARD", "securityConfiguration": ["type": "WPA2_PERSONAL"]]]
        default:
            data = [["id": "demo-resource", "name": operation.title.replacingOccurrences(of: "列出", with: ""), "status": "ACTIVE", "type": operation.moduleID, "description": "演示资源"]]
        }
        let object: [String: Any] = operation.method == "GET"
            ? ["data": data, "count": data.count, "totalCount": data.count, "demo": true]
            : ["success": true, "operation": operation.title, "path": path, "demo": true]
        let encoded = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
        return String(decoding: encoded, as: UTF8.self)
    }
}

struct VisualResourceWorkspaceView: View {
    @EnvironmentObject private var model: AppModel
    let moduleID: String
    @State private var selectedListID = ""
    @State private var search = ""
    @State private var snapshot = OfficialResourceSnapshot.empty
    @State private var selectedItemID: String?
    @State private var inspectorItem: OfficialResourceItem?
    @State private var operationSheet: OfficialAPIOperation?
    @State private var operationItem: OfficialResourceItem?
    @State private var loading = false

    private var module: OfficialAPIModule? { OfficialAPICatalog.module(moduleID) }
    private var operations: [OfficialAPIOperation] { OfficialAPICatalog.operations(for: moduleID) }
    private var listOperations: [OfficialAPIOperation] {
        let result = operations.filter { operation in
            operation.method == "GET" && operation.pathParameters.isEmpty && operation.requiredQueryParameters.allSatisfy {
                queryValues(operation.defaultQuery)[$0]?.isEmpty == false
            }
        }
        return result.isEmpty ? Array(operations.filter { $0.method == "GET" }.prefix(1)) : result
    }
    private var primaryOperation: OfficialAPIOperation? {
        operations.first { $0.isWrite && $0.pathParameters.isEmpty && ($0.id.hasPrefix("create") || $0.id == "adoptDevice") }
    }
    private var otherOperations: [OfficialAPIOperation] {
        operations.filter { !listOperations.contains($0) && $0.id != primaryOperation?.id }
    }
    private var filteredItems: [OfficialResourceItem] {
        let term = search.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !term.isEmpty else { return snapshot.items }
        return snapshot.items.filter { item in
            [item.name, item.subtitle, item.state, item.type, item.address, item.detail, item.id].contains {
                $0.localizedCaseInsensitiveContains(term)
            }
        }
    }
    private var selectedOperation: OfficialAPIOperation? {
        listOperations.first { $0.id == selectedListID } ?? listOperations.first
    }

    var body: some View {
        VStack(spacing: 0) {
            resourceHeader
            Divider()
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 18) {
                    ForEach(listOperations) { operation in
                        Button {
                            selectedListID = operation.id
                            loadList()
                        } label: {
                            VStack(spacing: 8) {
                                Text(operation.title).fontWeight(selectedListID == operation.id ? .semibold : .regular)
                                Rectangle().fill(selectedListID == operation.id ? Color.accentColor : Color.clear).frame(height: 2)
                            }
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(selectedListID == operation.id ? Color.accentColor : Color.secondary)
                    }
                }
                .padding(.horizontal, 20)
            }
            .frame(height: 45)
            Divider()

            VStack(spacing: 12) {
                HStack(spacing: 10) {
                    ResourceMetricCard(title: "资源总数", value: snapshot.totalCount, note: "当前分类", symbol: moduleSymbol, tint: .blue)
                    ResourceMetricCard(title: "正常 / 可用", value: snapshot.healthyCount, note: availabilityText, symbol: "checkmark.circle", tint: .green)
                    ResourceMetricCard(title: "需要关注", value: snapshot.attentionCount, note: "离线、停用或待处理", symbol: "exclamationmark.triangle", tint: .orange)
                    ResourceMetricCard(title: "资源类型", value: snapshot.distributions.count, note: "来自实时返回数据", symbol: "square.grid.2x2", tint: .purple)
                }

                HStack(spacing: 10) {
                    ResourceHealthPanel(snapshot: snapshot)
                    ResourceTypePanel(distributions: snapshot.distributions)
                }
                .frame(height: 145)

                resourceTable
            }
            .padding(16)
        }
        .background(Color(nsColor: .controlBackgroundColor).opacity(0.26))
        .overlay { if loading { ProgressView("正在读取官方资源…").padding(22).background(.regularMaterial, in: RoundedRectangle(cornerRadius: 8)) } }
        .inspector(isPresented: Binding(get: { inspectorItem != nil }, set: { if !$0 { inspectorItem = nil } })) {
            if let inspectorItem { ResourceInspector(item: inspectorItem, detailOperation: detailOperation) { operation in prepare(operation, item: inspectorItem) } }
        }
        .sheet(item: $operationSheet) { operation in
            OfficialOperationFormView(operation: operation, selectedItem: operationItem) { parameters, query, body in
                execute(operation, item: operationItem, parameters: parameters, query: query, body: body)
            }
        }
        .onAppear { resetModule() }
        .onChange(of: moduleID) { _, _ in resetModule() }
        .onChange(of: selectedItemID) { _, id in
            inspectorItem = snapshot.items.first { $0.id == id }
        }
    }

    private var resourceHeader: some View {
        HStack(alignment: .center) {
            Label {
                VStack(alignment: .leading, spacing: 4) {
                    Text(module?.title ?? "官方资源").font(.system(size: 24, weight: .semibold))
                    Text(module?.description ?? "通过 UniFi 官方 Integration API 管理资源。")
                        .font(.callout).foregroundStyle(.secondary)
                }
            } icon: {
                Image(systemName: moduleSymbol).font(.title2).foregroundStyle(.tint)
            }
            Spacer()
            Button("刷新", systemImage: "arrow.clockwise") { loadList() }
            if let primaryOperation {
                Button(primaryOperation.title, systemImage: "plus") { prepare(primaryOperation, item: nil) }
                    .buttonStyle(.borderedProminent)
                    .disabled(primaryOperation.isWrite && !model.writeReady)
            }
            if !otherOperations.isEmpty {
                Menu("更多操作", systemImage: "ellipsis.circle") {
                    ForEach(otherOperations) { operation in
                        Button(operation.title) { prepare(operation, item: inspectorItem) }
                            .disabled(operation.isWrite && !model.writeReady)
                    }
                }
            }
        }
        .padding(20)
    }

    private var resourceTable: some View {
        VStack(spacing: 0) {
            HStack {
                TextField("搜索名称、状态、类型、地址或 ID", text: $search)
                    .textFieldStyle(.roundedBorder).frame(maxWidth: 340)
                Spacer()
                Text("显示 \(filteredItems.count) / \(snapshot.items.count) 条").font(.caption).foregroundStyle(.secondary)
            }
            .padding(11)
            Divider()
            Table(filteredItems, selection: $selectedItemID) {
                TableColumn("资源") { item in
                    HStack(spacing: 9) {
                        Image(systemName: item.symbol).foregroundStyle(.tint).frame(width: 25, height: 25).background(Color.accentColor.opacity(0.10), in: RoundedRectangle(cornerRadius: 6))
                        VStack(alignment: .leading, spacing: 2) { Text(item.name).fontWeight(.medium); Text(item.subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
                    }
                }.width(min: 190, ideal: 250)
                TableColumn("状态", value: \.state).width(min: 90, ideal: 110)
                TableColumn("类型 / 型号", value: \.type).width(min: 110, ideal: 150)
                TableColumn("地址 / 标识", value: \.address).width(min: 110, ideal: 150)
                TableColumn("说明", value: \.detail).width(min: 130, ideal: 180)
            }
            .overlay {
                if filteredItems.isEmpty {
                    ContentUnavailableView(search.isEmpty ? "暂无资源" : "没有匹配的资源", systemImage: moduleSymbol, description: Text(search.isEmpty ? "当前官方接口没有返回资源。" : "请调整搜索条件后重试。"))
                }
            }
        }
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
        .overlay { RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.10)) }
        .frame(minHeight: 260)
    }

    private var detailOperation: OfficialAPIOperation? {
        operations.first { $0.method == "GET" && !$0.pathParameters.isEmpty && ($0.id.localizedCaseInsensitiveContains("details") || $0.id.hasPrefix("get")) }
    }

    private var moduleSymbol: String {
        switch moduleID {
        case "devices": return "externaldrive.connected.to.line.below"
        case "clients": return "person.2"
        case "networks": return "point.3.connected.trianglepath.dotted"
        case "wifi": return "wifi"
        case "hotspot": return "ticket"
        case "firewall": return "shield.lefthalf.filled"
        case "traffic": return "list.bullet.rectangle"
        case "switching": return "arrow.triangle.swap"
        case "resources": return "server.rack"
        default: return "info.circle"
        }
    }

    private var availabilityText: String {
        snapshot.totalCount == 0 ? "0%" : "\(snapshot.healthyCount * 100 / max(1, snapshot.totalCount))% 可用"
    }

    private func resetModule() {
        selectedListID = listOperations.first?.id ?? ""
        search = ""
        snapshot = .empty
        selectedItemID = nil
        inspectorItem = nil
        loadList()
    }

    private func loadList() {
        guard let operation = selectedOperation else { return }
        Task { @MainActor in
            loading = true
            defer { loading = false }
            do {
                let response = try await model.requestOfficialResource(operation, query: operation.defaultQuery)
                snapshot = try OfficialResourcePresenter.parse(response, moduleID: moduleID)
                model.status = "已读取 \(snapshot.items.count) 条\(operation.title.replacingOccurrences(of: "列出", with: ""))"
            } catch {
                snapshot = .empty
                model.status = "读取失败：\(error.localizedDescription)"
                model.errorMessage = error.localizedDescription
            }
        }
    }

    private func prepare(_ operation: OfficialAPIOperation, item: OfficialResourceItem?) {
        if operation.method == "GET", !operation.hasBody, operation.defaultQuery.isEmpty,
           !operation.pathParameters.isEmpty, operation.pathParameters.allSatisfy({ $0.lowercased().hasSuffix("id") }), let item {
            let parameters = Dictionary(uniqueKeysWithValues: operation.pathParameters.map { ($0, item.id) })
            execute(operation, item: item, parameters: parameters, query: "", body: nil)
        } else {
            operationItem = item
            operationSheet = operation
        }
    }

    private func execute(_ operation: OfficialAPIOperation, item: OfficialResourceItem?, parameters: [String: String], query: String, body: String?) {
        Task { @MainActor in
            loading = true
            defer { loading = false }
            do {
                let response = try await model.requestOfficialResource(operation, parameters: parameters, query: query, requestBody: body)
                if operation.method == "GET" {
                    inspectorItem = try OfficialResourcePresenter.parse(response, moduleID: moduleID).items.first
                } else {
                    loadList()
                }
            } catch {
                model.status = "\(operation.title)失败：\(error.localizedDescription)"
                model.errorMessage = error.localizedDescription
            }
        }
    }

    private func queryValues(_ query: String) -> [String: String] {
        Dictionary(uniqueKeysWithValues: query.trimmingCharacters(in: CharacterSet(charactersIn: "?")).split(separator: "&").compactMap { part in
            let pair = part.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard pair.count == 2 else { return nil }
            return (String(pair[0]), String(pair[1]))
        })
    }
}

private struct ResourceMetricCard: View {
    let title: String
    let value: Int
    let note: String
    let symbol: String
    let tint: Color
    var body: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 5) {
                Text(title).font(.caption).foregroundStyle(.secondary)
                Text("\(value)").font(.title2.weight(.semibold))
                Text(note).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 8)
            Image(systemName: symbol).foregroundStyle(tint).frame(width: 30, height: 30).background(tint.opacity(0.11), in: RoundedRectangle(cornerRadius: 7))
        }
        .padding(13).frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
        .overlay { RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.10)) }
    }
}

private struct ResourceHealthPanel: View {
    let snapshot: OfficialResourceSnapshot
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack { Text("资源健康状态").fontWeight(.semibold); Spacer(); Text("实时快照").font(.caption).foregroundStyle(.secondary) }
            HealthRow(label: "正常", value: snapshot.healthyCount, total: snapshot.totalCount, tint: .green)
            HealthRow(label: "需要关注", value: snapshot.attentionCount, total: snapshot.totalCount, tint: .orange)
            HealthRow(label: "状态未知", value: snapshot.unknownCount, total: snapshot.totalCount, tint: .gray)
        }
        .padding(14).frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
        .overlay { RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.10)) }
    }
}

private struct HealthRow: View {
    let label: String
    let value: Int
    let total: Int
    let tint: Color
    var body: some View {
        HStack { Text(label).frame(width: 72, alignment: .leading); ProgressView(value: Double(value), total: Double(max(1, total))).tint(tint); Text("\(value)").monospacedDigit().frame(width: 28, alignment: .trailing) }
        .font(.caption)
    }
}

private struct ResourceTypePanel: View {
    let distributions: [OfficialResourceDistribution]
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack { Text("类型分布").fontWeight(.semibold); Spacer(); Text("实时数据").font(.caption).foregroundStyle(.secondary) }
            ForEach(distributions.prefix(4)) { item in
                HStack { Text(item.label).lineLimit(1).frame(width: 100, alignment: .leading); ProgressView(value: item.percentage).tint(.blue); Text("\(item.count)").monospacedDigit().frame(width: 24, alignment: .trailing) }.font(.caption)
            }
            if distributions.isEmpty { Text("暂无可分组字段").font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, maxHeight: .infinity) }
        }
        .padding(14).frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
        .overlay { RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.10)) }
    }
}

private struct ResourceInspector: View {
    let item: OfficialResourceItem
    let detailOperation: OfficialAPIOperation?
    let run: (OfficialAPIOperation) -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 11) {
                Image(systemName: item.symbol).font(.title2).foregroundStyle(.tint).frame(width: 42, height: 42).background(Color.accentColor.opacity(0.11), in: RoundedRectangle(cornerRadius: 8))
                VStack(alignment: .leading, spacing: 3) { Text(item.name).font(.title3.weight(.semibold)); Label(item.state, systemImage: "circle.fill").font(.caption).foregroundStyle(item.needsAttention ? .orange : .green) }
            }
            Divider()
            Text("资源详情").font(.headline)
            ScrollView {
                Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 10) {
                    ForEach(item.fields) { field in
                        GridRow { Text(field.label).foregroundStyle(.secondary); Text(field.value).textSelection(.enabled) }
                    }
                }
            }
            Spacer()
            if let detailOperation { Button("刷新完整详情", systemImage: "arrow.clockwise") { run(detailOperation) }.buttonStyle(.borderedProminent).frame(maxWidth: .infinity) }
            Button("复制资源 ID", systemImage: "doc.on.doc") { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(item.id, forType: .string) }.frame(maxWidth: .infinity)
        }
        .padding(18).inspectorColumnWidth(min: 300, ideal: 350, max: 430)
    }
}

private struct OperationFormField: Identifiable {
    let id = UUID()
    let category: String
    let key: String
    let label: String
    var value: String
    let required: Bool
    let kind: String
}

private struct OfficialOperationFormView: View {
    @Environment(\.dismiss) private var dismiss
    let operation: OfficialAPIOperation
    let selectedItem: OfficialResourceItem?
    let onSubmit: ([String: String], String, String?) -> Void
    @State private var fields: [OperationFormField]
    @State private var validation = ""
    @State private var confirmingWrite = false

    init(operation: OfficialAPIOperation, selectedItem: OfficialResourceItem?, onSubmit: @escaping ([String: String], String, String?) -> Void) {
        self.operation = operation
        self.selectedItem = selectedItem
        self.onSubmit = onSubmit
        _fields = State(initialValue: Self.makeFields(operation: operation, selectedItem: selectedItem))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 5) { Text(operation.title).font(.title2.weight(.semibold)); Text(operation.description).foregroundStyle(.secondary); if operation.isWrite { Label("此操作会修改 UniFi 配置", systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.orange).padding(.top, 5) } }.padding(20)
            Divider()
            Form {
                if fields.isEmpty { ContentUnavailableView("无需额外字段", systemImage: "checkmark.circle") }
                ForEach($fields) { $field in
                    LabeledContent {
                        TextField(field.required ? "必填" : "可选", text: $field.value).textFieldStyle(.roundedBorder)
                    } label: {
                        VStack(alignment: .leading, spacing: 2) { Text(field.label); Text(field.category).font(.caption2).foregroundStyle(.secondary) }
                    }
                }
            }
            .formStyle(.grouped)
            if !validation.isEmpty { Text(validation).foregroundStyle(.red).font(.caption).padding(.horizontal, 20) }
            Divider()
            HStack {
                Spacer()
                Button("取消") { dismiss() }
                Button(operation.isWrite ? "检查并执行" : "读取") {
                    if operation.isWrite { confirmingWrite = true } else { submit() }
                }
                .buttonStyle(.borderedProminent)
            }
            .padding(16)
        }
        .frame(width: 620, height: 600)
        .alert("确认执行官方写操作？", isPresented: $confirmingWrite) {
            Button("取消", role: .cancel) { }
            Button("确认执行", role: operation.isDestructive ? .destructive : nil) { submit() }
        } message: { Text("\(operation.title)会直接修改 UniFi，请确认字段内容正确。") }
    }

    private func submit() {
        do {
            for field in fields where field.required && field.value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                throw UniFiError.api("请填写“\(field.label)”。")
            }
            let parameters = Dictionary(uniqueKeysWithValues: fields.filter { $0.category == "对象" }.map { ($0.key, $0.value) })
            let query = fields.filter { $0.category == "选项" && !$0.value.isEmpty }.map {
                "\(Self.queryEscape($0.key))=\(Self.queryEscape($0.value))"
            }.joined(separator: "&")
            var body: String?
            if operation.hasBody {
                var object = (try JSONSerialization.jsonObject(with: Data(operation.defaultBody.utf8)) as? [String: Any]) ?? [:]
                for field in fields where field.category == "参数" {
                    object = Self.setting(object, path: field.key.split(separator: ".").map(String.init), value: try Self.parseValue(field))
                }
                let encoded = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
                body = String(decoding: encoded, as: UTF8.self)
            }
            onSubmit(parameters, query, body)
            dismiss()
        } catch {
            validation = error.localizedDescription
        }
    }

    private static func makeFields(operation: OfficialAPIOperation, selectedItem: OfficialResourceItem?) -> [OperationFormField] {
        var fields = operation.pathParameters.map { parameter in
            OperationFormField(category: "对象", key: parameter, label: friendlyField(parameter), value: parameter.lowercased().hasSuffix("id") ? (selectedItem?.id ?? "") : "", required: true, kind: "String")
        }
        let required = Set(operation.requiredQueryParameters)
        for (key, value) in queryDictionary(operation.defaultQuery) {
            fields.append(OperationFormField(category: "选项", key: key, label: friendlyField(key), value: value, required: required.contains(key), kind: "String"))
        }
        if operation.hasBody, let object = try? JSONSerialization.jsonObject(with: Data(operation.defaultBody.utf8)) as? [String: Any] {
            flattenBody(object, prefix: "", output: &fields)
        }
        return fields
    }

    private static func flattenBody(_ object: [String: Any], prefix: String, output: inout [OperationFormField]) {
        for (key, value) in object.sorted(by: { $0.key < $1.key }) {
            let path = prefix.isEmpty ? key : "\(prefix).\(key)"
            if let child = value as? [String: Any] { flattenBody(child, prefix: path, output: &output); continue }
            let kind: String
            let display: String
            if let boolean = value as? Bool { kind = "Bool"; display = boolean ? "true" : "false" }
            else if let array = value as? [Any] { kind = "Array"; display = array.map { String(describing: $0) }.joined(separator: ", ") }
            else if let number = value as? NSNumber { kind = "Number"; display = number.stringValue }
            else { kind = "String"; display = String(describing: value) }
            output.append(OperationFormField(category: "参数", key: path, label: friendlyField(path), value: display, required: false, kind: kind))
        }
    }

    private static func parseValue(_ field: OperationFormField) throws -> Any {
        switch field.kind {
        case "Bool": guard let value = Bool(field.value) else { throw UniFiError.api("“\(field.label)”只能填写 true 或 false。") }; return value
        case "Number": guard let value = Double(field.value) else { throw UniFiError.api("“\(field.label)”必须是数字。") }; return value
        case "Array":
            let tokens = field.value.split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }
            return tokens.map { token -> Any in
                if let boolean = Bool(token) { return boolean }
                if let number = Double(token) { return number }
                return token
            }
        default: return field.value
        }
    }

    private static func setting(_ dictionary: [String: Any], path: [String], value: Any) -> [String: Any] {
        guard let key = path.first else { return dictionary }
        var copy = dictionary
        if path.count == 1 { copy[key] = value; return copy }
        let child = copy[key] as? [String: Any] ?? [:]
        copy[key] = setting(child, path: Array(path.dropFirst()), value: value)
        return copy
    }

    private static func queryDictionary(_ query: String) -> [String: String] {
        Dictionary(uniqueKeysWithValues: query.trimmingCharacters(in: CharacterSet(charactersIn: "?")).split(separator: "&").compactMap { part in
            let pair = part.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard pair.count == 2 else { return nil }
            return (String(pair[0]), String(pair[1]))
        })
    }

    private static func queryEscape(_ value: String) -> String {
        value.addingPercentEncoding(withAllowedCharacters: CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))) ?? value
    }

    private static func friendlyField(_ path: String) -> String {
        let leaf = path.split(separator: ".").last.map(String.init) ?? path
        let known = ["deviceId": "设备 ID", "clientId": "客户端 ID", "networkId": "网络 ID", "wifiBroadcastId": "WiFi 广播 ID", "voucherId": "凭证 ID", "portIdx": "端口编号", "macAddress": "MAC 地址", "offset": "起始位置", "limit": "读取数量", "filter": "筛选条件", "timeLimitMinutes": "有效分钟数"]
        if let label = known[leaf] { return label }
        return leaf.enumerated().reduce(into: "") { result, pair in if pair.offset > 0, pair.element.isUppercase { result.append(" ") }; result.append(pair.element) }
    }
}
