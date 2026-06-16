using System.Collections.Concurrent;
using System.Text.Json;

namespace IntuneWipePortal.Services;

/// <summary>
/// In-memory collector that accumulates per-Function-App metrics from
/// Event Grid audit events received via the webhook. Maintains a sliding
/// 30-minute window of counters (requests, errors) and last-seen timestamps.
///
/// Thread-safe — called from the webhook endpoint on every notification.
/// </summary>
public sealed class EventGridMetricsCollector
{
    private readonly ConcurrentDictionary<string, AppMetricsBucket> _buckets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Well-known roles pre-seeded so the UI never shows "no data" for known apps.
    /// </summary>
    private static readonly string[] KnownRoles = { "web", "proc", "wipe", "autopilot", "bitlocker", "rename" };

    public EventGridMetricsCollector()
    {
        foreach (var role in KnownRoles)
            _buckets.GetOrAdd(role, _ => new AppMetricsBucket());
    }

    /// <summary>
    /// Ingest a raw Event Grid event payload (array of events).
    /// Each event's <c>data</c> is expected to carry an <c>AuditStreamEnvelope</c>
    /// with <c>role</c>, <c>eventName</c>, <c>logLevel</c>, and <c>properties</c>.
    /// </summary>
    public void Ingest(JsonElement eventsPayload)
    {
        if (eventsPayload.ValueKind != JsonValueKind.Array) return;

        foreach (var evt in eventsPayload.EnumerateArray())
        {
            if (!evt.TryGetProperty("data", out var data)) continue;

            var role = data.TryGetProperty("role", out var r) ? r.GetString() : null;
            if (string.IsNullOrWhiteSpace(role)) continue;

            var eventName = data.TryGetProperty("eventName", out var en) ? en.GetString() : null;
            var logLevel = data.TryGetProperty("logLevel", out var ll) ? ll.GetString() : null;

            var isError = string.Equals(logLevel, "Error", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(logLevel, "Critical", StringComparison.OrdinalIgnoreCase);

            var bucket = _buckets.GetOrAdd(role, _ => new AppMetricsBucket());
            bucket.Record(eventName, isError);
        }
    }

    /// <summary>
    /// Returns a snapshot of per-app metrics for the last 30 minutes.
    /// </summary>
    public IReadOnlyDictionary<string, FunctionAppStatus> GetSnapshot()
    {
        var result = new Dictionary<string, FunctionAppStatus>(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);

        foreach (var (role, bucket) in _buckets)
        {
            var (total, errors, lastAt, lastEvent, lastError) = bucket.Summarize(cutoff);

            // If we've seen events for this role before but the 30m window is empty,
            // that means the app is simply idle (no traffic) — not broken.
            var health = total == 0 ? NodeHealth.Green  // idle = healthy (we know the role exists)
                       : errors > 0 && (double)errors / total > 0.5 ? NodeHealth.Red
                       : errors > 0 ? NodeHealth.Yellow
                       : NodeHealth.Green;

            result[role] = new FunctionAppStatus(
                AppName: role,
                Health: health,
                Requests30m: total,
                Failures30m: errors,
                AvgDurationMs: null,
                LastRequestAt: lastAt,
                LastError: lastError);
        }

        return result;
    }

    /// <summary>
    /// Per-role sliding window of event observations.
    /// </summary>
    private sealed class AppMetricsBucket
    {
        private readonly ConcurrentQueue<EventTick> _ticks = new();
        private string? _lastEvent;
        private string? _lastError;
        private DateTimeOffset? _lastAt;

        public void Record(string? eventName, bool isError)
        {
            var now = DateTimeOffset.UtcNow;
            _ticks.Enqueue(new EventTick(now, isError));
            _lastEvent = eventName;
            _lastAt = now;
            if (isError && eventName is not null)
                _lastError = eventName;

            // Prune old entries (older than 35 min to keep a small buffer)
            var pruneThreshold = now.AddMinutes(-35);
            while (_ticks.TryPeek(out var oldest) && oldest.Timestamp < pruneThreshold)
                _ticks.TryDequeue(out _);
        }

        public (long total, long errors, DateTimeOffset? lastAt, string? lastEvent, string? lastError) Summarize(DateTimeOffset cutoff)
        {
            long total = 0, errors = 0;
            foreach (var tick in _ticks)
            {
                if (tick.Timestamp < cutoff) continue;
                total++;
                if (tick.IsError) errors++;
            }
            return (total, errors, _lastAt, _lastEvent, _lastError);
        }

        private readonly record struct EventTick(DateTimeOffset Timestamp, bool IsError);
    }
}
