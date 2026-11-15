using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Slotify.Core.Scheduling;

namespace Slotify.Voice.Scheduling;

public sealed class GoogleOptions
{
    public const string Section = "Google";

    /// <summary>Το JSON του service account ολόκληρο (για user-secrets ή μεταβλητή περιβάλλοντος).</summary>
    public string? ServiceAccountJson { get; set; }

    /// <summary>Ή η διαδρομή του αρχείου JSON του service account.</summary>
    public string? ServiceAccountFile { get; set; }

    public string CalendarBaseUrl { get; set; } = "https://www.googleapis.com/calendar/v3";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServiceAccountJson) || !string.IsNullOrWhiteSpace(ServiceAccountFile);
}

/// <summary>
/// Google Calendar με service account. Ο γιατρός κάνει κοινή χρήση του ημερολογίου του
/// με το email του service account (δικαίωμα «Αλλαγές σε εκδηλώσεις»), και από εκεί
/// διαβάζουμε τα πιασμένα διαστήματα και γράφουμε ραντεβού.
///
/// Χωρίς NuGet: το JWT (RS256) υπογράφεται με το RSA του .NET.
/// https://developers.google.com/identity/protocols/oauth2/service-account#httprest
/// </summary>
public sealed class GoogleCalendar(IHttpClientFactory httpFactory, IOptions<GoogleOptions> options, TimeProvider time, ILogger<GoogleCalendar> logger)
    : ICalendar
{
    public const string HttpClientName = "google";
    private const string Scope = "https://www.googleapis.com/auth/calendar";

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private ServiceAccount? _account;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpires;

    public async Task<IReadOnlyList<TimeRange>> GetBusyAsync(string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var body = new
        {
            timeMin = from.UtcDateTime.ToString("o"),
            timeMax = to.UtcDateTime.ToString("o"),
            items = new[] { new { id = calendarId } },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/freeBusy") { Content = JsonContent.Create(body) };
        var json = await SendAsync(request, ct);

        var calendar = json["calendars"]?[calendarId]
            ?? throw new CalendarAccessException($"Το Google δεν επέστρεψε το ημερολόγιο {calendarId}");

        if (calendar["errors"] is JsonArray { Count: > 0 } errors)
        {
            var reason = errors[0]?["reason"]?.GetValue<string>();
            throw new CalendarAccessException(
                $"Δεν υπάρχει πρόσβαση στο ημερολόγιο {calendarId} ({reason}). Έχει γίνει κοινή χρήση με το service account;");
        }

        var busy = new List<TimeRange>();
        foreach (var b in calendar["busy"]?.AsArray() ?? [])
        {
            busy.Add(new TimeRange(
                DateTimeOffset.Parse(b!["start"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(b!["end"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return busy;
    }

    public async Task<CreatedEvent> CreateEventAsync(string calendarId, CalendarEvent ev, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["summary"] = ev.Summary,
            ["description"] = ev.Description,
            ["start"] = new JsonObject { ["dateTime"] = ev.Start.ToString("yyyy-MM-ddTHH:mm:sszzz"), ["timeZone"] = ev.TimeZoneId },
            ["end"] = new JsonObject { ["dateTime"] = ev.End.ToString("yyyy-MM-ddTHH:mm:sszzz"), ["timeZone"] = ev.TimeZoneId },
        };
        if (ev.PrivateProperties is { Count: > 0 })
        {
            var props = new JsonObject();
            foreach (var (k, v) in ev.PrivateProperties) props[k] = v;
            body["extendedProperties"] = new JsonObject { ["private"] = props };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var json = await SendAsync(request, ct);
        return new CreatedEvent(json["id"]!.GetValue<string>(), json["htmlLink"]?.GetValue<string>());
    }

    private string BaseUrl => options.Value.CalendarBaseUrl.TrimEnd('/');

    // Singleton (κρατάει το token), οπότε παίρνουμε HttpClient από το factory σε κάθε κλήση.
    private HttpClient Http => httpFactory.CreateClient(HttpClientName);

    private async Task<JsonNode> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));
        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            throw new CalendarAccessException($"Google Calendar {(int)response.StatusCode}: δεν υπάρχει πρόσβαση στο ημερολόγιο. {text}");

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Google Calendar {Status}: {Body}", (int)response.StatusCode, text);
            throw new HttpRequestException($"Google Calendar απάντησε {(int)response.StatusCode}");
        }

        return JsonNode.Parse(text)!;
    }

    // ---------- OAuth με service account ----------

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && time.GetUtcNow() < _accessTokenExpires) return _accessToken;

            var account = _account ??= LoadServiceAccount();
            var assertion = CreateJwt(account, time.GetUtcNow());

            using var response = await Http.PostAsync(account.TokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            }), ct);

            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Google token {Status}: {Body}", (int)response.StatusCode, text);
                throw new CalendarAccessException($"Το Google αρνήθηκε το service account ({(int)response.StatusCode}).");
            }

            var token = JsonNode.Parse(text)!;
            _accessToken = token["access_token"]!.GetValue<string>();
            var expiresIn = token["expires_in"]?.GetValue<int>() ?? 3600;
            _accessTokenExpires = time.GetUtcNow().AddSeconds(expiresIn - 120); // ανανέωση λίγο πριν λήξει
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    internal static string CreateJwt(ServiceAccount account, DateTimeOffset now)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = account.ClientEmail,
            scope = Scope,
            aud = account.TokenUri,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddHours(1).ToUnixTimeSeconds(),
        }));

        var unsigned = $"{header}.{claims}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(account.PrivateKey);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private ServiceAccount LoadServiceAccount()
    {
        var o = options.Value;
        var json = !string.IsNullOrWhiteSpace(o.ServiceAccountJson) ? o.ServiceAccountJson
            : !string.IsNullOrWhiteSpace(o.ServiceAccountFile) ? File.ReadAllText(o.ServiceAccountFile)
            : throw new CalendarAccessException("Δεν έχει οριστεί Google:ServiceAccountJson ή Google:ServiceAccountFile.");

        var account = JsonSerializer.Deserialize<ServiceAccount>(json)
            ?? throw new CalendarAccessException("Το JSON του service account δεν διαβάζεται.");
        if (string.IsNullOrWhiteSpace(account.ClientEmail) || string.IsNullOrWhiteSpace(account.PrivateKey))
            throw new CalendarAccessException("Στο JSON του service account λείπει το client_email ή το private_key.");
        return account;
    }

    internal sealed record ServiceAccount(
        [property: JsonPropertyName("client_email")] string ClientEmail,
        [property: JsonPropertyName("private_key")] string PrivateKey,
        [property: JsonPropertyName("token_uri")] string TokenUri = "https://oauth2.googleapis.com/token");
}
