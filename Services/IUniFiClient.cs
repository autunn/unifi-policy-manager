using UniFiDnsManager.Models;

namespace UniFiDnsManager.Services;

public interface IUniFiClient : IDisposable
{
    string Target { get; }
    string Site { get; }
    string SiteId { get; }
    string ApplicationVersion { get; }
    IReadOnlyList<UniFiSite> Sites { get; }
    void SelectSite(UniFiSite site);
    Task<IReadOnlyList<UniFiSite>> ListSitesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DnsRecord>> ListRecordsAsync(CancellationToken cancellationToken = default);
    Task<DnsRecord?> CreateRecordAsync(DnsRecord record, CancellationToken cancellationToken = default);
    Task<DnsRecord?> UpdateRecordAsync(string id, DnsRecord record, CancellationToken cancellationToken = default);
    Task DeleteRecordAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OfficialPolicyRule>> ListPoliciesAsync(OfficialPolicyKind kind, CancellationToken cancellationToken = default);
    Task<OfficialPolicyRule?> CreatePolicyAsync(OfficialPolicyKind kind, string requestJson, CancellationToken cancellationToken = default);
    Task<OfficialPolicyRule?> UpdatePolicyAsync(OfficialPolicyKind kind, string id, string requestJson, CancellationToken cancellationToken = default);
    Task DeletePolicyAsync(OfficialPolicyKind kind, string id, CancellationToken cancellationToken = default);
    Task MovePolicyAsync(OfficialPolicyKind kind, string id, int direction, CancellationToken cancellationToken = default);
    Task<PolicyOrderingSnapshot> GetPolicyOrderingAsync(OfficialPolicyKind kind, CancellationToken cancellationToken = default);
    Task SetPolicyOrderingAsync(OfficialPolicyKind kind, PolicyOrderingSnapshot ordering, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PolicyReferenceItem>> ListPolicyReferencesAsync(CancellationToken cancellationToken = default);
    Task<string> ExecuteOfficialApiAsync(string method, string relativePath, string? requestJson = null, CancellationToken cancellationToken = default);
}

public sealed class UniFiApiException(string message, int? statusCode = null, string? details = null)
    : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
    public string? Details { get; } = details;
}
