namespace Slotify.Core.Clinics;

/// <summary>Μέρα που το ιατρείο είναι κλειστό (αργία, άδεια, συνέδριο).</summary>
public class ClinicClosure
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClinicId { get; set; }

    public DateOnly Date { get; set; }
    public string? Reason { get; set; }
}
