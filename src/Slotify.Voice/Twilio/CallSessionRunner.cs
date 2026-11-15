using System.Net.WebSockets;
using Slotify.Core.Abstractions;
using Slotify.Core.Calls;
using Slotify.Voice.Agents;

namespace Slotify.Voice.Twilio;

/// <summary>
/// Τρέχει μία κλήση από την αρχή ως το τέλος: διαβάζει τα μηνύματα του Twilio,
/// ελέγχει το token, γράφει την κλήση στη βάση και δίνει τον ήχο στον agent.
/// </summary>
public sealed class CallSessionRunner(
    IClinicDirectory clinics,
    ICallLog callLog,
    StreamTokens tokens,
    VoiceAgentFactory agents,
    ILogger<CallSessionRunner> logger)
{
    public async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        var stream = new TwilioMediaStream(socket, logger);
        IVoiceAgent? agent = null;
        Call? call = null;
        var failed = false;

        try
        {
            await foreach (var message in stream.ReadAsync(ct))
            {
                switch (message.Event)
                {
                    case "connected":
                        break;

                    case "start" when message.Start is { } start && agent is null:
                    {
                        var parameters = start.CustomParameters ?? [];
                        if (!Guid.TryParse(parameters.GetValueOrDefault("clinicId"), out var clinicId)
                            || !tokens.Verify(start.CallSid, clinicId, parameters.GetValueOrDefault("token")))
                        {
                            logger.LogWarning("Stream με λάθος token για κλήση {CallSid}, κλείνω", start.CallSid);
                            return;
                        }

                        var clinic = await clinics.GetAsync(clinicId, ct);
                        if (clinic is null)
                        {
                            logger.LogWarning("Το ιατρείο {ClinicId} δεν υπάρχει πια", clinicId);
                            return;
                        }

                        stream.StreamSid = start.StreamSid;
                        call = new Call
                        {
                            ClinicId = clinic.Id,
                            CallSid = start.CallSid,
                            StreamSid = start.StreamSid,
                            FromNumber = parameters.GetValueOrDefault("from"),
                        };

                        logger.LogInformation("Νέα κλήση {CallSid} στο {Clinic} από {From}",
                            start.CallSid, clinic.Name, call.FromNumber);

                        await SafelyAsync(() => callLog.StartAsync(call, ct), "καταγραφή έναρξης");

                        agent = agents.Create();
                        await agent.StartAsync(new CallContext(clinic, call, stream), ct);
                        break;
                    }

                    case "media" when agent is not null && message.Media is { } media:
                        if (media.Track is null or "inbound")
                            await agent.OnAudioAsync(Convert.FromBase64String(media.Payload), ct);
                        break;

                    case "dtmf" when agent is not null && message.Dtmf is { } dtmf:
                        await agent.OnDtmfAsync(dtmf.Digit, ct);
                        break;

                    case "mark" when agent is not null && message.Mark is { } mark:
                        await agent.OnMarkAsync(mark.Name, ct);
                        break;

                    case "stop":
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ο καλών έκλεισε ή σταματά ο server.
        }
        catch (Exception ex)
        {
            failed = true;
            logger.LogError(ex, "Σφάλμα στην κλήση {CallSid}", call?.CallSid);
        }
        finally
        {
            if (agent is not null) await SafelyAsync(() => agent.DisposeAsync().AsTask(), "κλείσιμο agent");

            var outcome = failed ? CallOutcome.Failed : agent?.Outcome ?? CallOutcome.Abandoned;
            var transcript = agent?.Transcript;
            if (call is not null)
                await SafelyAsync(() => callLog.CompleteAsync(call.CallSid, outcome, transcript: transcript, ct: CancellationToken.None),
                    "καταγραφή τέλους");

            await stream.HangUpAsync(CancellationToken.None);
        }
    }

    /// <summary>Ένα πρόβλημα στη βάση δεν πρέπει να ρίξει την κλήση.</summary>
    private async Task SafelyAsync(Func<Task> action, string what)
    {
        try { await action(); }
        catch (Exception ex) { logger.LogError(ex, "Απέτυχε: {What}", what); }
    }
}
