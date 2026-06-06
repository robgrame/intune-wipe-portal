namespace IntuneWipePortal.Models;

/// <summary>
/// One of the capabilities exposed by the <c>intune-device-actions</c> API.
/// Drives capability filtering on the dashboard and maps to the event-name
/// prefixes emitted by each capability's <c>*AuditEvents</c> class.
/// </summary>
public enum ActionCapability
{
    /// <summary>All capabilities (no filter).</summary>
    All = 0,
    Wipe,
    Autopilot,
    BitLocker,
}

public static class ActionCapabilityExtensions
{
    /// <summary>
    /// Returns the event-name prefix (with trailing dot) used by the
    /// capability-specific <c>*AuditEvents</c>. <c>All</c> returns <c>null</c>
    /// because no single prefix applies.
    /// </summary>
    public static string? EventPrefix(this ActionCapability c) => c switch
    {
        ActionCapability.Wipe      => "wipe.",
        ActionCapability.Autopilot => "autopilot.",
        ActionCapability.BitLocker => "bitlocker.",
        _ => null,
    };

    /// <summary>
    /// Value used in the <c>action.*</c> events' <c>actionType</c> property.
    /// Matches the canonical <c>ActionType</c> string carried on the queue
    /// messages (see <c>ActionRequestMessage</c> in the api repo).
    /// </summary>
    public static string? ActionTypeValue(this ActionCapability c) => c switch
    {
        ActionCapability.Wipe      => "wipe",
        ActionCapability.Autopilot => "autopilot-register",
        ActionCapability.BitLocker => "bitlocker-rotate",
        _ => null,
    };

    public static string DisplayName(this ActionCapability c) => c switch
    {
        ActionCapability.All       => "All",
        ActionCapability.Wipe      => "Wipe",
        ActionCapability.Autopilot => "Autopilot register",
        ActionCapability.BitLocker => "BitLocker key rotate",
        _ => c.ToString(),
    };

    /// <summary>
    /// Per-capability event used to count successfully <i>issued</i> actions
    /// (Graph call accepted by Intune). One per capability.
    /// </summary>
    public static string? IssuedEventName(this ActionCapability c) => c switch
    {
        ActionCapability.Wipe      => "wipe.graph.issued",
        ActionCapability.Autopilot => "autopilot.graph.import.issued",
        ActionCapability.BitLocker => "bitlocker.graph.rotate.issued",
        _ => null,
    };

    /// <summary>
    /// Per-capability event for permanent Graph failures (4xx other than 429,
    /// validation errors, etc.).
    /// </summary>
    public static string? FailedPermanentEventName(this ActionCapability c) => c switch
    {
        ActionCapability.Wipe      => "wipe.graph.failed-permanent",
        ActionCapability.Autopilot => "autopilot.graph.import.failed-permanent",
        ActionCapability.BitLocker => "bitlocker.graph.rotate.failed-permanent",
        _ => null,
    };

    /// <summary>Per-capability event for transient Graph errors (retried).</summary>
    public static string? TransientEventName(this ActionCapability c) => c switch
    {
        ActionCapability.Wipe      => "wipe.graph.transient-error",
        ActionCapability.Autopilot => "autopilot.graph.import.transient-error",
        ActionCapability.BitLocker => "bitlocker.graph.rotate.transient-error",
        _ => null,
    };

    public static IReadOnlyList<ActionCapability> Concrete()
        => new[] { ActionCapability.Wipe, ActionCapability.Autopilot, ActionCapability.BitLocker };
}
