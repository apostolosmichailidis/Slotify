namespace Slotify.Core.Notifications;

public enum NotificationChannel
{
    /// <summary>Email στο ιατρείο.</summary>
    Email = 1,

    /// <summary>SMS στον ασθενή. Χρεώνεται χωριστά στο ιατρείο.</summary>
    Sms = 2,
}

/// <summary>
/// Ένα email ή SMS που περιμένει να σταλεί. Γράφεται στη βάση στο τέλος της κλήσης και το στέλνει
/// ο worker του Voice, με επαναλήψεις. Έτσι τίποτα δεν χάνεται αν πέσει ο server ή η υπηρεσία αποστολής,
/// και τα SMS μετριούνται από εδώ για τη χρέωση.
/// </summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClinicId { get; set; }

    /// <summary>Η κλήση που το προκάλεσε. null για δοκιμαστικά από το διαχειριστικό.</summary>
    public Guid? CallId { get; set; }

    public NotificationChannel Channel { get; set; }

    /// <summary>Διευθύνσεις email (χωρισμένες με κόμμα) ή αριθμός κινητού σε E.164.</summary>
    public required string To { get; set; }

    /// <summary>Μόνο για email.</summary>
    public string? Subject { get; set; }

    public required string Body { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public int Attempts { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Τελευταίο σφάλμα. Αν γεμίσει χωρίς SentAt μετά τις επαναλήψεις, η αποστολή απέτυχε οριστικά.</summary>
    public string? Error { get; set; }

    /// <summary>Σε πόσα κομμάτια χρεώθηκε το SMS (τα ελληνικά χωράνε 70 χαρακτήρες στο κομμάτι).</summary>
    public int? Segments { get; set; }

    /// <summary>Το id του παρόχου (Twilio SID ή Message-Id), για αναζήτηση.</summary>
    public string? ProviderId { get; set; }

    public const int MaxAttempts = 8;

    public bool GaveUp => SentAt is null && Attempts >= MaxAttempts;
}
