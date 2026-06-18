using IntuneWipePortal.Services;

namespace IntuneWipePortal.Models;

/// <summary>
/// Counters returned by the 24h KPI KQL query.
/// One row per event name; consumer aggregates into card values.
/// </summary>
public sealed record KpiRow(string EventName, long Count);

/// <summary>
/// Per-capability counters within a <see cref="KpiSummary"/>.
/// </summary>
public sealed class CapabilityKpi
{
    /// <summary>
    /// Descriptor for the capability this bucket aggregates. Never <c>null</c>
    /// and never the synthetic <see cref="CapabilityDescriptor.All"/> sentinel
    /// — the per-capability breakdown only emits concrete capabilities.
    /// </summary>
    public required CapabilityDescriptor Capability { get; init; }
    public long Issued { get; set; }
    public long PermanentFailures { get; set; }
    public long TransientErrors { get; set; }
}

/// <summary>
/// Aggregated KPI values for the dashboard cards.
/// Action-level counters (Accepted/Denied/Completed/Failed/PollTimeout) come
/// from the capability-agnostic <c>action.*</c> taxonomy and are filterable by
/// <see cref="CapabilityDescriptor"/>. Per-capability Graph/REST call counters
/// live in <see cref="PerCapability"/>.
/// </summary>
public sealed class KpiSummary
{
    public long TotalRequests { get; set; }
    public long Accepted { get; set; }
    public long Denied { get; set; }

    // Action lifecycle (capability-agnostic — action.completed / action.failed
    // / action.poll-timeout are emitted by ActionStatusPollerFunction
    // regardless of the capability that issued the action).
    public long ActionCompleted { get; set; }
    public long ActionFailed { get; set; }
    public long ActionPollTimeout { get; set; }

    public List<CapabilityKpi> PerCapability { get; set; } = new();

    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Sum of <c>Issued</c> across all per-capability buckets.</summary>
    public long TotalIssued => PerCapability.Sum(p => p.Issued);
    public long TotalPermanentFailures => PerCapability.Sum(p => p.PermanentFailures);
    public long TotalTransientErrors   => PerCapability.Sum(p => p.TransientErrors);
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
    string? ActionType,
    string? DeviceName,
    string? IntuneDeviceId,
    string? EntraDeviceId,
    string? Reason,
    string? ExceptionType);

/// <summary>
/// One row of the "in-flight actions" widget: pairs the original capability
/// <c>*.graph.*.issued</c> event with the latest action-status observation
/// for each correlationId still non-terminal.
/// </summary>
public sealed record InFlightActionRow(
    string CorrelationId,
    string? ActionType,
    string? DeviceName,
    DateTimeOffset IssuedAt,
    DateTimeOffset LastUpdate,
    string CurrentState,
    int MinutesSinceIssued);

/// <summary>
/// Aggregate health summary for the banner on the dashboard Home page.
/// </summary>
public sealed record HealthSummary(
    NodeHealth OverallStatus,
    string StatusLabel,
    IReadOnlyList<HealthIssue> Issues);

public sealed record HealthIssue(
    string Severity,   // "critical", "warning", "info"
    string Icon,
    string Message);

/// <summary>
/// Average latency from request acceptance to terminal state.
/// </summary>
public sealed record LatencyStats(
    double? AvgMinutes,
    double? P95Minutes,
    long SampleCount);
