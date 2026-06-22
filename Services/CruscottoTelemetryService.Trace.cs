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

// Per-request correlation trace, recent-by-device, recommendation engine.
public sealed partial class CruscottoTelemetryService
{
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
                             reason = coalesce(tostring(Properties.reason), tostring(Properties.scheduleGateReason)),
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
                _log.LogWarning(ex, "Cruscotto: KQL trace by-corr failed for {Corr}", ForLog(correlationId));
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
            catch (Exception ex) { _log.LogDebug(ex, "Cruscotto: ledger lookup for {Id} failed", ForLog(intuneId)); }
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
            _log.LogWarning(ex, "Cruscotto: RecentByDevice KQL failed for {Key}", ForLog(key));
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
}
