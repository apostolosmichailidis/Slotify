using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Slotify.Core;
using Slotify.Core.Clinics;
using Slotify.Data;

namespace Slotify.Admin.Services;

public sealed record EditResult(bool Ok, string? Error = null, Guid? Id = null)
{
    public static EditResult Success(Guid? id = null) => new(true, Id: id);
    public static EditResult Fail(string error) => new(false, error);
}

public sealed record ClinicRow(
    Guid Id, string Name, string PhoneNumber, bool IsActive, bool HasCalendar,
    int ServiceCount, bool HasHours, int CallsThisMonth);

/// <summary>Τα στοιχεία του ιατρείου όπως τα γράφεις στη φόρμα.</summary>
public sealed class ClinicForm
{
    [Required(ErrorMessage = "Γράψε το όνομα του ιατρείου.")]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Γράψε τον αριθμό Twilio.")]
    public string PhoneNumber { get; set; } = "";

    [Required]
    public string TimeZoneId { get; set; } = "Europe/Athens";

    [MaxLength(256)]
    public string? GoogleCalendarId { get; set; }

    [MaxLength(1000)]
    public string? Greeting { get; set; }

    public string? KnowledgeBase { get; set; }

    [Range(0, 7 * 24 * 60, ErrorMessage = "Η προειδοποίηση πρέπει να είναι από 0 έως 10080 λεπτά.")]
    public int MinNoticeMinutes { get; set; } = 60;

    [Range(5, 120, ErrorMessage = "Το βήμα πρέπει να είναι από 5 έως 120 λεπτά.")]
    public int SlotStepMinutes { get; set; } = 15;

    [Range(1, 365, ErrorMessage = "Οι μέρες μπροστά πρέπει να είναι από 1 έως 365.")]
    public int BookingHorizonDays { get; set; } = 30;

    public bool IsActive { get; set; } = true;

    public static ClinicForm From(Clinic c) => new()
    {
        Name = c.Name,
        PhoneNumber = c.PhoneNumber,
        TimeZoneId = c.TimeZoneId,
        GoogleCalendarId = c.GoogleCalendarId,
        Greeting = c.Greeting,
        KnowledgeBase = c.KnowledgeBase,
        MinNoticeMinutes = c.MinNoticeMinutes,
        SlotStepMinutes = c.SlotStepMinutes,
        BookingHorizonDays = c.BookingHorizonDays,
        IsActive = c.IsActive,
    };
}

/// <summary>Ένα διάστημα ωραρίου στη φόρμα. Οι ώρες είναι nullable για να φαίνεται το κενό πεδίο.</summary>
public sealed class HoursRow
{
    public DayOfWeek Day { get; set; }
    public TimeOnly? Opens { get; set; }
    public TimeOnly? Closes { get; set; }
}

/// <summary>Ό,τι αλλάζει σε ένα ιατρείο από το διαχειριστικό. Κάθε κίνηση ανοίγει δικό της μικρό DbContext.</summary>
public sealed class ClinicEditor(IDbContextFactory<SlotifyDbContext> dbFactory, TimeProvider time)
{
    public async Task<List<ClinicRow>> ListAsync(CancellationToken ct = default)
    {
        var monthStart = Display.StartOfDayUtc(FirstOfMonth(Display.TodayInAthens(time)));
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var calls = await db.Calls
            .Where(c => c.StartedAt >= monthStart)
            .GroupBy(c => c.ClinicId)
            .Select(g => new { ClinicId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ClinicId, x => x.Count, ct);

        var clinics = await db.Clinics
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id, c.Name, c.PhoneNumber, c.IsActive, c.GoogleCalendarId,
                Services = c.Services.Count(s => s.IsActive),
                HasHours = c.OpeningHours.Any(),
            })
            .ToListAsync(ct);

        return clinics
            .Select(c => new ClinicRow(
                c.Id, c.Name, c.PhoneNumber, c.IsActive, !string.IsNullOrWhiteSpace(c.GoogleCalendarId),
                c.Services, c.HasHours, calls.GetValueOrDefault(c.Id)))
            .ToList();
    }

    /// <summary>Το ιατρείο με όλες τις υπηρεσίες (και τις ανενεργές) και τις κλειστές μέρες από σήμερα και μετά.</summary>
    public async Task<Clinic?> LoadAsync(Guid id, CancellationToken ct = default)
    {
        var today = Display.TodayInAthens(time);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Clinics
            .AsNoTracking()
            .AsSplitQuery()
            .Include(c => c.OpeningHours.OrderBy(h => h.Opens))
            .Include(c => c.Services.OrderBy(s => s.Name))
            .Include(c => c.Closures.Where(x => x.Date >= today).OrderBy(x => x.Date))
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<EditResult> SaveDetailsAsync(Guid? id, ClinicForm form, CancellationToken ct = default)
    {
        string phone;
        try
        {
            phone = PhoneNumbers.Normalize(form.PhoneNumber);
        }
        catch (ArgumentException)
        {
            return EditResult.Fail("Ο αριθμός δεν είναι σωστός.");
        }

        if (phone.Length is < 9 or > 16 || !phone[1..].All(char.IsAsciiDigit))
            return EditResult.Fail($"Ο αριθμός «{form.PhoneNumber}» δεν μοιάζει με τηλέφωνο. Γράψ' τον π.χ. +302310123456.");

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(form.TimeZoneId, out _))
            return EditResult.Fail($"Άγνωστη ζώνη ώρας «{form.TimeZoneId}».");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.Clinics.AnyAsync(c => c.PhoneNumber == phone && c.Id != id, ct))
            return EditResult.Fail($"Ο αριθμός {phone} ανήκει ήδη σε άλλο ιατρείο.");

        Clinic clinic;
        if (id is null)
        {
            clinic = new Clinic { Name = "", PhoneNumber = "" };
            db.Clinics.Add(clinic);
        }
        else
        {
            clinic = await db.Clinics.FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw new InvalidOperationException("Το ιατρείο δεν βρέθηκε.");
        }

        clinic.Name = form.Name.Trim();
        clinic.PhoneNumber = phone;
        clinic.TimeZoneId = form.TimeZoneId;
        clinic.GoogleCalendarId = Clean(form.GoogleCalendarId);
        clinic.Greeting = Clean(form.Greeting);
        clinic.KnowledgeBase = Clean(form.KnowledgeBase);
        clinic.MinNoticeMinutes = form.MinNoticeMinutes;
        clinic.SlotStepMinutes = form.SlotStepMinutes;
        clinic.BookingHorizonDays = form.BookingHorizonDays;
        clinic.IsActive = form.IsActive;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return EditResult.Fail($"Ο αριθμός {phone} ανήκει ήδη σε άλλο ιατρείο.");
        }

        form.PhoneNumber = phone;
        return EditResult.Success(clinic.Id);
    }

    /// <summary>Αντικαθιστά όλο το ωράριο. Κενές γραμμές αγνοούνται.</summary>
    public async Task<EditResult> SaveOpeningHoursAsync(Guid clinicId, IEnumerable<HoursRow> rows, CancellationToken ct = default)
    {
        var filled = rows.Where(r => r.Opens is not null || r.Closes is not null).ToList();

        foreach (var r in filled)
        {
            if (r.Opens is null || r.Closes is null)
                return EditResult.Fail($"{Display.DayName(r.Day)}: συμπλήρωσε και τις δύο ώρες ή σβήσε τη γραμμή.");
            if (r.Closes <= r.Opens)
                return EditResult.Fail($"{Display.DayName(r.Day)}: το κλείσιμο ({r.Closes:HH\\:mm}) πρέπει να είναι μετά το άνοιγμα ({r.Opens:HH\\:mm}).");
        }

        foreach (var day in filled.GroupBy(r => r.Day))
        {
            var ordered = day.OrderBy(r => r.Opens).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Opens < ordered[i - 1].Closes)
                    return EditResult.Fail($"{Display.DayName(day.Key)}: τα διαστήματα επικαλύπτονται.");
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.OpeningHours.Where(h => h.ClinicId == clinicId).ExecuteDeleteAsync(ct);
        db.OpeningHours.AddRange(filled.Select(r => new OpeningHours
        {
            ClinicId = clinicId,
            Day = r.Day,
            Opens = r.Opens!.Value,
            Closes = r.Closes!.Value,
        }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return EditResult.Success();
    }

    public async Task<EditResult> SaveServiceAsync(Guid clinicId, ClinicService input, CancellationToken ct = default)
    {
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return EditResult.Fail("Γράψε όνομα υπηρεσίας.");
        if (name.Length > 200) return EditResult.Fail("Το όνομα είναι πολύ μεγάλο.");
        if (input.DurationMinutes is < 5 or > 480) return EditResult.Fail("Η διάρκεια πρέπει να είναι από 5 έως 480 λεπτά.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.Services.AnyAsync(s => s.ClinicId == clinicId && s.Id != input.Id && s.Name.ToLower() == name.ToLower(), ct))
            return EditResult.Fail($"Υπάρχει ήδη υπηρεσία «{name}».");

        var service = await db.Services.FirstOrDefaultAsync(s => s.Id == input.Id && s.ClinicId == clinicId, ct);
        if (service is null)
        {
            service = new ClinicService { Name = name, ClinicId = clinicId };
            db.Services.Add(service);
        }

        service.Name = name;
        service.DurationMinutes = input.DurationMinutes;
        service.Description = Clean(input.Description);
        service.IsActive = input.IsActive;
        await db.SaveChangesAsync(ct);
        return EditResult.Success(service.Id);
    }

    public async Task DeleteServiceAsync(Guid clinicId, Guid serviceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Services.Where(s => s.Id == serviceId && s.ClinicId == clinicId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Κλείνει όλες τις μέρες από–έως. Όσες είναι ήδη κλειστές μένουν όπως είναι.</summary>
    public async Task<EditResult> AddClosuresAsync(Guid clinicId, DateOnly from, DateOnly to, string? reason, CancellationToken ct = default)
    {
        if (to < from) return EditResult.Fail("Η τελευταία μέρα είναι πριν από την πρώτη.");
        if (to.DayNumber - from.DayNumber > 366) return EditResult.Fail("Το διάστημα είναι πάνω από έναν χρόνο.");
        if (reason?.Length > 200) return EditResult.Fail("Η αιτία είναι πολύ μεγάλη.");

        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays);
        var added = await AddDaysAsync(clinicId, days.Select(d => (d, Clean(reason))), ct);
        return added == 0 ? EditResult.Fail("Όλες αυτές οι μέρες είναι ήδη κλειστές.") : EditResult.Success();
    }

    /// <summary>Προσθέτει τις επίσημες αργίες της χρονιάς που δεν έχουν περάσει.</summary>
    public async Task<int> AddGreekHolidaysAsync(Guid clinicId, int year, CancellationToken ct = default)
    {
        var today = Display.TodayInAthens(time);
        var holidays = GreekHolidays.For(year).Where(h => h.Date >= today).Select(h => (h.Date, (string?)h.Name));
        return await AddDaysAsync(clinicId, holidays, ct);
    }

    public async Task DeleteClosureAsync(Guid clinicId, Guid closureId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Closures.Where(x => x.Id == closureId && x.ClinicId == clinicId).ExecuteDeleteAsync(ct);
    }

    private async Task<int> AddDaysAsync(Guid clinicId, IEnumerable<(DateOnly Date, string? Reason)> days, CancellationToken ct)
    {
        var list = days.ToList();
        if (list.Count == 0) return 0;

        var first = list.Min(d => d.Date);
        var last = list.Max(d => d.Date);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = (await db.Closures
                .Where(x => x.ClinicId == clinicId && x.Date >= first && x.Date <= last)
                .Select(x => x.Date)
                .ToListAsync(ct))
            .ToHashSet();

        var fresh = list.Where(d => existing.Add(d.Date)).ToList();
        db.Closures.AddRange(fresh.Select(d => new ClinicClosure { ClinicId = clinicId, Date = d.Date, Reason = d.Reason }));
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }

    private static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
