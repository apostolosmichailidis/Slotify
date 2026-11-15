using System.Text.Json.Nodes;

namespace Slotify.Voice.Llm;

/// <summary>Ένα μήνυμα για το ιατρείο: ποιος πήρε, σε ποιο τηλέφωνο θέλει να τον καλέσουν και τι θέλει.</summary>
public sealed record CallMessage(string PatientName, string Phone, string Request)
{
    public string Summary => $"Μήνυμα: {PatientName}, {Phone}. {Request}";
}

/// <summary>
/// Το εργαλείο take_message. Η γραμματεία το χρησιμοποιεί όταν ο ασθενής θέλει κάτι που δεν μπορεί
/// να κάνει μόνη της (ραντεβού χωρίς ημερολόγιο, ακύρωση, να μιλήσει με τον γιατρό). Το μήνυμα γίνεται
/// η περίληψη της κλήσης και πάει στο email του ιατρείου.
/// </summary>
public static class MessageTool
{
    public const string Name = "take_message";

    public static readonly LlmTool Definition = new(
        Name,
        "Καταγράφει μήνυμα για το ιατρείο, που θα καλέσει τον ασθενή. Χρησιμοποίησέ το μία φορά, αφού μάθεις όνομα " +
        "και τι ακριβώς θέλει, και πριν πεις ότι θα τον καλέσουν. Αν μάθεις κάτι ακόμα, κάλεσέ το ξανά με όλα τα στοιχεία.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["patient_name"] = new JsonObject { ["type"] = "string", ["description"] = "Ονοματεπώνυμο." },
                ["phone"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Τηλέφωνο για να τον καλέσουν, αν δεν είναι ο αριθμός από τον οποίο καλεί.",
                },
                ["request"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Τι θέλει, σε μία-δύο προτάσεις για τον γιατρό. Π.χ. «Ραντεβού για καθαρισμό, " +
                                      "τον βολεύει Τρίτη ή Πέμπτη πρωί.» ή «Ακύρωση του ραντεβού της Παρασκευής στις 11:00.»",
                },
            },
            ["required"] = new JsonArray("patient_name", "request"),
        });

    public static (CallMessage? Message, string Result) Parse(JsonObject input, string? callerNumber)
    {
        var name = input["patient_name"]?.GetValue<string>()?.Trim();
        var request = input["request"]?.GetValue<string>()?.Trim();
        var phone = input["phone"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(phone)) phone = callerNumber;

        if (string.IsNullOrEmpty(name)) return (null, "Λείπει το όνομα. Ρώτα το και ξαναδοκίμασε.");
        if (string.IsNullOrEmpty(request)) return (null, "Λείπει τι θέλει ο ασθενής.");
        if (string.IsNullOrWhiteSpace(phone)) return (null, "Ο αριθμός του καλούντα είναι κρυφός. Ζήτα τηλέφωνο επικοινωνίας.");

        return (new CallMessage(name, phone, request),
            "Το μήνυμα καταγράφηκε και θα το δει το ιατρείο. Πες στον ασθενή ότι θα τον καλέσουν.");
    }
}
