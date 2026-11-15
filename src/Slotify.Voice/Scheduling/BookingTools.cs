using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Slotify.Core;
using Slotify.Core.Clinics;
using Slotify.Core.Scheduling;
using Slotify.Voice.Llm;

namespace Slotify.Voice.Scheduling;

public sealed record ToolResult(string Content, bool IsError = false);

public sealed record Booking(DateTime LocalStart, string Service, string PatientName, string Phone, string EventId);

/// <summary>
/// Τα εργαλεία ραντεβού που δίνουμε στο Claude για μία κλήση:
/// find_available_slots (ελεύθερες ώρες) και book_appointment (κλείσιμο).
/// Οι ώρες υπολογίζονται από τον δικό μας κώδικα (ωράριο + Google Calendar), ποτέ από το μοντέλο.
/// </summary>
public sealed class BookingTools(
    Clinic clinic,
    string? callerNumber,
    string callSid,
    ICalendar calendar,
    TimeProvider time,
    ILogger logger)
{
    private const int MaxSlotsShown = 6;
    private const int DefaultSearchDays = 7;

    /// <summary>
    /// Πρωί/απόγευμα όπως στα ελληνικά ιατρεία: πρωινή βάρδια ως τις 14:00, απογευματινή από τις 17:00.
    /// Το όριο στις 15:00 τις χωρίζει σωστά.
    /// </summary>
    private const int AfternoonFromHour = 15;

    /// <summary>Ελάχιστη απόσταση ανάμεσα σε ώρες που προτείνουμε την ίδια μέρα, για πραγματική επιλογή.</summary>
    private static readonly TimeSpan MinGapBetweenSuggestions = TimeSpan.FromMinutes(60);

    // Δύο ταυτόχρονες κλήσεις στο ίδιο ιατρείο δεν πρέπει να κλείσουν την ίδια ώρα.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CalendarLocks = new();

    private static readonly string[] StartFormats = ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm"];

    public Booking? LastBooking { get; private set; }

    public IReadOnlyList<LlmTool> Tools { get; } =
    [
        new("find_available_slots",
            "Βρίσκει ελεύθερες ώρες για ραντεβού στο ημερολόγιο του ιατρείου. Χρησιμοποίησέ το ΠΑΝΤΑ πριν προτείνεις ώρα.",
            Schema(
                ("service", "string", "Η υπηρεσία, όπως ονομάζεται στη λίστα υπηρεσιών.", true),
                ("from_date", "string", "Από ποια μέρα (YYYY-MM-DD). Αν λείπει, από σήμερα.", false),
                ("to_date", "string", "Μέχρι ποια μέρα (YYYY-MM-DD). Αν λείπει, μία εβδομάδα μετά.", false),
                ("part_of_day", "string", "\"morning\" (πρωινή βάρδια, πριν τις 15:00), \"afternoon\" (απογευματινή, από 15:00) ή \"any\".", false))),

        new("book_appointment",
            "Κλείνει ραντεβού. Μόνο σε ώρα που έδωσε το find_available_slots, και αφού ο ασθενής επιβεβαίωσε μέρα, ώρα και όνομα.",
            Schema(
                ("service", "string", "Η υπηρεσία.", true),
                ("start", "string", "Η ώρα έναρξης ακριβώς όπως την έδωσε το find_available_slots (YYYY-MM-DDTHH:MM).", true),
                ("patient_name", "string", "Ονοματεπώνυμο του ασθενή.", true),
                ("phone", "string", "Τηλέφωνο επικοινωνίας, αν δεν είναι ο αριθμός από τον οποίο καλεί.", false),
                ("notes", "string", "Σύντομη σημείωση για τον γιατρό (π.χ. πονάει δόντι κάτω αριστερά).", false))),
    ];

    public async Task<ToolResult?> TryExecuteAsync(LlmToolCall call, CancellationToken ct)
    {
        try
        {
            return call.Name switch
            {
                "find_available_slots" => await FindAsync(call.Input, ct),
                "book_appointment" => await BookAsync(call.Input, ct),
                _ => null,
            };
        }
        catch (CalendarAccessException ex)
        {
            logger.LogError(ex, "Πρόβλημα πρόσβασης στο ημερολόγιο του {Clinic}", clinic.Name);
            return new ToolResult(
                "Το ημερολόγιο δεν είναι διαθέσιμο αυτή τη στιγμή. Πάρε όνομα, υπηρεσία και ώρα που τον βολεύει, " +
                "και πες ότι το ιατρείο θα τον καλέσει για επιβεβαίωση.", IsError: true);
        }
    }

    // ---------- find_available_slots ----------

    private async Task<ToolResult> FindAsync(JsonObject input, CancellationToken ct)
    {
        if (ResolveService(Str(input, "service")) is not { } service)
            return UnknownService(Str(input, "service"));

        var today = Today;
        var from = ParseDate(Str(input, "from_date")) ?? today;
        if (from < today) from = today;
        var to = ParseDate(Str(input, "to_date")) ?? from.AddDays(DefaultSearchDays - 1);
        var horizon = today.AddDays(clinic.BookingHorizonDays);
        if (to > horizon) to = horizon;
        if (to < from) to = from;

        var slots = await ComputeSlotsAsync(service, from, to, ct);
        var part = Str(input, "part_of_day")?.ToLowerInvariant();
        var filtered = slots.Where(s => part switch
        {
            "morning" => Local(s.Start).Hour < AfternoonFromHour,
            "afternoon" => Local(s.Start).Hour >= AfternoonFromHour,
            _ => true,
        }).ToList();

        if (filtered.Count == 0)
        {
            return new ToolResult(
                $"Δεν υπάρχουν ελεύθερες ώρες για «{service.Name}» από {GreekText.Date(from.ToDateTime(TimeOnly.MinValue))} " +
                $"έως {GreekText.Date(to.ToDateTime(TimeOnly.MinValue))}" + (part is "morning" or "afternoon" ? " σε αυτό το μέρος της μέρας" : "") +
                ". Δοκίμασε άλλες μέρες ή άλλο μέρος της μέρας.");
        }

        var sb = new StringBuilder($"Ελεύθερες ώρες για «{service.Name}» ({service.DurationMinutes} λεπτά):\n");
        foreach (var slot in PickVaried(filtered))
        {
            var local = Local(slot.Start);
            sb.AppendLine($"- {GreekText.DateTime(local)} [start={local:yyyy-MM-ddTHH:mm}]");
        }
        if (filtered.Count > MaxSlotsShown)
            sb.AppendLine($"(Υπάρχουν κι άλλες, {filtered.Count} συνολικά. Αν καμία δεν βολεύει, ψάξε με άλλες ημερομηνίες.)");
        sb.Append("Πρότεινε 2 ή 3 από αυτές, όχι όλες.");
        return new ToolResult(sb.ToString());
    }

    /// <summary>
    /// Λίγες ώρες απλωμένες σε μέρες και μέσα στη μέρα (τουλάχιστον μία ώρα απόσταση),
    /// αντί για έξι συνεχόμενες το ίδιο πρωί ή μία μόνο ανά μέρα.
    /// </summary>
    private IEnumerable<TimeRange> PickVaried(List<TimeRange> slots)
    {
        var days = slots.GroupBy(s => Local(s.Start).Date).ToList();
        var perDay = Math.Max(2, (int)Math.Ceiling(MaxSlotsShown / (double)days.Count));
        var picked = new List<TimeRange>();

        foreach (var day in days)
        {
            var taken = 0;
            DateTimeOffset? last = null;
            foreach (var slot in day)
            {
                if (last is not null && slot.Start - last.Value < MinGapBetweenSuggestions) continue;
                picked.Add(slot);
                last = slot.Start;
                if (++taken >= perDay) break;
            }
            if (picked.Count >= MaxSlotsShown) break;
        }

        return picked.Take(MaxSlotsShown).OrderBy(s => s.Start);
    }

    // ---------- book_appointment ----------

    private async Task<ToolResult> BookAsync(JsonObject input, CancellationToken ct)
    {
        if (ResolveService(Str(input, "service")) is not { } service)
            return UnknownService(Str(input, "service"));

        var name = Str(input, "patient_name")?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length < 3)
            return new ToolResult("Λείπει το ονοματεπώνυμο. Ρώτα τον ασθενή.", IsError: true);

        if (!DateTime.TryParseExact(Str(input, "start"), StartFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localStart))
            return new ToolResult("Η ώρα δεν είναι σε μορφή YYYY-MM-DDTHH:MM. Χρησιμοποίησε ακριβώς την ώρα από το find_available_slots.", IsError: true);

        var phone = Str(input, "phone") is { Length: > 0 } p ? SafeNormalize(p) : callerNumber;
        if (string.IsNullOrWhiteSpace(phone))
            return new ToolResult("Δεν έχουμε τηλέφωνο επικοινωνίας. Ρώτα τον ασθενή.", IsError: true);

        var gate = CalendarLocks.GetOrAdd(clinic.GoogleCalendarId!, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Ξαναελέγχουμε τη στιγμή του κλεισίματος: μπορεί να την πήρε κάποιος άλλος στο μεταξύ.
            var date = DateOnly.FromDateTime(localStart);
            var slots = await ComputeSlotsAsync(service, date, date, ct);
            var slot = slots.FirstOrDefault(s => Local(s.Start) == localStart);
            if (slot == default)
            {
                var alternatives = slots.Take(3).Select(s => $"{Local(s.Start):HH:mm} [start={Local(s.Start):yyyy-MM-ddTHH:mm}]");
                return new ToolResult(
                    $"Η ώρα {GreekText.DateTime(localStart)} δεν είναι διαθέσιμη. " +
                    (slots.Count > 0 ? $"Ελεύθερες την ίδια μέρα: {string.Join(", ", alternatives)}." : "Εκείνη τη μέρα δεν υπάρχει άλλη ελεύθερη ώρα."),
                    IsError: true);
            }

            var notes = Str(input, "notes");
            var description = new StringBuilder()
                .AppendLine($"Ασθενής: {name}")
                .AppendLine($"Τηλέφωνο: {phone}")
                .AppendLine($"Υπηρεσία: {service.Name}");
            if (!string.IsNullOrWhiteSpace(notes)) description.AppendLine($"Σημείωση: {notes}");
            description.Append("Κλείστηκε τηλεφωνικά από την ψηφιακή γραμματεία Slotify.");

            var created = await calendar.CreateEventAsync(clinic.GoogleCalendarId!, new CalendarEvent(
                slot.Start, slot.End, clinic.TimeZoneId,
                $"{service.Name} – {name}",
                description.ToString(),
                new Dictionary<string, string> { ["slotifyCallSid"] = callSid, ["slotifyPhone"] = phone }), ct);

            LastBooking = new Booking(localStart, service.Name, name, phone, created.Id);
            logger.LogInformation("Ραντεβού {Clinic}: {When} {Service} για {Name}", clinic.Name, localStart, service.Name, name);

            return new ToolResult(
                $"Το ραντεβού κλείστηκε: {service.Name}, {GreekText.DateTime(localStart)}, στο όνομα {name}, τηλέφωνο {phone}. " +
                "Επιβεβαίωσέ το σύντομα στον ασθενή.");
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------- Κοινά ----------

    private async Task<IReadOnlyList<TimeRange>> ComputeSlotsAsync(ClinicService service, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var tz = clinic.TimeZone;
        var rangeStart = ToOffset(from.ToDateTime(TimeOnly.MinValue), tz);
        var rangeEnd = ToOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
        var busy = await calendar.GetBusyAsync(clinic.GoogleCalendarId!, rangeStart, rangeEnd, ct);

        return AvailabilityCalculator.FindFreeSlots(new AvailabilityRequest
        {
            OpeningHours = clinic.OpeningHours,
            ClosedDates = clinic.Closures.Select(c => c.Date).ToList(),
            Busy = busy,
            TimeZone = tz,
            DurationMinutes = service.DurationMinutes,
            Now = time.GetUtcNow(),
            FromDate = from,
            ToDate = to,
            StepMinutes = clinic.SlotStepMinutes,
            MinNoticeMinutes = clinic.MinNoticeMinutes,
            MaxResults = 500,
        });
    }

    private ClinicService? ResolveService(string? requested)
    {
        var services = clinic.Services.Where(s => s.IsActive).ToList();
        if (string.IsNullOrWhiteSpace(requested)) return services.Count == 1 ? services[0] : null;

        var wanted = GreekText.Fold(requested);
        return services.FirstOrDefault(s => GreekText.Fold(s.Name) == wanted)
            ?? services.FirstOrDefault(s => GreekText.Fold(s.Name).Contains(wanted) || wanted.Contains(GreekText.Fold(s.Name)));
    }

    private ToolResult UnknownService(string? requested) =>
        new($"Δεν υπάρχει υπηρεσία «{requested}». Οι υπηρεσίες είναι: " +
            string.Join(", ", clinic.Services.Where(s => s.IsActive).Select(s => s.Name)) + ". Ρώτα τον ασθενή ποια θέλει.",
            IsError: true);

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), clinic.TimeZone).DateTime);

    private DateTime Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, clinic.TimeZone).DateTime;

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo tz) => new(local, tz.GetUtcOffset(local));

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string? Str(JsonObject input, string key) =>
        input[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string SafeNormalize(string phone)
    {
        try { return PhoneNumbers.Normalize(phone); }
        catch (ArgumentException) { return phone; }
    }

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props)
            properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(props.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
        };
    }
}
