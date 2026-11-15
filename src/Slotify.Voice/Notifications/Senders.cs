using System.Net.Http.Headers;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Slotify.Voice.Notifications;

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>SMTP του παρόχου, π.χ. "smtp.mailersend.net". Κενό = τα email δεν στέλνονται.</summary>
    public string? SmtpHost { get; set; }

    /// <summary>587 με STARTTLS (το συνηθισμένο) ή 465 με TLS από την αρχή.</summary>
    public int SmtpPort { get; set; } = 587;

    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Διεύθυνση αποστολέα σε domain που έχεις επιβεβαιώσει στον πάροχο, π.χ. "noreply@slotify.gr".</summary>
    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "Slotify";

    /// <summary>Κρυπτογράφηση υποχρεωτική. false μόνο για τοπικό ψεύτικο SMTP (π.χ. Mailpit) σε δοκιμές.</summary>
    public bool RequireTls { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(FromAddress);
}

/// <summary>Αποτέλεσμα αποστολής. Permanent = δεν έχει νόημα να ξαναδοκιμάσουμε (π.χ. λάθος διεύθυνση).</summary>
public sealed record SendResult(bool Ok, string? ProviderId = null, int? Segments = null, string? Error = null, bool Permanent = false);

public interface IEmailSender
{
    Task<SendResult> SendAsync(string to, string subject, string html, CancellationToken ct);
}

public interface ISmsSender
{
    Task<SendResult> SendAsync(string to, string from, string body, CancellationToken ct);
}

/// <summary>Email μέσω SMTP. Δουλεύει με κάθε πάροχο (MailerSend, Brevo, Mailjet, Gmail…).</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task<SendResult> SendAsync(string to, string subject, string html, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured) return new SendResult(false, Error: "Δεν έχει ρυθμιστεί το Email (SMTP)", Permanent: true);
        var host = o.SmtpHost!;
        var fromAddress = o.FromAddress!;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, fromAddress));
        foreach (var address in to.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!MailboxAddress.TryParse(address, out var mailbox))
                return new SendResult(false, Error: $"Λάθος διεύθυνση email «{address}»", Permanent: true);
            message.To.Add(mailbox);
        }
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = html }.ToMessageBody();

        using var smtp = new SmtpClient { Timeout = 20_000 };
        var security = !o.RequireTls ? SecureSocketOptions.StartTlsWhenAvailable
            : o.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;
        await smtp.ConnectAsync(host, o.SmtpPort, security, ct);
        if (!string.IsNullOrEmpty(o.Username)) await smtp.AuthenticateAsync(o.Username, o.Password ?? "", ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
        return new SendResult(true, ProviderId: message.MessageId);
    }
}

/// <summary>SMS μέσω του Twilio Messaging API, με τον ίδιο λογαριασμό που σηκώνει τις κλήσεις.</summary>
public sealed class TwilioSmsSender(HttpClient http, IOptions<TwilioOptions> options) : ISmsSender
{
    public async Task<SendResult> SendAsync(string to, string from, string body, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.AccountSid) || string.IsNullOrWhiteSpace(o.AuthToken))
            return new SendResult(false, Error: "Λείπει το Twilio:AccountSid για τα SMS", Permanent: true);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{o.ApiBaseUrl.TrimEnd('/')}/2010-04-01/Accounts/{Uri.EscapeDataString(o.AccountSid)}/Messages.json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["To"] = to, ["From"] = from, ["Body"] = body }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{o.AccountSid}:{o.AuthToken}")));

        using var response = await http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;

        if (response.IsSuccessStatusCode)
        {
            var sid = root.TryGetProperty("sid", out var s) ? s.GetString() : null;
            int? segments = root.TryGetProperty("num_segments", out var n) && int.TryParse(n.ToString(), out var v) ? v : null;
            return new SendResult(true, sid, segments);
        }

        var error = root.TryGetProperty("message", out var m) ? m.GetString() : $"HTTP {(int)response.StatusCode}";
        // 4xx (λάθος αριθμός, μη επιτρεπτός αποστολέας…) δεν διορθώνονται με επανάληψη. Τα 429/5xx ναι.
        var permanent = (int)response.StatusCode is >= 400 and < 500 and not 429;
        return new SendResult(false, Error: $"Twilio: {error}", Permanent: permanent);
    }
}
