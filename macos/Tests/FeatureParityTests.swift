import Foundation
import CryptoKit
import XCTest
@testable import UniFiPolicyManagerMac

final class FeatureParityTests: XCTestCase {
    func testOfficialAPICatalogCoversNetwork10457() throws {
        let operations = OfficialAPICatalog.operations
        XCTAssertEqual(operations.count, 73)
        XCTAssertEqual(Set(operations.map(\.id)).count, operations.count)
        XCTAssertTrue(operations.allSatisfy { $0.pathTemplate.hasPrefix("/v1/") })
        XCTAssertTrue(operations.allSatisfy { ["GET", "POST", "PUT", "PATCH", "DELETE"].contains($0.method) })
        XCTAssertTrue(operations.allSatisfy { operation in OfficialAPICatalog.modules.contains { $0.id == operation.moduleID } })
        let fingerprintSource = operations
            .sorted { $0.id < $1.id }
            .map { "\($0.id)|\($0.method)|\($0.pathTemplate)" }
            .joined(separator: "\n")
        let fingerprint = SHA256.hash(data: Data(fingerprintSource.utf8)).map { String(format: "%02X", $0) }.joined()
        XCTAssertEqual(fingerprint, "1341E65B95B553EFDE7711386C138431A42D407424968112518E2A5158C35DD6")

        let expectedCounts = [
            "application": 2, "devices": 8, "clients": 3, "networks": 6,
            "wifi": 5, "hotspot": 5, "firewall": 13, "acl": 7,
            "switching": 6, "dns": 5, "traffic": 5, "resources": 8
        ]
        for (module, expected) in expectedCounts {
            XCTAssertEqual(OfficialAPICatalog.operations(for: module).count, expected, "Unexpected operation count for \(module)")
        }
        for operation in operations where operation.hasBody {
            XCTAssertFalse(operation.defaultBody.isEmpty, "\(operation.id) is missing a request template")
            _ = try JSONSerialization.jsonObject(with: Data(operation.defaultBody.utf8), options: [.fragmentsAllowed])
        }

        let portAction = try XCTUnwrap(operations.first { $0.id == "executePortAction" })
        let resolved = try portAction.resolvedPath(
            siteID: "site/id",
            parameters: ["deviceId": "device/id", "portIdx": "3"]
        )
        XCTAssertFalse(resolved.contains("{"))
        XCTAssertTrue(resolved.localizedCaseInsensitiveContains("site%2Fid"))
        XCTAssertTrue(resolved.localizedCaseInsensitiveContains("device%2Fid"))

        let ordering = try XCTUnwrap(operations.first { $0.id == "getFirewallPolicyOrdering" })
        XCTAssertThrowsError(try ordering.validateQuery(ordering.defaultQuery))
        XCTAssertNoThrow(try ordering.validateQuery("sourceFirewallZoneId=source-zone&destinationFirewallZoneId=destination-zone"))
    }

    @MainActor
    func testVisualResourcePresentationUsesOfficialResponseFields() async throws {
        let operation = try XCTUnwrap(OfficialAPICatalog.operations.first { $0.id == "getAdoptedDeviceOverviewPage" })
        let model = AppModel()
        model.startDemo()

        let response = try await model.requestOfficialResource(operation, query: operation.defaultQuery)
        let snapshot = try OfficialResourcePresenter.parse(response, moduleID: "devices")

        XCTAssertEqual(snapshot.items.count, 4)
        XCTAssertEqual(snapshot.totalCount, 4)
        XCTAssertEqual(snapshot.healthyCount, 3)
        XCTAssertTrue(snapshot.items.contains { $0.name == "Cloud Gateway Fiber" && $0.type == "UCG-Fiber" })
        XCTAssertFalse(snapshot.distributions.isEmpty)
    }

    func testOfficialResourcePagingHealthAndContextActions() throws {
        let pageOne = try OfficialResourcePresenter.parse(
            #"{"data":[{"code":"CN","name":"China"},{"code":"US","name":"United States"}],"totalCount":3}"#,
            moduleID: "resources"
        )
        let pageTwo = try OfficialResourcePresenter.parse(
            #"{"data":[{"code":"US","name":"United States"},{"code":"JP","name":"Japan"}],"totalCount":3}"#,
            moduleID: "resources"
        )
        let merged = OfficialResourceInteraction.mergePages([pageOne, pageTwo])
        XCTAssertEqual(merged.items.count, 3)
        XCTAssertEqual(merged.totalCount, 3)
        XCTAssertFalse(merged.hasHealthData)
        XCTAssertEqual(merged.healthyCount, 0)
        XCTAssertEqual(merged.attentionCount, 0)
        XCTAssertEqual(merged.unknownCount, 0)

        let networkList = try XCTUnwrap(OfficialAPICatalog.operations.first { $0.id == "getNetworksOverviewPage" })
        let networkOperations = OfficialAPICatalog.operations(for: "networks")
        XCTAssertEqual(OfficialResourceInteraction.primaryOperation(for: networkList, operations: networkOperations)?.id, "createNetwork")
        let itemActionIDs = Set(OfficialResourceInteraction.itemOperations(for: networkList, operations: networkOperations).map(\.id))
        XCTAssertTrue(Set(["getNetworkDetails", "updateNetwork", "deleteNetwork", "getNetworkReferences"]).isSubset(of: itemActionIDs))
        XCTAssertEqual(OfficialResourceInteraction.detailOperation(for: networkList, operations: networkOperations)?.id, "getNetworkDetails")

        let countryList = try XCTUnwrap(OfficialAPICatalog.operations.first { $0.id == "getCountries" })
        XCTAssertTrue(OfficialResourceInteraction.itemOperations(for: countryList, operations: OfficialAPICatalog.operations(for: "resources")).isEmpty)
        let pendingList = try XCTUnwrap(OfficialAPICatalog.operations.first { $0.id == "getPendingDevicePage" })
        XCTAssertEqual(OfficialResourceInteraction.itemOperations(for: pendingList, operations: OfficialAPICatalog.operations(for: "devices")).map(\.id), ["adoptDevice"])

        let pageQuery = OfficialResourceInteraction.pageQuery("offset=0&limit=50&filter=active", offset: 100, limit: 50)
        XCTAssertTrue(pageQuery.contains("offset=100"))
        XCTAssertTrue(pageQuery.contains("limit=50"))
        XCTAssertTrue(pageQuery.contains("filter=active"))
    }

    @MainActor
    func testSidebarSeparatesWorkspaceNetworkPolicyAndInfrastructure() {
        XCTAssertEqual(Set(WorkspaceSection.allCases.flatMap(\.pages)), Set(WorkspacePage.allCases))
        XCTAssertEqual(WorkspaceSection.allCases.map(\.title), ["工作台", "网络资源", "策略与安全", "基础设施与开发"])
        XCTAssertTrue(WorkspaceSection.policy.pages.contains(.dns))
        XCTAssertTrue(WorkspaceSection.policy.pages.contains(.acl))
        XCTAssertTrue(WorkspaceSection.policy.pages.contains(.firewall))
        XCTAssertEqual(WorkspacePage.apiAll.apiModuleID, "all")
    }

    func testSidebarSectionsExpandAndCollapseIndependently() {
        var state = SidebarExpansionState()
        XCTAssertTrue(WorkspaceSection.allCases.allSatisfy(state.isExpanded))

        state.setExpanded(false, for: .network)
        XCTAssertFalse(state.isExpanded(.network))
        XCTAssertTrue(state.isExpanded(.workspace))
        XCTAssertTrue(state.isExpanded(.policy))
        XCTAssertTrue(state.isExpanded(.infrastructure))

        state.reveal(.apiNetworks)
        XCTAssertTrue(state.isExpanded(.network))
    }

    @MainActor
    func testDNSTypeTabsContainSevenOfficialTypesAndDriveFiltering() {
        XCTAssertEqual(DNSRecordTypeTab.allCases.map(\.rawValue), ["NS", "A", "AAAA", "CNAME", "MX", "TXT", "SRV"])
        for type in DNSRecordTypeTab.allCases {
            XCTAssertEqual(type.makeDraft().recordType, type.rawValue)
        }

        let model = AppModel()
        model.dnsRecords = [
            DNSRecord(recordType: "NS", key: "forward.example.com", value: "192.168.1.10"),
            DNSRecord(recordType: "A", key: "a.example.com", value: "192.0.2.10"),
            DNSRecord(recordType: "A", key: "b.example.com", value: "192.0.2.11")
        ]
        model.selectedDNSType = .a

        XCTAssertEqual(model.dnsCount(for: .a), 2)
        XCTAssertEqual(model.filteredDNS.map(\.recordType), ["A", "A"])
    }

    func testDNSBatchParserSupportsAllOfficialTypesAndDeduplicates() throws {
        let csv = """
        类型,域名,值或服务器,TTL,优先级,权重,端口,服务,协议,启用
        NS,example.com,192.168.1.10,,,,,,,TRUE
        FORWARD_DOMAIN,example.com,192.168.1.10,,,,,,,TRUE
        A,a.example.com,192.0.2.10,300,,,,,,TRUE
        AAAA,aaaa.example.com,2001:db8::10,3600,,,,,,TRUE
        CNAME,www.example.com,target.example.com,300,,,,,,TRUE
        MX,example.com,mail.example.com,,10,,,,,TRUE
        TXT,_policy.example.com,managed=true,,,,,,,TRUE
        SRV,example.com,sip.example.com,,10,5,5060,_sip,_tcp,TRUE
        """

        let result = try DNSImportService.parseText(csv, defaultDNSServer: "192.168.1.10")

        XCTAssertEqual(result.records.count, 7)
        XCTAssertEqual(Set(result.records.map(\.recordType)), Set(["NS", "A", "AAAA", "CNAME", "MX", "TXT", "SRV"]))
        XCTAssertEqual(result.duplicateInput.count, 1)
        XCTAssertTrue(result.invalid.isEmpty)
    }

    func testWindowsBaselineFieldsDecodeOnMac() throws {
        let json = """
        {
          "schema_version": 2,
          "created_at": "2026-08-11T10:00:00.1234567+00:00",
          "target": "https://192.168.1.1",
          "site": "default",
          "site_id": "site-id",
          "network_version": "10.4.57",
          "dns_records": [
            { "_id": "dns-id", "record_type": "NS", "key": "example.com", "value": "192.168.1.10", "enabled": true }
          ],
          "acl_rules": [],
          "firewall_policies": [],
          "acl_ordering": {
            "kind": "Acl",
            "ordered_acl_rule_ids": ["acl-id"],
            "before_system_defined": [],
            "after_system_defined": []
          }
        }
        """
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".json")
        try Data(json.utf8).write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }

        let bundle = try PolicyBundleCodec.load(url)

        XCTAssertEqual(bundle.dnsRecords.first?.id, "dns-id")
        XCTAssertEqual(bundle.aclOrdering?.kind, .acl)
        XCTAssertEqual(bundle.aclOrdering?.orderedACLRuleIDs, ["acl-id"])
        XCTAssertTrue(bundle.hasDNSSection)
        XCTAssertTrue(bundle.hasACLSection)
        XCTAssertTrue(bundle.hasFirewallSection)
    }

    func testBundledForwardDomainCSVContains212UsableRules() throws {
        let repositoryRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let preset = repositoryRoot.appendingPathComponent("Presets/unifi-forward-domains-by-service.csv")

        let result = try DNSImportService.importFile(preset, defaultDNSServer: "192.168.1.10")

        XCTAssertEqual(result.records.count, 212)
        XCTAssertTrue(result.records.allSatisfy { $0.recordType == "NS" && $0.value == "192.168.1.10" })
        XCTAssertTrue(result.duplicateInput.isEmpty)
        XCTAssertTrue(result.invalid.isEmpty)
    }

    func testXLSXImportReadsInlineStringCells() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let sheetDirectory = root.appendingPathComponent("xl/worksheets", isDirectory: true)
        try FileManager.default.createDirectory(at: sheetDirectory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let sheet = """
        <?xml version="1.0" encoding="UTF-8"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <sheetData>
            <row r="1">
              <c r="A1" t="inlineStr"><is><t>类型</t></is></c>
              <c r="B1" t="inlineStr"><is><t>域名</t></is></c>
              <c r="C1" t="inlineStr"><is><t>值或服务器</t></is></c>
            </row>
            <row r="2">
              <c r="A2" t="inlineStr"><is><t>NS</t></is></c>
              <c r="B2" t="inlineStr"><is><t>xlsx.example.com</t></is></c>
              <c r="C2" t="inlineStr"><is><t>192.168.1.10</t></is></c>
            </row>
          </sheetData>
        </worksheet>
        """
        try Data(sheet.utf8).write(to: sheetDirectory.appendingPathComponent("sheet1.xml"))
        let archive = root.appendingPathComponent("rules.xlsx")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        process.currentDirectoryURL = root
        process.arguments = ["-qr", archive.path, "xl"]
        try process.run()
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0)

        let result = try DNSImportService.importFile(archive, defaultDNSServer: "192.168.1.10")

        XCTAssertEqual(result.records.count, 1)
        XCTAssertEqual(result.records.first?.key, "xlsx.example.com")
        XCTAssertTrue(result.invalid.isEmpty)
    }

    func testStrictSyncControlsDeletionPlan() {
        let current = [
            DNSRecord(id: "keep", recordType: "NS", key: "keep.example.com", value: "192.168.1.10"),
            DNSRecord(id: "remove", recordType: "NS", key: "remove.example.com", value: "192.168.1.10")
        ]
        let bundle = PolicyBundle(dnsRecords: [current[0]])
        let source = URL(fileURLWithPath: "/tmp/baseline.json")

        let safe = PolicyChangeService.buildPlan(bundle: bundle, sourceURL: source, currentDNS: current, currentACL: [], currentFirewall: [], synchronizeDeletes: false)
        let strict = PolicyChangeService.buildPlan(bundle: bundle, sourceURL: source, currentDNS: current, currentACL: [], currentFirewall: [], synchronizeDeletes: true)

        XCTAssertEqual(safe.count(.delete), 0)
        XCTAssertEqual(strict.count(.delete), 1)
        XCTAssertEqual(strict.items.first(where: { $0.action == .delete })?.isSelected, false)
    }
}
