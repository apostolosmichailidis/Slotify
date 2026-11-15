using System.Net;
using System.Text;
using Slotify.Core.Calls;
using Slotify.Core.Clinics;
using Slotify.Core.Notifications;
using Slotify.Voice.Scheduling;

namespace Slotify.Voice.Notifications;

/// <summary>
/// Τι στέλνουμε μετά από μια κλήση:
/// - email στο ιατρείο όταν θέλει κάτι από αυτό (ραντεβού, μήνυμα, κλήση που κόπηκε από σφάλμα),
/// - SMS επιβεβαίωσης στον ασθενή όταν κλείστηκε ραντεβού και το ιατρείο το έχει ενεργό.
/// Μόνο φτιάχνει τα μηνύματα. Τα στέλνει ο <see cref="NotificationWorker"/>.
/// </summary>
public static class CallNotifications
{
    public static List<Notification> Compose(Clinic clinic, Call call, CallOutcome outcome, string? summary, Booking? booking)
    {
        var result = new List<Notification>();

        if (!string.IsNullOrWhiteSpace(clinic.NotificationEmail)
            && outcome is CallOutcome.AppointmentBooked or CallOutcome.MessageTaken or CallOutcome.Failed)
        {
            var (subject, html) = DoctorEmail(clinic, call, outcome, summary);
            result.Add(new Notification
            {
                ClinicId = clinic.Id,
                CallId = call.Id,
                Channel = NotificationChannel.Email,
                To = clinic.NotificationEmail.Trim(),
                Subject = subject,
                Body = html,
            });
        }

        if (clinic.SendPatientSms && booking is not null && SmsText.IsGreekMobile(booking.Phone))
        {
            result.Add(new Notification
            {
                ClinicId = clinic.Id,
                CallId = call.Id,
                Channel = NotificationChannel.Sms,
                To = booking.Phone,
                Body = SmsText.BookingConfirmation(clinic.Name, clinic.PhoneNumber, booking.Service, booking.LocalStart),
            });
        }

        return result;
    }

    public static (string Subject, string Html) DoctorEmail(Clinic clinic, Call call, CallOutcome outcome, string? summary)
    {
        var from = call.FromNumber ?? "απόκρυψη";
        var when = TimeZoneInfo.ConvertTime(call.StartedAt, clinic.TimeZone).DateTime;

        var (subject, intro) = outcome switch
        {
            CallOutcome.AppointmentBooked => ($"Νέο ραντεβού: {FirstLine(summary)}", "Η γραμματεία έκλεισε ραντεβού στο ημερολόγιο."),
            CallOutcome.MessageTaken => ($"Καλέστε πίσω: {FirstLine(summary)}", "Ο ασθενής περιμένει να τον καλέσετε."),
            _ => ($"Κλήση που κόπηκε από {from}", "Η κλήση κόπηκε από τεχνικό πρόβλημα. Καλέστε πίσω τον αριθμό."),
        };

        var h = new StringBuilder();
        h.Append("<div style=\"font-family:system-ui,Segoe UI,Roboto,sans-serif;font-size:15px;line-height:1.5;color:#1d2330\">");
        h.Append($"<p>{E(intro)}</p>");
        if (!string.IsNullOrWhiteSpace(summary))
        {
            h.Append("<div style=\"background:#f3f4f6;border-radius:8px;padding:12px 14px;margin:12px 0\">");
            foreach (var line in summary.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                h.Append($"<div>{E(line)}</div>");
            h.Append("</div>");
        }
        h.Append("<table style=\"border-collapse:collapse\">");
        Row(h, "Κλήση", $"{GreekText.DateTime(when)}");
        Row(h, "Από", call.FromNumber is null ? "απόκρυψη" : $"<a href=\"tel:{E(call.FromNumber)}\">{E(call.FromNumber)}</a>", raw: true);
        h.Append("</table>");
        h.Append($"<p style=\"color:#6b7280;font-size:13px;margin-top:24px\">Από τη ψηφιακή γραμματεία του ιατρείου «{E(clinic.Name)}» (Slotify).</p>");
        h.Append("</div>");

        return (Truncate($"{clinic.Name} · {subject}", 300), h.ToString());
    }

    private static void Row(StringBuilder h, string label, string value, bool raw = false) =>
        h.Append($"<tr><td style=\"color:#6b7280;padding:2px 16px 2px 0\">{E(label)}</td><td>{(raw ? value : E(value))}</td></tr>");

    private static string FirstLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "χωρίς λεπτομέρειες";
        var line = s.Split('\n')[0];
        foreach (var prefix in new[] { "Ραντεβού: ", "Μήνυμα: " })
            if (line.StartsWith(prefix, StringComparison.Ordinal)) line = line[prefix.Length..];
        return Truncate(line, 150);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
