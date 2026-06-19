using Microsoft.ApplicationInsights;

namespace IntuneWipePortal.Services;

/// <summary>
/// Centralised custom event tracker for portal operations. Every user-
/// facing operation (wave CRUD, config change, cert upload, group search,
/// etc.) is emitted as a custom event so the team can build dashboards,
/// alerts, and funnels in App Insights.
/// </summary>
public sealed class PortalEventTracker
{
    private readonly TelemetryClient _tc;

    public PortalEventTracker(TelemetryClient tc) => _tc = tc;

    // ---- Schedule / wave events -----------------------------------------

    public void TrackWaveCreated(Guid waveId, string name, string actionType, string? createdBy) =>
        _tc.TrackEvent("Portal.WaveCreated", new Dictionary<string, string>
        {
            ["waveId"] = waveId.ToString(),
            ["name"] = name,
            ["actionType"] = actionType,
            ["createdBy"] = createdBy ?? "unknown",
        });

    public void TrackWaveUpdated(Guid waveId, string? updatedBy) =>
        _tc.TrackEvent("Portal.WaveUpdated", new Dictionary<string, string>
        {
            ["waveId"] = waveId.ToString(),
            ["updatedBy"] = updatedBy ?? "unknown",
        });

    public void TrackWaveDeleted(Guid waveId, string? deletedBy) =>
        _tc.TrackEvent("Portal.WaveDeleted", new Dictionary<string, string>
        {
            ["waveId"] = waveId.ToString(),
            ["deletedBy"] = deletedBy ?? "unknown",
        });

    public void TrackMemberAdded(Guid waveId, string deviceName, string entraDeviceId) =>
        _tc.TrackEvent("Portal.MemberAdded", new Dictionary<string, string>
        {
            ["waveId"] = waveId.ToString(),
            ["deviceName"] = deviceName,
            ["entraDeviceId"] = entraDeviceId,
        });

    public void TrackBulkImport(Guid waveId, int added, int skipped, int errors) =>
        _tc.TrackEvent("Portal.BulkImport", new Dictionary<string, string>
        {
            ["waveId"] = waveId.ToString(),
        }, new Dictionary<string, double>
        {
            ["added"] = added,
            ["skipped"] = skipped,
            ["errors"] = errors,
        });

    public void TrackMemberRemoved(Guid waveId, int count) =>
        _tc.TrackEvent("Portal.MemberRemoved", new Dictionary<string, string>
        {
            ["waveId"] = waveId.ToString(),
        }, new Dictionary<string, double>
        {
            ["count"] = count,
        });

    public void TrackGroupAssigned(Guid waveId, string groupId, string groupName) =>
        _tc.TrackEvent("Portal.GroupAssigned", new Dictionary<string, string>
        {
            ["waveId"] = waveId.ToString(),
            ["groupId"] = groupId,
            ["groupName"] = groupName,
        });

    public void TrackGroupRemoved(Guid waveId) =>
        _tc.TrackEvent("Portal.GroupRemoved", new Dictionary<string, string>
        {
            ["waveId"] = waveId.ToString(),
        });

    // ---- Configuration events -------------------------------------------

    public void TrackConfigChanged(string key, string? changedBy) =>
        _tc.TrackEvent("Portal.ConfigChanged", new Dictionary<string, string>
        {
            ["key"] = key,
            ["changedBy"] = changedBy ?? "unknown",
        });

    public void TrackCertUploaded(string type, string subject, string thumbprint) =>
        _tc.TrackEvent("Portal.CertUploaded", new Dictionary<string, string>
        {
            ["type"] = type,
            ["subject"] = subject,
            ["thumbprint"] = thumbprint,
        });

    // ---- Device / group search events -----------------------------------

    public void TrackDeviceSearch(string prefix, int resultCount) =>
        _tc.TrackEvent("Portal.DeviceSearch", new Dictionary<string, string>
        {
            ["prefix"] = prefix,
        }, new Dictionary<string, double>
        {
            ["resultCount"] = resultCount,
        });

    public void TrackGroupSearch(string prefix, int resultCount) =>
        _tc.TrackEvent("Portal.GroupSearch", new Dictionary<string, string>
        {
            ["prefix"] = prefix,
        }, new Dictionary<string, double>
        {
            ["resultCount"] = resultCount,
        });

    // ---- Cruscotto events -----------------------------------------------

    public void TrackLedgerReset(string correlationId, string? resetBy) =>
        _tc.TrackEvent("Portal.LedgerReset", new Dictionary<string, string>
        {
            ["correlationId"] = correlationId,
            ["resetBy"] = resetBy ?? "unknown",
        });

    public void TrackLedgerRearm(string correlationId, string? rearmBy) =>
        _tc.TrackEvent("Portal.LedgerRearm", new Dictionary<string, string>
        {
            ["correlationId"] = correlationId,
            ["rearmBy"] = rearmBy ?? "unknown",
        });

    // ---- Auth events ----------------------------------------------------

    public void TrackPageAccess(string page, string? user) =>
        _tc.TrackEvent("Portal.PageAccess", new Dictionary<string, string>
        {
            ["page"] = page,
            ["user"] = user ?? "anonymous",
        });
}
