using Azure.Identity;
using Azure.Monitor.Query;
using System.Security.Claims;
using IntuneWipePortal.Hubs;
using IntuneWipePortal.Services;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using IntuneWipePortal.Components;

var builder = WebApplication.CreateBuilder(args);

// --- Auth: Entra ID via OpenID Connect (Microsoft.Identity.Web). All pages
// require an authenticated user; app roles gate read access (see below).
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

// When endpoint authorization fails because the user is authenticated but
// lacks the required role, send them to a friendly access-denied page
// instead of looping. NOTE: Microsoft.Identity.Web signs the user in with the
// Cookies scheme (CookieAuthenticationDefaults.AuthenticationScheme), NOT the
// ASP.NET Identity "Identity.Application" scheme that ConfigureApplicationCookie
// targets. Configuring the wrong scheme left AccessDeniedPath at its default
// "/Account/AccessDenied", which is itself gated by the role FallbackPolicy →
// Forbid → redirect → infinite ERR_TOO_MANY_REDIRECTS loop. Configure the
// correct scheme so Forbid lands on the [AllowAnonymous] /access-denied page.
builder.Services.Configure<CookieAuthenticationOptions>(
    CookieAuthenticationDefaults.AuthenticationScheme, o =>
{
    // Authenticated but missing role → friendly access-denied page.
    o.AccessDeniedPath = "/access-denied";
    // Unauthenticated → friendly courtesy page with an explicit "Accedi"
    // button instead of an opaque auto-redirect to Entra. The button on that
    // page triggers MicrosoftIdentity/Account/SignIn (an explicit OIDC
    // challenge), so SSO still works on click.
    o.LoginPath = "/access-denied";
});

// Route implicit challenges (the FallbackPolicy below kicks unauthenticated
// users out) through the Cookies scheme so they land on the courtesy page at
// LoginPath, rather than the OpenIdConnect scheme which would auto-redirect to
// the Entra login. Explicit sign-in via the MicrosoftIdentity controller still
// challenges OpenIdConnect directly and is unaffected.
builder.Services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
{
    o.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
});

builder.Services.AddAuthorization(options =>
{
    // Either role grants read access; Auditor exists for future export
    // features. The legacy "Wipe.*" role names were retained transitionally
    // for a single rolling token-refresh window after the rename; that window
    // has now passed, so only the "Actions.*" roles are accepted.
    var readRoles = new[]
    {
        "Actions.Observer", "Actions.Auditor",
    };
    static bool HasRole(ClaimsPrincipal user, string role) =>
        user.Claims.Any(c =>
            (c.Type == "roles" ||
             c.Type == System.Security.Claims.ClaimTypes.Role) &&
            string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));

    options.AddPolicy("CanRead", p => p
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => readRoles.Any(r => HasRole(ctx.User, r))));

    // Write access to the schedule pages (create/edit/delete waves and
    // membership) requires an explicit operator role. Fail-closed: with
    // no Actions.Operator role on the token the schedule pages return
    // 403, while the read pages (Dashboard / Audit) remain accessible.
    // Add the role to the app registration when an operator should be
    // able to schedule wipes.
    options.AddPolicy("CanScheduleWrite", p => p
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => HasRole(ctx.User, "Actions.Operator")));

    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => readRoles.Any(r => HasRole(ctx.User, r)))
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

builder.Services.AddSingleton(sp =>
{
    // Centralized transient-fault handling for all KQL queries: a single
    // workspace hiccup is retried with exponential backoff instead of
    // surfacing as a raw error on the dashboards.
    var options = new LogsQueryClientOptions
    {
        Retry =
        {
            Mode = Azure.Core.RetryMode.Exponential,
            MaxRetries = 3,
            Delay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(10),
            NetworkTimeout = TimeSpan.FromSeconds(30),
        },
    };
    return new LogsQueryClient(sp.GetRequiredService<Azure.Core.TokenCredential>(), options);
});
builder.Services.AddSingleton<EventGridMetricsCollector>();
builder.Services.AddSingleton<AuditQueryService>();
builder.Services.AddSingleton<AuditExportService>();
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

// --- Response compression. Brotli/Gzip over HTTPS for the larger static
// assets (cruscotto.js ~55 KB, cruscotto.css ~13 KB) and SignalR/HTML payloads.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
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
app.UseResponseCompression();

// --- Security response headers. Hardens the portal against MIME sniffing,
// clickjacking and referrer leakage, and applies a Content-Security-Policy.
// All scripts/styles/fonts are now self-hosted (Bootstrap, Bootstrap Icons,
// Chart.js), so the policy can stay tight; 'unsafe-inline' is retained for the
// small inline theme bootstrap script and the inline styles still present in a
// few Razor pages. connect-src allows the SignalR WebSocket back to the origin.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' wss: https:; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "object-src 'none'";
    await next();
});

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
