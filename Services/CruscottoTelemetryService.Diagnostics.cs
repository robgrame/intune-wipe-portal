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

// Runtime diagnostics (poller heartbeat, freshness, anomalies) and restart.
public sealed partial class CruscottoTelemetryService
{
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
                    or Name startswith 'action.denied.'
                    or Name == 'action.schedule.gate-denied'
                    or tostring(Properties.reason) has_any ('denied','not-in-entra','not-allowed','group')
                | project TimeGenerated,
                          eventName = Name,
                          device = tostring(Properties.deviceName),
                          reason = coalesce(tostring(Properties.reason), tostring(Properties.scheduleGateReason)),
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
}
