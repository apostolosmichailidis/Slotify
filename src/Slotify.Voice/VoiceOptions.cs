using System.ComponentModel.DataAnnotations;

namespace Slotify.Voice;

public enum AgentMode
{
    /// <summary>Η κανονική γραμματεία: Deepgram → Claude → ElevenLabs.</summary>
    Assistant,

    /// <summary>Παίζει πίσω ό,τι ακούει. Για την πρώτη δοκιμή ότι ο ήχος πάει και έρχεται.</summary>
    Echo,

    /// <summary>Μόνο ακούει και γράφει στο log.</summary>
    Silent,
}

public sealed class VoiceOptions
{
    public const string Section = "Voice";

    /// <summary>
    /// Η δημόσια διεύθυνση που βλέπει το Twilio, π.χ. "https://abcd.ngrok-free.app".
    /// Χρειάζεται για το WebSocket URL και για τον έλεγχο υπογραφής.
    /// </summary>
    [Required, Url]
    public string PublicBaseUrl { get; set; } = "";

    public AgentMode Mode { get; set; } = AgentMode.Echo;

    /// <summary>Μυστικό για το token του WebSocket. Αν λείπει, φτιάχνεται τυχαίο σε κάθε εκκίνηση.</summary>
    public string? StreamSecret { get; set; }

    /// <summary>Αν οριστεί, φτιάχνεται δοκιμαστικό ιατρείο σε αυτόν τον αριθμό (μόνο σε Development).</summary>
    public string? DemoClinicPhoneNumber { get; set; }

    /// <summary>Το Google Calendar του δοκιμαστικού ιατρείου (π.χ. το email σου ή ένα νέο ημερολόγιο).</summary>
    public string? DemoClinicCalendarId { get; set; }

    /// <summary>Μετά από τόση σιωπή του καλούντα (αφού μιλήσει η γραμματεία) ρωτάμε «Είστε ακόμα εκεί;».</summary>
    [Range(3, 120)]
    public int SilencePromptSeconds { get; set; } = 12;

    /// <summary>Αν μετά την ερώτηση συνεχίσει η σιωπή για τόσα δευτερόλεπτα, αντίο και κλείσιμο.</summary>
    [Range(3, 120)]
    public int SilenceHangUpSeconds { get; set; } = 10;

    /// <summary>Μέγιστη διάρκεια κλήσης. Μετά η γραμματεία κλείνει ευγενικά, για να μην καίγονται λεπτά.</summary>
    [Range(1, 60)]
    public int MaxCallMinutes { get; set; } = 10;
}

public sealed class TwilioOptions
{
    public const string Section = "Twilio";

    /// <summary>Η διεύθυνση του Twilio API. Αλλάζει μόνο για δοκιμές.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.twilio.com";

    /// <summary>Το Account SID (AC…). Χρειάζεται μόνο για τα SMS στους ασθενείς.</summary>
    public string? AccountSid { get; set; }

    /// <summary>Το Auth Token του λογαριασμού Twilio, για να ελέγχουμε ότι οι κλήσεις έρχονται όντως από εκεί.</summary>
    public string? AuthToken { get; set; }

    /// <summary>Μόνο για τοπικές δοκιμές με curl. Στην παραγωγή πρέπει να είναι true.</summary>
    public bool ValidateSignature { get; set; } = true;
}
