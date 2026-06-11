using Azure;
using Azure.Core;
using Azure.Data.Tables;
using IntuneWipePortal.Models;

namespace IntuneWipePortal.Services;

/// <summary>
/// Portal-side write/read facade over the two Azure Tables that back the
/// wipe schedule (<c>wipeschedulewaves</c> and <c>wipeschedulemembers</c>),
/// hosted on the API project's Web role storage account.
/// <para>
/// This is intentionally a direct Table Storage client (no HTTP round-trip
/// via the Function App): the portal already authenticates with a managed
/// identity, and Azure Tables natively supports AAD authorization via
/// <c>DefaultAzureCredential</c> — same pattern used by
/// <c>AuditQueryService</c> for Log Analytics. The Storage Table Data
/// Contributor role assignment on the UAMI is a one-time infra change
/// documented in the README.
/// </para>
/// <para>
/// Schema contract: column names below MUST match the API-side
/// <c>WipeScheduleWave</c> / <c>WipeScheduleWaveMember</c> entities in
/// <c>IntuneDeviceActions.Capabilities.Wipe.Schedule</c>. Any rename here
/// must be made in lockstep with a matching rename in the wipe capability.
/// </para>
/// </summary>
public sealed class WipeScheduleService
{
    private readonly TableClient _waves;
    private readonly TableClient _members;
    private readonly ILogger<WipeScheduleService> _log;

    public WipeScheduleService(IConfiguration cfg, ILogger<WipeScheduleService> log)
    {
        _log = log;

        var account = cfg["WipeSchedule:StorageAccountName"]
            ?? throw new InvalidOperationException(
                "WipeSchedule:StorageAccountName is required (name of the API Web role's storage account hosting wipeschedule tables).");
        var wavesName = cfg["WipeSchedule:WavesTableName"] ?? "wipeschedulewaves";
        var membersName = cfg["WipeSchedule:MembersTableName"] ?? "wipeschedulemembers";

        var baseUri = new Uri($"https://{account}.table.core.windows.net");
        var cred = BuildCredential(cfg);
        _waves   = new TableClient(baseUri, wavesName, cred);
        _members = new TableClient(baseUri, membersName, cred);
    }

    private static TokenCredential BuildCredential(IConfiguration cfg)
    {
        var clientId = cfg["AZURE_CLIENT_ID"];
        var opts = new Azure.Identity.DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(clientId))
            opts.ManagedIdentityClientId = clientId;
        return new Azure.Identity.DefaultAzureCredential(opts);
    }

    // ----- list / get ------------------------------------------------------

    /// <summary>
    /// Returns every wave (with its member count) ordered by scheduled
    /// time ascending. Failures (missing tables, missing permission) are
    /// translated into a user-visible message via
    /// <see cref="WipeScheduleListResult"/>.
    /// </summary>
    public async Task<WipeScheduleListResult> ListWavesAsync(CancellationToken ct = default)
    {
        try
        {
            var waves = new List<WipeScheduleWaveEntity>();
            await foreach (var w in _waves.QueryAsync<WipeScheduleWaveEntity>(
                $"PartitionKey eq '{WipeScheduleWaveEntity.PartitionConstant}'",
                cancellationToken: ct))
            {
                waves.Add(w);
            }
            waves.Sort((a, b) => a.ScheduledAtUtc.CompareTo(b.ScheduledAtUtc));

            var views = new List<WipeScheduleWaveView>(waves.Count);
            foreach (var w in waves)
            {
                var count = await CountMembersAsync(w.WaveId, ct).ConfigureAwait(false);
                views.Add(WipeScheduleWaveView.FromEntity(w, count));
            }
            return new WipeScheduleListResult(views, null);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Table doesn't exist yet (no wave was ever created on this
            // deployment). Show empty list, not an error.
            return new WipeScheduleListResult(Array.Empty<WipeScheduleWaveView>(), null);
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            _log.LogWarning(ex,
                "Wipe schedule list failed: portal UAMI is missing 'Storage Table Data Contributor' on the API Web role storage account.");
            return new WipeScheduleListResult(Array.Empty<WipeScheduleWaveView>(),
                "Permission denied. The portal managed identity needs the 'Storage Table Data Contributor' role on the wipe storage account.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Wipe schedule list failed unexpectedly.");
            return new WipeScheduleListResult(Array.Empty<WipeScheduleWaveView>(),
                $"Unexpected error: {ex.Message}");
        }
    }

    public async Task<WipeScheduleWaveView?> GetWaveWithMembersAsync(Guid waveId,
        CancellationToken ct = default)
    {
        WipeScheduleWaveEntity? wave = null;
        try
        {
            var resp = await _waves.GetEntityAsync<WipeScheduleWaveEntity>(
                WipeScheduleWaveEntity.PartitionConstant,
                waveId.ToString("D").ToLowerInvariant(), cancellationToken: ct)
                .ConfigureAwait(false);
            wave = resp.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var members = new List<WipeScheduleMemberView>();
        try
        {
            await foreach (var m in _members.QueryAsync<WipeScheduleWaveMemberEntity>(
                $"PartitionKey eq '{waveId.ToString("D").ToLowerInvariant()}'",
                cancellationToken: ct))
            {
                members.Add(WipeScheduleMemberView.FromEntity(m));
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { /* no members table yet */ }

        members.Sort((a, b) => string.Compare(a.DeviceName, b.DeviceName,
            StringComparison.OrdinalIgnoreCase));

        return WipeScheduleWaveView.FromEntity(wave!, members.Count, members);
    }

    private async Task<int> CountMembersAsync(Guid waveId, CancellationToken ct)
    {
        var count = 0;
        try
        {
            await foreach (var _ in _members.QueryAsync<WipeScheduleWaveMemberEntity>(
                $"PartitionKey eq '{waveId.ToString("D").ToLowerInvariant()}'",
                select: new[] { "PartitionKey" },
                cancellationToken: ct))
            {
                count++;
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { /* none */ }
        return count;
    }

    // ----- create / update / delete waves ---------------------------------

    /// <summary>
    /// Creates a new wave. Returns the newly assigned wave id. Auto-creates
    /// the tables if missing.
    /// </summary>
    public async Task<Guid> CreateWaveAsync(string name, DateTimeOffset scheduledAtUtc,
        string status, string? description, string? createdBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Wave name is required.", nameof(name));
        if (!WipeWaveStatus.OperatorSelectable.Contains(status, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Status '{status}' is not operator-selectable. Allowed: {string.Join(", ", WipeWaveStatus.OperatorSelectable)}.",
                nameof(status));

        await EnsureTablesAsync(ct).ConfigureAwait(false);

        var id = Guid.NewGuid();
        var entity = new WipeScheduleWaveEntity
        {
            PartitionKey   = WipeScheduleWaveEntity.PartitionConstant,
            RowKey         = id.ToString("D").ToLowerInvariant(),
            ActionType     = WipeScheduleWaveEntity.ActionTypeConstant,
            Name           = name.Trim(),
            Description    = string.IsNullOrWhiteSpace(description) ? null : description!.Trim(),
            ScheduledAtUtc = scheduledAtUtc.ToUniversalTime(),
            Status         = status.ToLowerInvariant(),
            CreatedBy      = createdBy,
            CreatedAtUtc   = DateTimeOffset.UtcNow,
            UpdatedAtUtc   = DateTimeOffset.UtcNow,
        };
        await _waves.AddEntityAsync(entity, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Schedule wave created — waveId={WaveId} name={Name} scheduledAtUtc={ScheduledAtUtc:O} status={Status} createdBy={CreatedBy}",
            id, entity.Name, entity.ScheduledAtUtc, entity.Status, createdBy ?? "<anonymous>");
        return id;
    }

    /// <summary>
    /// Replaces the editable fields of an existing wave. Status transitions
    /// are validated: operators may move freely between
    /// <see cref="WipeWaveStatus.OperatorSelectable"/> states
    /// (Draft / Scheduled / Canceled), but cannot flip a wave to
    /// <c>Executing</c> or <c>Completed</c> from the UI — those statuses
    /// are owned by the runner / scheduler, and setting them manually
    /// would silently disable the temporal gate (because
    /// <see cref="WipeWaveStatus.ClientVisible"/> excludes Completed).
    /// </summary>
    public async Task UpdateWaveAsync(Guid waveId, string name, DateTimeOffset scheduledAtUtc,
        string status, string? description, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Wave name is required.", nameof(name));
        if (!WipeWaveStatus.OperatorSelectable.Contains(status, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Status '{status}' is not operator-selectable. Allowed: {string.Join(", ", WipeWaveStatus.OperatorSelectable)}.",
                nameof(status));

        var pk = WipeScheduleWaveEntity.PartitionConstant;
        var rk = waveId.ToString("D").ToLowerInvariant();
        var resp = await _waves.GetEntityAsync<WipeScheduleWaveEntity>(pk, rk, cancellationToken: ct)
            .ConfigureAwait(false);
        var w = resp.Value;

        // Block downgrading a runner/scheduler-owned status back to operator
        // state — e.g. flipping an executing wave back to draft would be
        // surprising and is almost certainly an operator mistake.
        if (string.Equals(w.Status, WipeWaveStatus.Executing, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, WipeWaveStatus.Canceled, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "An executing wave can only be transitioned to 'canceled' by an operator.");
        }
        if (string.Equals(w.Status, WipeWaveStatus.Completed, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A completed wave cannot be modified.");
        }

        w.Name           = name.Trim();
        w.Description    = string.IsNullOrWhiteSpace(description) ? null : description!.Trim();
        w.ScheduledAtUtc = scheduledAtUtc.ToUniversalTime();
        w.Status         = status.ToLowerInvariant();
        w.UpdatedAtUtc   = DateTimeOffset.UtcNow;
        await _waves.UpdateEntityAsync(w, w.ETag, TableUpdateMode.Replace, ct)
            .ConfigureAwait(false);
        _log.LogInformation(
            "Schedule wave updated — waveId={WaveId} name={Name} scheduledAtUtc={ScheduledAtUtc:O} status={Status}",
            waveId, w.Name, w.ScheduledAtUtc, w.Status);
    }

    public async Task DeleteWaveAsync(Guid waveId, CancellationToken ct = default)
    {
        var pk = waveId.ToString("D").ToLowerInvariant();
        // Members first; orphan members would be filtered out by the API
        // anyway but cleaning up is cheap.
        int memberCount = 0;
        try
        {
            await foreach (var m in _members.QueryAsync<WipeScheduleWaveMemberEntity>(
                $"PartitionKey eq '{pk}'", cancellationToken: ct))
            {
                try { await _members.DeleteEntityAsync(m.PartitionKey, m.RowKey, cancellationToken: ct); memberCount++; }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }

        try
        {
            await _waves.DeleteEntityAsync(WipeScheduleWaveEntity.PartitionConstant, pk,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }

        _log.LogInformation(
            "Schedule wave deleted — waveId={WaveId} membersDeleted={MemberCount}",
            waveId, memberCount);
    }

    // ----- members ---------------------------------------------------------

    public async Task AddMemberAsync(Guid waveId, Guid entraDeviceId, string deviceName,
        string? intuneDeviceId, string? addedBy, CancellationToken ct = default)
    {
        if (entraDeviceId == Guid.Empty)
            throw new ArgumentException("Entra device id is required.", nameof(entraDeviceId));
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("Device name is required.", nameof(deviceName));

        await EnsureTablesAsync(ct).ConfigureAwait(false);

        var entity = new WipeScheduleWaveMemberEntity
        {
            PartitionKey   = waveId.ToString("D").ToLowerInvariant(),
            RowKey         = entraDeviceId.ToString("D").ToLowerInvariant(),
            DeviceName     = deviceName.Trim(),
            IntuneDeviceId = string.IsNullOrWhiteSpace(intuneDeviceId) ? null : intuneDeviceId!.Trim(),
            AddedBy        = addedBy,
            AddedAtUtc     = DateTimeOffset.UtcNow,
        };
        // Upsert: re-adding an existing device just refreshes the metadata.
        await _members.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Schedule wave member added — waveId={WaveId} entraDeviceId={EntraDeviceId} deviceName={DeviceName} addedBy={AddedBy}",
            waveId, entraDeviceId, entity.DeviceName, addedBy ?? "<anonymous>");
    }

    public async Task RemoveMemberAsync(Guid waveId, Guid entraDeviceId, CancellationToken ct = default)
    {
        try
        {
            await _members.DeleteEntityAsync(
                waveId.ToString("D").ToLowerInvariant(),
                entraDeviceId.ToString("D").ToLowerInvariant(),
                cancellationToken: ct).ConfigureAwait(false);
            _log.LogInformation(
                "Schedule wave member removed — waveId={WaveId} entraDeviceId={EntraDeviceId}",
                waveId, entraDeviceId);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { /* idempotent */ }
    }

    // ----- bootstrap -------------------------------------------------------

    private async Task EnsureTablesAsync(CancellationToken ct)
    {
        await _waves.CreateIfNotExistsAsync(ct).ConfigureAwait(false);
        await _members.CreateIfNotExistsAsync(ct).ConfigureAwait(false);
    }
}

public sealed record WipeScheduleListResult(
    IReadOnlyList<WipeScheduleWaveView> Waves,
    string? Error);
