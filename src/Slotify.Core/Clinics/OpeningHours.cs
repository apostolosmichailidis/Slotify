namespace Slotify.Core.Clinics;

/// <summary>
/// Ένα διάστημα λειτουργίας μέσα σε μια μέρα. Μια μέρα μπορεί να έχει
/// περισσότερα από ένα, π.χ. πρωί 09:00–14:00 και απόγευμα 17:30–21:00.
/// </summary>
public class OpeningHours
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClinicId { get; set; }

    public DayOfWeek Day { get; set; }
    public TimeOnly Opens { get; set; }
    public TimeOnly Closes { get; set; }
}
