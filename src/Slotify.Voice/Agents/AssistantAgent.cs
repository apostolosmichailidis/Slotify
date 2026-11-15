using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Slotify.Core.Calls;
using Microsoft.Extensions.Options;
using Slotify.Voice.Llm;
using Slotify.Voice.Scheduling;
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
    ICalendar calendar,
    IOptions<GoogleOptions> google,
    IOptions<VoiceOptions> voice,
    TimeProvider time,
    ILogger<AssistantAgent> logger) : IVoiceAgent
{
    private const string StillTherePrompt = "Είστε ακόμα εκεί;";
    private const string SilenceGoodbye = "Δεν σας ακούω. Αν χρειάζεστε κάτι, καλέστε μας ξανά. Γεια σας.";
    private const string MaxDurationGoodbye =
        "Λυπάμαι, πρέπει να κλείσω τη γραμμή. Αν χρειάζεστε κάτι ακόμα, καλέστε μας ξανά. Γεια σας.";

    private const int MaxToolRounds = 5;

    /// <summary>Τόσα γράμματα σε ενδιάμεσο κείμενο αρκούν για να θεωρηθεί ότι ο καλών μιλάει (όχι θόρυβος).</summary>
    private const int BargeInMinChars = 3;

    private static readonly LlmTool EndCallTool =
        new("end_call",
            "Κλείνει την κλήση. Χρησιμοποίησέ το μόνο αφού έχεις πει αντίο και ο ασθενής δεν χρειάζεται κάτι άλλο.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Σύντομα γιατί κλείνει η κλήση." },
                },
            });

    private IReadOnlyList<LlmTool> _tools = [EndCallTool, MessageTool.Definition];
    private BookingTools? _booking;
    private CallMessage? _message;

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

    // Ό,τι αλλάζει τη ροή της συζήτησης (ακρόαση, διακοπές, χρονόμετρα) περνάει από εδώ, ένα τη φορά.
    private readonly SemaphoreSlim _control = new(1, 1);
    private Task _watchdog = Task.CompletedTask;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _quietSince;
    private bool _askedIfStillThere;

    public CallOutcome Outcome =>
        _booking?.LastBooking is not null ? CallOutcome.AppointmentBooked
        : _failed ? CallOutcome.Failed
        : _message is not null ? CallOutcome.MessageTaken
        : _userTurns > 0 ? CallOutcome.Completed
        : CallOutcome.Abandoned;

    public string? Summary
    {
        get
        {
            var parts = new List<string>();
            if (_booking?.LastBooking is { } b)
                parts.Add($"Ραντεβού: {b.Service}, {GreekText.DateTime(b.LocalStart)}, {b.PatientName}, {b.Phone}");
            if (_message is not null) parts.Add(_message.Summary);
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
    }

    public Booking? Booking => _booking?.LastBooking;

    public string? Transcript
    {
        get { lock (_transcript) return _transcript.Count == 0 ? null : string.Join('\n', _transcript); }
    }

    public async Task StartAsync(CallContext context, CancellationToken ct)
    {
        _context = context;
        var greeting = context.Clinic.EffectiveGreeting;

        // Ραντεβού κατευθείαν στο ημερολόγιο, αν το ιατρείο έχει συνδέσει Google Calendar.
        var calendarEnabled = google.Value.IsConfigured && !string.IsNullOrWhiteSpace(context.Clinic.GoogleCalendarId);
        if (calendarEnabled)
        {
            _booking = new BookingTools(context.Clinic, context.Call.FromNumber, context.Call.CallSid, calendar, time, logger);
            _tools = [EndCallTool, MessageTool.Definition, .. _booking.Tools];
        }

        _system = prompts.Build(context.Clinic, greeting, context.Call.FromNumber, calendarEnabled);

        _startedAt = _quietSince = time.GetUtcNow();
        _stt = await speechToText.StartAsync(ct);
        _listenLoop = Task.Run(() => ListenLoopAsync(_lifetime.Token));
        _watchdog = Task.Run(() => WatchdogLoopAsync(_lifetime.Token));

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
                await _control.WaitAsync(ct);
                try
                {
                    switch (ev)
                    {
                        case SttTranscript t:
                            if (t.Text.Length > 0)
                            {
                                // Ο καλών μιλάει: η σιωπή μετράει από την αρχή.
                                _quietSince = time.GetUtcNow();
                                _askedIfStillThere = false;
                            }

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
                finally
                {
                    _control.Release();
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
        var played = _roundSentences.Where(s => _playedMarks.ContainsKey(s.Mark)).ToList();
        var cutOff = played.Count < _roundSentences.Count;
        var heard = string.Join(' ', played.Select(s => s.Sentence));
        var text = heard.Length > 0 ? heard + (cutOff ? "…" : "") : null;

        await _context.Output.ClearAsync();
        _pendingMarks.Clear();

        if (_conversation.EndsWithAssistant)
        {
            // Ο γύρος είχε τελειώσει και έπαιζε ακόμα ο ήχος του. Αν πρόλαβε να ακουστεί
            // ολόκληρος, το ιστορικό είναι ήδη σωστό.
            if (cutOff)
            {
                _conversation.ReplaceLastAssistantText(text);
                ReplaceTranscript(_roundTranscriptIndex, text);
            }
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

    // ---------- Σιωπή και όριο διάρκειας ----------

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct);
                await _control.WaitAsync(ct);
                try { await CheckTimersAsync(); }
                finally { _control.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Σφάλμα στα χρονόμετρα της κλήσης");
        }
    }

    private async Task CheckTimersAsync()
    {
        if (_hangingUp) return;
        var o = voice.Value;
        var now = time.GetUtcNow();

        if (now - _startedAt >= TimeSpan.FromMinutes(o.MaxCallMinutes))
        {
            logger.LogInformation("Η κλήση έφτασε το όριο των {Minutes} λεπτών", o.MaxCallMinutes);
            await CloseLineAsync(MaxDurationGoodbye);
            return;
        }

        // Σιωπή μετράει μόνο όταν η γραμματεία δεν σκέφτεται, δεν μιλάει και δεν περιμένει να απαντήσει.
        if (IsBusy || GreetingPlaying || _pendingUserText.Length > 0)
        {
            _quietSince = now;
            return;
        }

        var quiet = now - _quietSince;
        if (!_askedIfStillThere && quiet >= TimeSpan.FromSeconds(o.SilencePromptSeconds))
        {
            _askedIfStillThere = true;
            AddTranscript("Γραμματεία", StillTherePrompt);
            StartTurn(ct => SpeakAsync(StillTherePrompt, ct, bypassGreetingWait: true));
        }
        else if (_askedIfStillThere && quiet >= TimeSpan.FromSeconds(o.SilenceHangUpSeconds))
        {
            logger.LogInformation("Σιωπή μετά το «Είστε ακόμα εκεί;», κλείνω");
            await CloseLineAsync(SilenceGoodbye);
        }
    }

    /// <summary>Σταματά ό,τι γίνεται, λέει μια τελευταία φράση και κλείνει τη γραμμή.</summary>
    private async Task CloseLineAsync(string goodbye)
    {
        _hangingUp = true;
        if (IsBusy && !GreetingPlaying)
        {
            await CancelTurnAsync();
            await _context.Output.ClearAsync();
            _pendingMarks.Clear();
        }
        else
        {
            await CancelTurnAsync();
        }

        AddTranscript("Γραμματεία", goodbye);
        StartTurn(async ct =>
        {
            await SpeakAsync(goodbye, ct, bypassGreetingWait: true);
            await WaitForPlaybackAsync(TimeSpan.FromSeconds(15), ct);
            await _context.Output.HangUpAsync(ct);
        });
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
            await foreach (var ev in languageModel.StreamAsync(new LlmRequest(_system, _tools, _conversation), ct))
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

            _conversation.AddAssistant(text.ToString(), calls);
            if (text.Length > 0) _roundTranscriptIndex = AddTranscript("Γραμματεία", text.ToString().Trim());
            if (calls.Count == 0) return;

            // Τα εργαλεία τρέχουν με το token της κλήσης, όχι του γύρου: μια διακοπή δεν πρέπει να
            // αφήσει tool_use χωρίς tool_result στο ιστορικό, ούτε ραντεβού κλεισμένο στη μέση.
            var results = new List<(string, string, bool)>();
            foreach (var call in calls) results.Add(await ExecuteToolAsync(call));
            _conversation.AddToolResults(results);

            // Ό,τι ειπώθηκε πριν από το εργαλείο («Μισό λεπτό να δω») είναι ήδη στο ιστορικό.
            _roundSentences.Clear();
            _roundTranscriptIndex = -1;

            if (_hangingUp) break;
            ct.ThrowIfCancellationRequested();
        }

        if (_hangingUp)
        {
            await WaitForPlaybackAsync(TimeSpan.FromSeconds(15), ct);
            await _context.Output.HangUpAsync(ct);
        }
    }

    private async Task<(string ToolUseId, string Content, bool IsError)> ExecuteToolAsync(LlmToolCall call)
    {
        logger.LogInformation("Εργαλείο {Tool}: {Input}", call.Name,
            call.Input.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        if (call.Name == "end_call")
        {
            _hangingUp = true;
            return (call.Id, "Η κλήση κλείνει.", false);
        }

        if (call.Name == MessageTool.Name)
        {
            var (message, text) = MessageTool.Parse(call.Input, _context.Call.FromNumber);
            if (message is not null) _message = message;
            return (call.Id, text, message is null);
        }

        try
        {
            if (_booking is not null && await _booking.TryExecuteAsync(call, _lifetime.Token) is { } result)
            {
                logger.LogDebug("Αποτέλεσμα {Tool}: {Result}", call.Name, result.Content);
                return (call.Id, result.Content, result.IsError);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Ένα πρόβλημα στο ημερολόγιο δεν πρέπει να κλείσει την κλήση.
            logger.LogError(ex, "Το εργαλείο {Tool} απέτυχε", call.Name);
            return (call.Id,
                "Τεχνικό πρόβλημα με το ημερολόγιο. Πάρε όνομα, υπηρεσία και ώρα που τον βολεύει, κατέγραψέ τα " +
                "με το take_message και πες ότι το ιατρείο θα τον καλέσει για επιβεβαίωση.", true);
        }

        return (call.Id, $"Άγνωστο εργαλείο {call.Name}", true);
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
        try { await _watchdog; } catch { }
        if (_stt is not null) await _stt.DisposeAsync();
        _turnCts?.Dispose();
        _lifetime.Dispose();
    }
}
