using Slotify.Core.Scheduling;

namespace Slotify.Voice.Scheduling;

/// <summary>Ένα ραντεβού για να γραφτεί στο ημερολόγιο του ιατρείου.</summary>
public sealed record CalendarEvent(
    DateTimeOffset Start,
    DateTimeOffset End,
    string TimeZoneId,
    string Summary,
    string Description,
    IReadOnlyDictionary<string, string>? PrivateProperties = null);

public sealed record CreatedEvent(string Id, string? HtmlLink);

/// <summary>Το ημερολόγιο ενός ιατρείου. Σήμερα Google Calendar, αύριο ίσως κι άλλα.</summary>
public interface ICalendar
{
    /// <summary>Τα πιασμένα διαστήματα στο [from, to).</summary>
    Task<IReadOnlyList<TimeRange>> GetBusyAsync(string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<CreatedEvent> CreateEventAsync(string calendarId, CalendarEvent ev, CancellationToken ct);
}

/// <summary>Το ημερολόγιο δεν βρέθηκε ή δεν έχει γίνει κοινή χρήση με τον λογαριασμό υπηρεσίας.</summary>
public sealed class CalendarAccessException(string message, Exception? inner = null) : Exception(message, inner);
