using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Slotify.Core.Calls;
using Slotify.Voice.Llm;
using Slotify.Voice.Speech;

namespace Slotify.Voice.Agents;

/// <summary>
/// Η κανονική γραμματεία: ακούει (Deepgram), σκέφτεται (Claude), μιλάει (ElevenLabs).
///
/// Ροή μιας σειράς:
///   ο καλών μιλάει → τελικό κείμενο → Claude σε ροή → κάθε πρόταση πάει αμέσως στη φωνή → Twilio.
///
/// Διακοπή (barge-in): αν ο καλών αρχίσει να μιλάει όσο η γραμματεία σκέφτεται ή μιλάει,
/// ακυρώνουμε την απάντηση και στέλνουμε "clear" στο Twilio, ώστε να σωπάσει αμέσως.
/// Στο ιστορικό μένει μόνο ό,τι πρόλαβε να ακουστεί (το ξέρουμε από τα marks του Twilio).
///
/// Ο χαιρετισμός δεν διακόπτεται ποτέ, γιατί περιέχει την ενημέρωση ότι ο καλών μιλάει
/// με AI (Κανονισμός AI της ΕΕ). Αν ο καλών μιλήσει από πάνω, η απάντηση ετοιμάζεται
/// κανονικά και ο ήχος της ξεκινά μόλις τελειώσει ο χαιρετισμός.
/// </summary>
public sealed class AssistantAgent(
    ISpeechToText speechToText,
    ITextToSpeech textToSpeech,
    ILanguageModel languageModel,
    PromptBuilder prompts,
    ILogger<AssistantAgent> logger) : IVoiceAgent
{
    private const int MaxToolRounds = 5;

    /// <summary>Τόσα γράμματα σε ενδιάμεσο κείμενο αρκούν για να θεωρηθεί ότι ο καλών μιλάει (όχι θόρυβος).</summary>
    private const int BargeInMinChars = 3;

    private static readonly IReadOnlyList<LlmTool> Tools =
    [
        new("end_call",
            "Κλείνει την κλήση. Χρησιμοποίησέ το μόνο αφού έχεις πει αντίο και ο ασθενής δεν χρειάζεται κάτι άλλο.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Σύντομα γιατί κλείνει η κλήση." },
                },
            }),
    ];

    private readonly Conversation _conversation = new();
    private readonly StringBuilder _pendingUserText = new();
    private readonly List<string> _transcript = [];
    private readonly CancellationTokenSource _lifetime = new();

    // Marks που στείλαμε στο Twilio και δεν έχουν παίξει ακόμα / marks που έπαιξαν.
    private readonly ConcurrentDictionary<string, byte> _pendingMarks = new();
    private readonly ConcurrentDictionary<string, byte> _playedMarks = new();

    // Οι προτάσεις του τρέχοντος γύρου με το mark τους, για να ξέρουμε τι ακούστηκε πριν από μια διακοπή.
    private readonly List<(string Mark, string Sentence)> _roundSentences = [];
    private int _roundTranscriptIndex = -1;

    private CallContext _context = null!;
    private ISttSession? _stt;
    private Task _listenLoop = Task.CompletedTask;
    private Task _greeting = Task.CompletedTask;
    private string? _greetingMark;
    private Task _turn = Task.CompletedTask;
    private CancellationTokenSource? _turnCts;
    private string _system = "";
    private int _markSeq;
    private int _userTurns;
    private volatile bool _hangingUp;
    private volatile bool _failed;

    public CallOutcome Outcome => _failed ? CallOutcome.Failed : _userTurns > 0 ? CallOutcome.Completed : CallOutcome.Abandoned;

    public string? Transcript
    {
        get { lock (_transcript) return _transcript.Count == 0 ? null : string.Join('\n', _transcript); }
    }

    public async Task StartAsync(CallContext context, CancellationToken ct)
    {
        _context = context;
        var greeting = context.Clinic.EffectiveGreeting;
        _system = prompts.Build(context.Clinic, greeting);

        _stt = await speechToText.StartAsync(ct);
        _listenLoop = Task.Run(() => ListenLoopAsync(_lifetime.Token));

        AddTranscript("Γραμματεία", greeting);
        _greeting = Task.Run(async () =>
        {
            try { _greetingMark = await SpeakAsync(greeting, _lifetime.Token, bypassGreetingWait: true); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { await HandleFailureAsync(ex); }
        });
    }

    public ValueTask OnAudioAsync(ReadOnlyMemory<byte> mulaw, CancellationToken ct) =>
        _stt is null ? ValueTask.CompletedTask : _stt.SendAudioAsync(mulaw, ct);

    public ValueTask OnMarkAsync(string name, CancellationToken ct)
    {
        _playedMarks[name] = 0;
        _pendingMarks.TryRemove(name, out _);
        return ValueTask.CompletedTask;
    }

    /// <summary>Ο χαιρετισμός ετοιμάζεται ή παίζει ακόμα.</summary>
    private bool GreetingPlaying =>
        !_greeting.IsCompleted || (_greetingMark is { } mark && _pendingMarks.ContainsKey(mark));

    /// <summary>Η γραμματεία σκέφτεται ή μιλάει.</summary>
    private bool IsBusy => !_turn.IsCompleted || !_pendingMarks.IsEmpty;

    // ---------- Ακρόαση ----------

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var ev in _stt!.Events.ReadAllAsync(ct))
            {
                switch (ev)
                {
                    case SttTranscript t:
                        if (t.Text.Length >= BargeInMinChars && IsBusy && !_hangingUp)
                            await InterruptAsync();

                        if (t.IsFinal && t.Text.Length > 0)
                            _pendingUserText.Append(' ').Append(t.Text);

                        if (t.SpeechFinal)
                            await RespondToPendingAsync();
                        break;

                    case SttUtteranceEnd:
                        await RespondToPendingAsync();
                        break;

                    case SttClosed closed:
                        logger.LogWarning("Η αναγνώριση φωνής σταμάτησε: {Reason}", closed.Reason);
                        if (!_lifetime.IsCancellationRequested)
                        {
                            _failed = true;
                            await _context.Output.HangUpAsync(CancellationToken.None);
                        }
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Σφάλμα στην ακρόαση");
        }
    }

    /// <summary>
    /// Σταματά ό,τι κάνει η γραμματεία (σκέψη ή ομιλία) και κρατάει στο ιστορικό μόνο
    /// ό,τι πρόλαβε πραγματικά να ακουστεί, ώστε το Claude να ξέρει πού έμεινε.
    /// </summary>
    private async Task InterruptAsync()
    {
        if (!IsBusy) return;

        if (GreetingPlaying)
        {
            // Καμία απάντηση δεν έχει βγάλει ακόμα ήχο (περιμένει τον χαιρετισμό),
            // οπότε αρκεί να ακυρωθεί. Δεν στέλνουμε "clear", για να μην κοπεί ο χαιρετισμός.
            await CancelTurnAsync();
            return;
        }

        logger.LogDebug("Ο καλών μίλησε από πάνω, σταματάω");
        await CancelTurnAsync();

        // Τι ακούστηκε: το μετράμε ΠΡΙΝ το "clear". Μετά το "clear" το Twilio επιστρέφει αμέσως
        // marks και για τον ήχο που σβήστηκε, και δεν θα ξεχωρίζαμε τι παίχτηκε πραγματικά.
        var heard = string.Join(' ', _roundSentences
            .Where(s => _playedMarks.ContainsKey(s.Mark))
            .Select(s => s.Sentence));
        var text = heard.Length > 0 ? heard + "…" : null;

        await _context.Output.ClearAsync();
        _pendingMarks.Clear();

        if (_conversation.EndsWithAssistant)
        {
            // Ο γύρος είχε τελειώσει, έπαιζε ακόμα ο ήχος του.
            _conversation.ReplaceLastAssistantText(text);
            ReplaceTranscript(_roundTranscriptIndex, text);
        }
        else if (text is not null)
        {
            // Ο γύρος κόπηκε στη μέση.
            _conversation.AddAssistant(text, []);
            AddTranscript("Γραμματεία", text);
        }

        _roundSentences.Clear();
        _roundTranscriptIndex = -1;
    }

    private async Task RespondToPendingAsync()
    {
        var text = _pendingUserText.ToString().Trim();
        if (text.Length == 0 || _hangingUp) return;
        _pendingUserText.Clear();

        // Αν σκεφτόμασταν ή μιλούσαμε ακόμα για την προηγούμενη φράση, σταματάμε.
        // Η νέα φράση ενώνεται με την προηγούμενη στο ιστορικό.
        await InterruptAsync();

        _userTurns++;
        AddTranscript("Ασθενής", text);
        _conversation.AddUserText(text);
        StartTurn(RespondAsync);
    }

    // ---------- Απάντηση ----------

    private void StartTurn(Func<CancellationToken, Task> work)
    {
        // Ο προηγούμενος γύρος έχει ήδη τελειώσει εδώ (τον ακυρώνει ή τον περιμένει το InterruptAsync).
        _turnCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _turnCts = cts;
        _turn = Task.Run(async () =>
        {
            try { await work(cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception ex) { await HandleFailureAsync(ex); }
        });
    }

    private async Task CancelTurnAsync()
    {
        if (_turnCts is { } cts)
        {
            await cts.CancelAsync();
            await _turn;
            cts.Dispose();
            _turnCts = null;
        }
    }

    private async Task RespondAsync(CancellationToken ct)
    {
        for (var round = 0; round < MaxToolRounds; round++)
        {
            var text = new StringBuilder();
            var calls = new List<LlmToolCall>();
            var splitter = new SentenceSplitter();
            _roundSentences.Clear();
            _roundTranscriptIndex = -1;

            // Αν ο καλών διακόψει, το OperationCanceledException φεύγει προς τα πάνω
            // και το InterruptAsync γράφει στο ιστορικό ό,τι πρόλαβε να ακουστεί.
            await foreach (var ev in languageModel.StreamAsync(new LlmRequest(_system, Tools, _conversation), ct))
            {
                switch (ev)
                {
                    case LlmTextDelta d:
                        text.Append(d.Text);
                        foreach (var sentence in splitter.Push(d.Text))
                            await SpeakAsync(sentence, ct);
                        break;
                    case LlmToolCall call:
                        calls.Add(call);
                        break;
                }
            }

            if (splitter.Flush() is { } rest) await SpeakAsync(rest, ct);

            // Από εδώ ως το τέλος του γύρου δεν υπάρχει await, ώστε το ιστορικό να μη
            // μείνει ποτέ με tool_use χωρίς tool_result.
            _conversation.AddAssistant(text.ToString(), calls);
            if (text.Length > 0) _roundTranscriptIndex = AddTranscript("Γραμματεία", text.ToString().Trim());
            if (calls.Count == 0) return;

            _conversation.AddToolResults(calls.Select(ExecuteTool).ToList());
            if (_hangingUp) break;
        }

        if (_hangingUp)
        {
            await WaitForPlaybackAsync(TimeSpan.FromSeconds(15), ct);
            await _context.Output.HangUpAsync(ct);
        }
    }

    private (string ToolUseId, string Content, bool IsError) ExecuteTool(LlmToolCall call)
    {
        logger.LogInformation("Εργαλείο {Tool}: {Input}", call.Name, call.Input.ToJsonString());
        switch (call.Name)
        {
            case "end_call":
                _hangingUp = true;
                return (call.Id, "Η κλήση κλείνει.", false);

            default:
                return (call.Id, $"Άγνωστο εργαλείο {call.Name}", true);
        }
    }

    /// <summary>Λέει μια πρόταση και επιστρέφει το mark που θα γυρίσει όταν παιχτεί.</summary>
    private async Task<string> SpeakAsync(string text, CancellationToken ct, bool bypassGreetingWait = false)
    {
        // Η απάντηση περιμένει να τελειώσει ο χαιρετισμός, ώστε ένα "clear" να μην τον κόψει ποτέ.
        if (!bypassGreetingWait)
        {
            while (GreetingPlaying) await Task.Delay(50, ct);
        }

        await foreach (var chunk in textToSpeech.SynthesizeAsync(text, ct))
            await _context.Output.SendAudioAsync(chunk, ct);

        // Το Twilio μάς επιστρέφει το mark όταν παίξει όλος ο ήχος ως εδώ.
        var mark = $"s{Interlocked.Increment(ref _markSeq)}";
        _pendingMarks[mark] = 0;
        if (!bypassGreetingWait) _roundSentences.Add((mark, text));
        await _context.Output.SendMarkAsync(mark, ct);
        return mark;
    }

    private async Task WaitForPlaybackAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!_pendingMarks.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(100, ct);
    }

    private async Task HandleFailureAsync(Exception ex)
    {
        if (_failed) return;
        _failed = true;
        _hangingUp = true;
        logger.LogError(ex, "Σφάλμα στην απάντηση της γραμματείας");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await SpeakAsync("Συγγνώμη, έχω ένα τεχνικό πρόβλημα. Θα σας καλέσει το ιατρείο το συντομότερο.",
                timeout.Token, bypassGreetingWait: true);
            await WaitForPlaybackAsync(TimeSpan.FromSeconds(10), timeout.Token);
        }
        catch (Exception speakEx)
        {
            logger.LogWarning(speakEx, "Δεν μπόρεσα ούτε να πω το μήνυμα σφάλματος");
        }
        await _context.Output.HangUpAsync(CancellationToken.None);
    }

    private int AddTranscript(string who, string text)
    {
        lock (_transcript)
        {
            _transcript.Add($"{who}: {text}");
            return _transcript.Count - 1;
        }
    }

    private void ReplaceTranscript(int index, string? text)
    {
        lock (_transcript)
        {
            if (index < 0 || index >= _transcript.Count) return;
            _transcript[index] = $"Γραμματεία: {text ?? "(διακόπηκε πριν ακουστεί)"}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try { await _greeting; } catch { }
        try { await _turn; } catch { }
        try { await _listenLoop; } catch { }
        if (_stt is not null) await _stt.DisposeAsync();
        _turnCts?.Dispose();
        _lifetime.Dispose();
    }
}
