using System.Text;

namespace Slotify.Core;

public static class PhoneNumbers
{
    /// <summary>
    /// Φέρνει έναν αριθμό σε μορφή E.164 (π.χ. "+302310123456"), για να ταιριάζει
    /// με αυτό που στέλνει το Twilio. Δέχεται κενά, παύλες, παρενθέσεις και "00" μπροστά.
    /// Ελληνικοί αριθμοί 10 ψηφίων χωρίς πρόθεμα παίρνουν +30.
    /// </summary>
    public static string Normalize(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);

        var digits = new StringBuilder();
        foreach (var c in raw.Trim())
        {
            if (char.IsAsciiDigit(c)) digits.Append(c);
        }

        var value = digits.ToString();
        if (raw.TrimStart().StartsWith('+')) return "+" + value;
        if (value.StartsWith("00")) return "+" + value[2..];
        if (value.Length == 10 && (value[0] == '2' || value[0] == '6')) return "+30" + value;
        return "+" + value;
    }
}
