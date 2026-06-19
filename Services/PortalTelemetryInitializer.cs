using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;

namespace IntuneWipePortal.Services;

/// <summary>
/// Enriches every telemetry item with the portal cloud role name so that
/// App Insights can distinguish portal telemetry from other services
/// sharing the same AI resource.
/// </summary>
public sealed class PortalTelemetryInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        telemetry.Context.Cloud.RoleName = "devact-portal";
        telemetry.Context.Cloud.RoleInstance =
            Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? Environment.MachineName;
    }
}
