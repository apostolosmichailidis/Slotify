namespace Slotify.Voice.Agents;

/// <summary>
/// Πρώτη δοκιμή: ό,τι λες ακούγεται πίσω με μικρή καθυστέρηση.
/// Αν ακούς τη φωνή σου, τότε Twilio → server → Twilio δουλεύει και στις δύο κατευθύνσεις.
/// </summary>
public sealed class EchoAgent(ILogger<EchoAgent> logger) : IVoiceAgent
{
    private CallContext? _context;
    private long _bytes;

    public Task StartAsync(CallContext context, CancellationToken ct)
    {
        _context = context;
        logger.LogInformation("Echo για {Clinic}, κλήση {CallSid}", context.Clinic.Name, context.Call.CallSid);
        return Task.CompletedTask;
    }

    public ValueTask OnAudioAsync(ReadOnlyMemory<byte> mulaw, CancellationToken ct)
    {
        _bytes += mulaw.Length;
        return _context!.Output.SendAudioAsync(mulaw, ct);
    }

    public ValueTask OnDtmfAsync(string digit, CancellationToken ct)
    {
        logger.LogInformation("Πατήθηκε {Digit}", digit);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        // 8000 bytes μ-law = 1 δευτερόλεπτο ήχου.
        logger.LogInformation("Echo τέλος, ακούστηκαν {Seconds:F1} δευτερόλεπτα", _bytes / 8000.0);
        return ValueTask.CompletedTask;
    }
}
