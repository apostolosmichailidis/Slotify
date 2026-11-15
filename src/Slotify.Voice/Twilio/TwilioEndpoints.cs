using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Slotify.Core.Abstractions;

namespace Slotify.Voice.Twilio;

public static class TwilioEndpoints
{
    public static IEndpointRouteBuilder MapTwilioEndpoints(this IEndpointRouteBuilder app)
    {
        // 1. Το Twilio καλεί εδώ μόλις χτυπήσει το τηλέφωνο και ρωτάει τι να κάνει.
        //    Απαντάμε με TwiML: «σύνδεσε τον ήχο σε αυτό το WebSocket».
        app.MapPost("/twilio/voice", HandleIncomingCallAsync);

        // 2. Το Twilio ανοίγει εδώ το WebSocket και στέλνει τον ήχο ζωντανά.
        app.Map("/twilio/stream", async (HttpContext http, CallSessionRunner runner) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await runner.RunAsync(socket, http.RequestAborted);
        });

        return app;
    }

    private static async Task<IResult> HandleIncomingCallAsync(
        HttpContext http,
        IClinicDirectory clinics,
        TwilioSignatureValidator validator,
        StreamTokens tokens,
        IOptions<VoiceOptions> voice,
        IOptions<TwilioOptions> twilio,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Slotify.Twilio");
        var ct = http.RequestAborted;
        var form = await http.Request.ReadFormAsync(ct);
        var baseUrl = voice.Value.PublicBaseUrl.TrimEnd('/');

        if (twilio.Value.ValidateSignature)
        {
            var url = baseUrl + http.Request.Path + http.Request.QueryString;
            var parameters = form.SelectMany(f => f.Value.Select(v => KeyValuePair.Create(f.Key, v ?? "")));
            if (!validator.IsValid(url, parameters, http.Request.Headers["X-Twilio-Signature"]))
            {
                logger.LogWarning("Webhook χωρίς σωστή υπογραφή Twilio από {Ip}", http.Connection.RemoteIpAddress);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
        }

        var callSid = form["CallSid"].ToString();
        var to = form["To"].ToString();
        var from = form["From"].ToString();
        if (string.IsNullOrWhiteSpace(callSid) || string.IsNullOrWhiteSpace(to))
            return Results.BadRequest();

        var clinic = await clinics.FindByPhoneNumberAsync(to, ct);
        if (clinic is null)
        {
            logger.LogWarning("Κλήση στον {To} που δεν ανήκει σε ενεργό ιατρείο", to);
            return TwiML(new XElement("Response", new XElement("Reject")));
        }

        var streamUrl = ToWebSocketUrl(baseUrl) + "/twilio/stream";
        var response = new XElement("Response",
            new XElement("Connect",
                new XElement("Stream",
                    new XAttribute("url", streamUrl),
                    Parameter("clinicId", clinic.Id.ToString("N")),
                    Parameter("from", from),
                    Parameter("token", tokens.Create(callSid, clinic.Id)))));

        return TwiML(response);
    }

    private static XElement Parameter(string name, string value) =>
        new("Parameter", new XAttribute("name", name), new XAttribute("value", value));

    private static IResult TwiML(XElement response) =>
        Results.Content(
            new XDocument(new XDeclaration("1.0", "UTF-8", null), response).Declaration + response.ToString(SaveOptions.DisableFormatting),
            "text/xml; charset=utf-8");

    internal static string ToWebSocketUrl(string httpUrl) =>
        httpUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + httpUrl[8..]
        : httpUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + httpUrl[7..]
        : httpUrl;
}
