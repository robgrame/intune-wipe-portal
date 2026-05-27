namespace IntuneWipePortal.Models;

/// <summary>
/// Counters returned by the 24h KPI KQL query.
/// One row per event name; consumer aggregates into card values.
/// </summary>
public sealed record KpiRow(string EventName, long Count);

/// <summary>
/// Aggregated KPI values for the dashboard cards.
/// </summary>
public sealed class KpiSummary
{
    public long TotalRequests { get; set; }
    public long Accepted { get; set; }
    public long Denied { get; set; }
    public long WipesIssued { get; set; }
    public long PermanentFailures { get; set; }
    public long TransientErrors { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One row of the time-series chart: one bucket × one event name.
/// </summary>
public sealed record TimeSeriesPoint(DateTimeOffset Bucket, string EventName, long Count);

/// <summary>
/// One row of the deny-breakdown bar chart.
/// </summary>
public sealed record DenyBreakdownRow(string EventName, long Count);

/// <summary>
/// One row of the recent audit events table.
/// </summary>
public sealed record AuditEventRow(
    DateTimeOffset Timestamp,
    string EventName,
    string CorrelationId,
    string? DeviceName,
    string? IntuneDeviceId,
    string? EntraDeviceId,
    string? Reason,
    string? ExceptionType);
