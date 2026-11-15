using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slotify.Voice.Twilio;

// Τα μηνύματα που στέλνει το Twilio Media Streams μέσα από το WebSocket.
// https://www.twilio.com/docs/voice/media-streams/websocket-messages

public sealed record TwilioInbound(
    string Event,
    string? StreamSid,
    TwilioStart? Start,
    TwilioMedia? Media,
    TwilioMark? Mark,
    TwilioDtmf? Dtmf);

public sealed record TwilioStart(
    string StreamSid,
    string CallSid,
    string? AccountSid,
    Dictionary<string, string>? CustomParameters,
    TwilioMediaFormat? MediaFormat);

public sealed record TwilioMediaFormat(string? Encoding, int SampleRate, int Channels);

public sealed record TwilioMedia(string? Track, string? Chunk, string? Timestamp, string Payload);

public sealed record TwilioMark(string Name);

public sealed record TwilioDtmf(string? Track, string Digit);

internal static class TwilioJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
