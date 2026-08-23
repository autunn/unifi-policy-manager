using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniFiDnsManager.Models;

namespace UniFiDnsManager.Services;

public sealed class UniFiClient : IUniFiClient
{
    private const int PageSize = 200;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions DisplayJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private UniFiSite? _selectedSite;

    public string Target { get; }
    public string Site => _selectedSite?.DisplayName ?? "未选择";
    public string SiteId => _selectedSite?.Id ?? string.Empty;
    public string ApplicationVersion { get; private set; } = "未知";
    public IReadOnlyList<UniFiSite> Sites { get; private set; } = [];

    private string ApiRoot => "/proxy/network/integration/v1";
    private string DnsPoliciesPath => $"{ApiRoot}/sites/{Uri.EscapeDataString(RequireSiteId())}/dns/policies";
    private string AclRulesPath => $"{ApiRoot}/sites/{Uri.EscapeDataString(RequireSiteId())}/acl-rules";
    private string FirewallPoliciesPath => $"{ApiRoot}/sites/{Uri.EscapeDataString(RequireSiteId())}/firewall/policies";

    private UniFiClient(string target, string apiKey, bool verifyTls)
    {
        Target = NormalizeTarget(target);
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        if (!verifyTls)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(Target),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("UniFi-Policy-Manager/4.1.1");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", apiKey);
    }

    public static async Task<UniFiClient> ConnectAsync(
        string target,
        string apiKey,
        bool verifyTls,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new UniFiApiException("请输入 UniFi API Key。可在 unifi.ui.com → Settings → API Keys 创建。");

        var client = new UniFiClient(target, apiKey.Trim(), verifyTls);
        try
        {
            using (var info = await client.SendAsync(HttpMethod.Get, $"{client.ApiRoot}/info", null, cancellationToken))
            {
                if (info.RootElement.TryGetProperty("applicationVersion", out var version) && version.ValueKind == JsonValueKind.String)
                    client.ApplicationVersion = version.GetString() ?? "未知";
            }

            client.Sites = await client.ListSitesAsync(cancellationToken);
            if (client.Sites.Count == 0)
                throw new UniFiApiException("API Key 可用，但此 Network 应用没有返回任何可管理站点。");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void SelectSite(UniFiSite site)
    {
        if (!Guid.TryParse(site.Id, out _)) throw new UniFiApiException("站点 ID 不是有效的 UUID。");
        if (!Sites.Any(item => string.Equals(item.Id, site.Id, StringComparison.OrdinalIgnoreCase)))
            throw new UniFiApiException("所选站点不属于当前 Network 应用。");
        _selectedSite = site;
    }

    public async Task<IReadOnlyList<UniFiSite>> ListSitesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<UniFiSite>();
        var offset = 0;
        while (true)
        {
            using var document = await SendAsync(HttpMethod.Get, $"{ApiRoot}/sites?offset={offset}&limit={PageSize}", null, cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new UniFiApiException("UniFi 站点列表响应格式不正确。");

            foreach (var item in data.EnumerateArray())
            {
                var id = GetRequiredString(item, "id");
                var internalReference = GetRequiredString(item, "internalReference");
                var name = GetRequiredString(item, "name");
                result.Add(new UniFiSite(id, internalReference, name));
            }

            var count = root.TryGetProperty("count", out var countElement) && countElement.TryGetInt32(out var pageCount)
                ? pageCount
                : data.GetArrayLength();
            var total = root.TryGetProperty("totalCount", out var totalElement) && totalElement.TryGetInt64(out var totalCount)
                ? totalCount
                : result.Count;
            offset += count;
            if (count == 0 || offset >= total) break;
        }
        return result;
    }

    public async Task<IReadOnlyList<DnsRecord>> ListRecordsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<DnsRecord>();
        var offset = 0;
        while (true)
        {
            using var document = await SendAsync(HttpMethod.Get, $"{DnsPoliciesPath}?offset={offset}&limit={PageSize}", null, cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new UniFiApiException("UniFi DNS Policy 列表响应格式不正确。");

            foreach (var item in data.EnumerateArray()) result.Add(OfficialDnsPolicyMapper.Parse(item));

            var count = root.TryGetProperty("count", out var countElement) && countElement.TryGetInt32(out var pageCount)
                ? pageCount
                : data.GetArrayLength();
            var total = root.TryGetProperty("totalCount", out var totalElement) && totalElement.TryGetInt64(out var totalCount)
                ? totalCount
                : result.Count;
            offset += count;
            if (count == 0 || offset >= total) break;
        }
        return result;
    }

    public async Task<DnsRecord?> CreateRecordAsync(DnsRecord record, CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync(HttpMethod.Post, DnsPoliciesPath, OfficialDnsPolicyMapper.BuildPayload(record), cancellationToken);
        return document.RootElement.ValueKind == JsonValueKind.Object ? OfficialDnsPolicyMapper.Parse(document.RootElement) : null;
    }

    public async Task<DnsRecord?> UpdateRecordAsync(string id, DnsRecord record, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out _)) throw new UniFiApiException("DNS Policy ID 不是有效的 UUID。");
        using var document = await SendAsync(HttpMethod.Put, $"{DnsPoliciesPath}/{Uri.EscapeDataString(id)}", OfficialDnsPolicyMapper.BuildPayload(record), cancellationToken);
        return document.RootElement.ValueKind == JsonValueKind.Object ? OfficialDnsPolicyMapper.Parse(document.RootElement) : null;
    }

    public async Task DeleteRecordAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out _)) throw new UniFiApiException("DNS Policy ID 不是有效的 UUID。");
        using var responseDocument = await SendAsync(HttpMethod.Delete, $"{DnsPoliciesPath}/{Uri.EscapeDataString(id)}", null, cancellationToken);
    }

    public async Task<IReadOnlyList<OfficialPolicyRule>> ListPoliciesAsync(OfficialPolicyKind kind, CancellationToken cancellationToken = default)
    {
        var path = GetPolicyPath(kind);
        var result = new List<OfficialPolicyRule>();
        var offset = 0;
        while (true)
        {
            using var document = await SendAsync(HttpMethod.Get, $"{path}?offset={offset}&limit={PageSize}", null, cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new UniFiApiException("UniFi 策略列表响应格式不正确。");
            foreach (var item in data.EnumerateArray()) result.Add(OfficialPolicyRule.Parse(kind, item));

            var count = root.TryGetProperty("count", out var countElement) && countElement.TryGetInt32(out var pageCount)
                ? pageCount
                : data.GetArrayLength();
            var total = root.TryGetProperty("totalCount", out var totalElement) && totalElement.TryGetInt64(out var totalCount)
                ? totalCount
                : result.Count;
            offset += count;
            if (count == 0 || offset >= total) break;
        }
        return result.OrderBy(item => item.Index).ToList();
    }

    public async Task<OfficialPolicyRule?> CreatePolicyAsync(OfficialPolicyKind kind, string requestJson, CancellationToken cancellationToken = default)
    {
        var normalized = OfficialPolicyJson.NormalizeAndValidate(kind, requestJson);
        using var document = await SendAsync(HttpMethod.Post, GetPolicyPath(kind), JsonNode.Parse(normalized), cancellationToken);
        return document.RootElement.ValueKind == JsonValueKind.Object ? OfficialPolicyRule.Parse(kind, document.RootElement) : null;
    }

    public async Task<OfficialPolicyRule?> UpdatePolicyAsync(OfficialPolicyKind kind, string id, string requestJson, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out _)) throw new UniFiApiException("策略 ID 不是有效的 UUID。");
        var normalized = OfficialPolicyJson.NormalizeAndValidate(kind, requestJson);
        using var document = await SendAsync(HttpMethod.Put, $"{GetPolicyPath(kind)}/{Uri.EscapeDataString(id)}", JsonNode.Parse(normalized), cancellationToken);
        return document.RootElement.ValueKind == JsonValueKind.Object ? OfficialPolicyRule.Parse(kind, document.RootElement) : null;
    }

    public async Task DeletePolicyAsync(OfficialPolicyKind kind, string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out _)) throw new UniFiApiException("策略 ID 不是有效的 UUID。");
        using var responseDocument = await SendAsync(HttpMethod.Delete, $"{GetPolicyPath(kind)}/{Uri.EscapeDataString(id)}", null, cancellationToken);
    }

    public async Task MovePolicyAsync(OfficialPolicyKind kind, string id, int direction, CancellationToken cancellationToken = default)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!Guid.TryParse(id, out _)) throw new UniFiApiException("策略 ID 不是有效的 UUID。");
        var ordering = await GetPolicyOrderingAsync(kind, cancellationToken);
        if (kind == OfficialPolicyKind.Acl)
        {
            Swap(ordering.OrderedAclRuleIds, id, direction);
        }
        else
        {
            if (ordering.BeforeSystemDefined.Contains(id, StringComparer.OrdinalIgnoreCase)) Swap(ordering.BeforeSystemDefined, id, direction);
            else if (ordering.AfterSystemDefined.Contains(id, StringComparer.OrdinalIgnoreCase)) Swap(ordering.AfterSystemDefined, id, direction);
            else throw new UniFiApiException("该防火墙策略不是可排序的用户定义策略。");
        }
        await SetPolicyOrderingAsync(kind, ordering, cancellationToken);
    }

    public async Task<PolicyOrderingSnapshot> GetPolicyOrderingAsync(OfficialPolicyKind kind, CancellationToken cancellationToken = default)
    {
        var path = $"{GetPolicyPath(kind)}/ordering";
        using var document = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        if (kind == OfficialPolicyKind.Acl)
        {
            return new PolicyOrderingSnapshot
            {
                Kind = kind,
                OrderedAclRuleIds = ReadStringArray(document.RootElement, "orderedAclRuleIds")
            };
        }

        if (!document.RootElement.TryGetProperty("orderedFirewallPolicyIds", out var ordered) || ordered.ValueKind != JsonValueKind.Object)
            throw new UniFiApiException("防火墙排序响应格式不正确。");
        return new PolicyOrderingSnapshot
        {
            Kind = kind,
            BeforeSystemDefined = ReadStringArray(ordered, "beforeSystemDefined"),
            AfterSystemDefined = ReadStringArray(ordered, "afterSystemDefined")
        };
    }

    public async Task SetPolicyOrderingAsync(OfficialPolicyKind kind, PolicyOrderingSnapshot ordering, CancellationToken cancellationToken = default)
    {
        var path = $"{GetPolicyPath(kind)}/ordering";
        JsonObject payload;
        if (kind == OfficialPolicyKind.Acl)
        {
            ValidatePolicyIds(ordering.OrderedAclRuleIds);
            payload = new JsonObject { ["orderedAclRuleIds"] = JsonSerializer.SerializeToNode(ordering.OrderedAclRuleIds) };
        }
        else
        {
            ValidatePolicyIds(ordering.BeforeSystemDefined);
            ValidatePolicyIds(ordering.AfterSystemDefined);
            payload = new JsonObject
            {
                ["orderedFirewallPolicyIds"] = new JsonObject
                {
                    ["beforeSystemDefined"] = JsonSerializer.SerializeToNode(ordering.BeforeSystemDefined),
                    ["afterSystemDefined"] = JsonSerializer.SerializeToNode(ordering.AfterSystemDefined)
                }
            };
        }
        using var responseDocument = await SendAsync(HttpMethod.Put, path, payload, cancellationToken);
    }

    public async Task<IReadOnlyList<PolicyReferenceItem>> ListPolicyReferencesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<PolicyReferenceItem>();
        var siteRoot = $"{ApiRoot}/sites/{Uri.EscapeDataString(RequireSiteId())}";
        await TryAppendReferencesAsync($"{siteRoot}/networks", "网络", result, cancellationToken);
        await TryAppendReferencesAsync($"{siteRoot}/firewall/zones", "防火墙区域", result, cancellationToken);
        await TryAppendReferencesAsync($"{siteRoot}/devices", "设备", result, cancellationToken);
        await TryAppendReferencesAsync($"{siteRoot}/traffic-matching-lists", "流量匹配列表", result, cancellationToken);
        await TryAppendReferencesAsync($"{siteRoot}/vpn/servers", "VPN 服务器", result, cancellationToken);
        await TryAppendReferencesAsync($"{siteRoot}/vpn/site-to-site-tunnels", "站点到站点 VPN", result, cancellationToken);
        await TryAppendReferencesAsync($"{siteRoot}/device-tags", "设备标签", result, cancellationToken);
        return result.DistinctBy(item => (item.Kind, item.Id)).OrderBy(item => item.Kind).ThenBy(item => item.Name).ToList();
    }

    public async Task<string> ExecuteOfficialApiAsync(
        string method,
        string relativePath,
        string? requestJson = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedMethod = method.Trim().ToUpperInvariant();
        if (normalizedMethod is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
            throw new UniFiApiException("不支持的 HTTP 方法。");
        if (!relativePath.StartsWith("/v1/", StringComparison.Ordinal) || relativePath.Contains("..", StringComparison.Ordinal)
            || relativePath.Contains("://", StringComparison.Ordinal))
            throw new UniFiApiException("只允许调用官方 Network /v1 端点。");

        JsonNode? payload = null;
        if (!string.IsNullOrWhiteSpace(requestJson))
        {
            try { payload = JsonNode.Parse(requestJson); }
            catch (JsonException ex) { throw new UniFiApiException($"请求体不是有效 JSON：{ex.Message}"); }
        }

        using var document = await SendAsync(
            new HttpMethod(normalizedMethod),
            $"/proxy/network/integration{relativePath}",
            payload,
            cancellationToken);
        return JsonSerializer.Serialize(document.RootElement, DisplayJsonOptions);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UniFiApiException("连接 UCG 超时。");
        }
        catch (HttpRequestException ex)
        {
            throw new UniFiApiException($"无法连接 UCG：{ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = ExtractErrorMessage(text) ?? $"UniFi 请求失败（HTTP {(int)response.StatusCode}）。";
                throw new UniFiApiException(message, (int)response.StatusCode, text.Length > 4000 ? text[..4000] : text);
            }
            if (string.IsNullOrWhiteSpace(text)) return JsonDocument.Parse("null");
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { throw new UniFiApiException("UniFi 返回了非 JSON 响应。"); }
        }
    }

    private string RequireSiteId() => !string.IsNullOrWhiteSpace(SiteId)
        ? SiteId
        : throw new UniFiApiException("请先选择要管理的 UniFi 站点。");

    private string GetPolicyPath(OfficialPolicyKind kind) => kind switch
    {
        OfficialPolicyKind.Acl => AclRulesPath,
        OfficialPolicyKind.Firewall => FirewallPoliciesPath,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private async Task AppendReferencesAsync(string path, string kind, List<PolicyReferenceItem> result, CancellationToken cancellationToken)
    {
        var initialCount = result.Count;
        var offset = 0;
        while (true)
        {
            using var document = await SendAsync(HttpMethod.Get, $"{path}?offset={offset}&limit={PageSize}", null, cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;
            foreach (var item in data.EnumerateArray())
            {
                var id = GetRequiredString(item, "id");
                var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? id
                    : id;
                result.Add(new PolicyReferenceItem(id, name, kind));
            }
            var count = root.TryGetProperty("count", out var countElement) && countElement.TryGetInt32(out var pageCount) ? pageCount : data.GetArrayLength();
            var total = root.TryGetProperty("totalCount", out var totalElement) && totalElement.TryGetInt64(out var totalCount) ? totalCount : result.Count - initialCount;
            offset += count;
            if (count == 0 || offset >= total) break;
        }
    }

    private async Task TryAppendReferencesAsync(string path, string kind, List<PolicyReferenceItem> result, CancellationToken cancellationToken)
    {
        try { await AppendReferencesAsync(path, kind, result, cancellationToken); }
        catch (UniFiApiException exception) when (exception.StatusCode is 403 or 404) { }
    }

    private static List<string> ReadStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            throw new UniFiApiException($"排序响应缺少数组：{name}");
        return array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList();
    }

    private static void Swap(List<string> ids, string id, int direction)
    {
        var index = ids.FindIndex(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new UniFiApiException("该策略不在用户定义排序列表中。");
        var target = index + direction;
        if (target < 0 || target >= ids.Count) return;
        (ids[index], ids[target]) = (ids[target], ids[index]);
    }

    private static void ValidatePolicyIds(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (!Guid.TryParse(id, out _)) throw new UniFiApiException($"排序列表包含无效的策略 ID：{id}");
        }
    }

    private static string GetRequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new UniFiApiException($"UniFi 响应缺少字段：{name}");

    private static string? ExtractErrorMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var message = document.RootElement.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
                ? messageValue.GetString()
                : null;
            var code = document.RootElement.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String
                ? codeValue.GetString()
                : null;
            var requestId = document.RootElement.TryGetProperty("requestId", out var requestValue) && requestValue.ValueKind == JsonValueKind.String
                ? requestValue.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(message)) return code;
            if (!string.IsNullOrWhiteSpace(code)) message += $" [{code}]";
            if (!string.IsNullOrWhiteSpace(requestId)) message += $"；Request ID: {requestId}";
            return message;
        }
        catch (JsonException) { return null; }
    }

    private static string NormalizeTarget(string value)
    {
        var raw = (value ?? string.Empty).Trim().TrimEnd('/');
        if (raw.Length == 0) throw new UniFiApiException("请输入 UCG 地址。");
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new UniFiApiException("UCG 地址格式不正确。");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new UniFiApiException("UCG 地址只填写 IP 或主机名，不要附加页面路径。");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public void Dispose() => _httpClient.Dispose();
}
