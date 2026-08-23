import AppKit
import SwiftUI

struct OfficialAPIWorkspaceView: View {
    @EnvironmentObject var model: AppModel
    let moduleID: String
    @State private var search = ""
    @State private var selectedOperationID: String?
    @State private var pathParameters = ""
    @State private var query = ""
    @State private var requestBody = ""
    @State private var confirmingWrite = false

    private var module: OfficialAPIModule? { OfficialAPICatalog.module(moduleID) }
    private var operations: [OfficialAPIOperation] { OfficialAPICatalog.operations(for: moduleID) }
    private var filteredOperations: [OfficialAPIOperation] {
        let term = search.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !term.isEmpty else { return operations }
        return operations.filter {
            [$0.title, $0.method, $0.pathTemplate, $0.id].contains { $0.localizedCaseInsensitiveContains(term) }
        }
    }
    private var selectedOperation: OfficialAPIOperation? {
        operations.first { $0.id == selectedOperationID }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .bottom, spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(moduleID == "all" ? "全部官方端点" : (module?.title ?? "官方 API 工作台"))
                        .font(.system(size: 24, weight: .bold))
                    Text(moduleID == "all"
                        ? "覆盖 UniFi Network v10.4.57 OpenAPI 中的 44 条路径、73 个操作。"
                        : (module?.description ?? "使用 Ubiquiti 官方 Network Integration API。"))
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Text("\(operations.count) 个官方操作")
                    .font(.caption.bold())
                    .foregroundStyle(.tint)
                    .padding(.horizontal, 11)
                    .padding(.vertical, 6)
                    .background(Color.accentColor.opacity(0.10), in: Capsule())
            }
            .padding(20)

            Divider()

            HSplitView {
                VStack(spacing: 0) {
                    HStack(spacing: 6) {
                        Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                        TextField("搜索端点", text: $search).textFieldStyle(.plain)
                        if !search.isEmpty {
                            Button { search = "" } label: { Image(systemName: "xmark.circle.fill") }
                                .buttonStyle(.plain).foregroundStyle(.secondary)
                        }
                    }
                    .padding(9)
                    .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                    .padding(12)

                    List(filteredOperations, selection: $selectedOperationID) { operation in
                        VStack(alignment: .leading, spacing: 5) {
                            HStack(spacing: 7) {
                                Text(operation.method)
                                    .font(.caption2.bold().monospaced())
                                    .foregroundStyle(.indigo)
                                    .padding(.horizontal, 6).padding(.vertical, 2)
                                    .background(Color.indigo.opacity(0.10), in: RoundedRectangle(cornerRadius: 4))
                                Text(operation.title).fontWeight(.semibold).lineLimit(1)
                            }
                            Text(operation.pathTemplate)
                                .font(.caption2.monospaced())
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                        }
                        .padding(.vertical, 4)
                        .tag(operation.id)
                    }
                    .listStyle(.inset)
                }
                .frame(minWidth: 270, idealWidth: 315, maxWidth: 380)

                Group {
                    if let operation = selectedOperation {
                        operationEditor(operation)
                    } else {
                        ContentUnavailableView("选择一个官方操作", systemImage: "chevron.left.forwardslash.chevron.right")
                    }
                }
                .frame(minWidth: 520)
            }
        }
        .onAppear { resetSelection() }
        .onChange(of: moduleID) { _, _ in resetSelection() }
        .onChange(of: selectedOperationID) { _, _ in loadSelectedOperation() }
        .alert("确认执行官方写操作？", isPresented: $confirmingWrite) {
            Button("取消", role: .cancel) { }
            Button("确认执行", role: .destructive) { executeSelected() }
        } message: {
            if let operation = selectedOperation {
                Text("\(operation.method) \(resolvedPathPreview(operation))\n\n此操作会直接修改 UniFi。")
            }
        }
    }

    @ViewBuilder
    private func operationEditor(_ operation: OfficialAPIOperation) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 7) {
                HStack(spacing: 8) {
                    Text(operation.method).font(.caption.bold().monospaced()).foregroundStyle(.indigo)
                    Text(operation.title).font(.title2.bold())
                }
                Text(operation.description).foregroundStyle(.secondary)
                Text(resolvedPathPreview(operation))
                    .font(.caption.monospaced())
                    .foregroundStyle(.tint)
                    .textSelection(.enabled)
            }
            .padding(18)

            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    HStack(alignment: .top, spacing: 12) {
                        fieldGroup("路径参数（每行 name=value）") {
                            TextEditor(text: $pathParameters).font(.caption.monospaced()).frame(height: 64)
                        }
                        fieldGroup(queryLabel(operation)) {
                            TextEditor(text: $query).font(.caption.monospaced()).frame(height: 64)
                        }
                    }

                    if operation.hasBody {
                        HStack {
                            Text("请求体 JSON").font(.headline)
                            Spacer()
                            Button("格式化", systemImage: "text.alignleft") { formatRequest() }
                        }
                        TextEditor(text: $requestBody)
                            .font(.system(.caption, design: .monospaced))
                            .frame(minHeight: 150, idealHeight: 210)
                            .padding(6)
                            .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                            .overlay { RoundedRectangle(cornerRadius: 6).stroke(Color.primary.opacity(0.12)) }
                    }

                    HStack {
                        Text("响应结果").font(.headline)
                        Spacer()
                        Button("复制结果", systemImage: "doc.on.doc") { copyResponse() }
                    }
                    TextEditor(text: .constant(model.officialAPIResponse))
                        .font(.system(.caption, design: .monospaced))
                        .frame(minHeight: operation.hasBody ? 180 : 330)
                        .padding(6)
                        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                        .overlay { RoundedRectangle(cornerRadius: 6).stroke(Color.primary.opacity(0.12)) }
                        .disabled(true)
                }
                .padding(18)
            }

            Divider()
            HStack {
                Label(
                    operation.isWrite ? "写操作执行前需要再次确认" : "只读操作；Site ID 自动使用当前站点",
                    systemImage: operation.isWrite ? "exclamationmark.triangle" : "checkmark.shield"
                )
                .font(.caption).foregroundStyle(operation.isWrite ? .orange : .secondary)
                Spacer()
                Button(operation.isWrite ? "检查并执行" : "执行官方请求") {
                    if operation.isWrite { confirmingWrite = true } else { executeSelected() }
                }
                .buttonStyle(.borderedProminent)
                .disabled(model.busy || (operation.isWrite && !model.writeReady))
            }
            .padding(14)
        }
    }

    private func fieldGroup<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.caption.bold())
            content()
                .padding(4)
                .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                .overlay { RoundedRectangle(cornerRadius: 6).stroke(Color.primary.opacity(0.12)) }
        }
        .frame(maxWidth: .infinity)
    }

    private func resetSelection() {
        search = ""
        selectedOperationID = operations.first?.id
        loadSelectedOperation()
    }

    private func loadSelectedOperation() {
        guard let operation = selectedOperation else { return }
        pathParameters = operation.pathParameters.map { "\($0)=" }.joined(separator: "\n")
        query = operation.defaultQuery
        requestBody = operation.defaultBody
        model.officialAPIResponse = "准备执行 \(operation.method) \(operation.pathTemplate)"
    }

    private func parseParameters() throws -> [String: String] {
        var output: [String: String] = [:]
        for line in pathParameters.split(whereSeparator: \.isNewline) {
            let parts = line.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard parts.count == 2 else { throw UniFiError.api("路径参数格式不正确：\(line)") }
            output[String(parts[0]).trimmingCharacters(in: .whitespaces)] = String(parts[1]).trimmingCharacters(in: .whitespaces)
        }
        return output
    }

    private func resolvedPathPreview(_ operation: OfficialAPIOperation) -> String {
        let path = (try? operation.resolvedPath(siteID: model.selectedSite?.id ?? "{currentSiteId}", parameters: parseParameters()))
            ?? operation.pathTemplate
        let normalizedQuery = query.trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "?"))
        let singleLineQuery = normalizedQuery
            .replacingOccurrences(of: "\r\n", with: "&")
            .replacingOccurrences(of: "\r", with: "&")
            .replacingOccurrences(of: "\n", with: "&")
        return singleLineQuery.isEmpty ? path : "\(path)?\(singleLineQuery)"
    }

    private func executeSelected() {
        guard let operation = selectedOperation else { return }
        do {
            try operation.validateQuery(query)
            model.executeOfficialOperation(
                operation,
                parameters: try parseParameters(),
                query: query,
                requestBody: requestBody
            )
        } catch {
            model.errorMessage = error.localizedDescription
        }
    }

    private func formatRequest() {
        do {
            let object = try JSONSerialization.jsonObject(with: Data(requestBody.utf8), options: [.fragmentsAllowed])
            let data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .fragmentsAllowed])
            requestBody = String(decoding: data, as: UTF8.self)
        } catch {
            model.errorMessage = "JSON 格式错误：\(error.localizedDescription)"
        }
    }

    private func queryLabel(_ operation: OfficialAPIOperation) -> String {
        operation.requiredQueryParameters.isEmpty
            ? "查询参数（不含 ?）"
            : "查询参数（必填：\(operation.requiredQueryParameters.joined(separator: "、"))）"
    }

    private func copyResponse() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(model.officialAPIResponse, forType: .string)
    }
}
