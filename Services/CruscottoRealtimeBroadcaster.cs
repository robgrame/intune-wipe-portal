using System.Text.Json;
using IntuneWipePortal.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace IntuneWipePortal.Services;

/// <summary>
/// Polls the telemetry snapshot server-side and pushes updates to connected
/// dashboard clients via SignalR. This removes per-client polling pressure
/// while keeping current pull-based data sources unchanged.
/// </summary>
public sealed class CruscottoRealtimeBroadcaster : BackgroundService
{
    private readonly CruscottoTelemetryService _telemetry;
    private readonly IHubContext<CruscottoHub> _hub;
    private readonly ILogger<CruscottoRealtimeBroadcaster> _log;
    private readonly TimeSpan _interval;

    private string? _lastSnapshotHash;

    public CruscottoRealtimeBroadcaster(
        CruscottoTelemetryService telemetry,
        IHubContext<CruscottoHub> hub,
        IConfiguration configuration,
        ILogger<CruscottoRealtimeBroadcaster> log)
    {
        _telemetry = telemetry;
        _hub = hub;
        _log = log;
        var seconds = configuration.GetValue("Cruscotto:RealtimePushIntervalSeconds", 3);
        _interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _telemetry.SnapshotAsync(stoppingToken);
                var hash = ComputeHash(snapshot);

                if (!string.Equals(hash, _lastSnapshotHash, StringComparison.Ordinal))
                {
                    _lastSnapshotHash = hash;
                    await _hub.Clients.All.SendAsync("snapshot", snapshot, cancellationToken: stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cruscotto realtime broadcaster tick failed");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private static string ComputeHash(DashboardSnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot);
    }
}
