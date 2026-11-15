namespace Slotify.Core.Scheduling;

/// <summary>Ένα διάστημα χρόνου [Start, End). Π.χ. ένα ραντεβού που υπάρχει ήδη στο ημερολόγιο.</summary>
public readonly record struct TimeRange(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;
}
