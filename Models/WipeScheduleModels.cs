using Azure;
using Azure.Data.Tables;

namespace IntuneWipePortal.Models;

/// <summary>
/// Operational status of a wipe schedule wave. Mirrors
/// <c>IntuneDeviceActions.Schedule.WaveStatus</c> in the API repo. Strings
/// MUST stay in sync — they are the shared contract between the portal
/// (write-side) and the wipe capability (read-side).
/// </summary>
public static class WipeWaveStatus
{
    public const string Draft     = "draft";
    public const string Scheduled = "scheduled";
    public const string Executing = "executing";
    public const string Completed = "completed";
    public const string Canceled  = "canceled";

    public static readonly string[] All =
        { Draft, Scheduled, Executing, Completed, Canceled };

    /// <summary>
    /// Statuses the UI lets a human OPERATOR choose. <see cref="Executing"/>
    /// is owned by the future scheduler (operators must not set it manually
    /// or they bypass the runner's temporal gate); <see cref="Completed"/>
    /// is owned by the runner after a successful wipe. <see cref="Canceled"/>
    /// stays operator-settable so a wave can be aborted, but the service
    /// rejects flipping it to Completed/Executing.
    /// </summary>
    public static readonly string[] OperatorSelectable =
        { Draft, Scheduled, Canceled };

    public static readonly HashSet<string> Mutable =
        new(StringComparer.OrdinalIgnoreCase) { Draft, Scheduled };

    public static readonly HashSet<string> ClientVisible =
        new(StringComparer.OrdinalIgnoreCase) { Scheduled, Executing };
}

/// <summary>
/// Storage entity for a wipe wave. PartitionKey is constant
/// <c>WipeScheduleWave</c>; RowKey is the wave id (GUID, lowercased dashed
/// form). Property names match the API-side
/// <c>IntuneDeviceActions.Capabilities.Wipe.Schedule.WipeScheduleWave</c>.
/// </summary>
public sealed class WipeScheduleWaveEntity : ITableEntity
{
    public const string PartitionConstant = "WipeScheduleWave";

    public string PartitionKey { get; set; } = PartitionConstant;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string ActionType { get; set; } = "wipe";
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset ScheduledAtUtc { get; set; }
    public string Status { get; set; } = WipeWaveStatus.Draft;
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>
    /// Optional Entra group object id. When set, membership is determined by
    /// group membership instead of individual device rows.
    /// </summary>
    public string? EntraGroupId { get; set; }

    /// <summary>Display name of the Entra group (denormalized for UI).</summary>
    public string? EntraGroupName { get; set; }

    public Guid WaveId =>
        Guid.TryParse(RowKey, out var g) ? g : Guid.Empty;
}

/// <summary>
/// Storage entity for a device-in-wave membership. PartitionKey = wave id,
/// RowKey = entra device id (both GUIDs, lowercased dashed form). Mirrors
/// <c>IntuneDeviceActions.Capabilities.Wipe.Schedule.WipeScheduleWaveMember</c>.
/// </summary>
public sealed class WipeScheduleWaveMemberEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string DeviceName { get; set; } = string.Empty;
    public string? IntuneDeviceId { get; set; }
    public string? AddedBy { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }

    public Guid WaveId =>
        Guid.TryParse(PartitionKey, out var g) ? g : Guid.Empty;

    public Guid EntraDeviceId =>
        Guid.TryParse(RowKey, out var g) ? g : Guid.Empty;
}

/// <summary>
/// View model: a wave + the count of its members + (optionally) the
/// members themselves. Used by the Schedule pages to render lists and
/// detail panels without leaking the storage entity onto the UI.
/// </summary>
public sealed class WipeScheduleWaveView
{
    public Guid WaveId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string ActionType { get; init; } = "wipe";
    public DateTimeOffset ScheduledAtUtc { get; init; }
    public string Status { get; init; } = WipeWaveStatus.Draft;
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public int MemberCount { get; init; }
    public IReadOnlyList<WipeScheduleMemberView> Members { get; init; }
        = Array.Empty<WipeScheduleMemberView>();

    /// <summary>Entra group id (when membership is group-based).</summary>
    public string? EntraGroupId { get; init; }
    /// <summary>Denormalized group name for display.</summary>
    public string? EntraGroupName { get; init; }
    /// <summary>True when wave uses group-based membership.</summary>
    public bool IsGroupBased => !string.IsNullOrEmpty(EntraGroupId);

    public bool IsMutable => WipeWaveStatus.Mutable.Contains(Status);
    public bool HasFired  => ScheduledAtUtc <= DateTimeOffset.UtcNow;

    public static WipeScheduleWaveView FromEntity(WipeScheduleWaveEntity e,
        int memberCount = 0,
        IReadOnlyList<WipeScheduleMemberView>? members = null) => new()
        {
            WaveId         = e.WaveId,
            Name           = e.Name,
            Description    = e.Description,
            ActionType     = e.ActionType,
            ScheduledAtUtc = e.ScheduledAtUtc,
            Status         = e.Status,
            CreatedBy      = e.CreatedBy,
            CreatedAtUtc   = e.CreatedAtUtc,
            UpdatedAtUtc   = e.UpdatedAtUtc,
            MemberCount    = memberCount,
            Members        = members ?? Array.Empty<WipeScheduleMemberView>(),
            EntraGroupId   = e.EntraGroupId,
            EntraGroupName = e.EntraGroupName,
        };
}

public sealed class WipeScheduleMemberView
{
    public Guid EntraDeviceId { get; init; }
    public string DeviceName { get; init; } = string.Empty;
    public string? IntuneDeviceId { get; init; }
    public string? AddedBy { get; init; }
    public DateTimeOffset AddedAtUtc { get; init; }

    public static WipeScheduleMemberView FromEntity(WipeScheduleWaveMemberEntity m) => new()
    {
        EntraDeviceId  = m.EntraDeviceId,
        DeviceName     = m.DeviceName,
        IntuneDeviceId = m.IntuneDeviceId,
        AddedBy        = m.AddedBy,
        AddedAtUtc     = m.AddedAtUtc,
    };
}
