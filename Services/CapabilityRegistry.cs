using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using IntuneWipePortal.Models;
using Microsoft.Extensions.Caching.Memory;

namespace IntuneWipePortal.Services;

/// <summary>
/// Discovers the set of active capabilities from Log Analytics telemetry so
/// the dashboard renders dynamically — no portal code change or redeploy is
/// needed when the API team ships a new capability. The discovery query
/// projects DISTINCT <c>actionType</c> values from <c>action.*</c> events over
/// the lookback window; each discovered actionType is resolved through
/// <see cref="KnownCapabilities"/> for a curated display name + event-name
/// mapping, with a sensible fallback for capabilities not yet curated.
///
/// Results are cached in-process for <see cref="CacheTtl"/> to keep KQL traffic
/// minimal (capability set changes are rare) — sufficient for a single-instance
/// portal; if we ever scale out, an out-of-process cache could replace this.
/// </summary>
public sealed class CapabilityRegistry
{
    /// <summary>Cache TTL. Short enough that a new capability shows up within minutes.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Lookback window for the discovery query.</summary>
    public static readonly TimeSpan DiscoveryWindow = TimeSpan.FromDays(30);

    private const string CacheKey = "capability-registry:all";

    private readonly LogsQueryClient _client;
    private readonly string _workspaceId;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CapabilityRegistry> _log;

    public CapabilityRegistry(
        LogsQueryClient client,
        IConfiguration cfg,
        IMemoryCache cache,
        ILogger<CapabilityRegistry> log)
    {
        _client = client;
        _workspaceId = cfg["Monitor:WorkspaceId"]
            ?? throw new InvalidOperationException("Monitor:WorkspaceId is required");
        _cache = cache;
        _log = log;
    }

    /// <summary>
    /// Returns <c>[All, ...discovered]</c>. The "All" sentinel is always first.
    /// Discovered capabilities are sorted by display name for stable UI.
    /// On query failure returns a fallback of just the curated well-known
    /// capabilities — the dashboard stays usable even if LAW is briefly
    /// unreachable.
    /// </summary>
    public async Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue<IReadOnlyList<CapabilityDescriptor>>(CacheKey, out var cached) && cached is not null)
            return cached;

        IReadOnlyList<CapabilityDescriptor> result;
        try
        {
            result = await DiscoverAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Capability discovery failed — falling back to curated well-known set");
            result = BuildFallbackList();
        }

        _cache.Set(CacheKey, result, CacheTtl);
        return result;
    }

    /// <summary>
    /// Concrete (non-All) capabilities only. Convenience for KPI aggregation.
    /// </summary>
    public async Task<IReadOnlyList<CapabilityDescriptor>> GetConcreteAsync(CancellationToken ct)
    {
        var all = await GetCapabilitiesAsync(ct);
        return all.Where(c => !c.IsAll).ToList();
    }

    private async Task<IReadOnlyList<CapabilityDescriptor>> DiscoverAsync(CancellationToken ct)
    {
        // Query DISTINCT actionType values from action.* events. We project
        // lastSeen too, only for debug logging — it isn't surfaced in the UI.
        const string kql = """
            AppEvents
            | where TimeGenerated > ago(30d)
            | where Name startswith "action."
            | extend atype = tostring(Properties.actionType)
            | where isnotempty(atype)
            | summarize lastSeen = max(TimeGenerated) by atype
            | order by atype asc
            """;

        var response = await _client.QueryWorkspaceAsync(
            _workspaceId,
            kql,
            new QueryTimeRange(DiscoveryWindow),
            cancellationToken: ct);

        var discovered = new List<CapabilityDescriptor> { CapabilityDescriptor.All };
        foreach (var row in response.Value.Table.Rows)
        {
            var atype = row.GetString("atype");
            if (string.IsNullOrWhiteSpace(atype)) continue;
            discovered.Add(KnownCapabilities.Resolve(atype));
        }

        // If no capability ever emitted action.* events (cold workspace), fall
        // back to the curated set so the tabs still render.
        if (discovered.Count == 1) // only the All sentinel
        {
            _log.LogInformation("No actionType discovered in LAW — using curated well-known set");
            return BuildFallbackList();
        }

        // Stable order: All first, then by display name.
        return discovered
            .OrderBy(c => c.IsAll ? 0 : 1)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Curated well-known capabilities when discovery is unavailable or empty.
    /// Mirrors the entries baked into <see cref="KnownCapabilities"/>.
    /// </summary>
    private static IReadOnlyList<CapabilityDescriptor> BuildFallbackList()
    {
        var concrete = new[] { "wipe", "autopilot-register", "bitlocker-rotate", "device-rename" }
            .Select(KnownCapabilities.Resolve);

        return new List<CapabilityDescriptor> { CapabilityDescriptor.All }
            .Concat(concrete.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }
}
