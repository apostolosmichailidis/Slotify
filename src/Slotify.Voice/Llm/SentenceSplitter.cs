using System.Text;

namespace Slotify.Voice.Llm;

/// <summary>
/// Κόβει το κείμενο που έρχεται σε ροή σε προτάσεις, ώστε η πρώτη πρόταση να
/// πάει στη φωνή αμέσως και ο καλών να μην περιμένει όλη την απάντηση.
/// Στα ελληνικά το ερωτηματικό είναι ";" (και το U+037E).
/// </summary>
public sealed class SentenceSplitter
{
    private const int SoftLimit = 160;
    private readonly StringBuilder _buffer = new();

    public IEnumerable<string> Push(string delta)
    {
        _buffer.Append(delta);
        var results = new List<string>();

        while (true)
        {
            var cut = FindCut();
            if (cut < 0) break;

            var sentence = _buffer.ToString(0, cut + 1).Trim();
            _buffer.Remove(0, cut + 1);
            if (sentence.Length > 0) results.Add(sentence);
        }

        return results;
    }

    /// <summary>Ό,τι έμεινε στο τέλος της απάντησης.</summary>
    public string? Flush()
    {
        var rest = _buffer.ToString().Trim();
        _buffer.Clear();
        return rest.Length > 0 ? rest : null;
    }

    private int FindCut()
    {
        // Τέλος πρότασης: σημείο στίξης που ακολουθείται από κενό.
        // Περιμένουμε το κενό ώστε το "10.30" ή το "κ.λπ." να μην κοπούν στη μέση.
        for (var i = 0; i < _buffer.Length - 1; i++)
        {
            var c = _buffer[i];
            if (c == '\n') return i;
            if (IsTerminator(c) && char.IsWhiteSpace(_buffer[i + 1]) && i >= 2) return i;
        }

        // Πολύ μεγάλη πρόταση χωρίς τελεία: κόβουμε στο τελευταίο κόμμα μέσα στο όριο.
        if (_buffer.Length > SoftLimit)
        {
            for (var i = Math.Min(_buffer.Length - 2, SoftLimit); i > 20; i--)
            {
                if (_buffer[i] == ',' && char.IsWhiteSpace(_buffer[i + 1])) return i;
            }
        }

        return -1;
    }

    private static bool IsTerminator(char c) => c is '.' or '!' or '?' or ';' or ';' or '…';
}
