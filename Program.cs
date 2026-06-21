using Azure.Identity;
using Azure.Monitor.Query;
using IntuneWipePortal.Hubs;
using IntuneWipePortal.Services;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using intune_wipe_portal.Components;

var builder = WebApplication.CreateBuilder(args);

// --- Auth: Entra ID via OpenID Connect (Microsoft.Identity.Web). All pages
// require an authenticated user; app roles gate read access (see below).
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

// When endpoint authorization fails because the user is authenticated but
// lacks the required role, send them to a friendly access-denied page
// instead of an opaque 403.
builder.Services.ConfigureApplicationCookie(o =>
{
    o.AccessDeniedPath = "/access-denied";
});

builder.Services.AddAuthorization(options =>
{
    // Either role grants read access; Auditor exists for future export
    // features. Old "Wipe.*" role names are kept in the list to support a
    // single rolling token-refresh window after the rename — they can be
    // removed once all users have re-signed-in and the app registration
    // exposes only the new "Actions.*" roles.
    var readRoles = new[]
    {
        "Actions.Observer", "Actions.Auditor",
        "Wipe.Observer", "Wipe.Auditor", // legacy, transitional
    };
    options.AddPolicy("CanRead", p => p.RequireRole(readRoles));

    // Write access to the schedule pages (create/edit/delete waves and
    // membership) requires an explicit operator role. Fail-closed: with
    // no Actions.Operator role on the token the schedule pages return
    // 403, while the read pages (Dashboard / Audit) remain accessible.
    // Add the role to the app registration when an operator should be
    // able to schedule wipes.
    options.AddPolicy("CanScheduleWrite",
        p => p.RequireRole("Actions.Operator"));

    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole(readRoles)
        .Build();
});

builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI()
    .AddJsonOptions(o =>
    {
        // Serializza enum (NodeHealth, ...) come stringhe ("Green", "Yellow", ...)
        // altrimenti il JS lato cruscotto chiama .toLowerCase() su un numero e
        // crash con "(status || \"\").toLowerCase is not a function".
        o.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// --- App Insights / Log Analytics client. DefaultAzureCredential will pick up
// the user-assigned managed identity in Azure via AZURE_CLIENT_ID; in dev it
// falls back to Azure CLI / VS sign-in.
builder.Services.AddSingleton<Azure.Core.TokenCredential>(_ =>
{
    var clientId = builder.Configuration["AZURE_CLIENT_ID"];
    var credOptions = new DefaultAzureCredentialOptions();
    if (!string.IsNullOrWhiteSpace(clientId))
    {
        credOptions.ManagedIdentityClientId = clientId;
    }
    return new DefaultAzureCredential(credOptions);
});

// --- Application Insights SDK: server-side telemetry (requests, exceptions,
// dependencies, custom events). The connection string comes from
// APPLICATIONINSIGHTS_CONNECTION_STRING app setting.
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddSingleton<ITelemetryInitializer, PortalTelemetryInitializer>();

// Blazor circuit lifecycle tracker — captures circuit open/close/disconnect
// events so we can diagnose "page closes after 1-2s" symptoms.
builder.Services.AddScoped<CircuitHandler, PortalCircuitHandler>();

// Custom event tracker for portal operations (wave CRUD, config, certs, etc.)
builder.Services.AddSingleton<PortalEventTracker>();

builder.Services.AddSingleton(sp => new LogsQueryClient(sp.GetRequiredService<Azure.Core.TokenCredential>()));
builder.Services.AddSingleton<EventGridMetricsCollector>();
builder.Services.AddSingleton<AuditQueryService>();
builder.Services.AddSingleton<CruscottoTelemetryService>();
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<AppConfigManagementService>();
builder.Services.AddSingleton<DeviceLookupService>();
builder.Services.AddSingleton<CapabilityRegistry>();
builder.Services.AddSingleton<WipeScheduleService>();
builder.Services.AddHostedService<CruscottoRealtimeBroadcaster>();
builder.Services.AddSignalR()
    .AddJsonProtocol(o =>
    {
        o.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        o.PayloadSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// --- Razor components (Interactive Server) + cascading auth state so
// AuthorizeView / AuthorizeRouteView work end-to-end.
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- HSTS / forwarded headers for App Service
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
        Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers(); // Microsoft.Identity.Web sign-in callback
app.MapHub<CruscottoHub>("/hubs/cruscotto").RequireAuthorization("CanRead");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
