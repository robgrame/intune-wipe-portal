namespace IntuneWipePortal.Services;

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
