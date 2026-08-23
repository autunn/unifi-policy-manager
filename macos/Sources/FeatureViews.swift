import AppKit
import SwiftUI

struct DNSBatchPanel: View {
    @EnvironmentObject var model: AppModel
    @Binding var selectedForwardIDs: Set<String>
    @State private var expanded = false

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .top, spacing: 14) {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("转发域默认 DNS 服务器").font(.caption).foregroundStyle(.secondary)
                        TextField("例如 192.168.1.10", text: $model.batchDNSServer)
                            .textFieldStyle(.roundedBorder)
                        Button("加载内置规则（212）", systemImage: "shippingbox.fill") { model.loadBundledDNSRules() }
                            .buttonStyle(.borderedProminent)
                        Button("选择外部规则文件", systemImage: "doc.badge.plus") { model.chooseDNSImportFile() }
                        Button("保存 CSV 模板", systemImage: "square.and.arrow.down") { model.saveDNSTemplate() }
                        Spacer(minLength: 0)
                        Button("预览并新增", systemImage: "checklist") { model.previewDNSBatchAdd() }
                            .buttonStyle(.borderedProminent)
                            .disabled(!model.writeReady)
                    }
                    .frame(width: 245)

                    VStack(alignment: .leading, spacing: 7) {
                        Text("DNS 规则清单").font(.caption).foregroundStyle(.secondary)
                        TextEditor(text: $model.batchEditorText)
                            .font(.system(.caption, design: .monospaced))
                            .frame(height: 190)
                            .scrollIndicators(.visible)
                            .padding(6)
                            .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                            .overlay { RoundedRectangle(cornerRadius: 6).stroke(Color.primary.opacity(0.12)) }
                        Text(model.importSummary).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    }
                }

                Divider()
                HStack {
                    Text("批量删除仅适用于转发域名；当前已选择 \(selectedForwardIDs.count) 条。")
                        .font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    Button("选择当前转发域") {
                        selectedForwardIDs.formUnion(model.dnsRecords.filter(\.isForwardDomain).map(\.stableID))
                    }
                    Button("清除选择") { selectedForwardIDs.removeAll() }
                }
            }
            .padding(.top, 12)
        } label: {
            Label("批量新增 DNS 规则", systemImage: "square.stack.3d.up.fill")
                .font(.headline)
        }
        .padding(14)
        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
        .overlay { RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.10)) }
        .sheet(item: $model.dnsBatchPreview) { preview in
            DNSBatchPreviewView(preview: preview) { model.applyDNSBatchAdd(preview) }
        }
    }
}

private struct DNSBatchPreviewView: View {
    @Environment(\.dismiss) private var dismiss
    let preview: DNSBatchPreview
    let confirm: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("批量新增预览").font(.title2.bold())
            HStack(spacing: 12) {
                PreviewCount(title: "待新增", value: preview.pending.count, color: .blue)
                PreviewCount(title: "已存在", value: preview.existing.count, color: .green)
                PreviewCount(title: "文件内重复", value: preview.duplicateInput.count, color: .orange)
                PreviewCount(title: "无效", value: preview.invalid.count, color: .red)
            }
            Table(preview.pending) {
                TableColumn("类型") { Text($0.typeLabel) }.width(90)
                TableColumn("域名") { Text($0.key).lineLimit(1) }.width(min: 190, ideal: 260)
                TableColumn("值 / 服务器") { Text($0.value).lineLimit(1) }.width(min: 180, ideal: 250)
                TableColumn("附加参数") { Text($0.extraLabel).foregroundStyle(.secondary) }.width(min: 130, ideal: 170)
            }
            .alternatingRowBackgrounds(.enabled)
            if !preview.invalid.isEmpty {
                DisclosureGroup("查看无效项目（\(preview.invalid.count)）") {
                    ScrollView { Text(preview.invalid.joined(separator: "\n")).font(.caption.monospaced()).frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled) }
                        .frame(height: 90)
                }
            }
            HStack {
                Text("正式新增前会再次读取当前策略并保存完整快照。").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("取消") { dismiss() }
                Button("新增 \(preview.pending.count) 条") { confirm(); dismiss() }
                    .buttonStyle(.borderedProminent)
                    .disabled(preview.pending.isEmpty)
            }
        }
        .padding(22)
        .frame(width: 900, height: 600)
    }
}

private struct PreviewCount: View {
    let title: String
    let value: Int
    let color: Color
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            Text("\(value)").font(.title2.bold()).foregroundStyle(color)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(color.opacity(0.08), in: RoundedRectangle(cornerRadius: 7))
    }
}

struct PolicyChangeView: View {
    @EnvironmentObject var model: AppModel
    @State private var confirmingExecution = false

    private var plan: PolicyChangePlan? { model.changePlan }
    private var hasOrdering: Bool {
        guard let plan else { return false }
        return !(plan.bundle.aclOrdering?.orderedACLRuleIDs.isEmpty ?? true) ||
            !(plan.bundle.firewallOrdering?.beforeSystemDefined.isEmpty ?? true) ||
            !(plan.bundle.firewallOrdering?.afterSystemDefined.isEmpty ?? true)
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .bottom, spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("策略变更中心").font(.system(size: 24, weight: .bold))
                    Text("导入 Windows 或 macOS 导出的完整基线，统一预览 DNS、ACL、防火墙和排序差异。")
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button("加载 JSON", systemImage: "doc.badge.plus") { model.loadPolicyBaseline() }
                Button("加载上次快照", systemImage: "clock.arrow.circlepath") { model.loadLastBackupAsPlan() }
                    .disabled(model.lastBackupURL == nil)
                Toggle("严格同步", isOn: Binding(
                    get: { model.synchronizeDeletes },
                    set: { model.setSynchronizeDeletes($0) }
                ))
                .toggleStyle(.switch)
                .help("开启后生成删除项，并允许恢复基线中的用户策略排序；删除项仍需手动选择。")
            }
            .padding(20)

            Divider()

            if let plan {
                VStack(spacing: 12) {
                    HStack(spacing: 10) {
                        ChangeCount(title: "新增", value: plan.count(.add), color: .blue)
                        ChangeCount(title: "更新", value: plan.count(.update), color: .orange)
                        ChangeCount(title: "删除", value: plan.count(.delete), color: .red)
                        ChangeCount(title: "不变", value: plan.count(.unchanged), color: .green)
                        ChangeCount(title: "无效", value: plan.count(.invalid), color: .purple)
                    }

                    HStack {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(plan.sourceURL.lastPathComponent).fontWeight(.semibold)
                            Text(plan.synchronizeDeletes ? "严格同步已开启；删除项必须手动选择，执行完成后恢复用户策略排序。" : "默认只执行新增和更新，不删除现有策略，也不修改排序。")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("选择安全变更") { model.selectSafePlanItems() }
                        Button("选择全部（含删除）", role: .destructive) { model.selectAllPlanItems() }
                        Button("清除选择") { model.clearPlanSelection() }
                    }

                    Table(plan.items) {
                        TableColumn("执行") { item in
                            Toggle("执行", isOn: Binding(
                                get: { model.changePlan?.items.first(where: { $0.id == item.id })?.isSelected ?? false },
                                set: { model.togglePlanItem(item.id, selected: $0) }
                            ))
                            .labelsHidden()
                            .toggleStyle(.checkbox)
                            .disabled(!item.action.actionable)
                        }.width(48)
                        TableColumn("范围") { item in Text(item.scope.label) }.width(75)
                        TableColumn("操作") { item in ChangeActionPill(action: item.action) }.width(78)
                        TableColumn("规则") { item in Text(item.name).fontWeight(.medium).lineLimit(1) }.width(min: 160, ideal: 230)
                        TableColumn("变更内容") { item in Text(item.details).foregroundStyle(.secondary).lineLimit(2) }.width(min: 260, ideal: 420)
                        TableColumn("状态") { item in Text(item.status).lineLimit(1) }.width(min: 100, ideal: 150)
                    }
                    .alternatingRowBackgrounds(.enabled)

                    HStack {
                        Text("已选择 \(plan.selectedCount) 项\(plan.selectedDeleteCount > 0 ? "，其中删除 \(plan.selectedDeleteCount) 项" : "")。")
                            .font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button("恢复基线排序") { model.applyLoadedOrdering() }
                            .disabled(!plan.synchronizeDeletes || !hasOrdering || !model.writeReady)
                        Button("执行所选变更") {
                            if plan.selectedDeleteCount > 0 { confirmingExecution = true }
                            else { model.executeSelectedPlanChanges() }
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(plan.selectedCount == 0 || !model.writeReady)
                    }
                }
                .padding(16)
            } else {
                ContentUnavailableView(
                    "尚未加载策略基线",
                    systemImage: "arrow.triangle.2.circlepath",
                    description: Text("先导出当前基线作为备份，或加载由 Windows/macOS 版本导出的 JSON 文件。")
                )
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .alert("执行包含删除的变更？", isPresented: $confirmingExecution) {
            Button("取消", role: .cancel) { }
            Button("确认执行", role: .destructive) { model.executeSelectedPlanChanges() }
        } message: {
            Text("已选择 \(plan?.selectedDeleteCount ?? 0) 个删除项。程序会先保存完整实时快照，但删除仍会立即写入 UniFi。")
        }
    }
}

private struct ChangeCount: View {
    let title: String
    let value: Int
    let color: Color
    var body: some View {
        HStack {
            Text(title).font(.caption).foregroundStyle(.secondary)
            Spacer()
            Text("\(value)").font(.title3.bold()).foregroundStyle(color)
        }
        .padding(11)
        .frame(maxWidth: .infinity)
        .background(color.opacity(0.07), in: RoundedRectangle(cornerRadius: 7))
    }
}

private struct ChangeActionPill: View {
    let action: PolicyChangeAction
    var color: Color {
        switch action { case .add: return .blue; case .update: return .orange; case .delete: return .red; case .unchanged: return .green; case .invalid: return .purple }
    }
    var body: some View {
        Text(action.label).font(.caption.bold()).foregroundStyle(color)
            .padding(.horizontal, 7).padding(.vertical, 3)
            .background(color.opacity(0.11), in: Capsule())
    }
}
