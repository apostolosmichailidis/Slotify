using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

namespace Slotify.Voice.Speech;

public interface ITextToSpeech
{
    /// <summary>Μετατρέπει κείμενο σε ήχο μ-law 8 kHz mono και τον επιστρέφει σε κομμάτια, όσο παράγεται.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, CancellationToken ct);
}

public sealed class ElevenLabsOptions
{
    public const string Section = "ElevenLabs";

    [Required]
    public string ApiKey { get; set; } = "";

    /// <summary>Το Voice ID από τη βιβλιοθήκη φωνών του ElevenLabs.</summary>
    [Required]
    public string VoiceId { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.elevenlabs.io";

    /// <summary>Το Flash είναι το γρηγορότερο μοντέλο, κατάλληλο για τηλέφωνο.</summary>
    public string ModelId { get; set; } = "eleven_flash_v2_5";

    public string LanguageCode { get; set; } = "el";

    public double Stability { get; set; } = 0.5;
    public double SimilarityBoost { get; set; } = 0.75;
    public double Speed { get; set; } = 1.0;
}

/// <summary>
/// Φωνή από το ElevenLabs σε ροή. Ζητάμε απευθείας ulaw_8000, που είναι η μορφή του Twilio,
/// οπότε δεν χρειάζεται καμία μετατροπή.
/// https://elevenlabs.io/docs/api-reference/text-to-speech/stream
/// </summary>
public sealed class ElevenLabsTextToSpeech(HttpClient http, IOptions<ElevenLabsOptions> options, ILogger<ElevenLabsTextToSpeech> logger)
    : ITextToSpeech
{
    // 1600 bytes = 200 ms ήχου. Μικρά κομμάτια ώστε το "clear" να κόβει γρήγορα.
    private const int ChunkBytes = 1600;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var o = options.Value;
        var url = $"{o.BaseUrl.TrimEnd('/')}/v1/text-to-speech/{Uri.EscapeDataString(o.VoiceId)}/stream?output_format=ulaw_8000";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new
            {
                text,
                model_id = o.ModelId,
                language_code = o.LanguageCode,
                voice_settings = new { stability = o.Stability, similarity_boost = o.SimilarityBoost, speed = o.Speed },
            }),
        };
        request.Headers.Add("xi-api-key", o.ApiKey);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("ElevenLabs {Status}: {Body}", (int)response.StatusCode, body);
            throw new HttpRequestException($"ElevenLabs απάντησε {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[ChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            // Νέο array κάθε φορά: ο καλών μπορεί να κρατήσει το κομμάτι μετά το επόμενο read.
            yield return buffer.AsMemory(0, read).ToArray();
        }
    }
}
