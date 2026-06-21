using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace IntuneWipePortal.Services;

/// <summary>
/// Aggrega le metriche per la pagina di <b>Monitoraggio</b> (<c>/monitoraggio</c>),
/// una vista d'insieme distinta dal Cruscotto operativo: invece di tracciare il
/// flusso del singolo messaggio, espone serie temporali e classifiche utili a
/// valutare lo stato di salute complessivo della pipeline IntuneDeviceActions.
///
/// <para>Tutte le metriche derivano da query KQL sul Log Analytics workspace che
/// fronteggia App Insights (tabelle <c>AppEvents</c>, <c>AppRequests</c>,
/// <c>AppDependencies</c>, <c>AppExceptions</c>, <c>AppPerformanceCounters</c>),
/// più lo snapshot delle code Service Bus riusato da
/// <see cref="CruscottoTelemetryService"/>. Ogni query è fail-soft: un errore o
/// l'assenza di configurazione restituisce una sezione vuota senza far cadere la
/// pagina.</para>
/// </summary>
public sealed class MetricsService
{
    private readonly LogsQueryClient? _logs;
    private readonly string? _workspaceId;
    private readonly CruscottoTelemetryService _cruscotto;
    private readonly ILogger<MetricsService> _log;

    public MetricsService(
        LogsQueryClient logs,
        IConfiguration cfg,
        CruscottoTelemetryService cruscotto,
        ILogger<MetricsService> log)
    {
        _logs = logs;
        _workspaceId = cfg["Monitor:WorkspaceId"];
        _cruscotto = cruscotto;
        _log = log;
    }

    /// <summary>
    /// Costruisce lo snapshot completo della pagina di monitoraggio sulla
    /// finestra temporale indicata (default 24h). Le sezioni sono calcolate in
    /// parallelo e ogni eventuale errore viene degradato a sezione vuota.
    /// </summary>
    public async Task<MonitoringSnapshot> GetSnapshotAsync(TimeSpan window, CancellationToken ct)
    {
        var bin = ChooseBin(window);

        var throughputTask  = SafeAsync(() => GetThroughputAsync(window, bin, ct), Array.Empty<ThroughputPoint>());
        var responseTask    = SafeAsync(() => GetResponseTimesAsync(window, bin, ct), Array.Empty<ResponseTimePoint>());
        var resourcesTask   = SafeAsync(() => GetResourceUsageAsync(window, ct), Array.Empty<ComponentLoad>());
        var perfTask        = SafeAsync(() => GetPerformanceCountersAsync(window, ct), Array.Empty<ResourceCounter>());
        var slowOpsTask     = SafeAsync(() => GetSlowestOperationsAsync(window, ct), Array.Empty<OperationStat>());
        var dependencyTask  = SafeAsync(() => GetSlowestDependenciesAsync(window, ct), Array.Empty<DependencyStat>());
        var exceptionsTask  = SafeAsync(() => GetTopExceptionsAsync(window, ct), Array.Empty<ExceptionStat>());
        var devicesTask     = SafeAsync(() => GetTopDevicesAsync(window, ct), Array.Empty<DeviceStat>());
        var usersTask       = SafeAsync(() => GetTopUsersAsync(window, ct), Array.Empty<UserStat>());
        var outcomeTask     = SafeAsync(() => GetOutcomeBreakdownAsync(window, ct), new OutcomeBreakdown(0, 0, 0, 0));
        var distinctTask    = SafeAsync(() => GetDistinctCountsAsync(window, ct), new DistinctCounts(0, 0));
        var queuesTask      = SafeAsync(() => GetQueueBacklogAsync(ct), Array.Empty<QueueBacklog>());

        await Task.WhenAll(
            throughputTask, responseTask, resourcesTask, perfTask, slowOpsTask,
            dependencyTask, exceptionsTask, devicesTask, usersTask, outcomeTask,
            distinctTask, queuesTask);

        var throughput = await throughputTask;
        var responses  = await responseTask;
        var outcome    = await outcomeTask;
        var distinct   = await distinctTask;

        var totalProcessed = throughput.Sum(p => p.Completed + p.Failed);
        var totalRequests  = throughput.Sum(p => p.Accepted);
        var totalErrors    = outcome.Failed + outcome.Timeout;
        var totalTerminal  = outcome.Completed + outcome.Failed + outcome.Timeout + outcome.Denied;
        var errorRate      = totalTerminal > 0 ? Math.Round(100.0 * totalErrors / totalTerminal, 1) : 0;

        // Latenza P95/avg complessiva = media pesata sui campioni dei bin.
        double? p95 = null, avg = null;
        var samples = responses.Sum(r => r.Count);
        if (samples > 0)
        {
            p95 = Math.Round(responses.Where(r => r.P95Ms.HasValue).Sum(r => r.P95Ms!.Value * r.Count) / samples, 0);
            avg = Math.Round(responses.Where(r => r.AvgMs.HasValue).Sum(r => r.AvgMs!.Value * r.Count) / samples, 0);
        }

        var kpis = new MonitoringKpis(
            TotalRequests:     totalRequests,
            TotalProcessed:    totalProcessed,
            AvgResponseMs:     avg,
            P95ResponseMs:     p95,
            ErrorRatePercent:  errorRate,
            DistinctDevices:   distinct.Devices,
            DistinctUsers:     distinct.Users,
            QueueBacklog:      (await queuesTask).Sum(q => q.Active),
            DeadLetters:       (await queuesTask).Sum(q => q.DeadLetter));

        return new MonitoringSnapshot(
            GeneratedAt:        DateTimeOffset.UtcNow,
            WindowHours:        window.TotalHours,
            Kqlavailable:       _logs is not null && !string.IsNullOrEmpty(_workspaceId),
            Kpis:               kpis,
            Throughput:         throughput,
            ResponseTimes:      responses,
            ComponentLoad:      await resourcesTask,
            ResourceCounters:   await perfTask,
            SlowestOperations:  await slowOpsTask,
            SlowestDependencies:await dependencyTask,
            TopExceptions:      await exceptionsTask,
            TopDevices:         await devicesTask,
            TopUsers:           await usersTask,
            Outcome:            outcome,
            Queues:             await queuesTask);
    }

    // ─── Serie temporali ─────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ThroughputPoint>> GetThroughputAsync(TimeSpan window, string bin, CancellationToken ct)
    {
        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name in ("action.request.accepted", "action.completed", "action.failed")
            | summarize
                Accepted  = countif(Name == "action.request.accepted"),
                Completed = countif(Name == "action.completed"),
                Failed    = countif(Name == "action.failed")
              by bin(TimeGenerated, {{bin}})
            | order by TimeGenerated asc
            """;
        return await ExecuteAsync(query, window, ct, r => new ThroughputPoint(
            r.GetDateTimeOffset("TimeGenerated") ?? DateTimeOffset.MinValue,
            r.GetInt64("Accepted") ?? 0,
            r.GetInt64("Completed") ?? 0,
            r.GetInt64("Failed") ?? 0));
    }

    private async Task<IReadOnlyList<ResponseTimePoint>> GetResponseTimesAsync(TimeSpan window, string bin, CancellationToken ct)
    {
        var query = $$"""
            AppRequests
            | where TimeGenerated > ago({{ToKql(window)}})
            | summarize
                AvgMs = avg(DurationMs),
                P50Ms = percentile(DurationMs, 50),
                P95Ms = percentile(DurationMs, 95),
                Count = count()
              by bin(TimeGenerated, {{bin}})
            | order by TimeGenerated asc
            """;
        return await ExecuteAsync(query, window, ct, r => new ResponseTimePoint(
            r.GetDateTimeOffset("TimeGenerated") ?? DateTimeOffset.MinValue,
            r.GetDouble("AvgMs"),
            r.GetDouble("P50Ms"),
            r.GetDouble("P95Ms"),
            r.GetInt64("Count") ?? 0));
    }

    // ─── Utilizzo risorse ────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ComponentLoad>> GetResourceUsageAsync(TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppRequests
            | where TimeGenerated > ago({{ToKql(window)}})
            | summarize
                Requests  = count(),
                Failures  = countif(Success == false),
                AvgMs     = avg(DurationMs),
                P95Ms     = percentile(DurationMs, 95)
              by Role = AppRoleName
            | order by Requests desc
            | take 20
            """;
        return await ExecuteAsync(query, window, ct, r => new ComponentLoad(
            r.GetString("Role") ?? "(sconosciuto)",
            r.GetInt64("Requests") ?? 0,
            r.GetInt64("Failures") ?? 0,
            r.GetDouble("AvgMs"),
            r.GetDouble("P95Ms")));
    }

    private async Task<IReadOnlyList<ResourceCounter>> GetPerformanceCountersAsync(TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppPerformanceCounters
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name in ("% Processor Time", "Process CPU", "Available Bytes", "Private Bytes",
                             "% Committed Bytes In Use", "Requests/Sec", "Request Execution Time")
            | summarize Avg = avg(Value), Max = max(Value) by Role = AppRoleName, Counter = Name
            | order by Role asc, Counter asc
            | take 60
            """;
        return await ExecuteAsync(query, window, ct, r => new ResourceCounter(
            r.GetString("Role") ?? "(sconosciuto)",
            r.GetString("Counter") ?? "",
            r.GetDouble("Avg"),
            r.GetDouble("Max")));
    }

    // ─── Bottleneck ──────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<OperationStat>> GetSlowestOperationsAsync(TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppRequests
            | where TimeGenerated > ago({{ToKql(window)}})
            | summarize
                Count    = count(),
                Failures = countif(Success == false),
                AvgMs    = avg(DurationMs),
                P95Ms    = percentile(DurationMs, 95)
              by Operation = Name, Role = AppRoleName
            | where Count > 0
            | top 10 by P95Ms desc
            """;
        return await ExecuteAsync(query, window, ct, r => new OperationStat(
            r.GetString("Operation") ?? "(operazione)",
            r.GetString("Role") ?? "",
            r.GetInt64("Count") ?? 0,
            r.GetInt64("Failures") ?? 0,
            r.GetDouble("AvgMs"),
            r.GetDouble("P95Ms")));
    }

    private async Task<IReadOnlyList<DependencyStat>> GetSlowestDependenciesAsync(TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppDependencies
            | where TimeGenerated > ago({{ToKql(window)}})
            | summarize
                Calls    = count(),
                Failures = countif(Success == false),
                AvgMs    = avg(DurationMs),
                P95Ms    = percentile(DurationMs, 95)
              by Target, Type = DependencyType
            | where Calls > 0
            | top 10 by P95Ms desc
            """;
        return await ExecuteAsync(query, window, ct, r => new DependencyStat(
            r.GetString("Target") ?? "(dipendenza)",
            r.GetString("Type") ?? "",
            r.GetInt64("Calls") ?? 0,
            r.GetInt64("Failures") ?? 0,
            r.GetDouble("AvgMs"),
            r.GetDouble("P95Ms")));
    }

    private async Task<IReadOnlyList<ExceptionStat>> GetTopExceptionsAsync(TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppExceptions
            | where TimeGenerated > ago({{ToKql(window)}})
            | summarize Count = count(), LastSeen = max(TimeGenerated)
              by Type = ExceptionType, Role = AppRoleName
            | top 10 by Count desc
            """;
        return await ExecuteAsync(query, window, ct, r => new ExceptionStat(
            r.GetString("Type") ?? "(eccezione)",
            r.GetString("Role") ?? "",
            r.GetInt64("Count") ?? 0,
            r.GetDateTimeOffset("LastSeen")));
    }

    // ─── Device & utenti ─────────────────────────────────────────────────────

    private async Task<IReadOnlyList<DeviceStat>> GetTopDevicesAsync(TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name startswith "action."
            | extend device = tostring(Properties.deviceName)
            | where isnotempty(device)
            | summarize
                Actions   = countif(Name == "action.request.accepted"),
                Completed = countif(Name == "action.completed"),
                Failed    = countif(Name == "action.failed"),
                LastSeen  = max(TimeGenerated)
              by device
            | top 10 by Actions desc
            """;
        return await ExecuteAsync(query, window, ct, r => new DeviceStat(
            r.GetString("device") ?? "(device)",
            r.GetInt64("Actions") ?? 0,
            r.GetInt64("Completed") ?? 0,
            r.GetInt64("Failed") ?? 0,
            r.GetDateTimeOffset("LastSeen")));
    }

    private async Task<IReadOnlyList<UserStat>> GetTopUsersAsync(TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name == "action.request.accepted"
            | extend caller = tostring(Properties.callerUpn)
            | where isnotempty(caller)
            | summarize Requests = count(), Devices = dcount(tostring(Properties.intuneDeviceId)), LastSeen = max(TimeGenerated)
              by caller
            | top 10 by Requests desc
            """;
        return await ExecuteAsync(query, window, ct, r => new UserStat(
            r.GetString("caller") ?? "(utente)",
            r.GetInt64("Requests") ?? 0,
            r.GetInt64("Devices") ?? 0,
            r.GetDateTimeOffset("LastSeen")));
    }

    private async Task<OutcomeBreakdown> GetOutcomeBreakdownAsync(TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name == "action.completed"
                or Name == "action.failed"
                or Name == "action.poll-timeout"
                or Name startswith "action.denied."
                or Name == "action.schedule.gate-denied"
            | summarize
                Completed = countif(Name == "action.completed"),
                Failed    = countif(Name == "action.failed"),
                Timeout   = countif(Name == "action.poll-timeout"),
                Denied    = countif(Name startswith "action.denied." or Name == "action.schedule.gate-denied")
            """;
        var rows = await ExecuteAsync(query, window, ct, r => new OutcomeBreakdown(
            r.GetInt64("Completed") ?? 0,
            r.GetInt64("Failed") ?? 0,
            r.GetInt64("Timeout") ?? 0,
            r.GetInt64("Denied") ?? 0));
        return rows.Count > 0 ? rows[0] : new OutcomeBreakdown(0, 0, 0, 0);
    }

    private async Task<DistinctCounts> GetDistinctCountsAsync(TimeSpan window, CancellationToken ct)
    {
        var query = $$"""
            AppEvents
            | where TimeGenerated > ago({{ToKql(window)}})
            | where Name startswith "action."
            | summarize
                Devices = dcount(tostring(Properties.intuneDeviceId)),
                Users   = dcount(tostring(Properties.callerUpn))
            """;
        var rows = await ExecuteAsync(query, window, ct, r => new DistinctCounts(
            r.GetInt64("Devices") ?? 0,
            r.GetInt64("Users") ?? 0));
        return rows.Count > 0 ? rows[0] : new DistinctCounts(0, 0);
    }

    // ─── Code Service Bus (riuso snapshot Cruscotto) ─────────────────────────

    private async Task<IReadOnlyList<QueueBacklog>> GetQueueBacklogAsync(CancellationToken ct)
    {
        var snapshot = await _cruscotto.SnapshotAsync(ct);
        return snapshot.Queues
            .Select(q => new QueueBacklog(q.Name, q.Active, q.DeadLetter, q.Scheduled, q.Status))
            .ToArray();
    }

    // ─── Helper ──────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<T>> ExecuteAsync<T>(
        string query, TimeSpan window, CancellationToken ct, Func<LogsTableRow, T> map)
    {
        if (_logs is null || string.IsNullOrEmpty(_workspaceId))
            return Array.Empty<T>();

        var response = await _logs.QueryWorkspaceAsync(
            _workspaceId, query, new QueryTimeRange(window), cancellationToken: ct);
        var table = response.Value.Table;
        var result = new List<T>(table.Rows.Count);
        foreach (var row in table.Rows) result.Add(map(row));
        return result;
    }

    private async Task<T> SafeAsync<T>(Func<Task<T>> work, T fallback)
    {
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Monitoraggio: sezione metriche fallita, degrado a vuoto");
            return fallback;
        }
    }

    private static string ToKql(TimeSpan ts) => ts.TotalDays >= 1
        ? $"{(int)ts.TotalDays}d"
        : ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h"
            : $"{(int)ts.TotalMinutes}m";

    /// <summary>Sceglie un bin temporale ragionevole (~40-50 punti) per la finestra.</summary>
    private static string ChooseBin(TimeSpan window) => window.TotalHours switch
    {
        <= 1   => "1m",
        <= 6   => "5m",
        <= 24  => "30m",
        <= 72  => "1h",
        <= 168 => "3h",
        _      => "6h",
    };
}

// ─── DTO ─────────────────────────────────────────────────────────────────────

public sealed record MonitoringSnapshot(
    DateTimeOffset GeneratedAt,
    double WindowHours,
    bool Kqlavailable,
    MonitoringKpis Kpis,
    IReadOnlyList<ThroughputPoint> Throughput,
    IReadOnlyList<ResponseTimePoint> ResponseTimes,
    IReadOnlyList<ComponentLoad> ComponentLoad,
    IReadOnlyList<ResourceCounter> ResourceCounters,
    IReadOnlyList<OperationStat> SlowestOperations,
    IReadOnlyList<DependencyStat> SlowestDependencies,
    IReadOnlyList<ExceptionStat> TopExceptions,
    IReadOnlyList<DeviceStat> TopDevices,
    IReadOnlyList<UserStat> TopUsers,
    OutcomeBreakdown Outcome,
    IReadOnlyList<QueueBacklog> Queues);

public sealed record MonitoringKpis(
    long TotalRequests,
    long TotalProcessed,
    double? AvgResponseMs,
    double? P95ResponseMs,
    double ErrorRatePercent,
    long DistinctDevices,
    long DistinctUsers,
    long QueueBacklog,
    long DeadLetters);

public sealed record ThroughputPoint(
    DateTimeOffset Timestamp, long Accepted, long Completed, long Failed);

public sealed record ResponseTimePoint(
    DateTimeOffset Timestamp, double? AvgMs, double? P50Ms, double? P95Ms, long Count);

public sealed record ComponentLoad(
    string Role, long Requests, long Failures, double? AvgMs, double? P95Ms);

public sealed record ResourceCounter(
    string Role, string Counter, double? Avg, double? Max);

public sealed record OperationStat(
    string Operation, string Role, long Count, long Failures, double? AvgMs, double? P95Ms);

public sealed record DependencyStat(
    string Target, string Type, long Calls, long Failures, double? AvgMs, double? P95Ms);

public sealed record ExceptionStat(
    string Type, string Role, long Count, DateTimeOffset? LastSeen);

public sealed record DeviceStat(
    string Device, long Actions, long Completed, long Failed, DateTimeOffset? LastSeen);

public sealed record UserStat(
    string Caller, long Requests, long Devices, DateTimeOffset? LastSeen);

public sealed record OutcomeBreakdown(
    long Completed, long Failed, long Timeout, long Denied);

public sealed record DistinctCounts(long Devices, long Users);

public sealed record QueueBacklog(
    string Name, long Active, long DeadLetter, long Scheduled, NodeHealth Status);
