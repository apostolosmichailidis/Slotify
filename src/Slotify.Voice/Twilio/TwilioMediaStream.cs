using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Slotify.Voice.Twilio;

/// <summary>Ό,τι χρειάζεται ένας agent για να μιλήσει στον καλούντα.</summary>
public interface IAudioOutput
{
    /// <summary>Στέλνει ήχο μ-law 8 kHz mono προς τον καλούντα.</summary>
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> mulaw, CancellationToken ct = default);

    /// <summary>Σημάδι: το Twilio το επιστρέφει όταν παίξει όλος ο ήχος που στάλθηκε πριν από αυτό.</summary>
    ValueTask SendMarkAsync(string name, CancellationToken ct = default);

    /// <summary>Σταματά αμέσως ό,τι παίζει (όταν ο καλών μιλήσει από πάνω).</summary>
    ValueTask ClearAsync(CancellationToken ct = default);

    /// <summary>Κλείνει το stream. Αφού δεν ακολουθεί άλλο TwiML, κλείνει και η κλήση.</summary>
    Task HangUpAsync(CancellationToken ct = default);
}

/// <summary>Τυλίγει το WebSocket του Twilio: διαβάζει μηνύματα και στέλνει ήχο πίσω.</summary>
public sealed class TwilioMediaStream(WebSocket socket, ILogger logger) : IAudioOutput
{
    private const int MaxMessageBytes = 1 << 20;

    // Το WebSocket δεν επιτρέπει δύο ταυτόχρονες αποστολές.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string? StreamSid { get; set; }

    public async IAsyncEnumerable<TwilioInbound> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            buffer.ResetWrittenCount();
            ValueWebSocketReceiveResult result;
            do
            {
                var memory = buffer.GetMemory(4096);
                try
                {
                    result = await socket.ReceiveAsync(memory, ct);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                    logger.LogDebug(ex, "Το WebSocket έκλεισε");
                    yield break;
                }

                if (result.MessageType == WebSocketMessageType.Close) yield break;

                buffer.Advance(result.Count);
                if (buffer.WrittenCount > MaxMessageBytes)
                {
                    logger.LogWarning("Πολύ μεγάλο μήνυμα από το WebSocket, κλείνω");
                    yield break;
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            TwilioInbound? message;
            try
            {
                message = JsonSerializer.Deserialize<TwilioInbound>(buffer.WrittenSpan, TwilioJson.Options);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Μήνυμα από Twilio που δεν διαβάζεται");
                continue;
            }

            if (message is not null) yield return message;
        }
    }

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> mulaw, CancellationToken ct = default) =>
        SendAsync(new { @event = "media", streamSid = StreamSid, media = new { payload = Convert.ToBase64String(mulaw.Span) } }, ct);

    public ValueTask SendMarkAsync(string name, CancellationToken ct = default) =>
        SendAsync(new { @event = "mark", streamSid = StreamSid, mark = new { name } }, ct);

    public ValueTask ClearAsync(CancellationToken ct = default) =>
        SendAsync(new { @event = "clear", streamSid = StreamSid }, ct);

    public async Task HangUpAsync(CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
        }
        catch (WebSocketException) { }
        finally { _sendLock.Release(); }
    }

    private async ValueTask SendAsync<T>(T message, CancellationToken ct)
    {
        if (StreamSid is null) throw new InvalidOperationException("Δεν έχει έρθει ακόμα το μήνυμα start.");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, TwilioJson.Options);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (socket.State != WebSocketState.Open) return;
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "Αποστολή σε κλειστό WebSocket");
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
