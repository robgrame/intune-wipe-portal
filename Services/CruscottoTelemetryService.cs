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
public sealed partial class CruscottoTelemetryService
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
                | where Name startswith 'action.denied.' or Name == 'action.schedule.gate-denied'
                | project TimeGenerated,
                          eventName = Name,
                          deviceName = tostring(Properties.deviceName),
                          correlationId = tostring(Properties.correlationId),
                          actionType = tostring(Properties.actionType),
                          reason = coalesce(tostring(Properties.reason), tostring(Properties.scheduleGateReason))
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
