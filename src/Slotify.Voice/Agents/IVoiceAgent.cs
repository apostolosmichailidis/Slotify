using Slotify.Core.Calls;
using Slotify.Core.Clinics;
using Slotify.Voice.Scheduling;
using Slotify.Voice.Twilio;

namespace Slotify.Voice.Agents;

/// <summary>Όλα όσα ξέρει ένας agent για την κλήση που χειρίζεται.</summary>
public sealed record CallContext(Clinic Clinic, Call Call, IAudioOutput Output);

/// <summary>
/// Ο «εγκέφαλος» μιας κλήσης. Παίρνει τον ήχο του καλούντα και αποφασίζει τι θα του πει.
/// Ένα instance ανά κλήση: Assistant (η κανονική γραμματεία), Echo και Silent για δοκιμές.
/// </summary>
public interface IVoiceAgent : IAsyncDisposable
{
    Task StartAsync(CallContext context, CancellationToken ct);

    /// <summary>Ένα κομμάτι ήχου μ-law 8 kHz από τον καλούντα (συνήθως 20 ms = 160 bytes).</summary>
    ValueTask OnAudioAsync(ReadOnlyMemory<byte> mulaw, CancellationToken ct);

    /// <summary>Πλήκτρο που πάτησε ο καλών.</summary>
    ValueTask OnDtmfAsync(string digit, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Το Twilio έπαιξε όλο τον ήχο μέχρι αυτό το σημάδι.</summary>
    ValueTask OnMarkAsync(string name, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Τελικό αποτέλεσμα για το ιστορικό, όταν κλείσει η κλήση.</summary>
    CallOutcome Outcome => CallOutcome.Completed;

    /// <summary>Η απομαγνητοφώνηση της κλήσης, αν υπάρχει.</summary>
    string? Transcript => null;

    /// <summary>Σύντομη περίληψη για τον γιατρό (π.χ. το ραντεβού που κλείστηκε).</summary>
    string? Summary => null;

    /// <summary>Το ραντεβού που κλείστηκε, αν κλείστηκε. Από αυτό φτιάχνεται το SMS στον ασθενή.</summary>
    Booking? Booking => null;
}

public sealed class VoiceAgentFactory(IServiceProvider services, Microsoft.Extensions.Options.IOptions<VoiceOptions> options)
{
    public IVoiceAgent Create() => options.Value.Mode switch
    {
        AgentMode.Assistant => ActivatorUtilities.CreateInstance<AssistantAgent>(services),
        AgentMode.Echo => ActivatorUtilities.CreateInstance<EchoAgent>(services),
        AgentMode.Silent => ActivatorUtilities.CreateInstance<SilentAgent>(services),
        _ => throw new InvalidOperationException($"Άγνωστο Voice:Mode {options.Value.Mode}"),
    };
}
