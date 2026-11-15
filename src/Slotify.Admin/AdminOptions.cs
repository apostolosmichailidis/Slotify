using System.ComponentModel.DataAnnotations;

namespace Slotify.Admin;

/// <summary>Ο λογαριασμός του διαχειριστή. Προς το παρόν ένας, από τις ρυθμίσεις.</summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    [Required]
    public string Username { get; set; } = "admin";

    /// <summary>Μπαίνει σε user-secrets ή στο .env, ποτέ στο appsettings.json.</summary>
    [Required(ErrorMessage = "Λείπει το Admin:Password. Βάλ' το σε user-secrets ή στο .env (ADMIN_PASSWORD).")]
    [MinLength(12, ErrorMessage = "Το Admin:Password πρέπει να έχει τουλάχιστον 12 χαρακτήρες.")]
    public string Password { get; set; } = "";

    /// <summary>
    /// Φάκελος για τα κλειδιά που υπογράφουν το cookie σύνδεσης. Αν λείπει, τα κλειδιά
    /// μένουν στη μνήμη και κάθε επανεκκίνηση σε αποσυνδέει.
    /// </summary>
    public string? KeysDirectory { get; set; }
}
