using System.Globalization;
using Slotify.Core.Calls;

namespace Slotify.Admin.Services;

/// <summary>Πώς δείχνουμε ώρες, διάρκειες και αποτελέσματα στα ελληνικά.</summary>
public static class Display
{
    public static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
    public static readonly CultureInfo Greek = CultureInfo.GetCultureInfo("el-GR");

    /// <summary>Δευτέρα πρώτη, όπως στην Ελλάδα.</summary>
    public static readonly DayOfWeek[] Week =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    public static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Δευτέρα",
        DayOfWeek.Tuesday => "Τρίτη",
        DayOfWeek.Wednesday => "Τετάρτη",
        DayOfWeek.Thursday => "Πέμπτη",
        DayOfWeek.Friday => "Παρασκευή",
        DayOfWeek.Saturday => "Σάββατο",
        _ => "Κυριακή",
    };

    public static DateTimeOffset ToAthens(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Athens);

    public static DateOnly TodayInAthens(TimeProvider time) => DateOnly.FromDateTime(ToAthens(time.GetUtcNow()).DateTime);

    /// <summary>Τα μεσάνυχτα μιας τοπικής ημερομηνίας, σε UTC (για ερωτήματα στη βάση).</summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, Athens.GetUtcOffset(local)).ToUniversalTime();
    }

    public static string DateTime(DateTimeOffset t) => ToAthens(t).ToString("ddd d MMM yyyy, HH:mm", Greek);

    public static string Time(DateTimeOffset t) => ToAthens(t).ToString("HH:mm", Greek);

    public static string Date(DateOnly d) => d.ToString("ddd d MMMM yyyy", Greek);

    public static string Duration(double? seconds)
    {
        if (seconds is null) return "—";
        var s = (int)Math.Round(seconds.Value);
        return s < 60 ? $"{s}″" : $"{s / 60}′ {s % 60:00}″";
    }

    /// <summary>Το Twilio χρεώνει κάθε κλήση στρογγυλεμένη προς τα πάνω στο λεπτό.</summary>
    public static int BilledMinutes(double seconds) => (int)Math.Ceiling(seconds / 60.0);

    public static string Outcome(CallOutcome o) => o switch
    {
        CallOutcome.InProgress => "Σε εξέλιξη",
        CallOutcome.AppointmentBooked => "Ραντεβού",
        CallOutcome.MessageTaken => "Μήνυμα",
        CallOutcome.InformationOnly => "Πληροφορίες",
        CallOutcome.TransferredToHuman => "Μεταφορά",
        CallOutcome.Abandoned => "Έκλεισε",
        CallOutcome.Failed => "Σφάλμα",
        CallOutcome.Completed => "Ολοκληρώθηκε",
        _ => o.ToString(),
    };

    /// <summary>Η κλάση CSS του σήματος για κάθε αποτέλεσμα.</summary>
    public static string OutcomeTone(CallOutcome o) => o switch
    {
        CallOutcome.AppointmentBooked => "good",
        CallOutcome.MessageTaken => "info",
        CallOutcome.Failed => "bad",
        CallOutcome.InProgress => "live",
        CallOutcome.Abandoned => "muted",
        _ => "neutral",
    };
}
