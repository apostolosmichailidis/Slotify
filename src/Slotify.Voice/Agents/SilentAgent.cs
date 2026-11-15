namespace Slotify.Voice.Agents;

/// <summary>Μόνο ακούει. Χρήσιμο για να δεις στα logs ότι έρχεται ήχος, χωρίς να στέλνεις τίποτα πίσω.</summary>
public sealed class SilentAgent(ILogger<SilentAgent> logger) : IVoiceAgent
{
    private long _bytes;

    public Task StartAsync(CallContext context, CancellationToken ct)
    {
        logger.LogInformation("Silent για {Clinic}, κλήση {CallSid}", context.Clinic.Name, context.Call.CallSid);
        return Task.CompletedTask;
    }

    public ValueTask OnAudioAsync(ReadOnlyMemory<byte> mulaw, CancellationToken ct)
    {
        _bytes += mulaw.Length;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        logger.LogInformation("Silent τέλος, ήρθαν {Seconds:F1} δευτερόλεπτα ήχου", _bytes / 8000.0);
        return ValueTask.CompletedTask;
    }
}
