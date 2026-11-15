using System.Text.Json.Nodes;

namespace Slotify.Voice.Llm;

/// <summary>
/// Το ιστορικό της συζήτησης, στη μορφή του Messages API του Claude.
/// Φροντίζει να εναλλάσσονται σωστά user / assistant, ακόμα κι όταν ο καλών
/// διακόψει τη γραμματεία στη μέση.
/// </summary>
public sealed class Conversation
{
    private readonly JsonArray _messages = [];

    public JsonArray Messages => _messages;

    public void AddUserText(string text)
    {
        // Δύο συνεχόμενα user μηνύματα δεν επιτρέπονται. Αν ο καλών συνέχισε να μιλάει
        // πριν απαντήσουμε (ή μετά από αποτέλεσμα εργαλείου), τα βάζουμε στο ίδιο μήνυμα.
        if (LastRole == "user" && _messages[^1]!["content"] is JsonArray blocks)
        {
            if (blocks.Count > 0 && blocks[^1]!["type"]?.GetValue<string>() == "text")
            {
                var last = blocks[^1]!;
                last["text"] = $"{last["text"]!.GetValue<string>()} {text}";
            }
            else
            {
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }
            return;
        }

        AddUserBlocks(new JsonObject { ["type"] = "text", ["text"] = text });
    }

    /// <summary>Η απάντηση της γραμματείας. Τα tool calls μπαίνουν μόνο αν ολοκληρώθηκαν.</summary>
    public void AddAssistant(string text, IReadOnlyList<LlmToolCall> toolCalls)
    {
        var blocks = new JsonArray();
        if (!string.IsNullOrWhiteSpace(text))
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        foreach (var call in toolCalls)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = call.Id,
                ["name"] = call.Name,
                ["input"] = call.Input.DeepClone(),
            });
        }

        if (blocks.Count == 0) return;
        _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
    }

    public void AddToolResults(IEnumerable<(string ToolUseId, string Content, bool IsError)> results)
    {
        var blocks = new JsonArray();
        foreach (var (id, content, isError) in results)
        {
            var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = content };
            if (isError) block["is_error"] = true;
            blocks.Add(block);
        }
        AddUserBlocks([.. blocks.Select(b => b!.DeepClone())]);
    }

    /// <summary>
    /// Αλλάζει το κείμενο της τελευταίας απάντησης της γραμματείας (π.χ. σε ό,τι πρόλαβε να ακουστεί
    /// πριν τη διακόψουν). Με null σβήνεται το κείμενο, και αν δεν μείνει τίποτα σβήνεται το μήνυμα.
    /// </summary>
    public bool ReplaceLastAssistantText(string? text)
    {
        if (LastRole != "assistant" || _messages[^1]!["content"] is not JsonArray blocks) return false;

        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i]!["type"]?.GetValue<string>() == "text") blocks.RemoveAt(i);
        }

        if (!string.IsNullOrWhiteSpace(text))
            blocks.Insert(0, new JsonObject { ["type"] = "text", ["text"] = text });

        if (blocks.Count == 0) _messages.RemoveAt(_messages.Count - 1);
        return true;
    }

    public bool EndsWithAssistant => LastRole == "assistant";

    public bool EndsWithUser => LastRole == "user";

    private string? LastRole => _messages.Count == 0 ? null : _messages[^1]!["role"]?.GetValue<string>();

    private void AddUserBlocks(params JsonNode[] blocks) =>
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(blocks) });
}
