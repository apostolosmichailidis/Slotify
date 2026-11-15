using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Slotify.Voice.Twilio;

/// <summary>
/// Ελέγχει το header X-Twilio-Signature, ώστε να δεχόμαστε webhooks μόνο από το Twilio.
/// Αλγόριθμος: HMAC-SHA1 με κλειδί το Auth Token, πάνω στο πλήρες URL και στις
/// παραμέτρους της φόρμας ταξινομημένες κατά όνομα (όνομα + τιμή, χωρίς διαχωριστικό).
/// </summary>
public sealed class TwilioSignatureValidator(IOptions<TwilioOptions> twilio)
{
    public bool IsValid(string fullUrl, IEnumerable<KeyValuePair<string, string>> formParams, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;

        var token = twilio.Value.AuthToken;
        if (string.IsNullOrEmpty(token)) return false;

        var data = new StringBuilder(fullUrl);
        foreach (var (key, value) in formParams.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            data.Append(key).Append(value);
        }

        var expected = HMACSHA1.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(data.ToString()));

        byte[] given;
        try { given = Convert.FromBase64String(signature); }
        catch (FormatException) { return false; }

        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
