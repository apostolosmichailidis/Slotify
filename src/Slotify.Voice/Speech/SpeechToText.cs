using System.Threading.Channels;

namespace Slotify.Voice.Speech;

/// <summary>Ό,τι μας λέει η υπηρεσία αναγνώρισης φωνής.</summary>
public abstract record SttEvent;

/// <summary>
/// Κείμενο που αναγνωρίστηκε.
/// IsFinal: αυτό το κομμάτι δεν θα αλλάξει άλλο.
/// SpeechFinal: ο καλών έκανε παύση, πιθανότατα τελείωσε η φράση του.
/// </summary>
public sealed record SttTranscript(string Text, bool IsFinal, bool SpeechFinal) : SttEvent;

/// <summary>Πέρασε αρκετή σιωπή μετά την τελευταία λέξη. Δίχτυ ασφαλείας αν δεν ήρθε SpeechFinal.</summary>
public sealed record SttUtteranceEnd : SttEvent;

/// <summary>Η υπηρεσία δεν είναι διαθέσιμη άλλο (έπεσε η σύνδεση).</summary>
public sealed record SttClosed(string? Reason) : SttEvent;

public interface ISpeechToText
{
    Task<ISttSession> StartAsync(CancellationToken ct);
}

/// <summary>Μία συνεδρία αναγνώρισης, όσο κρατάει η κλήση.</summary>
public interface ISttSession : IAsyncDisposable
{
    /// <summary>Ήχος μ-law 8 kHz mono.</summary>
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> mulaw, CancellationToken ct);

    ChannelReader<SttEvent> Events { get; }
}
