using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Slotify.Admin;
using Slotify.Admin.Auth;
using Slotify.Admin.Components;
using Slotify.Admin.Services;
using Slotify.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AdminOptions>()
    .Bind(builder.Configuration.GetSection(AdminOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Slotify")
    ?? throw new InvalidOperationException("Λείπει το ConnectionStrings:Slotify");
builder.Services.AddSlotifyData(connectionString);

builder.Services.AddScoped<ClinicEditor>();
builder.Services.AddScoped<DashboardQueries>();

// Σύνδεση με cookie. Ένας διαχειριστής, από τις ρυθμίσεις.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.Cookie.Name = "slotify.admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);
builder.Services.AddCascadingAuthenticationState();

var keysDirectory = builder.Configuration[$"{AdminOptions.Section}:KeysDirectory"];
if (!string.IsNullOrWhiteSpace(keysDirectory))
{
    builder.Services.AddDataProtection()
        .SetApplicationName("Slotify.Admin")
        .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
}

// Λίγες προσπάθειες σύνδεσης ανά λεπτό από κάθε διεύθυνση.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AccountEndpoints.LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
});

// Πίσω από reverse proxy (Caddy/nginx) με HTTPS: να ξέρει ότι το αίτημα ήρθε με https, για το secure cookie.
// Το X-Forwarded-For δεν το δεχόμαστε, γιατί όποιος το στείλει θα ξέφευγε από το όριο προσπαθειών σύνδεσης.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Όποια εφαρμογή ξεκινήσει πρώτη (Voice ή Admin) φτιάχνει τη βάση.
await DatabaseInitializer.InitializeAsync(app.Services);

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapAccountEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
