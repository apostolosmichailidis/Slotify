namespace Slotify.Core.Clinics;

/// <summary>Οι επίσημες αργίες στην Ελλάδα, μαζί με τις κινητές του Ορθόδοξου Πάσχα.</summary>
public static class GreekHolidays
{
    public static IReadOnlyList<(DateOnly Date, string Name)> For(int year)
    {
        var easter = OrthodoxEaster(year);
        return
        [
            (new DateOnly(year, 1, 1), "Πρωτοχρονιά"),
            (new DateOnly(year, 1, 6), "Θεοφάνια"),
            (easter.AddDays(-48), "Καθαρά Δευτέρα"),
            (new DateOnly(year, 3, 25), "25η Μαρτίου"),
            (easter.AddDays(-2), "Μεγάλη Παρασκευή"),
            (easter, "Κυριακή του Πάσχα"),
            (easter.AddDays(1), "Δευτέρα του Πάσχα"),
            (new DateOnly(year, 5, 1), "Πρωτομαγιά"),
            (easter.AddDays(50), "Αγίου Πνεύματος"),
            (new DateOnly(year, 8, 15), "Δεκαπενταύγουστος"),
            (new DateOnly(year, 10, 28), "28η Οκτωβρίου"),
            (new DateOnly(year, 12, 25), "Χριστούγεννα"),
            (new DateOnly(year, 12, 26), "Σύναξη της Θεοτόκου"),
        ];
    }

    /// <summary>Ορθόδοξο Πάσχα (αλγόριθμος Meeus για το Ιουλιανό ημερολόγιο, +13 μέρες για 1900–2099).</summary>
    public static DateOnly OrthodoxEaster(int year)
    {
        if (year is < 1900 or > 2099) throw new ArgumentOutOfRangeException(nameof(year));

        var a = year % 4;
        var b = year % 7;
        var c = year % 19;
        var d = (19 * c + 15) % 30;
        var e = (2 * a + 4 * b - d + 34) % 7;
        var month = (d + e + 114) / 31;
        var day = (d + e + 114) % 31 + 1;
        return new DateOnly(year, month, day).AddDays(13);
    }
}
