using Microsoft.Extensions.Options;
using Slotify.Data;
using Slotify.Voice;
using Slotify.Voice.Agents;
using Slotify.Voice.Llm;
using Slotify.Voice.Notifications;
using Slotify.Voice.Scheduling;
using Slotify.Voice.Speech;
using Slotify.Voice.Twilio;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<VoiceOptions>()
    .Bind(builder.Configuration.GetSection(VoiceOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<TwilioOptions>()
    .Bind(builder.Configuration.GetSection(TwilioOptions.Section))
    .Validate(o => !o.ValidateSignature || !string.IsNullOrWhiteSpace(o.AuthToken),
        "Λείπει το Twilio:AuthToken. Βάλ' το, ή όρισε Twilio:ValidateSignature=false μόνο για τοπικές δοκιμές.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Slotify")
    ?? throw new InvalidOperationException("Λείπει το ConnectionStrings:Slotify");
builder.Services.AddSlotifyData(connectionString);

builder.Services.AddSingleton<TwilioSignatureValidator>();
builder.Services.AddSingleton<StreamTokens>();
builder.Services.AddSingleton<VoiceAgentFactory>();
builder.Services.AddSingleton<CallSessionRunner>();

// Η γραμματεία: ακούει, σκέφτεται, μιλάει.
var assistantMode = builder.Configuration.GetValue($"{VoiceOptions.Section}:Mode", AgentMode.Echo) == AgentMode.Assistant;
AddValidatedOptions<DeepgramOptions>(DeepgramOptions.Section);
AddValidatedOptions<ElevenLabsOptions>(ElevenLabsOptions.Section);
AddValidatedOptions<AnthropicOptions>(AnthropicOptions.Section);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<ISpeechToText, DeepgramSpeechToText>();
builder.Services.AddHttpClient<ITextToSpeech, ElevenLabsTextToSpeech>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<ILanguageModel, AnthropicLanguageModel>(c => c.Timeout = TimeSpan.FromSeconds(60));

// Ημερολόγιο: προαιρετικό. Χωρίς αυτό η γραμματεία κρατάει αιτήματα αντί να κλείνει ραντεβού.
builder.Services.AddOptions<GoogleOptions>().Bind(builder.Configuration.GetSection(GoogleOptions.Section));
builder.Services.AddHttpClient(GoogleCalendar.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<ICalendar, GoogleCalendar>();

// Email στα ιατρεία και SMS στους ασθενείς. Γράφονται στη βάση στο τέλος της κλήσης και τα στέλνει ο worker.
builder.Services.AddOptions<EmailOptions>().Bind(builder.Configuration.GetSection(EmailOptions.Section));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHttpClient<ISmsSender, TwilioSmsSender>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHostedService<NotificationWorker>();

// Τα κλειδιά ελέγχονται στην εκκίνηση μόνο όταν τρέχει η κανονική γραμματεία,
// ώστε το Echo να δουλεύει χωρίς λογαριασμούς.
void AddValidatedOptions<T>(string section) where T : class
{
    var o = builder.Services.AddOptions<T>().Bind(builder.Configuration.GetSection(section));
    if (assistantMode) o.ValidateDataAnnotations().ValidateOnStart();
}

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services);

var voiceOptions = app.Services.GetRequiredService<IOptions<VoiceOptions>>().Value;
if (app.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(voiceOptions.DemoClinicPhoneNumber))
{
    await DatabaseInitializer.SeedDemoClinicAsync(app.Services, voiceOptions.DemoClinicPhoneNumber, voiceOptions.DemoClinicCalendarId);
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/", () => "Slotify Voice");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapTwilioEndpoints();

app.Run();
