using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace IntuneWipePortal.Services;

/// <summary>
/// Tracks Blazor Server circuit lifecycle in App Insights. Logs circuit
/// creation, connection, reconnection, and — critically — unhandled
/// exceptions that kill the circuit (the "page closes and redirects to
/// dashboard" symptom).
/// </summary>
public sealed class PortalCircuitHandler : CircuitHandler
{
    private readonly TelemetryClient _tc;
    private readonly ILogger<PortalCircuitHandler> _log;

    public PortalCircuitHandler(TelemetryClient tc, ILogger<PortalCircuitHandler> log)
    {
        _tc = tc;
        _log = log;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        _log.LogInformation("Blazor circuit {CircuitId} opened.", circuit.Id);
        _tc.TrackEvent("Portal.CircuitOpened", new Dictionary<string, string>
        {
            ["circuitId"] = circuit.Id,
        });
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken ct)
    {
        _log.LogDebug("Blazor circuit {CircuitId} connection up.", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken ct)
    {
        _log.LogWarning("Blazor circuit {CircuitId} connection down.", circuit.Id);
        _tc.TrackEvent("Portal.CircuitConnectionDown", new Dictionary<string, string>
        {
            ["circuitId"] = circuit.Id,
        });
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        _log.LogInformation("Blazor circuit {CircuitId} closed.", circuit.Id);
        _tc.TrackEvent("Portal.CircuitClosed", new Dictionary<string, string>
        {
            ["circuitId"] = circuit.Id,
        });
        return Task.CompletedTask;
    }
}
