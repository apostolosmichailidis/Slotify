using Microsoft.EntityFrameworkCore;
using Slotify.Core.Calls;
using Slotify.Core.Notifications;
using Slotify.Data;

namespace Slotify.Admin.Services;

public sealed record CallRow(
    Guid Id, Guid ClinicId, string ClinicName, string? FromNumber,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, CallOutcome Outcome, string? Summary)
{
    public double? DurationSeconds => EndedAt is null ? null : (EndedAt.Value - StartedAt).TotalSeconds;
}

public sealed record ClinicMonth(Guid ClinicId, string ClinicName, int Calls, int Booked, int Failed, int BilledMinutes, int Sms, int SmsSegments);

public sealed record Dashboard(
    int CallsToday, int BookedToday,
    int Calls7Days, int Booked7Days,
    int CallsMonth, int BilledMinutesMonth, int FailedMonth,
    int InProgress,
    int SmsMonth, int SmsSegmentsMonth, int FailedNotifications,
    IReadOnlyList<ClinicMonth> PerClinic,
    IReadOnlyList<CallRow> Recent);

public sealed class CallFilter
{
    public Guid? ClinicId { get; set; }
    public CallOutcome? Outcome { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }

    /// <summary>Κομμάτι του τηλεφώνου του καλούντα.</summary>
    public string? Phone { get; set; }
}

public sealed record CallPage(IReadOnlyList<CallRow> Rows, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

public sealed record CallInfo(CallRow Row, string CallSid, string? Transcript, IReadOnlyList<Notification> Notifications);

public sealed class DashboardQueries(IDbContextFactory<SlotifyDbContext> dbFactory, TimeProvider time)
{
    public async Task<Dashboard> LoadAsync(CancellationToken ct = default)
    {
        var today = Display.TodayInAthens(time);
        var todayStart = Display.StartOfDayUtc(today);
        var weekStart = Display.StartOfDayUtc(today.AddDays(-6));
        var monthStart = Display.StartOfDayUtc(new DateOnly(today.Year, today.Month, 1));
        var since = weekStart < monthStart ? weekStart : monthStart;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Λίγες εκατοντάδες κλήσεις τον μήνα ανά ιατρείο: τις φέρνουμε και μετράμε εδώ,
        // που είναι πιο απλό από διάρκειες σε SQL.
        var calls = await db.Calls
            .AsNoTracking()
            .Where(c => c.StartedAt >= since)
            .Select(c => new { c.ClinicId, c.StartedAt, c.EndedAt, c.Outcome })
            .ToListAsync(ct);

        var names = await db.Clinics.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        // Τα SMS χρεώνονται ανά κομμάτι, οπότε μετράμε και τα κομμάτια. Μόνο όσα στάλθηκαν
        // και μόνο από κλήσεις: τα δοκιμαστικά του διαχειριστικού δεν χρεώνονται στο ιατρείο.
        var sms = await db.Notifications
            .Where(n => n.Channel == NotificationChannel.Sms && n.SentAt != null && n.SentAt >= monthStart && n.CallId != null)
            .GroupBy(n => n.ClinicId)
            .Select(g => new { ClinicId = g.Key, Count = g.Count(), Segments = g.Sum(n => n.Segments ?? 1) })
            .ToDictionaryAsync(x => x.ClinicId, ct);

        var failedNotifications = await db.Notifications
            .CountAsync(n => n.SentAt == null && n.Attempts >= Notification.MaxAttempts && n.CreatedAt >= weekStart, ct);

        var month = calls.Where(c => c.StartedAt >= monthStart).ToList();
        var week = calls.Where(c => c.StartedAt >= weekStart).ToList();
        var day = calls.Where(c => c.StartedAt >= todayStart).ToList();

        var perClinic = month
            .Select(c => c.ClinicId)
            .Concat(sms.Keys)
            .Distinct()
            .Select(id =>
            {
                var g = month.Where(c => c.ClinicId == id).ToList();
                var s = sms.GetValueOrDefault(id);
                return new ClinicMonth(
                    id,
                    names.GetValueOrDefault(id, "(διαγραμμένο)"),
                    g.Count,
                    g.Count(c => c.Outcome == CallOutcome.AppointmentBooked),
                    g.Count(c => c.Outcome == CallOutcome.Failed),
                    g.Where(c => c.EndedAt is not null).Sum(c => Display.BilledMinutes((c.EndedAt!.Value - c.StartedAt).TotalSeconds)),
                    s?.Count ?? 0,
                    s?.Segments ?? 0);
            })
            .OrderByDescending(x => x.Calls)
            .ToList();

        return new Dashboard(
            CallsToday: day.Count,
            BookedToday: day.Count(c => c.Outcome == CallOutcome.AppointmentBooked),
            Calls7Days: week.Count,
            Booked7Days: week.Count(c => c.Outcome == CallOutcome.AppointmentBooked),
            CallsMonth: month.Count,
            BilledMinutesMonth: perClinic.Sum(x => x.BilledMinutes),
            FailedMonth: month.Count(c => c.Outcome == CallOutcome.Failed),
            InProgress: day.Count(c => c.Outcome == CallOutcome.InProgress),
            SmsMonth: perClinic.Sum(x => x.Sms),
            SmsSegmentsMonth: perClinic.Sum(x => x.SmsSegments),
            FailedNotifications: failedNotifications,
            PerClinic: perClinic,
            Recent: (await SearchAsync(new CallFilter(), 1, 8, ct)).Rows);
    }

    public async Task<CallPage> SearchAsync(CallFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var q = db.Calls.AsNoTracking().AsQueryable();
        if (filter.ClinicId is { } clinicId) q = q.Where(c => c.ClinicId == clinicId);
        if (filter.Outcome is { } outcome) q = q.Where(c => c.Outcome == outcome);
        if (filter.From is { } from)
        {
            var start = Display.StartOfDayUtc(from);
            q = q.Where(c => c.StartedAt >= start);
        }
        if (filter.To is { } to)
        {
            var end = Display.StartOfDayUtc(to.AddDays(1));
            q = q.Where(c => c.StartedAt < end);
        }
        if (!string.IsNullOrWhiteSpace(filter.Phone))
        {
            var digits = new string(filter.Phone.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length > 0) q = q.Where(c => c.FromNumber != null && c.FromNumber.Contains(digits));
        }

        var total = await q.CountAsync(ct);
        var rows = await Project(db, q.OrderByDescending(c => c.StartedAt).Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(ct);
        rows = rows.OrderByDescending(r => r.StartedAt).ToList();

        return new CallPage(rows, total, page, pageSize);
    }

    public async Task<CallInfo?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var call = await db.Calls.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (call is null) return null;

        var clinicName = await db.Clinics.Where(c => c.Id == call.ClinicId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var row = new CallRow(call.Id, call.ClinicId, clinicName ?? "(διαγραμμένο)", call.FromNumber,
            call.StartedAt, call.EndedAt, call.Outcome, call.Summary);
        var notifications = await db.Notifications.AsNoTracking()
            .Where(n => n.CallId == id)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(ct);
        return new CallInfo(row, call.CallSid, call.Transcript, notifications);
    }

    private static IQueryable<CallRow> Project(SlotifyDbContext db, IQueryable<Call> q) =>
        from c in q
        join k in db.Clinics on c.ClinicId equals k.Id
        select new CallRow(c.Id, c.ClinicId, k.Name, c.FromNumber, c.StartedAt, c.EndedAt, c.Outcome, c.Summary);
}
