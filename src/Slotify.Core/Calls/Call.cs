namespace Slotify.Core.Calls;

/// <summary>Μια κλήση που σήκωσε η γραμματεία. Κρατιέται για ιστορικό και για χρέωση.</summary>
public class Call
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClinicId { get; set; }

    /// <summary>Το CallSid του Twilio. Μοναδικό.</summary>
    public required string CallSid { get; set; }
    public string? StreamSid { get; set; }

    public string? FromNumber { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }

    public CallOutcome Outcome { get; set; } = CallOutcome.InProgress;

    /// <summary>Σύντομη περίληψη για τον γιατρό.</summary>
    public string? Summary { get; set; }

    /// <summary>Η απομαγνητοφώνηση της κλήσης.</summary>
    public string? Transcript { get; set; }

    public double? DurationSeconds =>
        EndedAt is null ? null : (EndedAt.Value - StartedAt).TotalSeconds;
}

public enum CallOutcome
{
    InProgress = 0,
    AppointmentBooked = 1,
    MessageTaken = 2,
    InformationOnly = 3,
    TransferredToHuman = 4,
    Abandoned = 5,
    Failed = 6,
    Completed = 7,
}
