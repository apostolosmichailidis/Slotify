using System.Globalization;
using System.Text;

namespace Slotify.Voice.Scheduling;

/// <summary>Ημερομηνίες στα ελληνικά, ανεξάρτητα από το culture του server.</summary>
public static class GreekText
{
    public static readonly string[] Days =
        ["Κυριακή", "Δευτέρα", "Τρίτη", "Τετάρτη", "Πέμπτη", "Παρασκευή", "Σάββατο"];

    public static readonly string[] MonthsGenitive =
        ["Ιανουαρίου", "Φεβρουαρίου", "Μαρτίου", "Απριλίου", "Μαΐου", "Ιουνίου",
         "Ιουλίου", "Αυγούστου", "Σεπτεμβρίου", "Οκτωβρίου", "Νοεμβρίου", "Δεκεμβρίου"];

    /// <summary>"Τρίτη 17 Νοεμβρίου"</summary>
    public static string Date(DateTime local) => $"{Days[(int)local.DayOfWeek]} {local.Day} {MonthsGenitive[local.Month - 1]}";

    /// <summary>"Τρίτη 17 Νοεμβρίου στις 10:30"</summary>
    public static string DateTime(DateTime local) => $"{Date(local)} στις {local:HH:mm}";

    /// <summary>Για συγκρίσεις: πεζά, χωρίς τόνους και διαλυτικά ("Καθαρισμός" → "καθαρισμος").</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Replace('ς', 'σ').Trim();
    }
}
