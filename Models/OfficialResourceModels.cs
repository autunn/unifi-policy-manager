namespace UniFiDnsManager.Models;

public sealed record OfficialResourceField(string Label, string Value);

public sealed class OfficialResourceItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "未命名资源";
    public string Subtitle { get; init; } = "";
    public string State { get; init; } = "未知";
    public string Type { get; init; } = "其他";
    public string Address { get; init; } = "—";
    public string Detail { get; init; } = "—";
    public string Glyph { get; init; } = "◫";
    public string StateColor { get; init; } = "#98A2B3";
    public bool IsHealthy { get; init; }
    public bool NeedsAttention { get; init; }
    public string RawJson { get; init; } = "{}";
    public IReadOnlyList<OfficialResourceField> Fields { get; init; } = [];
}

public sealed record OfficialResourceDistributionItem(string Label, int Count, double Percentage);

public sealed class OfficialResourceSnapshot
{
    public IReadOnlyList<OfficialResourceItem> Items { get; init; } = [];
    public IReadOnlyList<OfficialResourceDistributionItem> TypeDistribution { get; init; } = [];
    public int TotalCount { get; init; }
    public int HealthyCount { get; init; }
    public int AttentionCount { get; init; }
    public int TypeCount { get; init; }
}

public sealed record OfficialOperationRequest(string RelativePath, string? RequestJson);
