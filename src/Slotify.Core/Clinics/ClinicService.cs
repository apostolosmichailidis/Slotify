namespace Slotify.Core.Clinics;

/// <summary>Μια υπηρεσία του ιατρείου, π.χ. "Καθαρισμός" 45 λεπτά.</summary>
public class ClinicService
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClinicId { get; set; }

    public required string Name { get; set; }
    public int DurationMinutes { get; set; } = 30;

    /// <summary>Σύντομη περιγραφή για να καταλαβαίνει το AI πότε ταιριάζει.</summary>
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
}
