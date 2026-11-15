using System.Text.Json.Nodes;

namespace Slotify.Voice.Llm;

public abstract record LlmEvent;

/// <summary>Ένα κομμάτι κειμένου της απάντησης, όπως παράγεται.</summary>
public sealed record LlmTextDelta(string Text) : LlmEvent;

/// <summary>Το μοντέλο θέλει να χρησιμοποιήσει ένα εργαλείο (π.χ. να κλείσει ραντεβού).</summary>
public sealed record LlmToolCall(string Id, string Name, JsonObject Input) : LlmEvent;

/// <summary>Τέλος απάντησης. StopReason: "end_turn", "tool_use", "max_tokens"…</summary>
public sealed record LlmCompleted(string? StopReason) : LlmEvent;

/// <summary>Ένα εργαλείο που μπορεί να χρησιμοποιήσει το μοντέλο. InputSchema σε JSON Schema.</summary>
public sealed record LlmTool(string Name, string Description, JsonObject InputSchema);

public sealed record LlmRequest(string System, IReadOnlyList<LlmTool> Tools, Conversation Conversation);

public interface ILanguageModel
{
    IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken ct);
}
