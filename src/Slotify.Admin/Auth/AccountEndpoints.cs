using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Slotify.Admin.Auth;

public static class AccountEndpoints
{
    public const string LoginRateLimit = "login";

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // Η φόρμα της σελίδας /login στέλνει εδώ. Το antiforgery ελέγχεται αυτόματα για [FromForm].
        app.MapPost("/account/login", async (
                HttpContext http,
                IOptions<AdminOptions> options,
                ILoggerFactory loggers,
                [FromForm] string? username,
                [FromForm] string? password,
                [FromForm] string? returnUrl) =>
            {
                var admin = options.Value;
                var ok = Matches(username, admin.Username) & Matches(password, admin.Password);
                var safeReturn = IsLocal(returnUrl) ? returnUrl! : "/";

                if (!ok)
                {
                    loggers.CreateLogger("Slotify.Admin.Login")
                        .LogWarning("Αποτυχημένη σύνδεση από {Ip}", http.Connection.RemoteIpAddress);
                    return Results.LocalRedirect($"/login?error=1&returnUrl={Uri.EscapeDataString(safeReturn)}");
                }

                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, admin.Username), new Claim(ClaimTypes.Role, "Admin")],
                    CookieAuthenticationDefaults.AuthenticationScheme);

                await http.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity),
                    new AuthenticationProperties { IsPersistent = true });

                return Results.LocalRedirect(safeReturn);
            })
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimit);

        app.MapPost("/account/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/login");
        });
    }

    /// <summary>Σύγκριση σε σταθερό χρόνο, για να μη μαρτυράει ο χρόνος απάντησης πόσοι χαρακτήρες ταιριάζουν.</summary>
    private static bool Matches(string? given, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given ?? "")),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
