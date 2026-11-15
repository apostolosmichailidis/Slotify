using Slotify.Core.Calls;
using Slotify.Core.Clinics;
using Slotify.Voice.Twilio;

namespace Slotify.Voice.Agents;

/// <summary>Όλα όσα ξέρει ένας agent για την κλήση που χειρίζεται.</summary>
public sealed record CallContext(Clinic Clinic, Call Call, IAudioOutput Output);

/// <summary>
/// Ο «εγκέφαλος» μιας κλήσης. Παίρνει τον ήχο του καλούντα και αποφασίζει τι θα του πει.
/// Ένα instance ανά κλήση. Τώρα έχουμε Echo και Silent. Επόμενο βήμα ο κανονικός
/// agent: Deepgram → Claude → φωνή.
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
}

public sealed class VoiceAgentFactory(IServiceProvider services, Microsoft.Extensions.Options.IOptions<VoiceOptions> options)
{
    public IVoiceAgent Create() => options.Value.Mode switch
    {
        AgentMode.Echo => ActivatorUtilities.CreateInstance<EchoAgent>(services),
        AgentMode.Silent => ActivatorUtilities.CreateInstance<SilentAgent>(services),
        _ => throw new InvalidOperationException($"Άγνωστο Voice:Mode {options.Value.Mode}"),
    };
}
