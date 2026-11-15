using System.Text;
using Slotify.Core.Clinics;

namespace Slotify.Voice.Llm;

/// <summary>Φτιάχνει τις οδηγίες (system prompt) της γραμματείας από τα στοιχεία του ιατρείου.</summary>
public sealed class PromptBuilder(TimeProvider time)
{
    private static readonly string[] Days =
        ["Κυριακή", "Δευτέρα", "Τρίτη", "Τετάρτη", "Πέμπτη", "Παρασκευή", "Σάββατο"];

    private static readonly string[] Months =
        ["Ιανουαρίου", "Φεβρουαρίου", "Μαρτίου", "Απριλίου", "Μαΐου", "Ιουνίου",
         "Ιουλίου", "Αυγούστου", "Σεπτεμβρίου", "Οκτωβρίου", "Νοεμβρίου", "Δεκεμβρίου"];

    public string Build(Clinic clinic, string greetingAlreadySpoken)
    {
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), clinic.TimeZone);
        var sb = new StringBuilder();

        sb.AppendLine($"""
            Είσαι η τηλεφωνική γραμματεία του ιατρείου «{clinic.Name}». Είσαι ψηφιακή βοηθός (AI) και, αν σε ρωτήσουν, το λες ευθέως.
            Μιλάς στο τηλέφωνο με ασθενείς, στα ελληνικά, ευγενικά και ζεστά, όπως μια καλή γραμματέας.

            Τώρα είναι {Days[(int)now.DayOfWeek]} {now.Day} {Months[now.Month - 1]} {now.Year}, ώρα {now:HH:mm}.

            Μόλις σήκωσες το τηλέφωνο και είπες: «{greetingAlreadySpoken}»

            ## Πώς μιλάς
            - Αυτό είναι τηλεφωνική συζήτηση: απαντάς σε 1–2 σύντομες προτάσεις και κάνεις μία ερώτηση τη φορά.
            - Ό,τι γράφεις ακούγεται από συνθέτη φωνής. Χωρίς λίστες, κουκκίδες, markdown, emoji ή συντομογραφίες.
            - Ώρες και ημερομηνίες τις λες όπως μιλάμε: «την Τρίτη στις δέκα και μισή», όχι «Τρ 10:30».
            - Το κείμενο του ασθενή έρχεται από αναγνώριση φωνής και μπορεί να έχει λάθη. Αν κάτι δεν βγαίνει νόημα, ζήτα ευγενικά να το επαναλάβει.
            - Ονόματα και τηλέφωνα τα επιβεβαιώνεις επαναλαμβάνοντάς τα.

            ## Όρια
            - Δεν δίνεις ιατρικές συμβουλές, διαγνώσεις ή οδηγίες για φάρμακα. Λες ότι θα το συζητήσει με τον γιατρό.
            - Αν ακούγεται επείγον (πόνος στο στήθος, δύσπνοια, αιμορραγία, λιποθυμία), λες αμέσως να καλέσει το 166 ή το 112.
            - Δεν επινοείς πληροφορίες. Αν δεν ξέρεις κάτι, λες ότι θα το μεταφέρεις στο ιατρείο.

            ## Τέλος κλήσης
            Όταν τελειώσει η δουλειά και ο ασθενής χαιρετήσει ή δεν χρειάζεται τίποτε άλλο, πες ένα σύντομο αντίο
            και μετά χρησιμοποίησε το εργαλείο end_call.
            """);

        sb.AppendLine("## Ωράριο");
        var byDay = clinic.OpeningHours.GroupBy(h => h.Day).ToDictionary(g => g.Key, g => g.OrderBy(h => h.Opens).ToList());
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
                                    DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            var hours = byDay.TryGetValue(day, out var list)
                ? string.Join(" και ", list.Select(h => $"{h.Opens:HH\\:mm}–{h.Closes:HH\\:mm}"))
                : "κλειστά";
            sb.AppendLine($"- {Days[(int)day]}: {hours}");
        }

        if (clinic.Closures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Κλειστά επίσης: " + string.Join(", ",
                clinic.Closures.OrderBy(c => c.Date).Select(c => $"{c.Date.Day} {Months[c.Date.Month - 1]}" +
                    (string.IsNullOrWhiteSpace(c.Reason) ? "" : $" ({c.Reason})"))));
        }

        if (clinic.Services.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Υπηρεσίες");
            foreach (var s in clinic.Services)
            {
                sb.Append($"- {s.Name}, {s.DurationMinutes} λεπτά");
                if (!string.IsNullOrWhiteSpace(s.Description)) sb.Append($": {s.Description}");
                sb.AppendLine();
            }
        }

        // Μέχρι να συνδεθεί το ημερολόγιο (επόμενο βήμα), η γραμματεία κρατάει αίτημα.
        sb.AppendLine("""

            ## Ραντεβού
            Προς το παρόν δεν βλέπεις το ημερολόγιο. Όταν κάποιος θέλει ραντεβού, ρωτάς για ποια υπηρεσία,
            ποια μέρα και ώρα τον βολεύει μέσα στο ωράριο, και το ονοματεπώνυμό του.
            Μετά λες ότι το ιατρείο θα τον καλέσει για να επιβεβαιώσει την ώρα.
            """);

        if (!string.IsNullOrWhiteSpace(clinic.KnowledgeBase))
        {
            sb.AppendLine();
            sb.AppendLine("## Πληροφορίες για το ιατρείο");
            sb.AppendLine(clinic.KnowledgeBase.Trim());
        }

        return sb.ToString();
    }
}
