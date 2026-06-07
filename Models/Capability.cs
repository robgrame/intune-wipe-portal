namespace IntuneWipePortal.Models;

/// <summary>
/// Describes one capability exposed by the <c>intune-device-actions</c> API.
/// Capabilities are no longer hard-coded as an enum: the registry discovers the
/// active set from Log Analytics telemetry (distinct <c>actionType</c> on
/// <c>action.*</c> events) and merges it with a static well-known table that
/// supplies display names and capability-specific Graph/REST event names used
/// by KPI counters. A new capability appears on the dashboard as soon as it
/// emits its first <c>action.request.accepted</c> event — no portal redeploy
/// needed.
/// </summary>
/// <param name="ActionTypeValue">
/// Canonical key (matches the <c>actionType</c> property on <c>action.*</c>
/// events and the <c>IActionRunner.Type</c> downstream).
/// <c>null</c> for the synthetic <see cref="All"/> sentinel.
/// </param>
/// <param name="EventPrefix">
/// Prefix (with trailing dot) for capability-specific events
/// (e.g. <c>wipe.</c>, <c>bitlocker.</c>). Used by the recent-events query to
/// scope the <c>Name startswith</c> clause. <c>null</c> for <see cref="All"/>.
/// </param>
/// <param name="DisplayName">Human-readable label rendered on tabs/columns.</param>
/// <param name="IssuedEventName">
/// Capability event emitted when the downstream call (Graph or customer REST)
/// is accepted. <c>null</c> means "do not count" — the per-capability Issued
/// KPI will stay 0 for capabilities that haven't been registered with explicit
/// event names yet.
/// </param>
/// <param name="FailedPermanentEventName">Capability event for permanent failures.</param>
/// <param name="TransientEventName">Capability event for transient (retried) errors.</param>
public sealed record CapabilityDescriptor(
    string? ActionTypeValue,
    string? EventPrefix,
    string DisplayName,
    string? IssuedEventName,
    string? FailedPermanentEventName,
    string? TransientEventName)
{
    /// <summary>True for the synthetic "all capabilities" sentinel.</summary>
    public bool IsAll => ActionTypeValue is null;

    /// <summary>Synthetic sentinel that disables capability filtering.</summary>
    public static readonly CapabilityDescriptor All = new(
        ActionTypeValue: null,
        EventPrefix: null,
        DisplayName: "All",
        IssuedEventName: null,
        FailedPermanentEventName: null,
        TransientEventName: null);
}

/// <summary>
/// Well-known capability registry. Capabilities listed here get curated display
/// names + explicit event-name mappings. Discovered capabilities not in this
/// table fall back to <see cref="BuildFallback"/>.
///
/// Adding an entry here is optional — a new capability will already appear on
/// the dashboard via auto-discovery — but doing so gives it a nicer label and
/// non-zero Issued/Failed KPIs (those counters key off the explicit event
/// names because event taxonomy differs per capability: Graph capabilities use
/// <c>{prefix}graph.{verb}.issued</c>, REST-based ones use <c>{prefix}rest.{verb}.issued</c>,
/// etc.).
/// </summary>
public static class KnownCapabilities
{
    /// <summary>
    /// Static table indexed by actionType. Curated event names follow the
    /// taxonomy emitted by each capability's <c>*AuditEvents</c> class in the
    /// API repo.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, CapabilityDescriptor> _byActionType =
        new Dictionary<string, CapabilityDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["wipe"] = new(
                ActionTypeValue: "wipe",
                EventPrefix: "wipe.",
                DisplayName: "Wipe",
                IssuedEventName: "wipe.graph.issued",
                FailedPermanentEventName: "wipe.graph.failed-permanent",
                TransientEventName: "wipe.graph.transient-error"),
            ["autopilot-register"] = new(
                ActionTypeValue: "autopilot-register",
                EventPrefix: "autopilot.",
                DisplayName: "Autopilot register",
                IssuedEventName: "autopilot.graph.import.issued",
                FailedPermanentEventName: "autopilot.graph.import.failed-permanent",
                TransientEventName: "autopilot.graph.import.transient-error"),
            ["bitlocker-rotate"] = new(
                ActionTypeValue: "bitlocker-rotate",
                EventPrefix: "bitlocker.",
                DisplayName: "BitLocker key rotate",
                IssuedEventName: "bitlocker.graph.rotate.issued",
                FailedPermanentEventName: "bitlocker.graph.rotate.failed-permanent",
                TransientEventName: "bitlocker.graph.rotate.transient-error"),
            ["device-rename"] = new(
                ActionTypeValue: "device-rename",
                EventPrefix: "rename.",
                DisplayName: "Device rename",
                IssuedEventName: "rename.rest.issued",
                FailedPermanentEventName: "rename.rest.failed-permanent",
                TransientEventName: "rename.rest.transient-error"),
        };

    /// <summary>
    /// Returns the curated descriptor for <paramref name="actionType"/>, or
    /// <see cref="BuildFallback"/> if unknown. Never returns <c>null</c>.
    /// </summary>
    public static CapabilityDescriptor Resolve(string actionType)
        => _byActionType.TryGetValue(actionType, out var d)
            ? d
            : BuildFallback(actionType);

    /// <summary>
    /// Builds a sensible descriptor for an actionType not present in the
    /// curated table. Convention: the event prefix is the first dash-segment
    /// of the actionType (e.g. <c>device-rename</c> → <c>device.</c>,
    /// <c>foo</c> → <c>foo.</c>) and the display name title-cases the
    /// actionType. Issued / Failed event names are left <c>null</c> because we
    /// cannot guess the per-capability sub-taxonomy (graph vs rest, verb
    /// shape, …); the per-capability counters will stay 0 until an entry is
    /// added to <see cref="_byActionType"/>.
    /// </summary>
    public static CapabilityDescriptor BuildFallback(string actionType)
    {
        var dash = actionType.IndexOf('-');
        var prefixCore = dash > 0 ? actionType[..dash] : actionType;
        return new CapabilityDescriptor(
            ActionTypeValue: actionType,
            EventPrefix: prefixCore + ".",
            DisplayName: TitleCase(actionType),
            IssuedEventName: null,
            FailedPermanentEventName: null,
            TransientEventName: null);
    }

    private static string TitleCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var parts = s.Split('-', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        return string.Join(' ', parts);
    }
}
