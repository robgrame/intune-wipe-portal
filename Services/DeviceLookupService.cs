using Azure.Core;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Authentication;

namespace IntuneWipePortal.Services;

/// <summary>
/// Searches Entra ID devices via Microsoft Graph for typeahead / autocomplete
/// on the schedule wave member form. Returns display name, Entra device id,
/// and Intune managed device id (resolved from /deviceManagement/managedDevices).
/// </summary>
public sealed class DeviceLookupService
{
    private readonly GraphServiceClient _graph;
    private readonly ILogger<DeviceLookupService> _log;

    public DeviceLookupService(TokenCredential credential, ILogger<DeviceLookupService> log)
    {
        _graph = new GraphServiceClient(credential);
        _log = log;
    }

    /// <summary>
    /// Searches devices whose displayName starts with <paramref name="prefix"/>.
    /// Returns at most <paramref name="top"/> results. For each result, also
    /// resolves the Intune managed device id (best-effort).
    /// </summary>
    public async Task<IReadOnlyList<DeviceLookupResult>> SearchAsync(
        string prefix, int top = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 2)
            return [];

        try
        {
            var resp = await _graph.Devices.GetAsync(cfg =>
            {
                cfg.QueryParameters.Filter = $"startsWith(displayName, '{EscapeOData(prefix)}')";
                cfg.QueryParameters.Select = new[]
                {
                    "id", "displayName", "deviceId", "mdmAppId",
                    "operatingSystem", "operatingSystemVersion",
                };
                cfg.QueryParameters.Top = top;
                cfg.QueryParameters.Orderby = new[] { "displayName" };
                cfg.Headers.Add("ConsistencyLevel", "eventual");
                cfg.QueryParameters.Count = true;
            }, ct).ConfigureAwait(false);

            if (resp?.Value is null) return [];

            var results = resp.Value
                .Select(d => new DeviceLookupResult
                {
                    DisplayName = d.DisplayName ?? string.Empty,
                    EntraDeviceId = d.DeviceId ?? string.Empty,
                    EntraObjectId = d.Id ?? string.Empty,
                    OperatingSystem = d.OperatingSystem,
                    OsVersion = d.OperatingSystemVersion,
                })
                .ToList();

            // Best-effort: resolve Intune managed device ids in parallel
            await ResolveIntuneIdsAsync(results, ct).ConfigureAwait(false);

            return results;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Device lookup failed for prefix '{Prefix}'.", prefix);
            return [];
        }
    }

    /// <summary>
    /// Resolves Intune managed device id for each result by querying
    /// /deviceManagement/managedDevices?$filter=azureADDeviceId eq '...'.
    /// Gracefully skips on permission errors (requires DeviceManagementManagedDevices.Read.All).
    /// </summary>
    private async Task ResolveIntuneIdsAsync(List<DeviceLookupResult> results, CancellationToken ct)
    {
        var tasks = results.Select(async r =>
        {
            try
            {
                var managed = await _graph.DeviceManagement.ManagedDevices.GetAsync(cfg =>
                {
                    cfg.QueryParameters.Filter = $"azureADDeviceId eq '{r.EntraDeviceId}'";
                    cfg.QueryParameters.Select = new[] { "id", "deviceName" };
                    cfg.QueryParameters.Top = 1;
                }, ct).ConfigureAwait(false);

                var first = managed?.Value?.FirstOrDefault();
                if (first is not null)
                    r.IntuneDeviceId = first.Id;
            }
            catch
            {
                // Permission missing or transient error — leave IntuneDeviceId null
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string EscapeOData(string value) =>
        value.Replace("'", "''");
}

public sealed class DeviceLookupResult
{
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>Entra device id (the GUID used in schedule wave membership).</summary>
    public string EntraDeviceId { get; init; } = string.Empty;
    /// <summary>Entra object id (directory object id).</summary>
    public string EntraObjectId { get; init; } = string.Empty;
    /// <summary>Intune managed device id (resolved best-effort, may be null).</summary>
    public string? IntuneDeviceId { get; set; }
    public string? OperatingSystem { get; init; }
    public string? OsVersion { get; init; }
}
