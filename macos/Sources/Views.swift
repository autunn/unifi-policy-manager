import AppKit
import SwiftUI

private enum Theme {
    static let accent = Color(red: 0.28, green: 0.59, blue: 1.0)
    static let sidebar = Color(red: 0.051, green: 0.055, blue: 0.063)
    static let line = Color.primary.opacity(0.10)
}

struct RootView: View {
    @EnvironmentObject var model: AppModel
    @State private var handledLaunchArguments = false

    var body: some View {
        Group {
            if model.connected { WorkspaceView() } else { LoginView() }
        }
        .tint(Theme.accent)
        .overlay {
            if model.busy {
                ZStack(alignment: .top) {
                    Color.clear.contentShape(Rectangle()).ignoresSafeArea()
                    ProgressView(model.status)
                        .controlSize(.regular)
                        .padding(.horizontal, 18)
                        .padding(.vertical, 13)
                        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 9))
                        .overlay { RoundedRectangle(cornerRadius: 9).stroke(Theme.line) }
                        .shadow(color: .black.opacity(0.14), radius: 12, y: 4)
                        .padding(.top, 16)
                }
                .transition(.move(edge: .top).combined(with: .opacity))
            }
        }
        .animation(.easeOut(duration: 0.15), value: model.busy)
        .alert("操作失败", isPresented: Binding(
            get: { model.errorMessage != nil },
            set: { if !$0 { model.errorMessage = nil } }
        )) {
            Button("好", role: .cancel) { model.errorMessage = nil }
        } message: {
            Text(model.errorMessage ?? "未知错误")
        }
        .sheet(isPresented: $model.showSitePicker) { SitePickerView() }
        .onAppear {
            guard !handledLaunchArguments else { return }
            handledLaunchArguments = true
            if CommandLine.arguments.contains("--demo") { model.startDemo() }
        }
    }
}

struct LoginView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        HStack(spacing: 0) {
            ZStack {
                Theme.sidebar
                VStack(alignment: .leading, spacing: 0) {
                    BrandView(compact: false)
                    Spacer()
                    Text("UniFi 策略管理")
                        .font(.system(size: 35, weight: .bold))
                        .foregroundStyle(.white)
                    Text("覆盖 UniFi Network 官方 Integration API 的 44 条路径、73 个操作，并保留 DNS、ACL 与防火墙专用管理流程。")
                        .font(.system(size: 15))
                        .foregroundStyle(Color.white.opacity(0.68))
                        .lineSpacing(6)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.top, 18)
                    VStack(alignment: .leading, spacing: 15) {
                        LoginFeature(icon: "checkmark.shield", text: "73 个官方 API 操作，不使用 SSH 或内部端点")
                        LoginFeature(icon: "arrow.counterclockwise", text: "DNS、ACL、防火墙写入前保存实时基线")
                        LoginFeature(icon: "key", text: "API Key 存储在 macOS 钥匙串")
                    }
                    .padding(.top, 34)
                    Spacer()
                    Text("Native macOS · Apple Silicon")
                        .font(.caption)
                        .foregroundStyle(Color.white.opacity(0.36))
                }
                .padding(42)
            }
            .frame(minWidth: 420, maxWidth: 500)

            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    Text("连接 UniFi Console")
                        .font(.system(size: 28, weight: .bold))
                    Text("填写 UCG 的本地地址和官方 API Key。")
                        .foregroundStyle(.secondary)
                        .padding(.top, 7)

                    Form {
                        Section {
                            TextField("192.168.1.1", text: $model.host)
                                .textFieldStyle(.roundedBorder)
                            SecureField("UniFi API Key", text: $model.apiKey)
                                .textFieldStyle(.roundedBorder)
                        } header: { Text("连接信息") }

                        Section {
                            Toggle("将 API Key 存入 macOS 钥匙串", isOn: $model.rememberKey)
                            Toggle("验证 UCG HTTPS 证书", isOn: $model.verifyTLS)
                        } header: { Text("安全") }
                    }
                    .formStyle(.grouped)
                    .scrollDisabled(true)
                    .frame(height: 260)
                    .padding(.horizontal, -20)
                    .padding(.top, 14)

                    Text("API Key 可在本地 Console → Integrations，或 unifi.ui.com → Settings → API Keys 创建。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.bottom, 14)

                    Button { model.connect() } label: {
                        Label("连接并读取策略", systemImage: "arrow.right.circle.fill")
                            .frame(maxWidth: .infinity)
                            .frame(height: 34)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(model.host.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || model.apiKey.isEmpty)

                    HStack {
                        Button("演示模式", systemImage: "play.rectangle") { model.startDemo() }
                        Spacer()
                        Button("清除保存的 API Key", systemImage: "key.slash") { model.forgetKey() }
                            .disabled(KeychainService.load().isEmpty && model.apiKey.isEmpty)
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .padding(.top, 16)

                    Divider().padding(.vertical, 24)
                    Label("自签名证书的 UCG 通常需要关闭证书验证。API Key 不会写入快照、导出文件或操作日志。", systemImage: "lock.shield")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: 530)
                .padding(48)
                .frame(maxWidth: .infinity, minHeight: 680)
            }
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }
}

private struct LoginFeature: View {
    let icon: String
    let text: String
    var body: some View { Label(text, systemImage: icon).foregroundStyle(Color.white.opacity(0.84)).font(.system(size: 13, weight: .medium)) }
}

struct BrandView: View {
    let compact: Bool
    var body: some View {
        HStack(spacing: 11) {
            ZStack {
                RoundedRectangle(cornerRadius: 7).fill(Theme.accent)
                Image(systemName: "point.3.filled.connected.trianglepath.dotted").foregroundStyle(.white).font(.system(size: compact ? 16 : 19, weight: .bold))
            }
            .frame(width: compact ? 34 : 40, height: compact ? 34 : 40)
            VStack(alignment: .leading, spacing: 1) {
                Text(compact ? "Policy Manager" : "UniFi Policy Manager").font(.system(size: compact ? 15 : 17, weight: .bold)).foregroundStyle(.white)
                Text("OFFICIAL API").font(.system(size: 9, weight: .bold)).foregroundStyle(Color(red: 0.35, green: 0.68, blue: 1.0))
            }
        }
    }
}

struct SitePickerView: View {
    @EnvironmentObject var model: AppModel
    @State private var selection: UniFiSite.ID?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("选择 UniFi 站点").font(.title2.bold())
            Text("此 API Key 可以管理多个站点。").foregroundStyle(.secondary)
            List(model.sites, selection: $selection) { site in
                VStack(alignment: .leading, spacing: 3) {
                    Text(site.displayName).fontWeight(.semibold)
                    Text(site.id).font(.caption.monospaced()).foregroundStyle(.secondary)
                }.tag(site.id)
            }
            .frame(width: 470, height: 240)
            HStack {
                Spacer()
                Button("取消") { model.showSitePicker = false }
                Button("继续") {
                    if let selection, let site = model.sites.first(where: { $0.id == selection }) { model.chooseSite(site) }
                }
                .buttonStyle(.borderedProminent)
                .disabled(selection == nil)
            }
        }
        .padding(24)
    }
}

struct WorkspaceView: View {
    @EnvironmentObject var model: AppModel
    @State private var sidebarExpansion = SidebarExpansionState()

    var body: some View {
        NavigationSplitView {
            VStack(spacing: 0) {
                BrandView(compact: true).padding(.horizontal, 18).padding(.top, 17).padding(.bottom, 22)
                List(selection: $model.selectedPage) {
                    ForEach(WorkspaceSection.allCases) { section in
                        DisclosureGroup(isExpanded: sectionExpansionBinding(section)) {
                            ForEach(section.pages) { page in
                                Label {
                                    HStack {
                                        Text(page.title)
                                        Spacer()
                                        if page == .dns { Text("\(model.dnsRecords.count)") }
                                        if page == .acl { Text("\(model.aclRules.count)") }
                                        if page == .firewall { Text("\(model.firewallRules.count)") }
                                    }.foregroundStyle(.white)
                                } icon: { Image(systemName: page.symbol).foregroundStyle(Color.white.opacity(0.72)) }
                                .pointingHandOnHover()
                                .tag(page)
                            }
                        } label: {
                            Text(section.title).foregroundStyle(Color.white.opacity(0.42)).font(.caption2.bold()).pointingHandOnHover()
                        }
                        .tint(Color.white.opacity(0.52))
                    }
                }
                .scrollContentBackground(.hidden)
                .listStyle(.sidebar)
                ConnectionBadge()
            }
            .background(Theme.sidebar)
            .navigationSplitViewColumnWidth(min: 210, ideal: 228, max: 250)
        } detail: {
            Group {
                switch model.selectedPage ?? .overview {
                case .overview: OverviewView()
                case .changes: PolicyChangeView()
                case .dns: DNSView()
                case .acl: PolicyListView(kind: .acl)
                case .firewall: PolicyListView(kind: .firewall)
                case .apiDevices, .apiClients, .apiNetworks, .apiWifi, .apiHotspot,
                     .apiFirewall, .apiTraffic, .apiSwitching, .apiResources, .apiApplication:
                    VisualResourceWorkspaceView(moduleID: (model.selectedPage ?? .apiDevices).apiModuleID ?? "devices")
                case .apiAll:
                    OfficialAPIWorkspaceView(moduleID: "all")
                }
            }
            .toolbar {
                WorkspaceToolbar()
            }
            .safeAreaInset(edge: .bottom) { StatusBar() }
        }
        .navigationSplitViewStyle(.balanced)
        .onChange(of: model.selectedPage) { _, page in
            guard let page else { return }
            withAnimation(.easeInOut(duration: 0.16)) {
                sidebarExpansion.reveal(page)
            }
        }
    }

    private func sectionExpansionBinding(_ section: WorkspaceSection) -> Binding<Bool> {
        Binding(
            get: { sidebarExpansion.isExpanded(section) },
            set: { expanded in
                withAnimation(.easeInOut(duration: 0.16)) {
                    sidebarExpansion.setExpanded(expanded, for: section)
                }
            }
        )
    }
}

private struct ConnectionBadge: View {
    @EnvironmentObject var model: AppModel
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 7) { Circle().fill(model.demoMode ? Color.orange : Color.green).frame(width: 8, height: 8); Text(model.demoMode ? "演示模式" : "官方 API").font(.caption.bold()).foregroundStyle(.white) }
            Text(model.selectedSite?.displayName ?? "未选择站点").font(.caption2).foregroundStyle(Color.white.opacity(0.52)).lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(13)
        .background(Color.white.opacity(0.06), in: RoundedRectangle(cornerRadius: 7))
        .padding(12)
    }
}

private struct WorkspaceToolbar: ToolbarContent {
    @EnvironmentObject var model: AppModel
    var body: some ToolbarContent {
        ToolbarItemGroup {
            Button { model.refreshAll() } label: { Label("刷新", systemImage: "arrow.clockwise") }.help("刷新全部策略")
            Button { model.exportBaseline() } label: { Label("导出基线", systemImage: "square.and.arrow.up") }.help("导出当前完整策略基线")
            Menu {
                Button("打开备份目录", systemImage: "folder") { model.revealBackups() }
                Divider()
                Button("断开连接", systemImage: "rectangle.portrait.and.arrow.right", role: .destructive) { model.disconnect() }
            } label: { Label("更多", systemImage: "ellipsis.circle") }
        }
    }
}

private struct StatusBar: View {
    @EnvironmentObject var model: AppModel
    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: model.busy ? "clock" : "checkmark.circle").foregroundStyle(model.busy ? .orange : .green)
            Text(model.status).lineLimit(1)
            Spacer()
            Text(model.versionLabel).foregroundStyle(.secondary)
        }
        .font(.caption)
        .padding(.horizontal, 14)
        .frame(height: 30)
        .background(.bar)
        .overlay(alignment: .top) { Divider() }
    }
}

struct OverviewView: View {
    @EnvironmentObject var model: AppModel
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                PageHeader(title: "策略概览", subtitle: "查看当前站点的策略状态与安全操作入口。")
                HStack {
                    VStack(alignment: .leading, spacing: 7) {
                        Text(model.demoMode ? "演示数据" : "已连接 \(model.targetLabel)").font(.title2.bold()).foregroundStyle(.white)
                        Text("Site: \(model.selectedSite?.displayName ?? "未知") · Network \(model.versionLabel)").font(.caption).foregroundStyle(Color.white.opacity(0.68))
                    }
                    Spacer()
                    Label("实时读取 · 官方 API", systemImage: "dot.radiowaves.left.and.right").font(.caption.bold()).foregroundStyle(Color(red: 0.68, green: 0.9, blue: 1))
                }
                .padding(22)
                .background(Color(red: 0.04, green: 0.16, blue: 0.36), in: RoundedRectangle(cornerRadius: 7))

                HStack(spacing: 12) {
                    StatView(label: "全部策略", note: "DNS + ACL + 防火墙", value: model.totalCount, color: .primary)
                    StatView(label: "DNS 记录", note: "支持 7 种类型", value: model.dnsRecords.count, color: Theme.accent)
                    StatView(label: "ACL 规则", note: "用户与系统规则", value: model.aclRules.count, color: .green)
                    StatView(label: "防火墙策略", note: "含区域引用", value: model.firewallRules.count, color: .orange)
                }

                HStack(alignment: .top, spacing: 14) {
                    GroupBox("快速操作") {
                        VStack(spacing: 0) {
                            ActionRow(icon: "network", title: "管理 DNS 记录", subtitle: "新增、编辑、启停或删除官方 DNS Policy") { model.selectedPage = .dns }
                            Divider()
                            ActionRow(icon: "arrow.triangle.2.circlepath", title: "策略变更中心", subtitle: "导入完整基线、预览差异并安全同步") { model.selectedPage = .changes }
                            Divider()
                            ActionRow(icon: "square.and.arrow.up", title: "导出当前基线", subtitle: "保存 DNS、ACL 和防火墙完整快照") { model.exportBaseline() }
                            Divider()
                            ActionRow(icon: "folder", title: "查看自动备份", subtitle: "每次写入前重新读取生成，便于审计和手动恢复") { model.revealBackups() }
                        }
                    }
                    .frame(maxWidth: .infinity)

                    GroupBox("写入保护") {
                        VStack(alignment: .leading, spacing: 17) {
                            SafetyRow(icon: "checkmark.shield.fill", text: "修改前重新读取并保存完整策略基线")
                            SafetyRow(icon: "lock.fill", text: "系统与派生策略保持只读")
                            SafetyRow(icon: "key.fill", text: "API Key 仅存储在 macOS 钥匙串")
                            SafetyRow(icon: "doc.badge.ellipsis", text: "日志与导出文件不包含 API Key")
                        }.padding(10)
                    }
                    .frame(width: 340)
                }
            }
            .padding(24)
        }
        .background(Color(nsColor: .controlBackgroundColor).opacity(0.3))
    }
}

private struct StatView: View {
    let label: String, note: String
    let value: Int
    let color: Color
    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            Text("\(value)").font(.system(size: 29, weight: .bold)).foregroundStyle(color)
            Text(note).font(.caption2).foregroundStyle(.tertiary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 7))
        .overlay { RoundedRectangle(cornerRadius: 7).stroke(Theme.line) }
    }
}

private struct ActionRow: View {
    let icon: String, title: String, subtitle: String
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            HStack(spacing: 13) {
                Image(systemName: icon).font(.system(size: 17)).foregroundStyle(Theme.accent).frame(width: 26)
                VStack(alignment: .leading, spacing: 3) { Text(title).fontWeight(.semibold); Text(subtitle).font(.caption).foregroundStyle(.secondary) }
                Spacer(); Image(systemName: "chevron.right").foregroundStyle(.tertiary)
            }.contentShape(Rectangle()).padding(.vertical, 13).padding(.horizontal, 10)
        }.buttonStyle(.plain)
    }
}

private struct SafetyRow: View {
    let icon: String, text: String
    var body: some View { Label(text, systemImage: icon).font(.callout).foregroundStyle(.secondary).symbolRenderingMode(.hierarchical) }
}

private struct PageHeader: View {
    let title: String, subtitle: String
    var body: some View { VStack(alignment: .leading, spacing: 4) { Text(title).font(.system(size: 24, weight: .bold)); Text(subtitle).foregroundStyle(.secondary) } }
}

struct DNSView: View {
    @EnvironmentObject var model: AppModel
    @State private var editingRecord: DNSRecord?
    @State private var showingEditor = false
    @State private var deletingRecord: DNSRecord?
    @State private var selectedForwardIDs = Set<String>()
    @State private var confirmingBatchDelete = false

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .bottom, spacing: 12) {
                PageHeader(title: "DNS 记录", subtitle: "管理转发域名、A、AAAA、CNAME、MX、TXT 与 SRV。")
                Spacer()
                SearchField(text: $model.search)
                if model.selectedDNSType == .forwardDomain {
                    Button(role: .destructive) { confirmingBatchDelete = true } label: {
                        Label("删除转发域（\(selectedForwardIDs.count)）", systemImage: "trash")
                    }
                    .disabled(selectedForwardIDs.isEmpty || !model.writeReady)
                }
                Button { editingRecord = nil; showingEditor = true } label: {
                    Label("新增 \(model.selectedDNSType.shortTitle)", systemImage: "plus")
                }
                    .buttonStyle(.borderedProminent)
                    .disabled(!model.writeReady)
            }
            .padding(20)

            DNSTypeTabBar()
                .padding(.horizontal, 20)

            HStack(spacing: 8) {
                Image(systemName: model.selectedDNSType.symbol).foregroundStyle(.tint)
                Text(model.selectedDNSType.description).foregroundStyle(.secondary)
                Spacer()
                Text("显示 \(model.filteredDNS.count) / \(model.dnsCount(for: model.selectedDNSType))")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            .font(.callout)
            .padding(.horizontal, 20)
            .padding(.vertical, 10)

            DNSBatchPanel(selectedForwardIDs: $selectedForwardIDs)
                .padding(.horizontal, 20)
                .padding(.bottom, 12)

            Divider()
            Table(model.filteredDNS) {
                TableColumn("") { record in
                    Group {
                        if record.isForwardDomain {
                            Toggle("选择", isOn: Binding(
                                get: { selectedForwardIDs.contains(record.stableID) },
                                set: { selected in
                                    if selected { selectedForwardIDs.insert(record.stableID) }
                                    else { selectedForwardIDs.remove(record.stableID) }
                                }
                            ))
                            .labelsHidden()
                            .toggleStyle(.checkbox)
                        } else { Color.clear }
                    }
                }.width(34)
                TableColumn("状态") { record in StatusPill(enabled: record.enabled) }.width(70)
                TableColumn("类型") { record in Text(record.typeLabel) }.width(90)
                TableColumn("域名") { record in Text(record.key).fontWeight(.medium).lineLimit(1) }.width(min: 180, ideal: 260)
                TableColumn("值 / 服务器") { record in Text(record.value).lineLimit(1) }.width(min: 160, ideal: 240)
                TableColumn("附加参数") { record in Text(record.extraLabel).foregroundStyle(.secondary).lineLimit(1) }.width(min: 100, ideal: 150)
                TableColumn("操作") { record in
                    HStack(spacing: 4) {
                        IconAction(symbol: record.enabled ? "pause.circle" : "play.circle", help: record.enabled ? "停用" : "启用") { model.toggleDNS(record) }.disabled(!model.writeReady)
                        IconAction(symbol: "pencil", help: "编辑") { editingRecord = record; showingEditor = true }.disabled(!model.writeReady)
                        IconAction(symbol: "trash", help: "删除", role: .destructive) { deletingRecord = record }.disabled(!model.writeReady)
                    }
                }.width(105)
            }
            .alternatingRowBackgrounds(.enabled)
            .overlay {
                if model.filteredDNS.isEmpty {
                    ContentUnavailableView(
                        model.search.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                            ? "还没有\(model.selectedDNSType.title)"
                            : "未找到匹配的\(model.selectedDNSType.title)",
                        systemImage: model.selectedDNSType.symbol,
                        description: Text(model.search.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                            ? "可新增第一条记录；批量规则请使用上方可折叠操作面板。"
                            : "请调整搜索条件后重试。")
                    )
                }
            }
        }
        .sheet(isPresented: $showingEditor) {
            DNSRecordEditor(record: editingRecord, initialType: model.selectedDNSType) { model.saveDNS($0) }
        }
        .alert("删除 DNS 记录？", isPresented: Binding(get: { deletingRecord != nil }, set: { if !$0 { deletingRecord = nil } })) {
            Button("取消", role: .cancel) { deletingRecord = nil }
            Button("删除", role: .destructive) { if let record = deletingRecord { model.deleteDNS(record) }; deletingRecord = nil }
        } message: { Text("删除前会重新读取并保存 DNS、ACL 和防火墙完整实时基线。") }
        .alert("批量删除转发域名？", isPresented: $confirmingBatchDelete) {
            Button("取消", role: .cancel) { }
            Button("删除 \(selectedForwardIDs.count) 条", role: .destructive) {
                let selected = model.dnsRecords.filter { selectedForwardIDs.contains($0.stableID) && $0.isForwardDomain }
                model.batchDeleteForwardDomains(selected)
                selectedForwardIDs.removeAll()
            }
        } message: { Text("只会删除选中的转发域名。执行前会保存完整实时策略快照。") }
    }
}

private struct DNSTypeTabBar: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 2) {
                ForEach(DNSRecordTypeTab.allCases) { type in
                    Button {
                        model.selectedDNSType = type
                    } label: {
                        HStack(spacing: 7) {
                            Text(type.shortTitle).fontWeight(.semibold)
                            Text("\(model.dnsCount(for: type))")
                                .font(.caption2.bold().monospacedDigit())
                                .foregroundStyle(model.selectedDNSType == type ? Color.accentColor : Color.secondary)
                                .padding(.horizontal, 7)
                                .padding(.vertical, 2)
                                .background((model.selectedDNSType == type ? Color.accentColor : Color.secondary).opacity(0.10), in: Capsule())
                        }
                        .foregroundStyle(model.selectedDNSType == type ? Color.accentColor : Color.primary)
                        .padding(.horizontal, 15)
                        .padding(.vertical, 12)
                        .contentShape(Rectangle())
                        .background(model.selectedDNSType == type ? Color.accentColor.opacity(0.07) : Color.clear)
                        .overlay(alignment: .bottom) {
                            Rectangle()
                                .fill(model.selectedDNSType == type ? Color.accentColor : Color.clear)
                                .frame(height: 3)
                        }
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("\(type.shortTitle)，\(model.dnsCount(for: type)) 条")
                    .accessibilityAddTraits(model.selectedDNSType == type ? .isSelected : [])
                }
            }
        }
        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 9))
        .overlay { RoundedRectangle(cornerRadius: 9).stroke(Color.primary.opacity(0.10)) }
    }
}

private struct SearchField: View {
    @Binding var text: String
    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
            TextField("搜索", text: $text).textFieldStyle(.plain)
            if !text.isEmpty { Button { text = "" } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.plain).foregroundStyle(.secondary) }
        }
        .padding(.horizontal, 8)
        .frame(width: 210, height: 28)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
        .overlay { RoundedRectangle(cornerRadius: 6).stroke(Theme.line) }
    }
}

private struct StatusPill: View {
    let enabled: Bool
    var body: some View {
        Text(enabled ? "启用" : "停用").font(.caption2.bold()).foregroundStyle(enabled ? Color.green : Color.secondary)
            .padding(.horizontal, 8).padding(.vertical, 3)
            .background((enabled ? Color.green : Color.gray).opacity(0.12), in: Capsule())
    }
}

private struct IconAction: View {
    let symbol: String
    let help: String
    var role: ButtonRole? = nil
    let action: () -> Void
    var body: some View {
        Button(role: role, action: action) { Image(systemName: symbol).frame(width: 19, height: 19) }
            .buttonStyle(.borderless).help(help)
    }
}

struct DNSRecordEditor: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var draft: DNSRecord
    @State private var localError: String?
    let onSave: (DNSRecord) -> Void
    private let types = ["NS", "A", "AAAA", "CNAME", "MX", "TXT", "SRV"]

    init(record: DNSRecord?, initialType: DNSRecordTypeTab = .forwardDomain, onSave: @escaping (DNSRecord) -> Void) {
        _draft = State(initialValue: record ?? initialType.makeDraft())
        self.onSave = onSave
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text(draft.id == nil ? "新增 DNS 记录" : "编辑 DNS 记录").font(.title2.bold())
            Form {
                Picker("记录类型", selection: $draft.recordType) { ForEach(types, id: \.self) { Text($0 == "NS" ? "转发域名" : $0) } }
                if draft.recordType == "SRV" {
                    TextField("域名", text: $draft.domain, prompt: Text("example.com"))
                    TextField("服务", text: $draft.service, prompt: Text("_sip"))
                    TextField("协议", text: $draft.protocolName, prompt: Text("_tcp"))
                } else {
                    TextField("域名", text: $draft.key, prompt: Text("example.com"))
                }
                TextField(valueLabel, text: $draft.value)
                if ["A", "AAAA", "CNAME"].contains(draft.recordType) { OptionalIntegerField(label: "TTL（秒，0 为自动）", value: $draft.ttl) }
                if ["MX", "SRV"].contains(draft.recordType) { OptionalIntegerField(label: "优先级", value: $draft.priority) }
                if draft.recordType == "SRV" {
                    OptionalIntegerField(label: "权重", value: $draft.weight)
                    OptionalIntegerField(label: "端口", value: $draft.port)
                }
                Toggle("启用记录", isOn: $draft.enabled)
            }
            .formStyle(.grouped)
            .scrollDisabled(true)
            .frame(height: draft.recordType == "SRV" ? 390 : 275)
            .padding(.horizontal, -20)

            HStack {
                if let localError { Text(localError).font(.caption).foregroundStyle(.red) }
                Spacer()
                Button("取消") { dismiss() }
                Button("保存") { save() }.buttonStyle(.borderedProminent).disabled(!model.writeReady)
            }
        }
        .padding(24)
        .frame(width: 520)
    }

    private var valueLabel: String {
        switch draft.recordType {
        case "NS": return "DNS 服务器 IP"
        case "A": return "IPv4 地址"
        case "AAAA": return "IPv6 地址"
        case "CNAME": return "目标域名"
        case "MX": return "邮件服务器域名"
        case "TXT": return "文本内容"
        case "SRV": return "服务器域名"
        default: return "值"
        }
    }

    private func save() {
        do {
            onSave(try UniFiPayloadValidator.normalizeDNS(draft))
            dismiss()
        } catch {
            localError = error.localizedDescription
        }
    }
}

private struct OptionalIntegerField: View {
    let label: String
    @Binding var value: Int?
    var body: some View { TextField(label, value: $value, format: .number).textFieldStyle(.roundedBorder) }
}

private enum PolicyOriginFilter: String, CaseIterable, Identifiable {
    case all
    case user
    case system

    var id: String { rawValue }
    var title: String {
        switch self {
        case .all: return "全部"
        case .user: return "用户定义"
        case .system: return "系统与派生"
        }
    }
    func includes(_ rule: PolicyRule) -> Bool {
        switch self {
        case .all: return true
        case .user: return rule.canModify
        case .system: return !rule.canModify
        }
    }
}

struct PolicyListView: View {
    @EnvironmentObject var model: AppModel
    let kind: PolicyKind
    @State private var editingRule: PolicyRule?
    @State private var showingEditor = false
    @State private var deletingRule: PolicyRule?
    @State private var originFilter: PolicyOriginFilter = .all

    private var allRules: [PolicyRule] { kind == .acl ? model.aclRules : model.firewallRules }
    private var visibleRules: [PolicyRule] { model.filteredPolicies(kind).filter(originFilter.includes) }
    private func count(for filter: PolicyOriginFilter) -> Int { allRules.filter(filter.includes).count }

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .bottom) {
                PageHeader(title: kind == .acl ? "ACL 规则" : "防火墙策略", subtitle: kind == .acl ? "管理官方 API 支持的 IPv4 与 MAC 访问控制规则。" : "管理用户定义防火墙策略；系统与派生策略保持只读。")
                Spacer()
                SearchField(text: $model.search)
                Button { editingRule = nil; showingEditor = true } label: { Label("新增策略", systemImage: "plus") }.buttonStyle(.borderedProminent).disabled(!model.writeReady)
            }.padding(20)
            Divider()
            HStack(spacing: 12) {
                Picker("规则来源", selection: $originFilter) {
                    ForEach(PolicyOriginFilter.allCases) { filter in
                        Text("\(filter.title) \(count(for: filter))").tag(filter)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 430)
                Text("当前显示 \(visibleRules.count) 条；系统与派生策略保持只读。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 10)
            .background(Color(nsColor: .controlBackgroundColor))
            Divider()
            Table(visibleRules) {
                TableColumn("状态") { rule in StatusPill(enabled: rule.enabled) }.width(70)
                TableColumn("名称") { rule in Text(rule.name).fontWeight(.medium).lineLimit(1) }.width(min: 180, ideal: 280)
                TableColumn(kind == .acl ? "类型" : "IP 范围") { rule in Text(rule.type) }.width(110)
                TableColumn("动作") { rule in Text(rule.action) }.width(90)
                TableColumn("来源") { rule in Text(rule.originLabel).foregroundStyle(rule.canModify ? .primary : .secondary) }.width(100)
                TableColumn("操作") { rule in
                    HStack(spacing: 4) {
                        IconAction(symbol: "arrow.up", help: "上移") { model.movePolicy(rule, direction: -1) }.disabled(!rule.canModify || !model.writeReady)
                        IconAction(symbol: "arrow.down", help: "下移") { model.movePolicy(rule, direction: 1) }.disabled(!rule.canModify || !model.writeReady)
                        IconAction(symbol: rule.enabled ? "pause.circle" : "play.circle", help: rule.enabled ? "停用" : "启用") { model.togglePolicy(rule) }.disabled(!rule.canModify || !model.writeReady)
                        IconAction(symbol: rule.canModify ? "pencil" : "doc.text.magnifyingglass", help: rule.canModify ? "编辑 JSON" : "查看 JSON") { editingRule = rule; showingEditor = true }.disabled(rule.canModify && !model.writeReady)
                        IconAction(symbol: "trash", help: "删除", role: .destructive) { deletingRule = rule }.disabled(!rule.canModify || !model.writeReady)
                    }
                }.width(155)
            }
            .alternatingRowBackgrounds(.enabled)
            .overlay {
                if visibleRules.isEmpty {
                    ContentUnavailableView(
                        "当前筛选没有\(kind.title)策略",
                        systemImage: "shield.slash",
                        description: Text("切换上方来源分段，或调整搜索条件。")
                    )
                }
            }
        }
        .sheet(isPresented: $showingEditor) { PolicyJSONEditor(kind: kind, rule: editingRule) { json in model.savePolicy(kind: kind, existing: editingRule, json: json) } }
        .alert("删除策略？", isPresented: Binding(get: { deletingRule != nil }, set: { if !$0 { deletingRule = nil } })) {
            Button("取消", role: .cancel) { deletingRule = nil }
            Button("删除", role: .destructive) { if let rule = deletingRule { model.deletePolicy(rule) }; deletingRule = nil }
        } message: { Text("删除前会重新读取并保存完整实时策略基线。系统与派生策略不能删除。") }
    }
}

struct PolicyJSONEditor: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let kind: PolicyKind
    let rule: PolicyRule?
    let onSave: (String) -> Void
    @State private var json: String
    @State private var localError: String?

    init(kind: PolicyKind, rule: PolicyRule?, onSave: @escaping (String) -> Void) {
        self.kind = kind; self.rule = rule; self.onSave = onSave
        _json = State(initialValue: rule?.editableJSON ?? Self.template(kind))
    }

    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(rule == nil ? "新增 \(kind.title) 策略" : (rule!.canModify ? "编辑 \(rule!.name)" : "查看 \(rule!.name)")).font(.title2.bold())
                        Text("使用官方 API 请求体 JSON").foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("格式化", systemImage: "text.alignleft") { formatJSON() }
                }
                TextEditor(text: $json)
                    .font(.system(.body, design: .monospaced))
                    .padding(8)
                    .background(Color(nsColor: .textBackgroundColor))
                    .overlay { RoundedRectangle(cornerRadius: 6).stroke(Theme.line) }
                    .disabled(rule?.canModify == false)
                HStack {
                    if let localError { Text(localError).font(.caption).foregroundStyle(.red) }
                    Spacer()
                    Button("关闭") { dismiss() }
                    if rule?.canModify != false { Button("验证并保存") { save() }.buttonStyle(.borderedProminent).disabled(!model.writeReady) }
                }
            }
            .padding(22)
            .frame(minWidth: 590)

            Divider()
            VStack(alignment: .leading, spacing: 12) {
                Text("参考 UUID").font(.headline)
                Text("双击可复制。").font(.caption).foregroundStyle(.secondary)
                List(model.references) { reference in
                    VStack(alignment: .leading, spacing: 3) {
                        Text(reference.name).fontWeight(.medium)
                        Text(reference.kind).font(.caption).foregroundStyle(.secondary)
                        Text(reference.id).font(.caption2.monospaced()).foregroundStyle(.tertiary).textSelection(.enabled)
                    }
                    .contentShape(Rectangle())
                    .onTapGesture(count: 2) { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(reference.id, forType: .string) }
                }
            }
            .padding(18)
            .frame(width: 270)
        }
        .frame(width: 900, height: 620)
    }

    private func formatJSON() {
        do {
            let object = try JSONSerialization.jsonObject(with: Data(json.utf8))
            json = String(decoding: try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]), as: UTF8.self)
            localError = nil
        } catch { localError = "JSON 格式错误：\(error.localizedDescription)" }
    }

    private func save() {
        do {
            let object = try UniFiPayloadValidator.policyPayload(kind, json: json)
            let data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
            onSave(String(decoding: data, as: UTF8.self))
            dismiss()
        } catch { localError = error.localizedDescription }
    }

    private static func template(_ kind: PolicyKind) -> String {
        if kind == .acl {
            return """
            {
              "type" : "IPV4",
              "name" : "新建 IPv4 ACL",
              "description" : "",
              "enabled" : false,
              "action" : "BLOCK"
            }
            """
        }
        return """
        {
          "name" : "新建防火墙策略",
          "description" : "",
          "enabled" : false,
          "loggingEnabled" : false,
          "action" : { "type" : "BLOCK" },
          "source" : { "zoneId" : "<SOURCE_ZONE_UUID>" },
          "destination" : { "zoneId" : "<DESTINATION_ZONE_UUID>" },
          "ipProtocolScope" : { "ipVersion" : "IPV4" }
        }
        """
    }
}
