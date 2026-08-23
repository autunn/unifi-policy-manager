using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniFiDnsManager.Models;

namespace UniFiDnsManager.Services;

public static class SelfTest
{
    public static async Task<bool> RunAsync(string outputPath)
    {
        var tests = new List<object>();
        var success = true;
        async Task CheckAsync(string name, Func<Task> test)
        {
            try { await test(); tests.Add(new { name, ok = true, error = (string?)null }); }
            catch (Exception ex) { success = false; tests.Add(new { name, ok = false, error = ex.ToString() }); }
        }

        await CheckAsync("validate_all_dns_types", () =>
        {
            foreach (var item in AllRecordTypes()) _ = DnsValidator.Normalize(item);
            return Task.CompletedTask;
        });

        await CheckAsync("official_dns_policy_payload_and_response_mapping", () =>
        {
            var expectedTypes = new Dictionary<string, string>
            {
                ["NS"] = "FORWARD_DOMAIN",
                ["A"] = "A_RECORD",
                ["AAAA"] = "AAAA_RECORD",
                ["CNAME"] = "CNAME_RECORD",
                ["MX"] = "MX_RECORD",
                ["TXT"] = "TXT_RECORD",
                ["SRV"] = "SRV_RECORD"
            };

            foreach (var input in AllRecordTypes())
            {
                var normalized = DnsValidator.Normalize(input);
                var payload = OfficialDnsPolicyMapper.BuildPayload(input);
                if (!Equals(payload["type"], expectedTypes[input.RecordType]))
                    throw new Exception($"{input.RecordType} was mapped to the wrong official API type.");
                if (!payload.ContainsKey("domain")) throw new Exception($"{input.RecordType} payload is missing domain.");

                payload["id"] = Guid.NewGuid().ToString();
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
                var parsed = OfficialDnsPolicyMapper.Parse(document.RootElement);
                if (parsed.RecordType != normalized.RecordType ||
                    !string.Equals(parsed.Key, normalized.Key, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(parsed.Value, normalized.Value, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"{input.RecordType} official response mapping did not round-trip.");
            }
            return Task.CompletedTask;
        });

        await CheckAsync("dns_txt_identity_and_same_site_update_are_case_sensitive", () =>
        {
            var id = Guid.NewGuid().ToString();
            var current = new DnsRecord { Id = id, RecordType = "TXT", Key = "_token.example.com", Value = "Token=ABC", Enabled = true };
            var desired = current.Clone();
            desired.Value = "Token=abc";
            if (ImportService.IdentityKey(current) == ImportService.IdentityKey(desired))
                throw new Exception("TXT identity collapsed case-sensitive text values.");

            var bundle = new PolicyBundle
            {
                HasDnsSection = true,
                HasAclSection = false,
                HasFirewallSection = false,
                DnsRecords = [desired]
            };
            var plan = PolicyChangeService.BuildPlan(bundle, "self-test.json", [current], [], [], synchronizeDeletes: false);
            if (plan.Items.Count != 1 || plan.Items[0].Action != PolicyChangeAction.Update || plan.Items[0].CurrentId != id)
                throw new Exception("A same-site DNS record with a changed TXT value was not matched by official UUID.");
            return Task.CompletedTask;
        });

        await CheckAsync("official_acl_and_firewall_json_validation", () =>
        {
            _ = OfficialPolicyJson.NormalizeAndValidate(OfficialPolicyKind.Acl, OfficialPolicyJson.CreateTemplate(OfficialPolicyKind.Acl));
            var mac = OfficialPolicyJson.CreateTemplate(OfficialPolicyKind.Acl, "MAC").Replace("<NETWORK_UUID>", "40000000-0000-0000-0000-000000000001");
            _ = OfficialPolicyJson.NormalizeAndValidate(OfficialPolicyKind.Acl, mac);
            var firewall = OfficialPolicyJson.CreateTemplate(OfficialPolicyKind.Firewall)
                .Replace("<SOURCE_ZONE_UUID>", "30000000-0000-0000-0000-000000000001")
                .Replace("<DESTINATION_ZONE_UUID>", "30000000-0000-0000-0000-000000000002");
            _ = OfficialPolicyJson.NormalizeAndValidate(OfficialPolicyKind.Firewall, firewall);
            return Task.CompletedTask;
        });

        await CheckAsync("official_api_catalog_covers_network_10_4_57", async () =>
        {
            var operations = OfficialApiCatalog.Operations;
            if (operations.Count != 73) throw new Exception($"Expected 73 official operations, got {operations.Count}.");
            if (operations.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != operations.Count)
                throw new Exception("Official API operation IDs are not unique.");
            if (operations.Any(item => !item.PathTemplate.StartsWith("/v1/", StringComparison.Ordinal)))
                throw new Exception("An official API operation is outside the /v1 namespace.");
            if (operations.Any(item => item.Method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")))
                throw new Exception("An official API operation uses an unsupported HTTP method.");
            if (operations.Any(item => !OfficialApiCatalog.Modules.Any(module => module.Id == item.ModuleId)))
                throw new Exception("An official API operation references an unknown sidebar module.");
            var fingerprintSource = string.Join('\n', operations
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => $"{item.Id}|{item.Method}|{item.PathTemplate}"));
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)));
            if (fingerprint != "1341E65B95B553EFDE7711386C138431A42D407424968112518E2A5158C35DD6")
                throw new Exception($"The operation catalog does not match the official Network v10.4.57 fingerprint: {fingerprint}.");

            var expectedModuleCounts = new Dictionary<string, int>
            {
                ["application"] = 2, ["devices"] = 8, ["clients"] = 3, ["networks"] = 6,
                ["wifi"] = 5, ["hotspot"] = 5, ["firewall"] = 13, ["acl"] = 7,
                ["switching"] = 6, ["dns"] = 5, ["traffic"] = 5, ["resources"] = 8
            };
            foreach (var (module, expected) in expectedModuleCounts)
            {
                var actual = OfficialApiCatalog.ForModule(module).Count;
                if (actual != expected) throw new Exception($"Module {module} expected {expected} operations, got {actual}.");
            }

            foreach (var operation in operations.Where(item => item.HasBody))
            {
                if (string.IsNullOrWhiteSpace(operation.DefaultBody))
                    throw new Exception($"{operation.Id} is missing a request-body template.");
                using var _ = JsonDocument.Parse(operation.DefaultBody);
            }

            var portAction = operations.Single(item => item.Id == "executePortAction");
            var resolved = portAction.ResolvePath("site/id", new Dictionary<string, string>
            {
                ["deviceId"] = "device/id",
                ["portIdx"] = "3"
            });
            if (resolved.Contains('{') || !resolved.Contains("site%2Fid", StringComparison.OrdinalIgnoreCase)
                || !resolved.Contains("device%2Fid", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Official path parameter replacement or escaping failed.");

            var ordering = operations.Single(item => item.Id == "getFirewallPolicyOrdering");
            try
            {
                ordering.ValidateQuery(ordering.DefaultQuery);
                throw new Exception("Required firewall ordering query parameters were not enforced.");
            }
            catch (InvalidOperationException) { }
            ordering.ValidateQuery("sourceFirewallZoneId=source-zone&destinationFirewallZoneId=destination-zone");

            using var demo = new DemoUniFiClient();
            var response = await demo.ExecuteOfficialApiAsync("GET", "/v1/info");
            using var responseDocument = JsonDocument.Parse(response);
            if (!responseDocument.RootElement.TryGetProperty("demo", out var demoFlag) || !demoFlag.GetBoolean())
                throw new Exception("The demo client did not execute a generic official endpoint.");
        });

        await CheckAsync("official_resource_presentation_uses_live_response_fields", async () =>
        {
            using var demo = new DemoUniFiClient();
            var response = await demo.ExecuteOfficialApiAsync("GET", "/v1/sites/demo/devices?offset=0&limit=50");
            var snapshot = OfficialResourcePresentationService.Parse(response, "devices");
            if (snapshot.Items.Count != 4 || snapshot.TotalCount != 4)
                throw new Exception("Visual device resources were not parsed from the official response envelope.");
            if (snapshot.HealthyCount != 3 || snapshot.AttentionCount != 1)
                throw new Exception("Visual health metrics were not derived from returned device states.");
            if (!snapshot.Items.Any(item => item.Name == "Cloud Gateway Fiber" && item.Type == "UCG-Fiber"))
                throw new Exception("Visual resource fields did not preserve the returned device identity and model.");
            if (snapshot.TypeDistribution.Count == 0)
                throw new Exception("Visual resource type distribution was not generated.");
        });

        await CheckAsync("official_resource_paging_health_and_context_actions", () =>
        {
            var countriesPageOne = OfficialResourcePresentationService.Parse(
                "{\"data\":[{\"code\":\"CN\",\"name\":\"China\"},{\"code\":\"US\",\"name\":\"United States\"}],\"totalCount\":3}",
                "resources");
            var countriesPageTwo = OfficialResourcePresentationService.Parse(
                "{\"data\":[{\"code\":\"US\",\"name\":\"United States\"},{\"code\":\"JP\",\"name\":\"Japan\"}],\"totalCount\":3}",
                "resources");
            var merged = OfficialResourceInteractionService.MergePages([countriesPageOne, countriesPageTwo]);
            if (merged.Items.Count != 3 || merged.TotalCount != 3)
                throw new Exception("Paged official resources were not merged and deduplicated.");
            if (merged.HasHealthData || merged.HealthyCount != 0 || merged.AttentionCount != 0 || merged.UnknownCount != 0)
                throw new Exception("Reference resources without a status field were incorrectly classified as healthy or unknown.");

            var networkList = OfficialApiCatalog.Operations.Single(operation => operation.Id == "getNetworksOverviewPage");
            var networkOperations = OfficialApiCatalog.ForModule("networks");
            if (OfficialResourceInteractionService.FindPrimaryOperation(networkList, networkOperations)?.Id != "createNetwork")
                throw new Exception("The network list did not resolve its collection create action.");
            var networkItemActions = OfficialResourceInteractionService.FindItemOperations(networkList, networkOperations);
            if (!new[] { "getNetworkDetails", "updateNetwork", "deleteNetwork", "getNetworkReferences" }
                .All(id => networkItemActions.Any(operation => operation.Id == id)))
                throw new Exception("Network item CRUD and reference actions were not resolved for the inspector.");
            if (OfficialResourceInteractionService.FindDetailOperation(networkList, networkOperations)?.Id != "getNetworkDetails")
                throw new Exception("The update safety prefetch did not select the network details endpoint.");

            var countryList = OfficialApiCatalog.Operations.Single(operation => operation.Id == "getCountries");
            if (OfficialResourceInteractionService.FindItemOperations(countryList, OfficialApiCatalog.ForModule("resources")).Count != 0)
                throw new Exception("Read-only country resources unexpectedly exposed item write actions.");
            var pendingList = OfficialApiCatalog.Operations.Single(operation => operation.Id == "getPendingDevicePage");
            if (OfficialResourceInteractionService.FindItemOperations(pendingList, OfficialApiCatalog.ForModule("devices")).Single().Id != "adoptDevice")
                throw new Exception("Pending devices did not expose the official adopt action.");

            var pageQuery = OfficialResourceInteractionService.BuildPageQuery("offset=0&limit=50&filter=active", 100, 50);
            if (!pageQuery.Contains("offset=100", StringComparison.Ordinal)
                || !pageQuery.Contains("limit=50", StringComparison.Ordinal)
                || !pageQuery.Contains("filter=active", StringComparison.Ordinal))
                throw new Exception("Paging query generation did not preserve existing query values.");
            return Task.CompletedTask;
        });

        await CheckAsync("official_update_form_prefills_selected_resource", () =>
        {
            var operation = OfficialApiCatalog.Operations.Single(item => item.Id == "updateNetwork");
            var selected = new OfficialResourceItem
            {
                Id = "network-id",
                RawJson = "{\"id\":\"network-id\",\"name\":\"Existing Network\",\"enabled\":false,\"management\":\"UNMANAGED\",\"vlanId\":42}"
            };
            var dialog = new OfficialOperationDialog(operation, "site-id", selected);
            var fields = dialog.FieldsGrid.Items.Cast<OperationFieldRow>().ToList();
            if (fields.Single(field => field.Key == "networkId").Value != "network-id"
                || fields.Single(field => field.Key == "name").Value != "Existing Network"
                || fields.Single(field => field.Key == "enabled").Value != "false"
                || fields.Single(field => field.Key == "vlanId").Value != "42")
                throw new Exception("The update form did not prefill editable template fields from the selected resource.");
            dialog.Close();
            return Task.CompletedTask;
        });

        await CheckAsync("secure_api_key_settings_roundtrip", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"unifi-policy-manager-settings-{Guid.NewGuid():N}");
            try
            {
                var service = new SecureSettingsService(directory);
                const string secret = "self-test-api-key-value";
                service.Save("192.0.2.1", verifyTls: true, rememberApiKey: true, apiKey: secret);
                var loaded = service.Load();
                if (loaded.Host != "192.0.2.1" || !loaded.VerifyTls || !loaded.RememberApiKey || loaded.ApiKey != secret)
                    throw new Exception("Encrypted connection settings did not round-trip.");
                if (File.ReadAllText(service.SettingsPath).Contains(secret, StringComparison.Ordinal))
                    throw new Exception("API Key was written to settings in plaintext.");
                service.ForgetApiKey("192.0.2.1", verifyTls: true);
                var forgotten = service.Load();
                if (forgotten.RememberApiKey || forgotten.ApiKey.Length > 0)
                    throw new Exception("Saved API Key was not removed.");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            return Task.CompletedTask;
        });

        await CheckAsync("wpf_window_initialization", () =>
        {
            var main = new MainWindow(demoMode: true);
            if (main.Icon is null) throw new Exception("Main window icon resource was not loaded.");
            if (main.FindName("BatchDeletePanelButton") is null) throw new Exception("Batch delete panel button was not loaded.");
            if (main.FindName("BatchDeleteSelectionText") is null) throw new Exception("Batch delete selection summary was not loaded.");
            if (main.FindName("BatchRulesTextBox") is null) throw new Exception("Batch DNS rules editor was not loaded.");
            if (main.FindName("LoadBundledRulesButton") is null) throw new Exception("Bundled forward-domain rules button was not loaded.");
            if (main.FindName("ChangeCenterPage") is null) throw new Exception("Policy change center was not loaded.");
            if (main.FindName("ChangePlanGrid") is null) throw new Exception("Policy change plan grid was not loaded.");
            if (main.FindName("RememberApiKeyCheckBox") is null) throw new Exception("Remember API Key checkbox was not loaded.");
            if (main.FindName("ForgetApiKeyButton") is null) throw new Exception("Forget API Key button was not loaded.");
            if (main.FindName("SidebarScrollViewer") is null) throw new Exception("The scrollable sidebar was not loaded.");
            var sidebarGroups = new[]
            {
                main.FindName("WorkspaceNavGroup") as System.Windows.Controls.Expander,
                main.FindName("NetworkResourcesNavGroup") as System.Windows.Controls.Expander,
                main.FindName("PolicySecurityNavGroup") as System.Windows.Controls.Expander,
                main.FindName("InfrastructureNavGroup") as System.Windows.Controls.Expander
            };
            if (sidebarGroups.Any(group => group is null))
                throw new Exception("One or more independently collapsible sidebar groups were not loaded.");
            if (sidebarGroups.Any(group => group!.IsExpanded != true))
                throw new Exception("Sidebar navigation groups must start expanded.");
            sidebarGroups[1]!.IsExpanded = false;
            if (sidebarGroups[1]!.IsExpanded || sidebarGroups[0]!.IsExpanded != true
                || sidebarGroups[2]!.IsExpanded != true || sidebarGroups[3]!.IsExpanded != true)
                throw new Exception("Collapsing one sidebar group changed another group's expansion state.");
            sidebarGroups[1]!.IsExpanded = true;
            if (sidebarGroups.Any(group => group!.IsExpanded != true))
                throw new Exception("The collapsed sidebar group did not expand independently.");
            if (main.FindName("OfficialApiPage") is not OfficialApiWorkspace apiWorkspace)
                throw new Exception("The official API workspace was not loaded.");
            if (main.FindName("VisualResourcePage") is not VisualResourceWorkspace visualWorkspace)
                throw new Exception("The visual resource workspace was not loaded.");
            if (visualWorkspace.FindName("DrawerActionsList") is null || visualWorkspace.FindName("DrawerCapabilityText") is null)
                throw new Exception("The visual resource inspector does not expose contextual actions and capability guidance.");
            var rowStyle = System.Windows.Application.Current.TryFindResource(typeof(System.Windows.Controls.DataGridRow)) as System.Windows.Style
                ?? throw new Exception("The shared DataGridRow style was not found.");
            var cursorSetter = rowStyle.Setters.OfType<System.Windows.Setter>()
                .FirstOrDefault(setter => setter.Property == System.Windows.FrameworkElement.CursorProperty);
            if (cursorSetter?.Value is not System.Windows.Input.Cursor cursor || cursor != System.Windows.Input.Cursors.Hand)
                throw new Exception("Clickable table rows do not use the hand cursor.");
            apiWorkspace.ShowModule("all");
            var dnsTabs = main.FindName("DnsTypeTabs") as System.Windows.Controls.TabControl
                ?? throw new Exception("DNS type tab navigation was not loaded.");
            if (dnsTabs.Items.Count != DnsTypes.All.Length) throw new Exception("DNS type navigation must contain exactly seven tabs.");
            var tabTypes = dnsTabs.Items.Cast<System.Windows.Controls.TabItem>().Select(item => item.Tag?.ToString()).ToArray();
            if (!tabTypes.SequenceEqual(DnsTypes.All)) throw new Exception("DNS tabs are not in the official record-type order.");
            if (main.FindName("AclUserSegment") is null || main.FindName("AclSystemSegment") is null)
                throw new Exception("ACL origin segmented navigation was not loaded.");
            if (main.FindName("FirewallUserSegment") is null || main.FindName("FirewallSystemSegment") is null)
                throw new Exception("Firewall origin segmented navigation was not loaded.");
            main.Close();
            foreach (var type in DnsTypes.All)
            {
                var editor = new RecordEditorWindow(initialType: type);
                var typePicker = editor.FindName("TypeComboBox") as System.Windows.Controls.ComboBox
                    ?? throw new Exception("DNS editor type picker was not loaded.");
                var selectedType = (typePicker.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
                if (selectedType != type) throw new Exception($"DNS editor did not inherit the {type} tab.");
                editor.Close();
            }
            foreach (var record in AllRecordTypes())
            {
                var editor = new RecordEditorWindow(DnsValidator.Normalize(record));
                if (editor.Icon is null) throw new Exception("Record editor icon resource was not loaded.");
                editor.Close();
            }
            var addPreview = new BatchPreview(
                [new DnsRecord { RecordType = "A", Key = "one.example.com", Value = "192.0.2.10", Ttl = 0 }],
                [new DnsRecord { RecordType = "NS", Key = "existing.example.com", Value = "192.168.1.10" }],
                ["A | duplicate.example.com"], ["invalid"]);
            var addWindow = new BatchPreviewWindow(addPreview);
            if (addWindow.Icon is null) throw new Exception("Batch preview icon resource was not loaded.");
            addWindow.Close();
            var deleteWindow = new BatchPreviewWindow([new DnsRecord { Id = "test", RecordType = "NS", Key = "one.example.com", Value = "192.168.1.10" }]);
            deleteWindow.Close();
            var siteWindow = new SiteSelectionWindow([new UniFiSite("00000000-0000-0000-0000-000000000001", "default", "Default")]);
            if (siteWindow.Icon is null) throw new Exception("Site selection icon resource was not loaded.");
            siteWindow.Close();
            var references = new[] { new PolicyReferenceItem("30000000-0000-0000-0000-000000000001", "Internal", "防火墙区域") };
            var policyEditor = new PolicyJsonEditorWindow(OfficialPolicyKind.Acl, references);
            if (policyEditor.Icon is null) throw new Exception("Policy editor icon resource was not loaded.");
            policyEditor.Close();
            return Task.CompletedTask;
        });

        await CheckAsync("datagrid_cell_template_centers_content", () =>
        {
            var style = System.Windows.Application.Current.TryFindResource(typeof(System.Windows.Controls.DataGridCell)) as System.Windows.Style
                ?? throw new Exception("The shared DataGridCell style was not found.");
            var templateSetter = style.Setters
                .OfType<System.Windows.Setter>()
                .FirstOrDefault(setter => setter.Property == System.Windows.Controls.Control.TemplateProperty)
                ?? throw new Exception("The DataGridCell style does not define a content-centering template.");
            var template = templateSetter.Value as System.Windows.Controls.ControlTemplate
                ?? throw new Exception("The DataGridCell template setter is invalid.");
            var cell = new System.Windows.Controls.DataGridCell
            {
                Template = template,
                Content = new System.Windows.Controls.TextBlock { Text = "center probe" }
            };
            cell.ApplyTemplate();
            var presenter = FindVisualDescendant<System.Windows.Controls.ContentPresenter>(cell)
                ?? throw new Exception("The DataGridCell template did not create a ContentPresenter.");
            if (presenter.VerticalAlignment != System.Windows.VerticalAlignment.Center)
                throw new Exception("The DataGridCell ContentPresenter is not vertically centered.");
            return Task.CompletedTask;
        });

        await CheckAsync("demo_crud_all_dns_types", async () =>
        {
            using var client = new DemoUniFiClient();
            foreach (var input in AllRecordTypes())
            {
                var created = await client.CreateRecordAsync(input);
                if (string.IsNullOrWhiteSpace(created?.Id)) throw new Exception($"{input.RecordType} create did not return an ID.");
                created.Enabled = false;
                var updated = await client.UpdateRecordAsync(created.Id, created);
                if (updated?.Enabled != false) throw new Exception($"{input.RecordType} update failed.");
                await client.DeleteRecordAsync(created.Id);
                if ((await client.ListRecordsAsync()).Any(item => item.Id == created.Id))
                    throw new Exception($"{input.RecordType} delete failed.");
            }
        });

        await CheckAsync("demo_crud_official_policy_types", async () =>
        {
            using var client = new DemoUniFiClient();
            var aclJson = OfficialPolicyJson.CreateTemplate(OfficialPolicyKind.Acl);
            var createdAcl = await client.CreatePolicyAsync(OfficialPolicyKind.Acl, aclJson) ?? throw new Exception("ACL create returned null.");
            var disabledAcl = OfficialPolicyJson.WithEnabled(OfficialPolicyKind.Acl, createdAcl.ToEditableJson(), false);
            await client.UpdatePolicyAsync(OfficialPolicyKind.Acl, createdAcl.Id, disabledAcl);
            await client.MovePolicyAsync(OfficialPolicyKind.Acl, createdAcl.Id, -1);
            await client.DeletePolicyAsync(OfficialPolicyKind.Acl, createdAcl.Id);

            var firewallJson = OfficialPolicyJson.CreateTemplate(OfficialPolicyKind.Firewall)
                .Replace("<SOURCE_ZONE_UUID>", "30000000-0000-0000-0000-000000000001")
                .Replace("<DESTINATION_ZONE_UUID>", "30000000-0000-0000-0000-000000000002");
            var createdFirewall = await client.CreatePolicyAsync(OfficialPolicyKind.Firewall, firewallJson) ?? throw new Exception("Firewall create returned null.");
            await client.DeletePolicyAsync(OfficialPolicyKind.Firewall, createdFirewall.Id);
        });

        await CheckAsync("policy_change_plan_roundtrip_and_execute", async () =>
        {
            using var client = new DemoUniFiClient();
            var dns = (await client.ListRecordsAsync()).ToList();
            var acl = (await client.ListPoliciesAsync(OfficialPolicyKind.Acl)).ToList();
            var firewall = (await client.ListPoliciesAsync(OfficialPolicyKind.Firewall)).ToList();
            var bundle = PolicyChangeService.CaptureBundle(
                client, dns, acl, firewall,
                await client.GetPolicyOrderingAsync(OfficialPolicyKind.Acl),
                await client.GetPolicyOrderingAsync(OfficialPolicyKind.Firewall));

            bundle.DnsRecords.First(record => record.RecordType == "NS").Value = "192.168.1.53";
            bundle.DnsRecords.RemoveAll(record => record.RecordType == "TXT");
            bundle.DnsRecords.Add(new DnsRecord { RecordType = "A", Key = "new.example.com", Value = "192.0.2.55", Ttl = 300, Enabled = true });

            var aclNode = JsonNode.Parse(bundle.AclRules[0].GetRawText())!.AsObject();
            aclNode["enabled"] = false;
            bundle.AclRules[0] = ParseElement(aclNode.ToJsonString());

            var newFirewallSourceId = Guid.NewGuid().ToString();
            var firewallNode = JsonNode.Parse(bundle.FirewallPolicies[0].GetRawText())!.AsObject();
            firewallNode["id"] = newFirewallSourceId;
            firewallNode["index"] = 1;
            firewallNode["name"] = "第二条演示防火墙策略";
            firewallNode["enabled"] = false;
            bundle.FirewallPolicies.Add(ParseElement(firewallNode.ToJsonString()));
            bundle.FirewallOrdering!.BeforeSystemDefined.Add(newFirewallSourceId);

            var temp = Path.Combine(Path.GetTempPath(), $"unifi-policy-bundle-{Guid.NewGuid():N}.json");
            try
            {
                await PolicyChangeService.SaveBundleAsync(temp, bundle);
                bundle = await PolicyChangeService.LoadBundleAsync(temp);
                var plan = PolicyChangeService.BuildPlan(bundle, temp, dns, acl, firewall, synchronizeDeletes: true);
                if (plan.AddCount != 2 || plan.UpdateCount != 2 || plan.DeleteCount != 1 || plan.InvalidCount != 0)
                    throw new Exception($"Unexpected change plan counts: add={plan.AddCount}, update={plan.UpdateCount}, delete={plan.DeleteCount}, invalid={plan.InvalidCount}. Updates: {string.Join(" | ", plan.Items.Where(item => item.Action == PolicyChangeAction.Update).Select(item => item.Name))}");

                foreach (var item in plan.Items.Where(item => item.IsActionable))
                {
                    item.IsSelected = true;
                    await PolicyChangeExecutor.ExecuteItemAsync(client, item);
                }
                await PolicyChangeExecutor.RestoreOrderingAsync(client, plan);

                var finalPlan = PolicyChangeService.BuildPlan(
                    bundle, temp,
                    await client.ListRecordsAsync(),
                    await client.ListPoliciesAsync(OfficialPolicyKind.Acl),
                    await client.ListPoliciesAsync(OfficialPolicyKind.Firewall),
                    synchronizeDeletes: true);
                if (finalPlan.Items.Any(item => item.IsActionable))
                    throw new Exception("Executing the change plan did not converge to the imported baseline.");
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        });

        await CheckAsync("legacy_dns_backup_does_not_delete_other_policy_types", async () =>
        {
            using var client = new DemoUniFiClient();
            var temp = Path.Combine(Path.GetTempPath(), $"unifi-legacy-dns-{Guid.NewGuid():N}.json");
            try
            {
                var dns = await client.ListRecordsAsync();
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new { created_at = DateTimeOffset.Now, records = dns }));
                var bundle = await PolicyChangeService.LoadBundleAsync(temp);
                var plan = PolicyChangeService.BuildPlan(
                    bundle, temp, dns,
                    await client.ListPoliciesAsync(OfficialPolicyKind.Acl),
                    await client.ListPoliciesAsync(OfficialPolicyKind.Firewall),
                    synchronizeDeletes: true);
                if (plan.Items.Any(item => item.Scope is PolicyChangeScope.Acl or PolicyChangeScope.Firewall))
                    throw new Exception("A legacy DNS-only backup generated ACL or firewall changes.");
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        });

        await CheckAsync("invalid_import_suppresses_strict_sync_deletes", async () =>
        {
            using var client = new DemoUniFiClient();
            var bundle = new PolicyBundle
            {
                DnsRecords = [new DnsRecord { RecordType = "A", Key = "", Value = "not-an-ip" }],
                HasAclSection = false,
                HasFirewallSection = false
            };
            var plan = PolicyChangeService.BuildPlan(
                bundle, "invalid.json",
                await client.ListRecordsAsync(), [], [],
                synchronizeDeletes: true);
            if (plan.InvalidCount == 0 || plan.DeleteCount != 0)
                throw new Exception("Invalid DNS input did not suppress strict-sync deletion items.");
        });

        await CheckAsync("xlsx_domain_column", () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), $"unifi-dns-test-{Guid.NewGuid():N}.xlsx");
            try
            {
                CreateTestXlsx(temp);
                var result = ImportService.ImportFile(temp);
                if (!result.Domains.SequenceEqual(["one.example.com", "two.example.com"]))
                    throw new Exception("Spreadsheet domain-column detection failed: " + string.Join(',', result.Domains));
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return Task.CompletedTask;
        });

        await CheckAsync("batch_import_all_dns_types", () =>
        {
            const string text =
                "类型,域名,值或服务器,TTL,优先级,权重,端口,服务,协议,启用\n" +
                "NS,forward.example.com,192.168.1.10,,,,,,,TRUE\n" +
                "A,a.example.com,192.0.2.10,0,,,,,,TRUE\n" +
                "AAAA,aaaa.example.com,2001:db8::10,3600,,,,,,TRUE\n" +
                "CNAME,cname.example.com,target.example.com,300,,,,,,TRUE\n" +
                "MX,mx.example.com,mail.example.com,,10,,,,,TRUE\n" +
                "TXT,_dmarc.example.com,v=DMARC1; p=none,,,,,,,TRUE\n" +
                "SRV,srv.example.com,sip.example.com,,10,5,5060,_sip,_tcp,TRUE\n";
            var result = ImportService.ParseText(text);
            if (result.Records.Count != 7 || result.DuplicateInput.Count != 0 || result.Invalid.Count != 0)
                throw new Exception($"Expected 7/0/0, got {result.Records.Count}/{result.DuplicateInput.Count}/{result.Invalid.Count}.");
            if (!result.Records.Select(record => record.RecordType).SequenceEqual(DnsTypes.All))
                throw new Exception("Batch import did not preserve all DNS record types.");
            foreach (var record in result.Records) _ = OfficialDnsPolicyMapper.BuildPayload(record);
            var roundTrip = ImportService.ParseText(ImportService.FormatRecordsForEditor(result.Records));
            if (roundTrip.Records.Count != 7 || roundTrip.Invalid.Count != 0)
                throw new Exception("Batch editor text did not round-trip all DNS records.");
            return Task.CompletedTask;
        });

        await CheckAsync("csv_template_roundtrip", () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), $"unifi-dns-template-{Guid.NewGuid():N}.csv");
            try
            {
                ImportService.SaveDnsRulesCsvTemplate(temp);
                var bytes = File.ReadAllBytes(temp);
                if (bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF)
                    throw new Exception("CSV template is missing the UTF-8 BOM required for Excel compatibility.");
                var untouched = ImportService.ImportFile(temp);
                if (untouched.Records.Count != 0 || untouched.Invalid.Count != 0)
                    throw new Exception("Commented template examples must not be imported.");
                var text = File.ReadAllText(temp, Encoding.UTF8).Replace("# NS,example.com", "NS,example.com", StringComparison.Ordinal);
                File.WriteAllText(temp, text, new UTF8Encoding(true));
                var filled = ImportService.ImportFile(temp);
                if (!filled.Domains.SequenceEqual(["example.com"]))
                    throw new Exception("Filled CSV template did not import the expected domain.");
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return Task.CompletedTask;
        });

        await CheckAsync("bundled_forward_domain_preset", () =>
        {
            const string dnsServer = "192.0.2.53";
            var result = ImportService.ImportBundledForwardDomains(dnsServer);
            if (result.Records.Count != 212 || result.DuplicateInput.Count != 0 || result.Invalid.Count != 0)
                throw new Exception($"Expected bundled preset 212/0/0, got {result.Records.Count}/{result.DuplicateInput.Count}/{result.Invalid.Count}.");
            if (result.Records.Any(record => record.RecordType != "NS" || record.Value != dnsServer))
                throw new Exception("The bundled preset did not apply the user-supplied default DNS server to every forward domain.");
            return Task.CompletedTask;
        });

        var realXlsx = Environment.GetEnvironmentVariable("UNIFI_DNS_TEST_XLSX");
        if (!string.IsNullOrWhiteSpace(realXlsx) && File.Exists(realXlsx))
        {
            await CheckAsync("provided_xlsx_78_domains", () =>
            {
                var result = ImportService.ImportFile(realXlsx);
                if (result.Domains.Count != 78 || result.DuplicateInput.Count != 0 || result.Invalid.Count != 0)
                    throw new Exception($"Expected 78/0/0, got {result.Domains.Count}/{result.DuplicateInput.Count}/{result.Invalid.Count}.");
                return Task.CompletedTask;
            });
        }

        var realCsv = Environment.GetEnvironmentVariable("UNIFI_DNS_TEST_CSV");
        if (!string.IsNullOrWhiteSpace(realCsv) && File.Exists(realCsv))
        {
            await CheckAsync("provided_dns_rules_csv", () =>
            {
                var result = ImportService.ImportFile(realCsv);
                var expected = int.TryParse(Environment.GetEnvironmentVariable("UNIFI_DNS_TEST_CSV_COUNT"), out var count) ? count : result.Records.Count;
                if (result.Records.Count != expected || result.DuplicateInput.Count != 0 || result.Invalid.Count != 0)
                    throw new Exception($"Expected {expected}/0/0, got {result.Records.Count}/{result.DuplicateInput.Count}/{result.Invalid.Count}.");
                foreach (var record in result.Records) _ = OfficialDnsPolicyMapper.BuildPayload(record);
                return Task.CompletedTask;
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new { success, at = DateTimeOffset.Now, tests }, new JsonSerializerOptions { WriteIndented = true }));
        return success;
    }

    private static DnsRecord[] AllRecordTypes() =>
    [
        new() { RecordType="NS", Key="forward-test.example.com", Value="192.168.1.10" },
        new() { RecordType="A", Key="a-test.example.com", Value="192.0.2.10", Ttl=0 },
        new() { RecordType="AAAA", Key="aaaa-test.example.com", Value="2001:0db8::10", Ttl=3600 },
        new() { RecordType="CNAME", Key="cname-test.example.com", Value="target.example.com", Ttl=300 },
        new() { RecordType="MX", Key="mx-test.example.com", Value="mail.example.com", Priority=10 },
        new() { RecordType="TXT", Key="_dmarc-test.example.com", Value="v=DMARC1; p=none" },
        new() { RecordType="SRV", Domain="srv-test.example.com", Service="_sip", Protocol="_tcp", Value="sip.example.com", Port=5060, Priority=10, Weight=5 }
    ];

    private static void CreateTestXlsx(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"></Types>");
        Write(archive, "xl/sharedStrings.xml", "<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>序号</t></si><si><t>新增域名</t></si><si><t>参考来源</t></si><si><t>1</t></si><si><t>one.example.com</t></si><si><t>https://github.com/example/list</t></si><si><t>2</t></si><si><t>two.example.com</t></si><si><t>https://example.org/source</t></si></sst>");
        Write(archive, "xl/worksheets/sheet1.xml", "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row><row r=\"2\"><c r=\"A2\" t=\"s\"><v>3</v></c><c r=\"B2\" t=\"s\"><v>4</v></c><c r=\"C2\" t=\"s\"><v>5</v></c></row><row r=\"3\"><c r=\"A3\" t=\"s\"><v>6</v></c><c r=\"B3\" t=\"s\"><v>7</v></c><c r=\"C3\" t=\"s\"><v>8</v></c></row></sheetData></worksheet>");
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static T? FindVisualDescendant<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
