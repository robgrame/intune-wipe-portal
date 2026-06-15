using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace IntuneWipePortal.Services;

/// <summary>
/// Cruscotto telemetry — aggregates real-time signals from the moving parts
/// of the IntuneDeviceActions pipeline so the operator page can render the
/// flow-of-energy view AND answer "where did request X get stuck and what
/// do I do about it?".
///
/// Data sources (all least-privilege, all via the portal's UAMI):
/// <list type="bullet">
///   <item><c>ServiceBusAdministrationClient</c> for queue active/dead-letter
///         counts (requires <i>Azure Service Bus Data Owner</i> on the
///         namespace).</item>
///   <item><c>BlobContainerClient</c> on the <c>action-ledger</c> container of
///         the Proc storage account for ledger entries + stuck detection
///         (requires <i>Storage Blob Data Reader</i> on the account).</item>
///   <item><c>LogsQueryClient</c> against the Log Analytics workspace fronting
///         App Insights (requires <i>Log Analytics Reader</i>, already granted).</item>
/// </list>
///
/// Ported from <c>intune-wipe-api\src\Web\Dashboard\DashboardTelemetryService.cs</c>.
/// The <c>ResetLedgerAsync</c> operation is intentionally not ported — reset
/// remains an API-side admin endpoint to preserve separation of duties.
/// </summary>
public sealed class CruscottoTelemetryService
{
    private readonly ServiceBusAdministrationClient? _sbAdmin;
    private readonly BlobContainerClient? _ledger;
    private readonly LogsQueryClient? _logs;
    private readonly string? _workspaceId;
    private readonly double _graceHours;
    private readonly ILogger<CruscottoTelemetryService> _log;

    private static readonly string[] FlowQueues =
    {
        "action-requests",
        "action-dispatch",
        "wipe-action",
        "autopilot-action",
        "bitlocker-action",
        "rename-action",
    };

    public CruscottoTelemetryService(
        IConfiguration cfg,
        TokenCredential cred,
        LogsQueryClient logs,
        ILogger<CruscottoTelemetryService> log)
    {
        _log = log;
        _logs = logs;
        _workspaceId = cfg["Monitor:WorkspaceId"];

        var sbFqdn = cfg["Cruscotto:ServiceBusFullyQualifiedNamespace"];
        if (!string.IsNullOrWhiteSpace(sbFqdn))
        {
            _sbAdmin = new ServiceBusAdministrationClient(sbFqdn, cred);
        }
        else
        {
            _log.LogWarning("Cruscotto:ServiceBusFullyQualifiedNamespace not configured — queue depth probes disabled.");
        }

        var ledgerAccount = cfg["Cruscotto:LedgerStorageAccount"];
        var ledgerContainer = cfg["Cruscotto:LedgerContainer"] ?? "action-ledger";
        if (!string.IsNullOrWhiteSpace(ledgerAccount))
        {
            _ledger = new BlobContainerClient(
                new Uri($"https://{ledgerAccount}.blob.core.windows.net/{ledgerContainer}"),
                cred);
        }
        else
        {
            _log.LogWarning("Cruscotto:LedgerStorageAccount not configured — ledger probe disabled.");
        }

        _graceHours = double.TryParse(cfg["Cruscotto:RearmGracePeriodHours"], out var g) && g > 0 ? g : 48;
    }

    // ─── Overview snapshot ────────────────────────────────────────────────────

    public async Task<DashboardSnapshot> SnapshotAsync(CancellationToken ct)
    {
        var queuesTask = LoadQueuesAsync(ct);
        var ledgerTask = LoadLedgerAsync(ct);
        var diagTask   = LoadDiagnosticsAsync(ct);
        await Task.WhenAll(queuesTask, ledgerTask, diagTask);

        var queues = await queuesTask;
        var ledger = await ledgerTask;
        var diag   = await diagTask;

        var warnings = new List<string>();
        foreach (var q in queues)
        {
            if (q.DeadLetter > 0) warnings.Add($"Queue '{q.Name}' has {q.DeadLetter} dead-lettered message(s).");
            else if (q.Active >= 50) warnings.Add($"Queue '{q.Name}' has {q.Active} active messages — backlog forming.");
        }
        if (ledger.StuckEntries > 0)
            warnings.Add($"{ledger.StuckEntries} ledger entry(ies) Issued past grace ({ledger.GraceHours}h) with no terminal observation. Future requests for those devices are blocked.");
        if (ledger.Error is not null) warnings.Add($"Ledger inspection failed: {ledger.Error}.");
        foreach (var d in diag.Issues) warnings.Add(d);

        return new DashboardSnapshot(
            GeneratedAt: DateTimeOffset.UtcNow,
            Queues: queues,
            Ledger: ledger,
            Diagnostics: diag,
            Warnings: warnings.ToArray());
    }

    private async Task<IReadOnlyList<QueueStatus>> LoadQueuesAsync(CancellationToken ct)
    {
        if (_sbAdmin is null)
        {
            return FlowQueues.Select(n => new QueueStatus(n, 0, 0, 0, null, NodeHealth.Unknown, "SB admin client not configured")).ToArray();
        }
        var results = new List<QueueStatus>(FlowQueues.Length);
        foreach (var name in FlowQueues)
        {
            try
            {
                var props = await _sbAdmin.GetQueueRuntimePropertiesAsync(name, ct);
                var active = props.Value.ActiveMessageCount;
                var dlq = props.Value.DeadLetterMessageCount;
                results.Add(new QueueStatus(
                    Name: name,
                    Active: active,
                    DeadLetter: dlq,
                    Scheduled: props.Value.ScheduledMessageCount,
                    AccessedAt: props.Value.AccessedAt,
                    Status: dlq > 0      ? NodeHealth.Red
                          : active >= 50 ? NodeHealth.Red
                          : active >= 10 ? NodeHealth.Yellow
                          :                NodeHealth.Green,
                    Error: null));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                results.Add(new QueueStatus(name, 0, 0, 0, null, NodeHealth.Unknown, "queue not provisioned"));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cruscotto: queue {Queue} runtime lookup failed", name);
                results.Add(new QueueStatus(name, 0, 0, 0, null, NodeHealth.Unknown, ex.GetType().Name));
            }
        }
        return results;
    }

    private async Task<LedgerStatus> LoadLedgerAsync(CancellationToken ct)
    {
        if (_ledger is null)
        {
            return new LedgerStatus(0, 0, null, null, Array.Empty<StuckLedgerEntry>(), _graceHours, NodeHealth.Unknown, "ledger client not configured");
        }
        var stuckBefore = DateTimeOffset.UtcNow.AddHours(-_graceHours);

        var total = 0;
        var stuck = 0;
        DateTimeOffset? oldestStuckIssuedAt = null;
        string? oldestStuckId = null;
        var stuckList = new List<StuckLedgerEntry>();

        try
        {
            await foreach (BlobItem item in _ledger.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: null, ct))
            {
                if (item.Name.StartsWith("_archive/", StringComparison.OrdinalIgnoreCase)) continue;
                total++;

                LedgerEntry? entry = null;
                try
                {
                    var resp = await _ledger.GetBlobClient(item.Name).DownloadContentAsync(ct);
                    entry = JsonSerializer.Deserialize<LedgerEntry>(resp.Value.Content.ToMemory().Span);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Cruscotto: ledger blob {Name} read failed", item.Name);
                    continue;
                }
                if (entry is null) continue;

                var isIssued   = string.Equals(entry.State, "Issued", StringComparison.OrdinalIgnoreCase);
                var noTerminal = string.IsNullOrEmpty(entry.LastTerminalState);
                if (isIssued && noTerminal && entry.IssuedAt is { } issued && issued < stuckBefore)
                {
                    stuck++;
                    stuckList.Add(new StuckLedgerEntry(
                        IntuneDeviceId: entry.IntuneDeviceId ?? "(unknown)",
                        CorrelationId: entry.CorrelationId,
                        IssuedAt: issued,
                        AgeHours: Math.Round((DateTimeOffset.UtcNow - issued).TotalHours, 1)));
                    if (oldestStuckIssuedAt is null || issued < oldestStuckIssuedAt)
                    {
                        oldestStuckIssuedAt = issued;
                        oldestStuckId = entry.IntuneDeviceId;
                    }
                }
            }
            var status = stuck > 0    ? NodeHealth.Red
                       : total >= 100 ? NodeHealth.Yellow
                       :                NodeHealth.Green;
            return new LedgerStatus(total, stuck, oldestStuckIssuedAt, oldestStuckId,
                                    stuckList.OrderByDescending(e => e.AgeHours).Take(10).ToArray(),
                                    _graceHours, status, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cruscotto: ledger enumeration failed");
            return new LedgerStatus(0, 0, null, null, Array.Empty<StuckLedgerEntry>(), _graceHours, NodeHealth.Unknown, ex.GetType().Name);
        }
    }

    private async Task<DiagnosticsStatus> LoadDiagnosticsAsync(CancellationToken ct)
    {
        var issues = new List<string>();
        DateTimeOffset? pollerLastTick = null;
        var pollerHealth = NodeHealth.Unknown;
        var capabilityFreshness = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);

        if (_logs is null || string.IsNullOrEmpty(_workspaceId))
        {
            issues.Add("Log Analytics workspace not configured (Monitor:WorkspaceId) — runtime probes unavailable.");
            return new DiagnosticsStatus(pollerLastTick, pollerHealth, capabilityFreshness, issues.ToArray(), KqlAvailable: false);
        }

        try
        {
            const string pollerQuery = @"
                AppRequests
                | where TimeGenerated > ago(30m)
                | where AppRoleName has 'proc' and Name has 'StatusPoller'
                | summarize last = max(TimeGenerated) by Success = tostring(Success)
            ";
            var result = await _logs.QueryWorkspaceAsync(_workspaceId, pollerQuery, new QueryTimeRange(TimeSpan.FromMinutes(30)), cancellationToken: ct);
            DateTimeOffset? successLast = null, failureLast = null;
            foreach (var row in result.Value.Table.Rows)
            {
                var success = row[0]?.ToString();
                var last = row[1] is DateTimeOffset dto ? dto : (row[1] is DateTime dt ? new DateTimeOffset(dt, TimeSpan.Zero) : (DateTimeOffset?)null);
                if (success == "True")       successLast = last;
                else if (success == "False") failureLast = last;
            }
            pollerLastTick = successLast ?? failureLast;
            if (successLast is null && failureLast is null)
            {
                pollerHealth = NodeHealth.Red;
                issues.Add("Status poller: nessuna invocazione nelle ultime 30 minuti. Il timer trigger su Proc potrebbe essere fermo (storage pna=Disabled?).");
            }
            else if (successLast is null)
            {
                pollerHealth = NodeHealth.Red;
                issues.Add($"Status poller: solo errori nelle ultime 30 min (ultimo fallimento {failureLast}). Aprire le exception su idactions-proc-* in App Insights.");
            }
            else if ((DateTimeOffset.UtcNow - successLast.Value).TotalMinutes > 10)
            {
                pollerHealth = NodeHealth.Yellow;
                issues.Add($"Status poller: ultimo tick OK {successLast:yyyy-MM-dd HH:mm:ss}Z (>10 min fa). Atteso ogni 1-2 min.");
            }
            else
            {
                pollerHealth = NodeHealth.Green;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cruscotto: poller heartbeat KQL failed");
            issues.Add($"Poller heartbeat lookup failed: {ex.GetType().Name} (la MSI del portale deve avere 'Log Analytics Reader' sul workspace).");
        }

        try
        {
            const string freshnessQuery = @"
                AppRequests
                | where TimeGenerated > ago(24h)
                | where AppRoleName has_any ('wipe','autopilot','bitlocker','rename')
                | summarize last = max(TimeGenerated) by AppRoleName
            ";
            var result = await _logs.QueryWorkspaceAsync(_workspaceId, freshnessQuery, new QueryTimeRange(TimeSpan.FromHours(24)), cancellationToken: ct);
            foreach (var row in result.Value.Table.Rows)
            {
                var role = row[0]?.ToString() ?? "(unknown)";
                var last = row[1] is DateTimeOffset dto ? dto : (row[1] is DateTime dt ? new DateTimeOffset(dt, TimeSpan.Zero) : (DateTimeOffset?)null);
                capabilityFreshness[role] = last;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cruscotto: capability freshness KQL failed");
            issues.Add($"Capability freshness lookup failed: {ex.GetType().Name}.");
        }

        return new DiagnosticsStatus(pollerLastTick, pollerHealth, capabilityFreshness, issues.ToArray(), KqlAvailable: true);
    }

    // ─── Per-request trace ───────────────────────────────────────────────────

    public async Task<RequestTrace> TraceByCorrelationAsync(string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("correlationId required", nameof(correlationId));
        correlationId = correlationId.Trim();

        var events = Array.Empty<TraceEvent>();
        if (_logs is not null && !string.IsNullOrEmpty(_workspaceId))
        {
            try
            {
                var safe = correlationId.Replace("'", "''");
                var q = $@"
                    union AppEvents, AppExceptions
                    | where TimeGenerated > ago(7d)
                    | where tostring(Properties.correlationId) =~ '{safe}'
                       or tostring(Properties.originalCorrelationId) =~ '{safe}'
                    | extend evt    = coalesce(Name, ExceptionType, 'event'),
                             role   = AppRoleName,
                             device = tostring(Properties.deviceName),
                             intune = tostring(Properties.intuneDeviceId),
                             reason = tostring(Properties.reason),
                             rearm  = tostring(Properties.rearmReason),
                             origCorr = tostring(Properties.originalCorrelationId)
                    | project TimeGenerated, evt, role, device, intune, reason, rearm, origCorr
                    | order by TimeGenerated asc
                    | take 200
                ";
                var result = await _logs.QueryWorkspaceAsync(_workspaceId, q, new QueryTimeRange(TimeSpan.FromDays(7)), cancellationToken: ct);
                events = result.Value.Table.Rows.Select(r =>
                {
                    var ts = r[0] is DateTimeOffset dto ? dto : new DateTimeOffset((DateTime)r[0]!, TimeSpan.Zero);
                    return new TraceEvent(
                        Timestamp: ts,
                        Name: r[1]?.ToString() ?? "",
                        Role: r[2]?.ToString() ?? "",
                        DeviceName: r[3]?.ToString(),
                        IntuneDeviceId: r[4]?.ToString(),
                        Reason: r[5]?.ToString(),
                        RearmReason: r[6]?.ToString(),
                        OriginalCorrelationId: r[7]?.ToString());
                }).ToArray();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cruscotto: KQL trace by-corr failed for {Corr}", correlationId);
            }
        }

        string? intuneId = events.Select(e => e.IntuneDeviceId).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        LedgerEntry? entry = null;
        if (_ledger is not null && !string.IsNullOrEmpty(intuneId))
        {
            try
            {
                var resp = await _ledger.GetBlobClient($"{intuneId!.ToLowerInvariant()}.json").DownloadContentAsync(ct);
                entry = JsonSerializer.Deserialize<LedgerEntry>(resp.Value.Content.ToMemory().Span);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { /* no ledger — fine */ }
            catch (Exception ex) { _log.LogDebug(ex, "Cruscotto: ledger lookup for {Id} failed", intuneId); }
        }

        var recommendation = Recommend(correlationId, events, entry);
        return new RequestTrace(
            CorrelationId: correlationId,
            DeviceName: events.Select(e => e.DeviceName).FirstOrDefault(s => !string.IsNullOrEmpty(s)),
            IntuneDeviceId: intuneId,
            Events: events,
            LedgerSummary: entry is null ? null : new LedgerSummary(
                State: entry.State, IssuedAt: entry.IssuedAt,
                LastTerminalState: entry.LastTerminalState,
                LastRearmedAt: entry.LastRearmedAt,
                ActionSequence: entry.ActionSequence,
                CorrelationId: entry.CorrelationId),
            Recommendation: recommendation);
    }

    public async Task<IReadOnlyList<DeviceRequestRow>> RecentByDeviceAsync(string deviceOrIntuneId, int take, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceOrIntuneId))
            return Array.Empty<DeviceRequestRow>();
        if (_logs is null || string.IsNullOrEmpty(_workspaceId))
            return Array.Empty<DeviceRequestRow>();

        var key = deviceOrIntuneId.Trim().Replace("'", "''");
        var q = $@"
            AppEvents
            | where TimeGenerated > ago(7d)
            | where Name in ('action.request.accepted','action.dispatch.received','wipe.action.consumed','action.already-issued')
            | where tostring(Properties.deviceName) =~ '{key}'
               or tolower(tostring(Properties.intuneDeviceId)) == tolower('{key}')
            | extend corr = tostring(Properties.correlationId),
                     device = tostring(Properties.deviceName),
                     intune = tostring(Properties.intuneDeviceId)
            | summarize firstSeen = min(TimeGenerated), lastEvent = arg_max(TimeGenerated, Name) by corr, device, intune
            | order by firstSeen desc
            | take {Math.Clamp(take, 1, 100)}
        ";
        try
        {
            var result = await _logs.QueryWorkspaceAsync(_workspaceId, q, new QueryTimeRange(TimeSpan.FromDays(7)), cancellationToken: ct);
            var rows = new List<DeviceRequestRow>();
            foreach (var r in result.Value.Table.Rows)
            {
                var firstSeen = r[1] is DateTimeOffset dto ? dto : new DateTimeOffset((DateTime)r[1]!, TimeSpan.Zero);
                var ts = r[3] is DateTimeOffset dto2 ? dto2 : new DateTimeOffset((DateTime)r[3]!, TimeSpan.Zero);
                rows.Add(new DeviceRequestRow(
                    CorrelationId: r[0]?.ToString() ?? "",
                    DeviceName: r[2]?.ToString(),
                    IntuneDeviceId: r[3]?.ToString(),
                    FirstSeen: firstSeen,
                    LastEvent: r[4]?.ToString() ?? "",
                    LastEventAt: ts));
            }
            return rows;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cruscotto: RecentByDevice KQL failed for {Key}", key);
            return Array.Empty<DeviceRequestRow>();
        }
    }

    // ─── Recommendation engine ───────────────────────────────────────────────

    private static Recommendation Recommend(string corr, IReadOnlyList<TraceEvent> events, LedgerEntry? entry)
    {
        if (events.Count == 0 && entry is null)
            return new Recommendation(
                Severity: "muted",
                Title: "Nessun evento trovato per questo correlationId nelle ultime 7 giorni.",
                Detail: "Verifica di averlo copiato correttamente. Eventi più vecchi di 7 giorni potrebbero essere stati ruotati fuori da App Insights.",
                ActionKind: "none", ActionPayload: null);

        var dedup = events.FirstOrDefault(e => e.Name == "action.already-issued");
        if (dedup is not null)
        {
            var origCorr = dedup.OriginalCorrelationId ?? entry?.CorrelationId ?? "(sconosciuto)";
            var intune   = dedup.IntuneDeviceId ?? entry?.IntuneDeviceId ?? "";
            return new Recommendation(
                Severity: "warn",
                Title: "Richiesta deduplicata: il ledger ha già una azione 'Issued' per il device.",
                Detail: $"L'ordine originale è correlationId={origCorr}, mai osservato in stato terminale dal poller. " +
                        "La richiesta corrente è stata correttamente NON inviata a Graph (idempotenza). " +
                        "Se sei sicuro che il device va wipeato di nuovo, resetta il ledger.",
                ActionKind: string.IsNullOrEmpty(intune) ? "none" : "reset-ledger",
                ActionPayload: intune);
        }

        var hasAccepted = events.Any(e => e.Name == "action.request.accepted");
        var hasReceived = events.Any(e => e.Name == "action.dispatch.received");
        if (hasAccepted && !hasReceived)
        {
            return new Recommendation(
                Severity: "error",
                Title: "La richiesta è stata accettata ma il dispatcher (Proc) non l'ha mai consumata.",
                Detail: "Cause frequenti: (1) idactions-proc-* fermo o unhealthy; (2) storage runtime con publicNetworkAccess=Disabled; (3) sottoscrizione SB rotta.",
                ActionKind: "open-azure-portal",
                ActionPayload: "function-app:proc");
        }

        var forwarded = events.FirstOrDefault(e => e.Name == "action.forwarded");
        var consumed  = events.FirstOrDefault(e => e.Name.EndsWith(".action.consumed", StringComparison.OrdinalIgnoreCase));
        if (forwarded is not null && consumed is null)
        {
            return new Recommendation(
                Severity: "error",
                Title: "Il dispatcher ha forwardato il messaggio alla capability ma il runner non l'ha mai consumato.",
                Detail: "Verifica lo stato del Function App della capability bersaglio (idactions-wipe-* / -autopilot- / -bitlocker- / -rename-).",
                ActionKind: "open-azure-portal",
                ActionPayload: "function-app:capability");
        }

        var failed = events.FirstOrDefault(e => e.Name.EndsWith(".action.failed", StringComparison.OrdinalIgnoreCase));
        if (failed is not null)
        {
            return new Recommendation(
                Severity: "error",
                Title: $"Il runner ha riportato fallimento ({failed.Name}).",
                Detail: failed.Reason ?? "Apri le exception in App Insights per il role indicato e cerca il correlationId per la stack trace completa.",
                ActionKind: "open-app-insights",
                ActionPayload: corr);
        }

        var completed = events.FirstOrDefault(e => e.Name.EndsWith(".action.completed", StringComparison.OrdinalIgnoreCase));
        if (completed is not null)
        {
            return new Recommendation(
                Severity: "ok",
                Title: "Il runner ha completato regolarmente. Il comando è stato accettato da Graph.",
                Detail: "Da qui in poi è Intune che applica l'azione al device. Lo status poller aggiornerà il tracker man mano che Intune segnala progressi.",
                ActionKind: "none", ActionPayload: null);
        }

        if (consumed is not null)
        {
            return new Recommendation(
                Severity: "warn",
                Title: "Il runner ha iniziato a processare ma non ha ancora emesso evento terminale.",
                Detail: "Se l'evento 'consumed' è recente (<5 min) probabilmente è normale — attendi il prossimo tick. Se è più vecchio, il runner è crashato durante la chiamata a Graph.",
                ActionKind: "open-app-insights",
                ActionPayload: corr);
        }

        return new Recommendation(
            Severity: "muted",
            Title: "Stato indeterminato.",
            Detail: $"Eventi trovati ({events.Count}) ma non corrispondono a un pattern noto. Vedi la timeline e/o il ledger.",
            ActionKind: "none", ActionPayload: null);
    }

    private sealed record LedgerEntry
    {
        public string? IntuneDeviceId { get; init; }
        public string  CorrelationId { get; init; } = "";
        public string? State { get; init; }
        public DateTimeOffset? IssuedAt { get; init; }
        public DateTimeOffset? LastRearmedAt { get; init; }
        public string? LastTerminalState { get; init; }
        public int ActionSequence { get; init; }
    }
}

// ─── Public DTOs ─────────────────────────────────────────────────────────────

public enum NodeHealth { Green, Yellow, Red, Unknown }

public sealed record DashboardSnapshot(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<QueueStatus> Queues,
    LedgerStatus Ledger,
    DiagnosticsStatus Diagnostics,
    string[] Warnings);

public sealed record QueueStatus(
    string Name, long Active, long DeadLetter, long Scheduled,
    DateTimeOffset? AccessedAt, NodeHealth Status, string? Error);

public sealed record StuckLedgerEntry(
    string IntuneDeviceId, string CorrelationId, DateTimeOffset IssuedAt, double AgeHours);

public sealed record LedgerStatus(
    int TotalEntries, int StuckEntries,
    DateTimeOffset? OldestStuckIssuedAt, string? OldestStuckIntuneDeviceId,
    IReadOnlyList<StuckLedgerEntry> TopStuck,
    double GraceHours, NodeHealth Status, string? Error);

public sealed record DiagnosticsStatus(
    DateTimeOffset? PollerLastTick,
    NodeHealth PollerHealth,
    IReadOnlyDictionary<string, DateTimeOffset?> CapabilityLastSeen,
    string[] Issues,
    bool KqlAvailable);

public sealed record RequestTrace(
    string CorrelationId,
    string? DeviceName,
    string? IntuneDeviceId,
    IReadOnlyList<TraceEvent> Events,
    LedgerSummary? LedgerSummary,
    Recommendation Recommendation);

public sealed record TraceEvent(
    DateTimeOffset Timestamp, string Name, string Role,
    string? DeviceName, string? IntuneDeviceId,
    string? Reason, string? RearmReason, string? OriginalCorrelationId);

public sealed record LedgerSummary(
    string? State, DateTimeOffset? IssuedAt, string? LastTerminalState,
    DateTimeOffset? LastRearmedAt, int ActionSequence, string CorrelationId);

public sealed record Recommendation(
    string Severity,
    string Title,
    string Detail,
    string ActionKind,
    string? ActionPayload);

public sealed record DeviceRequestRow(
    string CorrelationId, string? DeviceName, string? IntuneDeviceId,
    DateTimeOffset FirstSeen, string LastEvent, DateTimeOffset LastEventAt);
