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
        string? entraGroupId = null, string? entraGroupName = null,
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
            EntraGroupId   = string.IsNullOrWhiteSpace(entraGroupId) ? null : entraGroupId!.Trim(),
            EntraGroupName = string.IsNullOrWhiteSpace(entraGroupName) ? null : entraGroupName!.Trim(),
        };
        await _waves.AddEntityAsync(entity, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Schedule wave created — waveId={WaveId} name={Name} scheduledAtUtc={ScheduledAtUtc:O} status={Status} createdBy={CreatedBy} entraGroupId={EntraGroupId}",
            id, entity.Name, entity.ScheduledAtUtc, entity.Status, createdBy ?? "<anonymous>", entity.EntraGroupId ?? "<none>");
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
        string status, string? description,
        string? entraGroupId = null, string? entraGroupName = null,
        CancellationToken ct = default)
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
        w.EntraGroupId   = string.IsNullOrWhiteSpace(entraGroupId) ? null : entraGroupId!.Trim();
        w.EntraGroupName = string.IsNullOrWhiteSpace(entraGroupName) ? null : entraGroupName!.Trim();
        w.UpdatedAtUtc   = DateTimeOffset.UtcNow;
        await _waves.UpdateEntityAsync(w, w.ETag, TableUpdateMode.Replace, ct)
            .ConfigureAwait(false);
        _log.LogInformation(
            "Schedule wave updated — waveId={WaveId} name={Name} scheduledAtUtc={ScheduledAtUtc:O} status={Status} entraGroupId={EntraGroupId}",
            waveId, w.Name, w.ScheduledAtUtc, w.Status, w.EntraGroupId ?? "<none>");
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

    // ----- BULK members (the only practical path at 100s–1000s of devices) -

    private const int AzureTableBatchSize = 100; // Azure Table SDK hard limit

    /// <summary>
    /// Bulk-adds <paramref name="rows"/> to a wave using Azure Table batch
    /// transactions (max <see cref="AzureTableBatchSize"/> entities per
    /// transaction, all sharing the wave id as PartitionKey — which is
    /// already our schema). Internally deduplicates by Entra device id
    /// (last-write-wins on duplicate keys within the same input), uses
    /// <c>UpsertReplace</c> so re-adding an existing member just refreshes
    /// its metadata, and chunks the input so a 1000-device import becomes
    /// 10 round-trips instead of 1000.
    /// </summary>
    public async Task<BulkMemberAddResult> AddMembersBulkAsync(
        Guid waveId,
        IEnumerable<BulkMemberInput> rows,
        string? addedBy,
        CancellationToken ct = default)
    {
        if (waveId == Guid.Empty)
            throw new ArgumentException("Wave id is required.", nameof(waveId));

        await EnsureTablesAsync(ct).ConfigureAwait(false);

        var pk = waveId.ToString("D").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        // Dedup by RowKey inside the input (last wins).
        var deduped = new Dictionary<string, WipeScheduleWaveMemberEntity>(StringComparer.OrdinalIgnoreCase);
        var inputErrors = new List<string>();
        foreach (var r in rows ?? Array.Empty<BulkMemberInput>())
        {
            if (r.EntraDeviceId == Guid.Empty)
            {
                inputErrors.Add($"Skipped row '{r.DeviceName}': empty Entra device id.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(r.DeviceName))
            {
                inputErrors.Add($"Skipped row {r.EntraDeviceId}: empty device name.");
                continue;
            }
            var rk = r.EntraDeviceId.ToString("D").ToLowerInvariant();
            deduped[rk] = new WipeScheduleWaveMemberEntity
            {
                PartitionKey   = pk,
                RowKey         = rk,
                DeviceName     = r.DeviceName.Trim(),
                IntuneDeviceId = string.IsNullOrWhiteSpace(r.IntuneDeviceId) ? null : r.IntuneDeviceId!.Trim(),
                AddedBy        = addedBy,
                AddedAtUtc     = now,
            };
        }

        if (deduped.Count == 0)
            return new BulkMemberAddResult(Added: 0, Skipped: inputErrors.Count, Errors: inputErrors);

        int added = 0;
        var batchErrors = new List<string>(inputErrors);
        foreach (var chunk in Chunk(deduped.Values, AzureTableBatchSize))
        {
            var actions = chunk
                .Select(e => new TableTransactionAction(TableTransactionActionType.UpsertReplace, e))
                .ToList();
            try
            {
                await _members.SubmitTransactionAsync(actions, ct).ConfigureAwait(false);
                added += actions.Count;
            }
            catch (TableTransactionFailedException ex)
            {
                // Fallback: replay this chunk one-by-one so partial progress
                // is captured and we get per-row diagnostics instead of an
                // all-or-nothing failure.
                _log.LogWarning(ex,
                    "Bulk member transaction failed (waveId={WaveId}, chunkSize={Size}); falling back to per-entity upserts.",
                    waveId, actions.Count);
                foreach (var a in actions)
                {
                    try
                    {
                        await _members.UpsertEntityAsync((WipeScheduleWaveMemberEntity)a.Entity,
                            TableUpdateMode.Replace, ct).ConfigureAwait(false);
                        added++;
                    }
                    catch (Exception inner)
                    {
                        batchErrors.Add($"Row {a.Entity.RowKey}: {inner.Message}");
                    }
                }
            }
        }

        _log.LogInformation(
            "Schedule wave bulk-add — waveId={WaveId} added={Added} skipped={Skipped} errors={Errors} addedBy={AddedBy}",
            waveId, added, inputErrors.Count, batchErrors.Count - inputErrors.Count, addedBy ?? "<anonymous>");

        return new BulkMemberAddResult(Added: added,
            Skipped: deduped.Count - added + inputErrors.Count,
            Errors: batchErrors);
    }

    /// <summary>
    /// Bulk-removes the supplied Entra device ids from a wave. Unknown ids
    /// are silently ignored (idempotent). Chunks at the Azure Table batch
    /// limit; on transaction failure replays the chunk individually so
    /// partial progress is captured.
    /// </summary>
    public async Task<BulkMemberRemoveResult> RemoveMembersBulkAsync(
        Guid waveId,
        IEnumerable<Guid> entraDeviceIds,
        CancellationToken ct = default)
    {
        if (waveId == Guid.Empty)
            throw new ArgumentException("Wave id is required.", nameof(waveId));

        await EnsureTablesAsync(ct).ConfigureAwait(false);

        var pk = waveId.ToString("D").ToLowerInvariant();
        var rowKeys = (entraDeviceIds ?? Array.Empty<Guid>())
            .Where(g => g != Guid.Empty)
            .Select(g => g.ToString("D").ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (rowKeys.Count == 0)
            return new BulkMemberRemoveResult(Removed: 0, NotFound: 0, Errors: Array.Empty<string>());

        int removed = 0, notFound = 0;
        var errors = new List<string>();
        foreach (var chunk in Chunk(rowKeys, AzureTableBatchSize))
        {
            var actions = chunk.Select(rk =>
            {
                var stub = new WipeScheduleWaveMemberEntity { PartitionKey = pk, RowKey = rk, ETag = ETag.All };
                return new TableTransactionAction(TableTransactionActionType.Delete, stub, ETag.All);
            }).ToList();
            try
            {
                await _members.SubmitTransactionAsync(actions, ct).ConfigureAwait(false);
                removed += actions.Count;
            }
            catch (TableTransactionFailedException)
            {
                // One row was probably already gone — replay individually so
                // 404s become NotFound counters instead of breaking the chunk.
                foreach (var a in actions)
                {
                    try
                    {
                        await _members.DeleteEntityAsync(a.Entity.PartitionKey, a.Entity.RowKey,
                            cancellationToken: ct).ConfigureAwait(false);
                        removed++;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404)
                    {
                        notFound++;
                    }
                    catch (Exception inner)
                    {
                        errors.Add($"Row {a.Entity.RowKey}: {inner.Message}");
                    }
                }
            }
        }

        _log.LogInformation(
            "Schedule wave bulk-remove — waveId={WaveId} removed={Removed} notFound={NotFound} errors={Errors}",
            waveId, removed, notFound, errors.Count);
        return new BulkMemberRemoveResult(removed, notFound, errors);
    }

    private static IEnumerable<IReadOnlyList<T>> Chunk<T>(IEnumerable<T> source, int size)
    {
        var buf = new List<T>(size);
        foreach (var item in source)
        {
            buf.Add(item);
            if (buf.Count == size)
            {
                yield return buf;
                buf = new List<T>(size);
            }
        }
        if (buf.Count > 0) yield return buf;
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

/// <summary>
/// One device to add to a wave during a bulk import.
/// </summary>
public sealed record BulkMemberInput(string DeviceName, Guid EntraDeviceId, string? IntuneDeviceId);

/// <summary>
/// Outcome of <see cref="WipeScheduleService.AddMembersBulkAsync"/>.
/// <c>Added</c> is the number of rows successfully upserted (refresh of an
/// existing membership counts as an add). <c>Skipped</c> counts rows that
/// failed input validation (empty name / empty Entra id). <c>Errors</c>
/// carries human-readable diagnostics for both skipped and per-row backend
/// failures.
/// </summary>
public sealed record BulkMemberAddResult(int Added, int Skipped, IReadOnlyList<string> Errors);

/// <summary>
/// Outcome of <see cref="WipeScheduleService.RemoveMembersBulkAsync"/>.
/// </summary>
public sealed record BulkMemberRemoveResult(int Removed, int NotFound, IReadOnlyList<string> Errors);
