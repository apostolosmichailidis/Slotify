using Microsoft.Extensions.Options;
using Slotify.Data;
using Slotify.Voice;
using Slotify.Voice.Agents;
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

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services);

var demoNumber = app.Services.GetRequiredService<IOptions<VoiceOptions>>().Value.DemoClinicPhoneNumber;
if (app.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(demoNumber))
{
    await DatabaseInitializer.SeedDemoClinicAsync(app.Services, demoNumber);
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/", () => "Slotify Voice");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapTwilioEndpoints();

app.Run();
