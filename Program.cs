using Azure.Identity;
using Azure.Monitor.Query;
using IntuneWipePortal.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
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
    // Either role grants read access; Auditor exists for future export features.
    options.AddPolicy("CanRead", p => p.RequireRole("Wipe.Observer", "Wipe.Auditor"));
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole("Wipe.Observer", "Wipe.Auditor")
        .Build();
});

builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

// --- App Insights / Log Analytics client. DefaultAzureCredential will pick up
// the user-assigned managed identity in Azure via AZURE_CLIENT_ID; in dev it
// falls back to Azure CLI / VS sign-in.
builder.Services.AddSingleton(_ =>
{
    var clientId = builder.Configuration["AZURE_CLIENT_ID"];
    var credOptions = new DefaultAzureCredentialOptions();
    if (!string.IsNullOrWhiteSpace(clientId))
    {
        credOptions.ManagedIdentityClientId = clientId;
    }
    return new LogsQueryClient(new DefaultAzureCredential(credOptions));
});
builder.Services.AddSingleton<AuditQueryService>();

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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
