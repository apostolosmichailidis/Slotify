using Slotify.Core.Clinics;

namespace Slotify.Core.Scheduling;

/// <summary>Τι χρειάζεται ο υπολογισμός για να βρει ελεύθερες ώρες.</summary>
public sealed record AvailabilityRequest
{
    public required IReadOnlyCollection<OpeningHours> OpeningHours { get; init; }
    public IReadOnlyCollection<DateOnly> ClosedDates { get; init; } = [];

    /// <summary>Ό,τι είναι ήδη πιασμένο στο ημερολόγιο.</summary>
    public IReadOnlyCollection<TimeRange> Busy { get; init; } = [];

    public required TimeZoneInfo TimeZone { get; init; }
    public required int DurationMinutes { get; init; }

    /// <summary>Η τρέχουσα στιγμή. Δίνεται απ' έξω για να μπορεί να ελεγχθεί.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>Πρώτη και τελευταία μέρα αναζήτησης, σε τοπική ώρα του ιατρείου (και οι δύο μετράνε).</summary>
    public required DateOnly FromDate { get; init; }
    public required DateOnly ToDate { get; init; }

    public int StepMinutes { get; init; } = 15;
    public int MinNoticeMinutes { get; init; } = 60;
    public int MaxResults { get; init; } = 50;
}

/// <summary>
/// Βρίσκει ελεύθερες ώρες για ραντεβού. Δεν ξέρει τίποτα για βάση ή Google,
/// παίρνει μόνο δεδομένα και επιστρέφει αποτέλεσμα, οπότε ελέγχεται εύκολα.
/// </summary>
public static class AvailabilityCalculator
{
    public static IReadOnlyList<TimeRange> FindFreeSlots(AvailabilityRequest request)
    {
        if (request.DurationMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Η διάρκεια πρέπει να είναι θετική.");
        if (request.StepMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Το βήμα πρέπει να είναι θετικό.");

        var duration = TimeSpan.FromMinutes(request.DurationMinutes);
        var step = TimeSpan.FromMinutes(request.StepMinutes);
        var earliest = request.Now.AddMinutes(request.MinNoticeMinutes);
        var closed = request.ClosedDates.ToHashSet();
        var busy = request.Busy.OrderBy(b => b.Start).ToList();

        var hoursByDay = request.OpeningHours
            .Where(h => h.Closes > h.Opens)
            .GroupBy(h => h.Day)
            .ToDictionary(g => g.Key, g => g.OrderBy(h => h.Opens).ToList());

        var result = new List<TimeRange>();

        for (var date = request.FromDate; date <= request.ToDate; date = date.AddDays(1))
        {
            if (closed.Contains(date)) continue;
            if (!hoursByDay.TryGetValue(date.DayOfWeek, out var ranges)) continue;

            foreach (var range in ranges)
            {
                var localStart = date.ToDateTime(range.Opens);
                var localEnd = date.ToDateTime(range.Closes);

                for (var local = localStart; local + duration <= localEnd; local += step)
                {
                    // Ώρα που δεν υπάρχει λόγω αλλαγής θερινής ώρας.
                    if (request.TimeZone.IsInvalidTime(local)) continue;

                    var start = new DateTimeOffset(local, request.TimeZone.GetUtcOffset(local));
                    var slot = new TimeRange(start, start + duration);

                    if (slot.Start < earliest) continue;
                    if (busy.Any(b => b.Overlaps(slot))) continue;

                    result.Add(slot);
                    if (result.Count >= request.MaxResults) return result;
                }
            }
        }

        return result;
    }
}
