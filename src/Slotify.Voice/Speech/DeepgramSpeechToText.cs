using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Slotify.Voice.Speech;

public sealed class DeepgramOptions
{
    public const string Section = "Deepgram";

    [Required]
    public string ApiKey { get; set; } = "";

    public string Url { get; set; } = "wss://api.deepgram.com/v1/listen";
    public string Model { get; set; } = "nova-3";
    public string Language { get; set; } = "el";

    /// <summary>Πόση σιωπή (ms) θεωρείται τέλος φράσης. Μικρότερο = πιο γρήγορη απάντηση, αλλά κόβει όσους κάνουν παύσεις.</summary>
    public int EndpointingMs { get; set; } = 400;

    /// <summary>Δίχτυ ασφαλείας: μετά από τόση σιωπή (ms) η φράση θεωρείται σίγουρα τελειωμένη. Ελάχιστο 1000.</summary>
    public int UtteranceEndMs { get; set; } = 1000;
}

/// <summary>
/// Αναγνώριση φωνής σε πραγματικό χρόνο μέσω του WebSocket του Deepgram.
/// https://developers.deepgram.com/docs/live-streaming-audio
/// </summary>
public sealed class DeepgramSpeechToText(IOptions<DeepgramOptions> options, ILoggerFactory loggerFactory) : ISpeechToText
{
    public async Task<ISttSession> StartAsync(CancellationToken ct)
    {
        var o = options.Value;
        var query = string.Join('&',
            $"model={Uri.EscapeDataString(o.Model)}",
            $"language={Uri.EscapeDataString(o.Language)}",
            "encoding=mulaw",
            "sample_rate=8000",
            "channels=1",
            "interim_results=true",
            "punctuate=true",
            "smart_format=true",
            $"endpointing={o.EndpointingMs}",
            $"utterance_end_ms={Math.Max(1000, o.UtteranceEndMs)}");

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Token {o.ApiKey}");
        await socket.ConnectAsync(new Uri($"{o.Url}?{query}"), ct);

        var session = new DeepgramSession(socket, loggerFactory.CreateLogger<DeepgramSession>());
        session.Start();
        return session;
    }
}

internal sealed class DeepgramSession(ClientWebSocket socket, ILogger logger) : ISttSession
{
    private static readonly byte[] KeepAlive = """{"type":"KeepAlive"}"""u8.ToArray();
    private static readonly byte[] CloseStream = """{"type":"CloseStream"}"""u8.ToArray();

    private readonly Channel<SttEvent> _events = Channel.CreateUnbounded<SttEvent>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _receiveLoop;
    private Task? _keepAliveLoop;
    private long _lastSendTicks = Environment.TickCount64;

    public ChannelReader<SttEvent> Events => _events.Reader;

    public void Start()
    {
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_stop.Token));
        _keepAliveLoop = Task.Run(() => KeepAliveLoopAsync(_stop.Token));
    }

    public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> mulaw, CancellationToken ct) =>
        await SendAsync(mulaw, WebSocketMessageType.Binary, ct);

    private async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (socket.State != WebSocketState.Open) return;
            await socket.SendAsync(data, type, endOfMessage: true, ct);
            Interlocked.Exchange(ref _lastSendTicks, Environment.TickCount64);
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "Αποτυχία αποστολής στο Deepgram");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // Το Deepgram κλείνει τη σύνδεση μετά από ~10 δευτερόλεπτα χωρίς δεδομένα.
    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastSendTicks) > 3000)
                    await SendAsync(KeepAlive, WebSocketMessageType.Text, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(8192);
        string? reason = null;
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                buffer.ResetWrittenCount();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.GetMemory(8192), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        reason = socket.CloseStatusDescription;
                        return;
                    }
                    buffer.Advance(result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text && Parse(buffer.WrittenSpan) is { } ev)
                    _events.Writer.TryWrite(ev);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is WebSocketException or JsonException)
        {
            reason = ex.Message;
            logger.LogWarning(ex, "Η σύνδεση με το Deepgram διακόπηκε");
        }
        finally
        {
            _events.Writer.TryWrite(new SttClosed(reason));
            _events.Writer.TryComplete();
        }
    }

    internal static SttEvent? Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        switch (root.TryGetProperty("type", out var t) ? t.GetString() : null)
        {
            case "Results":
                var text = "";
                if (root.TryGetProperty("channel", out var channel)
                    && channel.TryGetProperty("alternatives", out var alternatives)
                    && alternatives.GetArrayLength() > 0
                    && alternatives[0].TryGetProperty("transcript", out var transcript))
                {
                    text = transcript.GetString() ?? "";
                }
                var isFinal = root.TryGetProperty("is_final", out var f) && f.GetBoolean();
                var speechFinal = root.TryGetProperty("speech_final", out var s) && s.GetBoolean();
                return new SttTranscript(text.Trim(), isFinal, speechFinal);

            case "UtteranceEnd":
                return new SttUtteranceEnd();

            default:
                return null; // Metadata, SpeechStarted κ.λπ.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await SendAsync(CloseStream, WebSocketMessageType.Text, CancellationToken.None);
            if (_receiveLoop is not null)
                await Task.WhenAny(_receiveLoop, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        catch { /* κλείνουμε έτσι κι αλλιώς */ }

        await _stop.CancelAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException) { }

        if (_keepAliveLoop is not null) await _keepAliveLoop;
        socket.Dispose();
        _stop.Dispose();
    }
}
