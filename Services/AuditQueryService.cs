using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using IntuneWipePortal.Models;

namespace IntuneWipePortal.Services;

/// <summary>
/// Read-only KQL access layer for the wipe observability dashboards.
///
/// Queries run server-side against the Log Analytics workspace backing the
/// `intune-wipe-api` Application Insights resource. The portal authenticates
/// via the user-assigned managed identity bound to the App Service
/// (<c>AZURE_CLIENT_ID</c> env var picked up by <c>DefaultAzureCredential</c>),
/// which must hold the <c>Log Analytics Reader</c> role on the workspace.
///
/// All event-name predicates target the <c>wipe.*</c> taxonomy emitted by
/// <c>Services/AuditEvents.cs</c> in the api repo.
/// </summary>
public sealed class AuditQueryService
{
    private const string EventPrefix = "wipe.";
    private const string Accepted = "wipe.request.accepted";
    private const string Issued = "wipe.graph.issued";
    private const string FailedPermanent = "wipe.graph.failed-permanent";
    private const string TransientError = "wipe.graph.transient-error";

    private readonly LogsQueryClient _client;
    private readonly string _workspaceId;
    private readonly ILogger<AuditQueryService> _log;

    public AuditQueryService(LogsQueryClient client, IConfiguration cfg, ILogger<AuditQueryService> log)
    {
        _client = client;
        _workspaceId = cfg["Monitor:WorkspaceId"]
            ?? throw new InvalidOperationException("Monitor:WorkspaceId is required");
        _log = log;
    }

    public async Task<KpiSummary> GetKpisAsync(TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name startswith "{{EventPrefix}}"
            | summarize Count = count() by Name
            """;

        var rows = await ExecuteAsync(query, window, ct,
            r => new KpiRow(r.GetString("Name") ?? "", r.GetInt64("Count") ?? 0));

        var s = new KpiSummary();
        foreach (var row in rows)
        {
            switch (row.EventName)
            {
                case Accepted:        s.Accepted += row.Count;          break;
                case Issued:          s.WipesIssued += row.Count;       break;
                case FailedPermanent: s.PermanentFailures += row.Count; break;
                case TransientError:  s.TransientErrors += row.Count;   break;
                default:
                    if (row.EventName.StartsWith("wipe.denied.", StringComparison.Ordinal))
                        s.Denied += row.Count;
                    break;
            }
        }
        s.TotalRequests = s.Accepted + s.Denied;
        return s;
    }

    public async Task<IReadOnlyList<TimeSeriesPoint>> GetTimeSeriesAsync(TimeSpan window, TimeSpan bucket, CancellationToken ct)
    {
        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name in ("{{Accepted}}", "{{Issued}}")
                  or Name startswith "wipe.denied."
            | summarize Count = count()
                by Bucket = bin(TimeGenerated, {{ToKql(bucket)}}),
                   EventName = case(
                       Name == "{{Accepted}}", "{{Accepted}}",
                       Name == "{{Issued}}",   "{{Issued}}",
                       "wipe.denied.*")
            | order by Bucket asc
            """;

        return await ExecuteAsync(query, window, ct,
            r => new TimeSeriesPoint(
                r.GetDateTimeOffset("Bucket") ?? DateTimeOffset.MinValue,
                r.GetString("EventName") ?? "",
                r.GetInt64("Count") ?? 0));
    }

    public async Task<IReadOnlyList<DenyBreakdownRow>> GetDenyBreakdownAsync(TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name startswith "wipe.denied."
            | summarize Count = count() by Name
            | order by Count desc
            """;

        return await ExecuteAsync(query, window, ct,
            r => new DenyBreakdownRow(r.GetString("Name") ?? "", r.GetInt64("Count") ?? 0));
    }

    public async Task<IReadOnlyList<AuditEventRow>> GetRecentEventsAsync(int take, TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name startswith "{{EventPrefix}}"
            | extend corr   = tostring(Properties.correlationId),
                     device = tostring(Properties.deviceName),
                     intune = tostring(Properties.intuneDeviceId),
                     entra  = tostring(Properties.entraDeviceId),
                     reason = tostring(Properties.reason),
                     exType = tostring(Properties.exceptionType)
            | project TimeGenerated, Name, corr, device, intune, entra, reason, exType
            | order by TimeGenerated desc
            | take {{take}}
            """;

        return await ExecuteAsync(query, window, ct, MapAuditRow);
    }

    public async Task<IReadOnlyList<AuditEventRow>> GetTrailAsync(string correlationId, CancellationToken ct)
    {
        // Defensive: correlationId comes from a route parameter; reject anything
        // not a hex/uuid-like string before interpolating into KQL.
        if (string.IsNullOrWhiteSpace(correlationId)
            || correlationId.Length is < 8 or > 64
            || !correlationId.All(c => char.IsLetterOrDigit(c) || c == '-'))
        {
            return Array.Empty<AuditEventRow>();
        }

        var query = $$"""
            AppEvents
            | where TimeGenerated > ago(30d)
            | where Name startswith "{{EventPrefix}}"
            | where tostring(Properties.correlationId) == "{{correlationId}}"
            | extend corr   = tostring(Properties.correlationId),
                     device = tostring(Properties.deviceName),
                     intune = tostring(Properties.intuneDeviceId),
                     entra  = tostring(Properties.entraDeviceId),
                     reason = tostring(Properties.reason),
                     exType = tostring(Properties.exceptionType)
            | project TimeGenerated, Name, corr, device, intune, entra, reason, exType
            | order by TimeGenerated asc
            """;

        return await ExecuteAsync(query, TimeSpan.FromDays(30), ct, MapAuditRow);
    }

    private static AuditEventRow MapAuditRow(LogsTableRow r) => new(
        r.GetDateTimeOffset("TimeGenerated") ?? DateTimeOffset.MinValue,
        r.GetString("Name") ?? "",
        r.GetString("corr") ?? "",
        NullIfEmpty(r.GetString("device")),
        NullIfEmpty(r.GetString("intune")),
        NullIfEmpty(r.GetString("entra")),
        NullIfEmpty(r.GetString("reason")),
        NullIfEmpty(r.GetString("exType")));

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private async Task<IReadOnlyList<T>> ExecuteAsync<T>(
        string query, TimeSpan window, CancellationToken ct, Func<LogsTableRow, T> map)
    {
        try
        {
            var response = await _client.QueryWorkspaceAsync(
                _workspaceId,
                query,
                new QueryTimeRange(window),
                cancellationToken: ct);
            var table = response.Value.Table;
            var result = new List<T>(table.Rows.Count);
            foreach (var row in table.Rows) result.Add(map(row));
            return result;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "KQL query failed (window={Window})", window);
            throw;
        }
    }

    private static string ToKql(TimeSpan ts) => ts.TotalDays >= 1
        ? $"{(int)ts.TotalDays}d"
        : ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h"
            : $"{(int)ts.TotalMinutes}m";
}
