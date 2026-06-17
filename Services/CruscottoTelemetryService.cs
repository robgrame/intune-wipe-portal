using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
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
    private readonly ServiceBusClient? _sbClient;
    private readonly TokenCredential _credential;
    private readonly BlobContainerClient? _ledger;
    private readonly LogsQueryClient? _logs;
    private readonly string? _workspaceId;
    private readonly string? _subscriptionId;
    private readonly string? _resourceGroupName;
    private readonly double _graceHours;
    private readonly ILogger<CruscottoTelemetryService> _log;
    private readonly EventGridMetricsCollector _metricsCollector;
    private static readonly HttpClient ManagementHttpClient = new();

    private static readonly string[] FlowQueues =
    {
        "action-requests",
        "action-dispatch",
        "wipe-action",
        "autopilot-action",
        "bitlocker-action",
        "rename-action",
    };

    private static readonly HashSet<string> RestartableFunctionApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "devact-web-dev",
        "devact-proc-dev",
        "devact-wipe-dev",
        "devact-autopilot-dev",
        "devact-bitlocker-dev",
        "devact-rename-dev",
        "idactions-web-dev",
        "idactions-proc-dev",
        "idactions-wipe-dev",
        "idactions-autopilot-dev",
        "idactions-bitlocker-dev",
        "idactions-rename-dev",
    };

    public CruscottoTelemetryService(
        IConfiguration cfg,
        TokenCredential cred,
        LogsQueryClient logs,
        EventGridMetricsCollector metricsCollector,
        ILogger<CruscottoTelemetryService> log)
    {
        _credential = cred;
        _log = log;
        _logs = logs;
        _metricsCollector = metricsCollector;
        _workspaceId = cfg["Monitor:WorkspaceId"];
        _subscriptionId = cfg["Cruscotto:SubscriptionId"] ?? ResolveSubscriptionIdFromWebsiteOwnerName();
        _resourceGroupName = cfg["Cruscotto:ResourceGroupName"]
                             ?? cfg["Cruscotto:ResourceGroup"]
                             ?? Environment.GetEnvironmentVariable("WEBSITE_RESOURCE_GROUP");

        var sbFqdn = cfg["Cruscotto:ServiceBusFullyQualifiedNamespace"];
        if (!string.IsNullOrWhiteSpace(sbFqdn))
        {
            _sbAdmin = new ServiceBusAdministrationClient(sbFqdn, cred);
            _sbClient = new ServiceBusClient(sbFqdn, cred);
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

    public async Task<FunctionRestartResult> RestartFunctionAppAsync(string functionAppName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(functionAppName))
            throw new ArgumentException("Function app name is required.", nameof(functionAppName));

        if (!RestartableFunctionApps.Contains(functionAppName))
            throw new ArgumentException($"Function app '{functionAppName}' is not in allowlist.", nameof(functionAppName));

        if (string.IsNullOrWhiteSpace(_subscriptionId) || string.IsNullOrWhiteSpace(_resourceGroupName))
            throw new InvalidOperationException("Cruscotto ARM context is not configured (Cruscotto:SubscriptionId / Cruscotto:ResourceGroupName).");

        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
            ct);

        var url = $"https://management.azure.com/subscriptions/{_subscriptionId}/resourceGroups/{_resourceGroupName}/providers/Microsoft.Web/sites/{functionAppName}/restart?api-version=2024-04-01";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await ManagementHttpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Restart failed for {functionAppName}: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}".Trim());
        }

        return new FunctionRestartResult(
            FunctionAppName: functionAppName,
            Accepted: true,
            HttpStatusCode: (int)response.StatusCode,
            RequestedAt: DateTimeOffset.UtcNow);
    }

    public async Task<QueuePurgeResult> PurgeQueueAsync(string queueName, int maxMessages, bool deadLetterQueue, CancellationToken ct)
    {
        if (_sbClient is null)
            throw new InvalidOperationException("Service Bus data client is not configured.");

        if (string.IsNullOrWhiteSpace(queueName) || !FlowQueues.Contains(queueName, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Queue '{queueName}' is not allowed.", nameof(queueName));

        var max = Math.Clamp(maxMessages, 1, 2_000);
        var drained = 0;

        await using var receiver = _sbClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            SubQueue = deadLetterQueue ? SubQueue.DeadLetter : SubQueue.None,
        });

        while (drained < max)
        {
            var chunk = Math.Min(100, max - drained);
            var messages = await receiver.ReceiveMessagesAsync(
                maxMessages: chunk,
                maxWaitTime: TimeSpan.FromSeconds(2),
                cancellationToken: ct);

            if (messages.Count == 0) break;

            foreach (var message in messages)
            {
                await receiver.CompleteMessageAsync(message, ct);
                drained++;
            }
        }

        return new QueuePurgeResult(
            QueueName: queueName,
            IsDeadLetterQueue: deadLetterQueue,
            DrainedMessages: drained,
            LimitReached: drained >= max,
            PerformedAt: DateTimeOffset.UtcNow);
    }

    public async Task<QueuePeekResult> PeekQueueAsync(string queueName, int top, bool deadLetterQueue, CancellationToken ct)
    {
        if (_sbClient is null)
            throw new InvalidOperationException("Service Bus data client is not configured.");

        if (string.IsNullOrWhiteSpace(queueName) || !FlowQueues.Contains(queueName, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Queue '{queueName}' is not allowed.", nameof(queueName));

        var take = Math.Clamp(top, 1, 25);
        var messages = new List<QueuePeekMessage>(take);

        await using var receiver = _sbClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = deadLetterQueue ? SubQueue.DeadLetter : SubQueue.None,
        });

        long? fromSequenceNumber = null;
        while (messages.Count < take)
        {
            var chunk = Math.Min(10, take - messages.Count);
            var peeked = await receiver.PeekMessagesAsync(chunk, fromSequenceNumber, ct);
            if (peeked.Count == 0)
                break;

            foreach (var message in peeked)
            {
                messages.Add(MapPeekMessage(message));
                fromSequenceNumber = message.SequenceNumber + 1;
                if (messages.Count >= take)
                    break;
            }
        }

        return new QueuePeekResult(
            QueueName: queueName,
            IsDeadLetterQueue: deadLetterQueue,
            RequestedMessages: take,
            RetrievedMessages: messages.Count,
            PerformedAt: DateTimeOffset.UtcNow,
            Messages: messages.ToArray());
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
            Warnings: warnings.ToArray(),
            DeniedRequests: await GetDeniedEventsWithFallbackAsync(ct));
    }

    /// <summary>
    /// Returns denied events from the in-memory collector, falling back to App Insights KQL
    /// when the collector is empty (e.g. after portal restart).
    /// </summary>
    private async Task<IReadOnlyList<DeniedEvent>> GetDeniedEventsWithFallbackAsync(CancellationToken ct)
    {
        var inMemory = _metricsCollector.GetDeniedEvents();
        if (inMemory.Count > 0) return inMemory;

        // Fallback: query App Insights for denied events from the last hour
        if (_logs is null || string.IsNullOrEmpty(_workspaceId)) return inMemory;

        try
        {
            const string query = @"
                AppEvents
                | where TimeGenerated > ago(1h)
                | where Name has '.denied'
                | project TimeGenerated,
                          eventName = Name,
                          deviceName = tostring(Properties.deviceName),
                          correlationId = tostring(Properties.correlationId),
                          actionType = tostring(Properties.actionType),
                          reason = tostring(Properties.reason)
                | order by TimeGenerated desc
                | take 20
            ";
            var result = await _logs.QueryWorkspaceAsync(_workspaceId, query, new QueryTimeRange(TimeSpan.FromHours(1)), cancellationToken: ct);
            var fallback = new List<DeniedEvent>();
            foreach (var row in result.Value.Table.Rows)
            {
                var ts = row[0] is DateTimeOffset dto ? dto : (row[0] is DateTime dt ? new DateTimeOffset(dt, TimeSpan.Zero) : DateTimeOffset.UtcNow);
                var eventName = row[1]?.ToString() ?? "(unknown)";
                var deviceName = row[2]?.ToString();
                var correlationId = row[3]?.ToString();
                var actionType = row[4]?.ToString();
                var reason = row[5]?.ToString();

                if (string.IsNullOrWhiteSpace(reason))
                {
                    reason = eventName switch
                    {
                        var e when e.Contains("not-in-allowed-group", StringComparison.OrdinalIgnoreCase) => "Device non nel gruppo autorizzato",
                        var e when e.Contains("group-check-failed", StringComparison.OrdinalIgnoreCase) => "Verifica gruppo fallita",
                        var e when e.Contains("not-in-entra", StringComparison.OrdinalIgnoreCase) => "Device non registrato in Entra ID",
                        var e when e.Contains("ownership-mismatch", StringComparison.OrdinalIgnoreCase) => "Device non appartiene all'utente richiedente",
                        var e when e.Contains("rate-limited", StringComparison.OrdinalIgnoreCase) => "Troppi tentativi (rate limit)",
                        var e when e.Contains("device-resolve-failed", StringComparison.OrdinalIgnoreCase) => "Risoluzione device fallita",
                        _ => eventName
                    };
                }

                fallback.Add(new DeniedEvent(ts, eventName, "kql-fallback", deviceName, correlationId, reason, actionType));
            }
            return fallback;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cruscotto: denied events KQL fallback failed");
            return inMemory;
        }
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
            var earlyApps = new Dictionary<string, FunctionAppStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var (role, status) in _metricsCollector.GetSnapshot())
                earlyApps[role] = status;
            return new DiagnosticsStatus(pollerLastTick, pollerHealth, capabilityFreshness, earlyApps, issues.ToArray(), KqlAvailable: false);
        }

        try
        {
            const string pollerQuery = @"
                AppTraces
                | where TimeGenerated > ago(30m)
                | where AppRoleName has 'proc'
                | where Message has 'Functions.ActionStatusPoller'
                | summarize
                    successLast = maxif(TimeGenerated, Message has '(Succeeded'),
                    failureLast = maxif(TimeGenerated, Message has '(Failed')
            ";
            var result = await _logs.QueryWorkspaceAsync(_workspaceId, pollerQuery, new QueryTimeRange(TimeSpan.FromMinutes(30)), cancellationToken: ct);
            DateTimeOffset? successLast = null, failureLast = null;
            if (result.Value.Table.Rows.Count > 0)
            {
                var row = result.Value.Table.Rows[0];
                successLast = row[0] is DateTimeOffset sDto ? sDto : (row[0] is DateTime sDt ? new DateTimeOffset(sDt, TimeSpan.Zero) : (DateTimeOffset?)null);
                failureLast = row[1] is DateTimeOffset fDto ? fDto : (row[1] is DateTime fDt ? new DateTimeOffset(fDt, TimeSpan.Zero) : (DateTimeOffset?)null);
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
            issues.Add($"Poller heartbeat lookup failed: {ex.GetType().Name}.");
        }

        try
        {
            const string freshnessQuery = @"
                union AppRequests, AppTraces
                | where TimeGenerated > ago(24h)
                | where AppRoleName has_any ('wipe','autopilot','bitlocker','rename')
                | summarize lastSeen = max(TimeGenerated) by AppRoleName
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

        try
        {
            const string anomaliesQuery = @"
                AppEvents
                | where TimeGenerated > ago(30m)
                | where Name has 'fallback.issued'
                    or Name has '.denied'
                    or tostring(Properties.reason) has_any ('denied','not-in-entra','not-allowed','group')
                | project TimeGenerated,
                          eventName = Name,
                          device = tostring(Properties.deviceName),
                          reason = tostring(Properties.reason),
                          corr = tostring(Properties.correlationId)
                | order by TimeGenerated desc
                | take 5
            ";
            var result = await _logs.QueryWorkspaceAsync(_workspaceId, anomaliesQuery, new QueryTimeRange(TimeSpan.FromMinutes(30)), cancellationToken: ct);
            foreach (var row in result.Value.Table.Rows)
            {
                var ts = row[0] is DateTimeOffset dto ? dto : (row[0] is DateTime dt ? new DateTimeOffset(dt, TimeSpan.Zero) : (DateTimeOffset?)null);
                var eventName = row[1]?.ToString() ?? "(event)";
                var device = row[2]?.ToString();
                var reason = row[3]?.ToString();
                var corr = row[4]?.ToString();
                issues.Add($"Recent anomaly: {eventName} @ {ts:yyyy-MM-dd HH:mm:ss}Z (device={device ?? "-"}, reason={reason ?? "-"}, corr={corr ?? "-"})");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cruscotto: recent anomalies KQL failed");
            issues.Add($"Recent anomalies lookup failed: {ex.GetType().Name}.");
        }

        var functionApps = new Dictionary<string, FunctionAppStatus>(StringComparer.OrdinalIgnoreCase);

        // Populate from real-time Event Grid metrics (no KQL needed)
        foreach (var (role, status) in _metricsCollector.GetSnapshot())
            functionApps[role] = status;

        return new DiagnosticsStatus(pollerLastTick, pollerHealth, capabilityFreshness, functionApps, issues.ToArray(), KqlAvailable: true);
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
                             state = tostring(Properties.state),
                             terminal = tostring(Properties.terminalState),
                             rawStatus = tostring(Properties.rawStatus),
                             rearm  = tostring(Properties.rearmReason),
                             origCorr = tostring(Properties.originalCorrelationId)
                    | project TimeGenerated, evt, role, device, intune, reason, state, terminal, rawStatus, rearm, origCorr
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
                        State: r[6]?.ToString(),
                        TerminalState: r[7]?.ToString(),
                        RawStatus: r[8]?.ToString(),
                        RearmReason: r[9]?.ToString(),
                        OriginalCorrelationId: r[10]?.ToString());
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
            | where tolower(tostring(Properties.deviceName)) has tolower('{key}')
               or tolower(tostring(Properties.intuneDeviceId)) == tolower('{key}')
            | extend corr = tostring(Properties.correlationId),
                     device = tostring(Properties.deviceName),
                     intune = tostring(Properties.intuneDeviceId),
                     ts = TimeGenerated,
                     evt = Name
            | project corr, device, intune, ts, evt
            | order by ts desc
            | take {Math.Clamp(take, 1, 500)}
        ";
        try
        {
            var result = await _logs.QueryWorkspaceAsync(_workspaceId, q, new QueryTimeRange(TimeSpan.FromDays(7)), cancellationToken: ct);
            var events = new List<(string Corr, string? Device, string? Intune, DateTimeOffset Ts, string Evt)>();
            foreach (var r in result.Value.Table.Rows)
            {
                var corr = r[0]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(corr)) continue;
                var ts = r[3] is DateTimeOffset dto
                    ? dto
                    : (r[3] is DateTime dt ? new DateTimeOffset(dt, TimeSpan.Zero) : DateTimeOffset.MinValue);
                if (ts == DateTimeOffset.MinValue) continue;
                events.Add((
                    Corr: corr,
                    Device: r[1]?.ToString(),
                    Intune: r[2]?.ToString(),
                    Ts: ts,
                    Evt: r[4]?.ToString() ?? ""));
            }

            return events
                .GroupBy(e => e.Corr, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var last = g.OrderByDescending(x => x.Ts).First();
                    var first = g.OrderBy(x => x.Ts).First();
                    return new DeviceRequestRow(
                        CorrelationId: g.Key,
                        DeviceName: last.Device,
                        IntuneDeviceId: last.Intune,
                        FirstSeen: first.Ts,
                        LastEvent: last.Evt,
                        LastEventAt: last.Ts);
                })
                .OrderByDescending(r => r.FirstSeen)
                .Take(Math.Clamp(take, 1, 100))
                .ToArray();
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

    private static QueuePeekMessage MapPeekMessage(ServiceBusReceivedMessage message)
    {
        var applicationProperties = message.ApplicationProperties.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : message.ApplicationProperties.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.ToString() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        return new QueuePeekMessage(
            SequenceNumber: message.SequenceNumber,
            MessageId: message.MessageId,
            CorrelationId: NullIfEmpty(message.CorrelationId),
            Subject: NullIfEmpty(message.Subject),
            SessionId: NullIfEmpty(message.SessionId),
            EnqueuedTimeUtc: message.EnqueuedTime == DateTimeOffset.MinValue ? null : message.EnqueuedTime,
            DeliveryCount: message.DeliveryCount,
            ContentType: NullIfEmpty(message.ContentType),
            BodyPreview: TruncateBody(message.Body.ToString(), 2000),
            BodyLength: message.Body.ToString().Length,
            DeadLetterReason: NullIfEmpty(message.DeadLetterReason),
            DeadLetterErrorDescription: NullIfEmpty(message.DeadLetterErrorDescription),
            ApplicationProperties: applicationProperties);
    }

    private static string TruncateBody(string? body, int maxChars)
    {
        if (string.IsNullOrEmpty(body))
            return string.Empty;

        return body.Length <= maxChars
            ? body
            : body[..maxChars] + "…";
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? ResolveSubscriptionIdFromWebsiteOwnerName()
    {
        var owner = Environment.GetEnvironmentVariable("WEBSITE_OWNER_NAME");
        if (string.IsNullOrWhiteSpace(owner))
            return null;

        var plus = owner.IndexOf('+');
        if (plus <= 0)
            return null;

        var subscriptionId = owner[..plus];
        return Guid.TryParse(subscriptionId, out _) ? subscriptionId : null;
    }
}

// ─── Public DTOs ─────────────────────────────────────────────────────────────

public enum NodeHealth { Green, Yellow, Red, Unknown }

public sealed record DashboardSnapshot(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<QueueStatus> Queues,
    LedgerStatus Ledger,
    DiagnosticsStatus Diagnostics,
    string[] Warnings,
    IReadOnlyList<DeniedEvent> DeniedRequests);

public sealed record QueueStatus(
    string Name, long Active, long DeadLetter, long Scheduled,
    DateTimeOffset? AccessedAt, NodeHealth Status, string? Error);

public sealed record QueuePurgeResult(
    string QueueName,
    bool IsDeadLetterQueue,
    int DrainedMessages,
    bool LimitReached,
    DateTimeOffset PerformedAt);

public sealed record QueuePeekResult(
    string QueueName,
    bool IsDeadLetterQueue,
    int RequestedMessages,
    int RetrievedMessages,
    DateTimeOffset PerformedAt,
    IReadOnlyList<QueuePeekMessage> Messages);

public sealed record QueuePeekMessage(
    long SequenceNumber,
    string MessageId,
    string? CorrelationId,
    string? Subject,
    string? SessionId,
    DateTimeOffset? EnqueuedTimeUtc,
    int DeliveryCount,
    string? ContentType,
    string BodyPreview,
    int BodyLength,
    string? DeadLetterReason,
    string? DeadLetterErrorDescription,
    IReadOnlyDictionary<string, string> ApplicationProperties);

public sealed record FunctionRestartResult(
    string FunctionAppName,
    bool Accepted,
    int HttpStatusCode,
    DateTimeOffset RequestedAt);

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
    Dictionary<string, FunctionAppStatus> FunctionApps,
    string[] Issues,
    bool KqlAvailable);

public sealed record FunctionAppStatus(
    string AppName,
    NodeHealth Health,
    long Requests30m,
    long Failures30m,
    double? AvgDurationMs,
    DateTimeOffset? LastRequestAt,
    string? LastError);

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
    string? Reason, string? State, string? TerminalState, string? RawStatus,
    string? RearmReason, string? OriginalCorrelationId);

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
