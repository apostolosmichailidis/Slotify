using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Slotify.Voice.Llm;

public sealed class AnthropicOptions
{
    public const string Section = "Anthropic";

    [Required]
    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>Το Haiku είναι το γρηγορότερο και φθηνότερο, αρκετό για ραντεβού.</summary>
    public string Model { get; set; } = "claude-haiku-5-5";

    /// <summary>Οι απαντήσεις στο τηλέφωνο είναι σύντομες. Όριο για να μη φεύγει το κόστος.</summary>
    public int MaxTokens { get; set; } = 400;
}

/// <summary>
/// Το Claude μέσω του Messages API, σε ροή (Server-Sent Events), ώστε η πρώτη πρόταση
/// να πηγαίνει στη φωνή πριν τελειώσει όλη η απάντηση.
/// https://docs.claude.com/en/api/messages-streaming
/// </summary>
public sealed class AnthropicLanguageModel(HttpClient http, IOptions<AnthropicOptions> options, ILogger<AnthropicLanguageModel> logger)
    : ILanguageModel
{
    public async IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var o = options.Value;

        var tools = new JsonArray();
        foreach (var tool in request.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = tool.InputSchema.DeepClone(),
            });
        }
        // Prompt caching: οδηγίες και εργαλεία είναι ίδια σε κάθε σειρά της κλήσης.
        if (tools.Count > 0) tools[^1]!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

        var body = new JsonObject
        {
            ["model"] = o.Model,
            ["max_tokens"] = o.MaxTokens,
            ["stream"] = true,
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = request.System,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            }),
            ["messages"] = request.Conversation.Messages.DeepClone(),
        };
        if (tools.Count > 0) body["tools"] = tools;

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("x-api-key", o.ApiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Claude {Status}: {Body}", (int)response.StatusCode, error);
            throw new HttpRequestException($"Claude απάντησε {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Τα tool_use έρχονται κομμάτι κομμάτι (partial_json), τα μαζεύουμε ανά block.
        var toolBlocks = new Dictionary<int, (string Id, string Name, StringBuilder Json)>();
        string? stopReason = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line.AsSpan(5).Trim();
            if (data.IsEmpty) continue;

            using var doc = JsonDocument.Parse(data.ToString());
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "content_block_start":
                {
                    var block = root.GetProperty("content_block");
                    if (block.GetProperty("type").GetString() == "tool_use")
                    {
                        toolBlocks[root.GetProperty("index").GetInt32()] =
                            (block.GetProperty("id").GetString()!, block.GetProperty("name").GetString()!, new StringBuilder());
                    }
                    break;
                }

                case "content_block_delta":
                {
                    var delta = root.GetProperty("delta");
                    switch (delta.GetProperty("type").GetString())
                    {
                        case "text_delta":
                            yield return new LlmTextDelta(delta.GetProperty("text").GetString() ?? "");
                            break;
                        case "input_json_delta":
                            if (toolBlocks.TryGetValue(root.GetProperty("index").GetInt32(), out var tb))
                                tb.Json.Append(delta.GetProperty("partial_json").GetString());
                            break;
                    }
                    break;
                }

                case "content_block_stop":
                {
                    var index = root.GetProperty("index").GetInt32();
                    if (toolBlocks.Remove(index, out var tb))
                    {
                        var json = tb.Json.Length == 0 ? "{}" : tb.Json.ToString();
                        var input = JsonNode.Parse(json) as JsonObject ?? [];
                        yield return new LlmToolCall(tb.Id, tb.Name, input);
                    }
                    break;
                }

                case "message_delta":
                    if (root.GetProperty("delta").TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                        stopReason = sr.GetString();
                    break;

                case "message_stop":
                    yield return new LlmCompleted(stopReason);
                    yield break;

                case "error":
                    var err = root.GetProperty("error");
                    throw new HttpRequestException(
                        $"Claude σφάλμα {err.GetProperty("type").GetString()}: {err.GetProperty("message").GetString()}");
            }
        }

        yield return new LlmCompleted(stopReason);
    }
}
