using Azure.Core;
using Azure.Data.AppConfiguration;

namespace IntuneWipePortal.Services;

/// <summary>
/// Reads and writes settings on Azure App Configuration for operator-driven
/// configuration management. Uses the portal's own UAMI (Data Owner role).
/// After writing, bumps the Sentinel key so Function Apps hot-reload within 30s.
/// </summary>
public sealed class AppConfigManagementService
{
    private readonly ConfigurationClient _client;
    private readonly ILogger<AppConfigManagementService> _log;
    private readonly PortalEventTracker _tracker;

    public AppConfigManagementService(IConfiguration config, TokenCredential credential,
        ILogger<AppConfigManagementService> log, PortalEventTracker tracker)
    {
        _log = log;
        _tracker = tracker;
        var endpoint = config["AppConfig:Endpoint"]
            ?? throw new InvalidOperationException("AppConfig:Endpoint not configured");
        _client = new ConfigurationClient(new Uri(endpoint), credential);
    }

    public async Task<string?> GetAsync(string key, string? label = null, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetConfigurationSettingAsync(key, label, ct);
            return response.Value.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read AppConfig key {Key}.", key);
            throw;
        }
    }

    public async Task SetAsync(string key, string value, string? label = null, CancellationToken ct = default)
    {
        try
        {
            var setting = new ConfigurationSetting(key, value, label);
            await _client.SetConfigurationSettingAsync(setting, cancellationToken: ct);
            _log.LogInformation("AppConfig key set: {Key} (label={Label})", key, label ?? "(none)");
            _tracker.TrackConfigChanged(key, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to set AppConfig key {Key}.", key);
            throw;
        }
    }

    public async Task<Dictionary<string, string?>> GetManyAsync(IEnumerable<string> keys, string? label = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            result[key] = await GetAsync(key, label, ct);
        }
        return result;
    }

    public async Task SetManyAsync(Dictionary<string, string> settings, string? label = null, CancellationToken ct = default)
    {
        foreach (var (key, value) in settings)
        {
            await SetAsync(key, value, label, ct);
        }
        await BumpSentinelAsync(ct);
    }

    /// <summary>
    /// Increments the Sentinel key to trigger hot-reload on all Function Apps
    /// (refresh interval = 30s).
    /// </summary>
    public async Task BumpSentinelAsync(CancellationToken ct = default)
    {
        var current = await GetAsync("Sentinel", null, ct) ?? "0";
        var next = (long.TryParse(current, out var n) ? n + 1 : DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        await SetAsync("Sentinel", next, null, ct);
        _log.LogInformation("Sentinel bumped to {Value}", next);
    }
}
