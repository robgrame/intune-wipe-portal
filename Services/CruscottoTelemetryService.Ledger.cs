using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace IntuneWipePortal.Services;

// Action-ledger inspection (stuck detection) and reset/archive.
public sealed partial class CruscottoTelemetryService
{
    private async Task<LedgerStatus> LoadLedgerAsync(CancellationToken ct)
    {
        if (_ledger is null)
        {
            return new LedgerStatus(0, 0, null, null, Array.Empty<StuckLedgerEntry>(), _graceHours, NodeHealth.Unknown, "ledger client not configured");
        }
        var stuckBefore = DateTimeOffset.UtcNow.AddHours(-_graceHours);

        var total = 0;
        var stuck = 0;
        DateTimeOffset? oldestStuckIssuedAt = null;
        string? oldestStuckId = null;
        var stuckList = new List<StuckLedgerEntry>();

        try
        {
            await foreach (BlobItem item in _ledger.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: null, ct))
            {
                if (item.Name.StartsWith("_archive/", StringComparison.OrdinalIgnoreCase)) continue;
                total++;

                LedgerEntry? entry = null;
                try
                {
                    var resp = await _ledger.GetBlobClient(item.Name).DownloadContentAsync(ct);
                    entry = JsonSerializer.Deserialize<LedgerEntry>(resp.Value.Content.ToMemory().Span);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Cruscotto: ledger blob {Name} read failed", item.Name);
                    continue;
                }
                if (entry is null) continue;

                var isIssued   = string.Equals(entry.State, "Issued", StringComparison.OrdinalIgnoreCase);
                var noTerminal = string.IsNullOrEmpty(entry.LastTerminalState);
                if (isIssued && noTerminal && entry.IssuedAt is { } issued && issued < stuckBefore)
                {
                    stuck++;
                    stuckList.Add(new StuckLedgerEntry(
                        IntuneDeviceId: entry.IntuneDeviceId ?? "(unknown)",
                        CorrelationId: entry.CorrelationId,
                        IssuedAt: issued,
                        AgeHours: Math.Round((DateTimeOffset.UtcNow - issued).TotalHours, 1)));
                    if (oldestStuckIssuedAt is null || issued < oldestStuckIssuedAt)
                    {
                        oldestStuckIssuedAt = issued;
                        oldestStuckId = entry.IntuneDeviceId;
                    }
                }
            }
            var status = stuck > 0    ? NodeHealth.Red
                       : total >= 100 ? NodeHealth.Yellow
                       :                NodeHealth.Green;
            return new LedgerStatus(total, stuck, oldestStuckIssuedAt, oldestStuckId,
                                    stuckList.OrderByDescending(e => e.AgeHours).Take(10).ToArray(),
                                    _graceHours, status, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cruscotto: ledger enumeration failed");
            return new LedgerStatus(0, 0, null, null, Array.Empty<StuckLedgerEntry>(), _graceHours, NodeHealth.Unknown, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Archives and deletes a stuck ledger entry by IntuneDeviceId.
    /// The original blob is copied under <c>_archive/</c> before deletion.
    /// </summary>
    public async Task<(bool Deleted, string? ArchivedAs)> ResetLedgerEntryAsync(string intuneDeviceId, string reason, CancellationToken ct)
    {
        if (_ledger is null)
            throw new InvalidOperationException("Ledger client not configured");

        var blobName = $"{intuneDeviceId.ToLowerInvariant()}.json";
        var client = _ledger.GetBlobClient(blobName);

        try
        {
            var download = await client.DownloadContentAsync(ct);
            var content = download.Value.Content.ToString();

            // Archive under _archive/ with timestamp so we keep a record
            var archiveName = $"_archive/{intuneDeviceId.ToLowerInvariant()}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json";
            var archivePayload = JsonSerializer.Serialize(new
            {
                originalContent = JsonSerializer.Deserialize<JsonElement>(content),
                archivedAt = DateTimeOffset.UtcNow,
                archivedReason = reason
            });
            await _ledger.GetBlobClient(archiveName).UploadAsync(
                new BinaryData(archivePayload),
                overwrite: true,
                cancellationToken: ct);

            await client.DeleteAsync(cancellationToken: ct);
            _log.LogInformation("Ledger entry {IntuneDeviceId} archived as {Archive} and deleted (reason: {Reason})",
                intuneDeviceId, archiveName, reason);
            return (true, archiveName);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return (false, null);
        }
    }

    private sealed record LedgerEntry
    {
        public string? IntuneDeviceId { get; init; }
        public string  CorrelationId { get; init; } = "";
        public string? State { get; init; }
        public DateTimeOffset? IssuedAt { get; init; }
        public DateTimeOffset? LastRearmedAt { get; init; }
        public string? LastTerminalState { get; init; }
        public int ActionSequence { get; init; }
    }
}
