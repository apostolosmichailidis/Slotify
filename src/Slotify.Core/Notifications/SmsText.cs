namespace Slotify.Core.Notifications;

public static class SmsText
{
    // Το βασικό αλφάβητο GSM 03.38 (με τα κεφαλαία ελληνικά που έχει). Ό,τι άλλο στέλνεται ως UCS-2.
    private const string Gsm7 =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
        "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    private const string Gsm7Extended = "^{}\\[~]|€";

    /// <summary>
    /// Σε πόσα κομμάτια θα σταλεί (και θα χρεωθεί) ένα SMS. Με λατινικά χωράνε 160 χαρακτήρες (153 όταν σπάει),
    /// με ελληνικά πεζά ή τόνους 70 (67 όταν σπάει).
    /// </summary>
    public static int Segments(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var gsm = true;
        var septets = 0;
        foreach (var c in text)
        {
            if (Gsm7.Contains(c)) septets++;
            else if (Gsm7Extended.Contains(c)) septets += 2;
            else { gsm = false; break; }
        }

        if (gsm) return septets <= 160 ? 1 : (septets + 152) / 153;

        // UCS-2: χαρακτήρες εκτός BMP (π.χ. emoji) πιάνουν δύο θέσεις, όπως και στο .NET string.
        var units = text.Length;
        return units <= 70 ? 1 : (units + 66) / 67;
    }

    private static readonly string[] Days = ["Κυριακή", "Δευτέρα", "Τρίτη", "Τετάρτη", "Πέμπτη", "Παρασκευή", "Σάββατο"];

    /// <summary>
    /// Η επιβεβαίωση ραντεβού στον ασθενή. Σύντομη, γιατί τα ελληνικά χωράνε 70 χαρακτήρες στο κομμάτι και κάθε κομμάτι χρεώνεται.
    /// Π.χ. «Οδοντιατρείο Δοκιμής: ραντεβού Καθαρισμός, Τρίτη 17/11 στις 10:30. Για αλλαγή: 2310123456».
    /// </summary>
    public static string BookingConfirmation(string clinicName, string clinicPhone, string service, DateTime localStart)
    {
        var phone = clinicPhone.StartsWith("+30", StringComparison.Ordinal) ? clinicPhone[3..] : clinicPhone;
        return $"{clinicName}: ραντεβού {service}, {Days[(int)localStart.DayOfWeek]} {localStart.Day}/{localStart.Month} " +
               $"στις {localStart:HH:mm}. Για αλλαγή: {phone}";
    }

    /// <summary>Ελληνικό κινητό (+3069…): μόνο σε τέτοια στέλνουμε SMS, για να μη φεύγουν χρεώσεις σε σταθερά ή εξωτερικό.</summary>
    public static bool IsGreekMobile(string? e164) =>
        e164 is { Length: 13 } && e164.StartsWith("+3069", StringComparison.Ordinal) && e164[1..].All(char.IsAsciiDigit);
}
