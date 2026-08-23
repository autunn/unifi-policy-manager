using UniFiDnsManager.Models;

namespace UniFiDnsManager.Services;

public static class OfficialResourceInteractionService
{
    public static bool SupportsPaging(OfficialApiOperation operation)
    {
        var query = ParseQuery(operation.DefaultQuery);
        return query.ContainsKey("offset") && query.ContainsKey("limit");
    }

    public static string BuildPageQuery(string defaultQuery, int offset, int limit)
    {
        var values = ParseQuery(defaultQuery);
        values["offset"] = Math.Max(0, offset).ToString();
        values["limit"] = Math.Clamp(limit, 1, 200).ToString();
        return string.Join("&", values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    public static OfficialResourceSnapshot MergePages(IEnumerable<OfficialResourceSnapshot> pages)
    {
        var items = new List<OfficialResourceItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in pages.SelectMany(page => page.Items))
        {
            var key = string.IsNullOrWhiteSpace(item.Id) ? item.RawJson : item.Id;
            if (seen.Add(key)) items.Add(item);
        }
        return OfficialResourcePresentationService.CreateSnapshot(items);
    }

    public static OfficialApiOperation? FindPrimaryOperation(
        OfficialApiOperation? listOperation,
        IReadOnlyList<OfficialApiOperation> operations)
    {
        if (listOperation is null) return null;
        if (listOperation.Id.Equals("getPendingDevicePage", StringComparison.OrdinalIgnoreCase))
            return operations.FirstOrDefault(operation => operation.Id.Equals("adoptDevice", StringComparison.OrdinalIgnoreCase));

        return operations.FirstOrDefault(operation =>
            operation.Method == "POST"
            && operation.PathParameters.Count == 0
            && operation.PathTemplate.Equals(listOperation.PathTemplate, StringComparison.OrdinalIgnoreCase)
            && (operation.Id.StartsWith("create", StringComparison.OrdinalIgnoreCase)
                || operation.Id.Equals("adoptDevice", StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<OfficialApiOperation> FindCollectionOperations(
        OfficialApiOperation? listOperation,
        IReadOnlyList<OfficialApiOperation> operations,
        OfficialApiOperation? primaryOperation)
    {
        if (listOperation is null) return [];
        var collectionPrefix = listOperation.PathTemplate.TrimEnd('/') + "/";
        return operations.Where(operation =>
                operation != listOperation
                && operation != primaryOperation
                && operation.PathParameters.Count == 0
                && (operation.PathTemplate.Equals(listOperation.PathTemplate, StringComparison.OrdinalIgnoreCase)
                    || operation.PathTemplate.StartsWith(collectionPrefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public static IReadOnlyList<OfficialApiOperation> FindItemOperations(
        OfficialApiOperation? listOperation,
        IReadOnlyList<OfficialApiOperation> operations)
    {
        if (listOperation is null) return [];
        if (listOperation.Id.Equals("getPendingDevicePage", StringComparison.OrdinalIgnoreCase))
        {
            var adopt = operations.FirstOrDefault(operation => operation.Id.Equals("adoptDevice", StringComparison.OrdinalIgnoreCase));
            return adopt is null ? [] : [adopt];
        }

        var itemPrefix = listOperation.PathTemplate.TrimEnd('/') + "/{";
        return operations.Where(operation =>
                operation.PathParameters.Count > 0
                && operation.PathTemplate.StartsWith(itemPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(operation => OperationPriority(operation))
            .ThenBy(operation => operation.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    public static OfficialApiOperation? FindDetailOperation(
        OfficialApiOperation? listOperation,
        IReadOnlyList<OfficialApiOperation> operations)
    {
        if (listOperation is null) return null;
        var itemOperations = FindItemOperations(listOperation, operations)
            .Where(operation => operation.Method == "GET")
            .ToList();
        return itemOperations.FirstOrDefault(operation => operation.Id.Contains("Details", StringComparison.OrdinalIgnoreCase))
            ?? itemOperations.FirstOrDefault(operation =>
                operation.PathTemplate.Count(character => character == '/') == listOperation.PathTemplate.Count(character => character == '/') + 1
                && operation.PathTemplate.EndsWith('}'))
            ?? itemOperations.FirstOrDefault();
    }

    private static int OperationPriority(OfficialApiOperation operation) => operation.Method switch
    {
        "GET" => 0,
        "PATCH" => 1,
        "PUT" => 2,
        "POST" => 3,
        "DELETE" => 4,
        _ => 5
    };

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Trim().TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0) continue;
            var key = Uri.UnescapeDataString(part[..separator].Replace("+", " ", StringComparison.Ordinal));
            var value = Uri.UnescapeDataString(part[(separator + 1)..].Replace("+", " ", StringComparison.Ordinal));
            result[key] = value;
        }
        return result;
    }
}
