using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Slotify.Voice.Twilio;

/// <summary>
/// Το webhook (που ελέγχεται με υπογραφή Twilio) βάζει ένα token στις παραμέτρους του stream.
/// Όταν ανοίξει το WebSocket, ελέγχουμε το token. Έτσι κανείς άλλος δεν μπορεί
/// να ανοίξει WebSocket και να καταναλώνει λεπτά στο όνομα ενός ιατρείου.
/// </summary>
public sealed class StreamTokens
{
    private readonly byte[] _key;

    public StreamTokens(IOptions<VoiceOptions> options)
    {
        var secret = options.Value.StreamSecret;
        _key = string.IsNullOrWhiteSpace(secret)
            ? RandomNumberGenerator.GetBytes(32)
            : SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public string Create(string callSid, Guid clinicId) =>
        Convert.ToBase64String(Sign(callSid, clinicId));

    public bool Verify(string callSid, Guid clinicId, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;

        byte[] given;
        try { given = Convert.FromBase64String(token); }
        catch (FormatException) { return false; }

        return CryptographicOperations.FixedTimeEquals(Sign(callSid, clinicId), given);
    }

    private byte[] Sign(string callSid, Guid clinicId) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{callSid}:{clinicId:N}"));
}
