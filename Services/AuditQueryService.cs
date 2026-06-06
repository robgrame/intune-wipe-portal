using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using IntuneWipePortal.Models;

namespace IntuneWipePortal.Services;

/// <summary>
/// Read-only KQL access layer for the device-actions observability dashboards.
///
/// Queries run server-side against the Log Analytics workspace backing the
/// <c>intune-device-actions</c> Application Insights resource. The portal
/// authenticates via the user-assigned managed identity bound to the App
/// Service (<c>AZURE_CLIENT_ID</c> env var picked up by
/// <c>DefaultAzureCredential</c>), which must hold the
/// <c>Log Analytics Reader</c> role on the workspace.
///
/// Event taxonomy:
/// <list type="bullet">
///   <item><description><c>action.*</c> — capability-agnostic pipeline events
///   (request received/accepted/denied, dispatch, polling status, ledger
///   lifecycle). Filtering by capability uses the <c>actionType</c> property
///   on the event.</description></item>
///   <item><description><c>wipe.* | autopilot.* | bitlocker.*</c> —
///   capability-specific events (Graph call outcomes, runner consumed/failed).
///   </description></item>
/// </list>
/// </summary>
public sealed class AuditQueryService
{
    // Capability-agnostic action.* events.
    private const string RequestAccepted = "action.request.accepted";
    private const string ActionCompleted = "action.completed";
    private const string ActionFailed    = "action.failed";
    private const string ActionPollTimeout = "action.poll-timeout";
    private const string ActionStateChanged = "action.state-changed";

    private static readonly string[] AllPrefixes = new[] { "action.", "wipe.", "autopilot.", "bitlocker." };

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

    /// <summary>
    /// Computes KPI counters for the dashboard cards over <paramref name="window"/>.
    /// When <paramref name="capability"/> is <see cref="ActionCapability.All"/>
    /// the action-level counters span all capabilities; otherwise they are
    /// filtered via the <c>actionType</c> property on the action.* events.
    /// </summary>
    public async Task<KpiSummary> GetKpisAsync(TimeSpan window, ActionCapability capability, CancellationToken ct)
    {
        var actionTypeFilter = capability.ActionTypeValue();
        // Use a `| where true` no-op when no capability filter is applied so
        // the KQL line layout stays stable (avoids whitespace-only lines that
        // can confuse error reporting and produce hard-to-read parse errors).
        var actionTypeClause = actionTypeFilter is null
            ? "| where true"
            : $"| where tostring(Properties.actionType) == \"{actionTypeFilter}\"";

        // action.* counters (capability filter via actionType property).
        var actionQuery = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name startswith "action."
            {{actionTypeClause}}
            | summarize Count = count() by Name
            """;

        var actionRows = await ExecuteAsync(actionQuery, window, ct,
            r => new KpiRow(r.GetString("Name") ?? "", r.GetInt64("Count") ?? 0));

        var s = new KpiSummary();
        foreach (var row in actionRows)
        {
            switch (row.EventName)
            {
                case RequestAccepted:   s.Accepted += row.Count;          break;
                case ActionCompleted:   s.ActionCompleted += row.Count;   break;
                case ActionFailed:      s.ActionFailed += row.Count;      break;
                case ActionPollTimeout: s.ActionPollTimeout += row.Count; break;
                default:
                    if (row.EventName.StartsWith("action.denied.", StringComparison.Ordinal))
                        s.Denied += row.Count;
                    break;
            }
        }
        s.TotalRequests = s.Accepted + s.Denied;

        // Per-capability Graph-call counters. When a single capability is
        // selected, query only its prefix; otherwise iterate the three.
        var caps = capability == ActionCapability.All
            ? ActionCapabilityExtensions.Concrete()
            : new[] { capability };

        foreach (var cap in caps)
        {
            var prefix = cap.EventPrefix();
            if (prefix is null) continue;

            var capQuery = $$"""
                AppEvents
                | where TimeGenerated > ago({{ToKql(window)}})
                | where Name startswith "{{prefix}}"
                | summarize Count = count() by Name
                """;
            var capRows = await ExecuteAsync(capQuery, window, ct,
                r => new KpiRow(r.GetString("Name") ?? "", r.GetInt64("Count") ?? 0));

            var bucket = new CapabilityKpi { Capability = cap };
            var issued    = cap.IssuedEventName();
            var permFail  = cap.FailedPermanentEventName();
            var transient = cap.TransientEventName();
            foreach (var row in capRows)
            {
                if (row.EventName == issued)         bucket.Issued += row.Count;
                else if (row.EventName == permFail)  bucket.PermanentFailures += row.Count;
                else if (row.EventName == transient) bucket.TransientErrors += row.Count;
            }
            s.PerCapability.Add(bucket);
        }

        return s;
    }

    public async Task<IReadOnlyList<DenyBreakdownRow>> GetDenyBreakdownAsync(
        TimeSpan window, ActionCapability capability, CancellationToken ct)
    {
        var actionTypeFilter = capability.ActionTypeValue();
        var actionTypeClause = actionTypeFilter is null
            ? "| where true"
            : $"| where tostring(Properties.actionType) == \"{actionTypeFilter}\"";

        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name startswith "action.denied."
            {{actionTypeClause}}
            | summarize Count = count() by Name
            | order by Count desc
            """;

        return await ExecuteAsync(query, window, ct,
            r => new DenyBreakdownRow(r.GetString("Name") ?? "", r.GetInt64("Count") ?? 0));
    }

    public async Task<IReadOnlyList<AuditEventRow>> GetRecentEventsAsync(
        int take, TimeSpan window, ActionCapability capability, CancellationToken ct)
    {
        var prefixListKql = BuildPrefixOrClause(capability);
        var actionTypeFilter = capability.ActionTypeValue();
        var actionTypeClause = actionTypeFilter is null
            ? "| where true"
            // Restrict only the action.* slice; capability prefix already
            // implies the capability for non-action.* events.
            : $"| where not(Name startswith \"action.\") or tostring(Properties.actionType) == \"{actionTypeFilter}\"";

        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where {{prefixListKql}}
            {{actionTypeClause}}
            | extend corr   = tostring(Properties.correlationId),
                     atype  = tostring(Properties.actionType),
                     device = tostring(Properties.deviceName),
                     intune = tostring(Properties.intuneDeviceId),
                     entra  = tostring(Properties.entraDeviceId),
                     reason = tostring(Properties.reason),
                     exType = tostring(Properties.exceptionType)
            | project TimeGenerated, Name, corr, atype, device, intune, entra, reason, exType
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

        var prefixListKql = BuildPrefixOrClause(ActionCapability.All);
        var query = $$"""
            AppEvents
            | where TimeGenerated > ago(30d)
            | where {{prefixListKql}}
            | where tostring(Properties.correlationId) == "{{correlationId}}"
            | extend corr   = tostring(Properties.correlationId),
                     atype  = tostring(Properties.actionType),
                     device = tostring(Properties.deviceName),
                     intune = tostring(Properties.intuneDeviceId),
                     entra  = tostring(Properties.entraDeviceId),
                     reason = tostring(Properties.reason),
                     exType = tostring(Properties.exceptionType)
            | project TimeGenerated, Name, corr, atype, device, intune, entra, reason, exType
            | order by TimeGenerated asc
            """;

        return await ExecuteAsync(query, TimeSpan.FromDays(30), ct, MapAuditRow);
    }

    /// <summary>
    /// Returns in-flight actions — those that have been issued (capability
    /// Graph call accepted) but for which no terminal <c>action.completed |
    /// action.failed | action.poll-timeout</c> event has been emitted yet.
    /// Useful to flag stuck devices on the dashboard.
    /// </summary>
    public async Task<IReadOnlyList<InFlightActionRow>> GetInFlightActionsAsync(
        TimeSpan window, ActionCapability capability, CancellationToken ct)
    {
        // Issued events: union of the per-capability *.graph.*.issued events,
        // tagged with the originating actionType so the UI can show it.
        var issuedClauses = (capability == ActionCapability.All
                ? ActionCapabilityExtensions.Concrete()
                : new[] { capability })
            .Select(c => $"(Name == \"{c.IssuedEventName()}\")")
            .ToArray();
        var issuedWhere = string.Join(" or ", issuedClauses);

        var actionTypeFilter = capability.ActionTypeValue();
        var terminalFilter = actionTypeFilter is null
            ? "| where true"
            : $"| where tostring(Properties.actionType) == \"{actionTypeFilter}\"";

        // Note: identifier 'latest' is reserved in some KQL contexts; we use
        // 'lastSeen' to avoid the parser tripping. The state projection uses
        // arg_max(timestamp, col) which already returns the column's value at
        // the row with the max timestamp — no '.state' suffix needed (that
        // suffix was a syntax error: arg_max returns a scalar, not a packed
        // dynamic, when given a single column).
        var query = $$"""
            let win = ago({{ToKql(window)}});
            let issued =
                AppEvents
                | where TimeGenerated > win
                | where {{issuedWhere}}
                | extend corr   = tostring(Properties.correlationId),
                         atype  = tostring(Properties.actionType),
                         device = tostring(Properties.deviceName)
                | summarize IssuedAt = min(TimeGenerated),
                            DeviceName = any(device),
                            ActionType = any(atype)
                            by corr;
            let terminal =
                AppEvents
                | where TimeGenerated > win
                | where Name in ("{{ActionCompleted}}", "{{ActionFailed}}", "{{ActionPollTimeout}}")
                {{terminalFilter}}
                | extend corr = tostring(Properties.correlationId)
                | distinct corr;
            let lastSeen =
                AppEvents
                | where TimeGenerated > win
                | where Name == "{{ActionStateChanged}}" or Name startswith "action.state-"
                | extend corr  = tostring(Properties.correlationId),
                         state = tostring(Properties.currentState)
                | summarize (LastUpdate, CurrentState) = arg_max(TimeGenerated, state)
                            by corr;
            issued
            | where corr !in (terminal)
            | join kind=leftouter (lastSeen) on corr
            | extend CurrentState = coalesce(CurrentState, "pending"),
                     LastUpdate   = coalesce(LastUpdate, IssuedAt)
            | extend MinutesSinceIssued = toint((now() - IssuedAt) / 1m)
            | project corr, ActionType, DeviceName, IssuedAt, LastUpdate, CurrentState, MinutesSinceIssued
            | order by IssuedAt desc
            | take 100
            """;

        return await ExecuteAsync(query, window, ct, r => new InFlightActionRow(
            r.GetString("corr") ?? "",
            NullIfEmpty(r.GetString("ActionType")),
            NullIfEmpty(r.GetString("DeviceName")),
            r.GetDateTimeOffset("IssuedAt") ?? DateTimeOffset.MinValue,
            r.GetDateTimeOffset("LastUpdate") ?? DateTimeOffset.MinValue,
            r.GetString("CurrentState") ?? "pending",
            (int)(r.GetInt64("MinutesSinceIssued") ?? 0)));
    }

    private static string BuildPrefixOrClause(ActionCapability capability)
    {
        // Build a (Name startswith "x" or Name startswith "y" ...) expression.
        // We always include "action." (capability-agnostic), plus the relevant
        // capability prefix(es).
        var prefixes = new List<string> { "action." };
        if (capability == ActionCapability.All)
        {
            prefixes.AddRange(AllPrefixes.Where(p => p != "action."));
        }
        else
        {
            var p = capability.EventPrefix();
            if (p is not null) prefixes.Add(p);
        }
        return string.Join(" or ",
            prefixes.Select(p => $"Name startswith \"{p}\""));
    }

    private static AuditEventRow MapAuditRow(LogsTableRow r) => new(
        r.GetDateTimeOffset("TimeGenerated") ?? DateTimeOffset.MinValue,
        r.GetString("Name") ?? "",
        r.GetString("corr") ?? "",
        NullIfEmpty(r.GetString("atype")),
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
