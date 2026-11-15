namespace Slotify.Core.Clinics;

/// <summary>
/// Ένα ιατρείο-πελάτης. Κάθε ιατρείο έχει τον δικό του αριθμό Twilio,
/// και από αυτόν καταλαβαίνουμε σε ποιο ιατρείο πήγε η κλήση.
/// </summary>
public class Clinic
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Το όνομα όπως το λέει η γραμματεία, π.χ. "Οδοντιατρείο Παπαδόπουλου".</summary>
    public required string Name { get; set; }

    /// <summary>Ο αριθμός Twilio σε μορφή E.164, π.χ. "+302310123456". Μοναδικός.</summary>
    public required string PhoneNumber { get; set; }

    /// <summary>IANA ζώνη ώρας. Για την Ελλάδα "Europe/Athens".</summary>
    public string TimeZoneId { get; set; } = "Europe/Athens";

    /// <summary>Το Google Calendar όπου γράφονται τα ραντεβού.</summary>
    public string? GoogleCalendarId { get; set; }

    /// <summary>Προαιρετικός δικός του χαιρετισμός. Αν λείπει, φτιάχνεται από το όνομα.</summary>
    public string? Greeting { get; set; }

    /// <summary>Ελεύθερο κείμενο με πληροφορίες για το ιατρείο (διεύθυνση, πάρκινγκ, τιμές…).</summary>
    public string? KnowledgeBase { get; set; }

    /// <summary>Πόση ώρα πριν από ένα ραντεβού σταματάμε να το κλείνουμε τηλεφωνικά.</summary>
    public int MinNoticeMinutes { get; set; } = 60;

    /// <summary>Ανά πόσα λεπτά ξεκινούν τα ραντεβού (π.χ. 15 → 10:00, 10:15, 10:30).</summary>
    public int SlotStepMinutes { get; set; } = 15;

    /// <summary>Πόσες μέρες μπροστά ψάχνουμε για ελεύθερες ώρες.</summary>
    public int BookingHorizonDays { get; set; } = 30;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<OpeningHours> OpeningHours { get; set; } = [];
    public List<ClinicService> Services { get; set; } = [];
    public List<ClinicClosure> Closures { get; set; } = [];

    public string EffectiveGreeting =>
        string.IsNullOrWhiteSpace(Greeting)
            ? $"Γεια σας, {Name}. Είμαι η ψηφιακή βοηθός του ιατρείου. Πώς μπορώ να σας βοηθήσω;"
            : Greeting;

    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
}
