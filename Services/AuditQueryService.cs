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
///   <item><description><c>{prefix}*</c> (e.g. <c>wipe.</c>, <c>bitlocker.</c>,
///   <c>rename.</c>) — capability-specific events (Graph or REST call outcomes,
///   runner consumed/failed). The set of prefixes is discovered dynamically
///   via <see cref="CapabilityRegistry"/>.</description></item>
/// </list>
///
/// All methods take a <see cref="CapabilityDescriptor"/> — pass
/// <see cref="CapabilityDescriptor.All"/> to span every capability. The service
/// pulls the full known-prefix set from <see cref="CapabilityRegistry"/> on
/// demand to keep "all capabilities" queries auto-discovering.
/// </summary>
public sealed class AuditQueryService
{
    // Capability-agnostic action.* events.
    private const string RequestAccepted = "action.request.accepted";
    private const string ActionCompleted = "action.completed";
    private const string ActionFailed    = "action.failed";
    private const string ActionPollTimeout = "action.poll-timeout";
    private const string ActionStateChanged = "action.state-changed";

    private readonly LogsQueryClient _client;
    private readonly string _workspaceId;
    private readonly CapabilityRegistry _registry;
    private readonly ILogger<AuditQueryService> _log;

    public AuditQueryService(
        LogsQueryClient client,
        IConfiguration cfg,
        CapabilityRegistry registry,
        ILogger<AuditQueryService> log)
    {
        _client = client;
        _workspaceId = cfg["Monitor:WorkspaceId"]
            ?? throw new InvalidOperationException("Monitor:WorkspaceId is required");
        _registry = registry;
        _log = log;
    }

    /// <summary>
    /// Computes KPI counters for the dashboard cards over <paramref name="window"/>.
    /// When <paramref name="capability"/> is <see cref="CapabilityDescriptor.All"/>
    /// the action-level counters span all capabilities; otherwise they are
    /// filtered via the <c>actionType</c> property on the action.* events.
    /// </summary>
    public async Task<KpiSummary> GetKpisAsync(TimeSpan window, CapabilityDescriptor capability, CancellationToken ct)
    {
        // Use a `| where true` no-op when no capability filter is applied so
        // the KQL line layout stays stable (avoids whitespace-only lines that
        // can confuse error reporting and produce hard-to-read parse errors).
        var actionTypeClause = capability.ActionTypeValue is null
            ? "| where true"
            : $"| where tostring(Properties.actionType) == \"{capability.ActionTypeValue}\"";

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

        // Per-capability call counters. When a single capability is
        // selected, query only its prefix; otherwise iterate every concrete
        // capability discovered by the registry.
        var caps = capability.IsAll
            ? await _registry.GetConcreteAsync(ct)
            : new[] { capability };

        foreach (var cap in caps)
        {
            if (cap.EventPrefix is null) continue;

            var capQuery = $$"""
                AppEvents
                | where TimeGenerated > ago({{ToKql(window)}})
                | where Name startswith "{{cap.EventPrefix}}"
                | summarize Count = count() by Name
                """;
            var capRows = await ExecuteAsync(capQuery, window, ct,
                r => new KpiRow(r.GetString("Name") ?? "", r.GetInt64("Count") ?? 0));

            var bucket = new CapabilityKpi { Capability = cap };
            foreach (var row in capRows)
            {
                if (cap.IssuedEventName is not null && row.EventName == cap.IssuedEventName)
                    bucket.Issued += row.Count;
                else if (cap.FailedPermanentEventName is not null && row.EventName == cap.FailedPermanentEventName)
                    bucket.PermanentFailures += row.Count;
                else if (cap.TransientEventName is not null && row.EventName == cap.TransientEventName)
                    bucket.TransientErrors += row.Count;
            }
            s.PerCapability.Add(bucket);
        }

        return s;
    }

    public async Task<IReadOnlyList<DenyBreakdownRow>> GetDenyBreakdownAsync(
        TimeSpan window, CapabilityDescriptor capability, CancellationToken ct)
    {
        var actionTypeClause = capability.ActionTypeValue is null
            ? "| where true"
            : $"| where tostring(Properties.actionType) == \"{capability.ActionTypeValue}\"";

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

    public async Task<IReadOnlyList<AuditEventRow>> GetRecentDeniedEventsAsync(
        int take, TimeSpan window, CapabilityDescriptor capability, CancellationToken ct)
    {
        var actionTypeClause = capability.ActionTypeValue is null
            ? "| where true"
            : $"| where tostring(Properties.actionType) == \"{capability.ActionTypeValue}\"";

        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name startswith "action.denied."
            {{actionTypeClause}}
            | project TimeGenerated,
                      Name,
                      correlationId = tostring(Properties.correlationId),
                      actionType    = tostring(Properties.actionType),
                      deviceName    = tostring(Properties.deviceName),
                      intuneDeviceId= tostring(Properties.intuneDeviceId),
                      entraDeviceId = tostring(Properties.entraDeviceId),
                      reason        = tostring(Properties.reason)
            | order by TimeGenerated desc
            | take {{take}}
            """;

        return await ExecuteAsync(query, window, ct,
            r => new AuditEventRow(
                r.GetDateTimeOffset("TimeGenerated") ?? DateTimeOffset.MinValue,
                r.GetString("Name") ?? "",
                r.GetString("correlationId") ?? "",
                r.GetString("actionType"),
                r.GetString("deviceName"),
                r.GetString("intuneDeviceId"),
                r.GetString("entraDeviceId"),
                r.GetString("reason"),
                ExceptionType: null));
    }

    public async Task<IReadOnlyList<AuditEventRow>> GetRecentEventsAsync(
        int take, TimeSpan window, CapabilityDescriptor capability, CancellationToken ct)
    {
        var prefixListKql = await BuildPrefixOrClauseAsync(capability, ct);
        var actionTypeClause = capability.ActionTypeValue is null
            ? "| where true"
            // Restrict only the action.* slice; capability prefix already
            // implies the capability for non-action.* events.
            : $"| where not(Name startswith \"action.\") or tostring(Properties.actionType) == \"{capability.ActionTypeValue}\"";

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

        var prefixListKql = await BuildPrefixOrClauseAsync(CapabilityDescriptor.All, ct);
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
    /// downstream call accepted) but for which no terminal
    /// <c>action.completed | action.failed | action.poll-timeout</c> event has
    /// been emitted yet. Capabilities with no curated <c>IssuedEventName</c>
    /// are skipped from the "issued" anchor (they cannot produce in-flight
    /// rows until added to <see cref="KnownCapabilities"/>).
    /// </summary>
    public async Task<IReadOnlyList<InFlightActionRow>> GetInFlightActionsAsync(
        TimeSpan window, CapabilityDescriptor capability, CancellationToken ct)
    {
        var caps = capability.IsAll
            ? await _registry.GetConcreteAsync(ct)
            : new[] { capability };

        var issuedNames = caps
            .Where(c => c.IssuedEventName is not null)
            .Select(c => c.IssuedEventName!)
            .ToArray();

        // No capability has an explicit Issued event yet → nothing to anchor
        // the in-flight query on. Return empty rather than emit invalid KQL.
        if (issuedNames.Length == 0) return Array.Empty<InFlightActionRow>();

        var issuedWhere = string.Join(" or ",
            issuedNames.Select(n => $"(Name == \"{n}\")"));

        var terminalFilter = capability.ActionTypeValue is null
            ? "| where true"
            : $"| where tostring(Properties.actionType) == \"{capability.ActionTypeValue}\"";

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

    /// <summary>
    /// Builds a <c>(Name startswith "x" or Name startswith "y" …)</c> clause
    /// covering the capability-agnostic <c>action.</c> prefix plus the
    /// capability-specific prefix(es) discovered from the registry.
    /// </summary>
    private async Task<string> BuildPrefixOrClauseAsync(CapabilityDescriptor capability, CancellationToken ct)
    {
        var prefixes = new List<string> { "action." };
        if (capability.IsAll)
        {
            var all = await _registry.GetConcreteAsync(ct);
            foreach (var c in all)
                if (c.EventPrefix is not null && !prefixes.Contains(c.EventPrefix))
                    prefixes.Add(c.EventPrefix);
        }
        else if (capability.EventPrefix is not null)
        {
            prefixes.Add(capability.EventPrefix);
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

    /// <summary>
    /// Returns average and P95 latency (minutes) from request acceptance to terminal state.
    /// </summary>
    public async Task<LatencyStats> GetLatencyAsync(TimeSpan window, CapabilityDescriptor capability, CancellationToken ct)
    {
        var actionTypeClause = capability.ActionTypeValue is null
            ? "| where true"
            : $"| where actionType == \"{capability.ActionTypeValue}\"";

        var query = $$"""
            let accepted = AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name == "action.request.accepted"
            | extend corr = tostring(Properties.correlationId), actionType = tostring(Properties.actionType)
            {{actionTypeClause}}
            | project corr, acceptedAt = TimeGenerated;
            let terminal = AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name in ("action.completed", "action.failed", "action.poll-timeout") or Name startswith "action.denied."
            | extend corr = tostring(Properties.correlationId)
            | project corr, terminalAt = TimeGenerated;
            accepted
            | join kind=inner terminal on corr
            | extend latencyMin = datetime_diff('second', terminalAt, acceptedAt) / 60.0
            | where latencyMin >= 0 and latencyMin < 1440
            | summarize AvgMin = avg(latencyMin), P95Min = percentile(latencyMin, 95), Samples = count()
            """;

        var rows = await ExecuteAsync(query, window, ct, r => new LatencyStats(
            AvgMinutes: r.GetDouble("AvgMin"),
            P95Minutes: r.GetDouble("P95Min"),
            SampleCount: r.GetInt64("Samples") ?? 0));

        return rows.Count > 0 ? rows[0] : new LatencyStats(null, null, 0);
    }

    /// <summary>
    /// Returns recent failed events (action.failed, permanent failures, exceptions) with details.
    /// </summary>
    public async Task<IReadOnlyList<AuditEventRow>> GetRecentFailedEventsAsync(
        int take, TimeSpan window, CapabilityDescriptor capability, CancellationToken ct)
    {
        var actionTypeClause = capability.ActionTypeValue is null
            ? "| where true"
            : $"| where tostring(Properties.actionType) == \"{capability.ActionTypeValue}\"";

        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name == "action.failed"
                or Name == "action.poll-timeout"
                or Name has ".error"
                or Name has ".failed"
            {{actionTypeClause}}
            | project TimeGenerated,
                      Name,
                      correlationId = tostring(Properties.correlationId),
                      actionType    = tostring(Properties.actionType),
                      deviceName    = tostring(Properties.deviceName),
                      intuneDeviceId= tostring(Properties.intuneDeviceId),
                      entraDeviceId = tostring(Properties.entraDeviceId),
                      reason        = coalesce(tostring(Properties.reason), tostring(Properties.exceptionMessage), tostring(Properties.graphErrorMsg)),
                      exType        = tostring(Properties.exceptionType)
            | order by TimeGenerated desc
            | take {{take}}
            """;

        return await ExecuteAsync(query, window, ct,
            r => new AuditEventRow(
                r.GetDateTimeOffset("TimeGenerated") ?? DateTimeOffset.MinValue,
                r.GetString("Name") ?? "",
                r.GetString("correlationId") ?? "",
                r.GetString("actionType"),
                r.GetString("deviceName"),
                r.GetString("intuneDeviceId"),
                r.GetString("entraDeviceId"),
                r.GetString("reason"),
                r.GetString("exType")));
    }
}
