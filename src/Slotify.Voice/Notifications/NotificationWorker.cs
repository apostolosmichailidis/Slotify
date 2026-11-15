using Microsoft.EntityFrameworkCore;
using Slotify.Core.Notifications;
using Slotify.Data;

namespace Slotify.Voice.Notifications;

/// <summary>
/// Στέλνει ό,τι περιμένει στον πίνακα notifications: email στα ιατρεία, SMS στους ασθενείς.
/// Ξαναδοκιμάζει με αυξανόμενη αναμονή (30″, 2′, 4′ … ως 1 ώρα) μέχρι <see cref="Notification.MaxAttempts"/> φορές.
/// Γράφονται και από το διαχειριστικό (δοκιμαστικά), γι' αυτό ο worker ελέγχει τη βάση κάθε λίγο.
/// </summary>
public sealed class NotificationWorker(
    IDbContextFactory<SlotifyDbContext> dbFactory,
    IEmailSender email,
    ISmsSender sms,
    TimeProvider time,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>Παλιότερα από αυτό δεν στέλνονται πια (ένα SMS για ραντεβού που πέρασε δεν βοηθάει κανέναν).</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Σφάλμα στον worker ειδοποιήσεων");
            }

            try { await Task.Delay(PollInterval, time, stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }

    public async Task SendDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var since = now - MaxAge;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var due = await db.Notifications
            .Where(n => n.SentAt == null && n.NextAttemptAt <= now && n.Attempts < Notification.MaxAttempts && n.CreatedAt >= since)
            .OrderBy(n => n.NextAttemptAt)
            .Take(20)
            .ToListAsync(ct);

        foreach (var n in due)
        {
            // Το «πιάνουμε» πρώτα με αλλαγή υπό όρο, ώστε δύο instances του Voice να μην το στείλουν και τα δύο.
            var claimed = await db.Notifications
                .Where(x => x.Id == n.Id && x.Attempts == n.Attempts && x.SentAt == null)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1)
                    .SetProperty(x => x.NextAttemptAt, now + Backoff(n.Attempts + 1)), ct);
            if (claimed == 0) continue;

            var result = await SendAsync(db, n, ct);

            await db.Notifications.Where(x => x.Id == n.Id).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.SentAt, result.Ok ? time.GetUtcNow() : null)
                .SetProperty(x => x.Error, result.Ok ? null : Truncate(result.Error))
                .SetProperty(x => x.ProviderId, result.ProviderId)
                .SetProperty(x => x.Segments, result.Ok ? result.Segments : null)
                .SetProperty(x => x.Attempts, x => result.Permanent ? Notification.MaxAttempts : x.Attempts), ct);

            if (result.Ok)
                logger.LogInformation("Στάλθηκε {Channel} {Id} σε {To}", n.Channel, n.Id, Mask(n));
            else
                logger.LogWarning("Δεν στάλθηκε {Channel} {Id} (προσπάθεια {Attempt}{Final}): {Error}",
                    n.Channel, n.Id, n.Attempts + 1, result.Permanent ? ", οριστικά" : "", result.Error);
        }
    }

    private async Task<SendResult> SendAsync(SlotifyDbContext db, Notification n, CancellationToken ct)
    {
        try
        {
            switch (n.Channel)
            {
                case NotificationChannel.Email:
                    return await email.SendAsync(n.To, n.Subject ?? "Slotify", n.Body, ct);

                case NotificationChannel.Sms:
                {
                    var sender = await db.Clinics.Where(c => c.Id == n.ClinicId).Select(c => c.SmsSenderId).FirstOrDefaultAsync(ct);
                    if (string.IsNullOrWhiteSpace(sender))
                        return new SendResult(false, Error: "Το ιατρείο δεν έχει αποστολέα SMS", Permanent: true);

                    var result = await sms.SendAsync(n.To, sender, n.Body, ct);
                    return result.Ok ? result with { Segments = result.Segments ?? SmsText.Segments(n.Body) } : result;
                }

                default:
                    return new SendResult(false, Error: $"Άγνωστο κανάλι {n.Channel}", Permanent: true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new SendResult(false, Error: ex.Message);
        }
    }

    /// <summary>30″, 2′, 4′, 8′, 16′, 32′, 60′…</summary>
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(3600, attempt <= 1 ? 30 : 60 * Math.Pow(2, attempt - 2)));

    private static string? Truncate(string? s) => s is null || s.Length <= 500 ? s : s[..499] + "…";

    /// <summary>Στο log δεν γράφουμε ολόκληρο το τηλέφωνο του ασθενή.</summary>
    private static string Mask(Notification n) =>
        n.Channel == NotificationChannel.Sms && n.To.Length > 6 ? n.To[..6] + new string('•', n.To.Length - 9) + n.To[^3..] : n.To;
}
