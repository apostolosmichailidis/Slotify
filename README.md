# Slotify

Τηλεφωνική AI γραμματεία για ιατρεία: σηκώνει το τηλέφωνο, μιλάει ελληνικά και κλείνει ραντεβού.

## Δομή

```
Slotify.slnx
src/
  Slotify.Core    Μοντέλα (ιατρείο, ωράριο, υπηρεσίες, κλήσεις) και λογική ελεύθερων ωρών. Χωρίς εξαρτήσεις.
  Slotify.Data    Postgres με Entity Framework Core.
  Slotify.Voice   ASP.NET Core API: webhook του Twilio + WebSocket για τον ζωντανό ήχο.
  Slotify.Admin   Διαχειριστικό (Blazor Server). Προς το παρόν σκέτο template.
docker-compose.yml  Postgres + Voice + Admin
```

## Πώς κυλάει μια κλήση

1. Ο ασθενής καλεί τον αριθμό Twilio του ιατρείου.
2. Το Twilio κάνει `POST /twilio/voice`. Ελέγχουμε την υπογραφή `X-Twilio-Signature`, βρίσκουμε το ιατρείο από τον αριθμό `To`
   και απαντάμε με TwiML `<Connect><Stream>` που δείχνει στο WebSocket μας, μαζί με ένα υπογεγραμμένο token.
3. Το Twilio ανοίγει `wss://.../twilio/stream` και στέλνει τον ήχο σε κομμάτια των 20 ms (μ-law, 8 kHz).
4. Το `CallSessionRunner` ελέγχει το token, γράφει την κλήση στη βάση και δίνει τον ήχο σε έναν `IVoiceAgent`.
5. Ο agent στέλνει ήχο πίσω από το ίδιο WebSocket. Όταν κλείσει το WebSocket, κλείνει και η κλήση.

Agents που υπάρχουν τώρα (ρύθμιση `Voice:Mode`):
- **Assistant**: η κανονική γραμματεία (δες παρακάτω).
- **Echo**: ακούς τη φωνή σου πίσω. Η πρώτη δοκιμή, για να δεις ότι ο ήχος πάει κι έρχεται.
- **Silent**: μόνο ακούει και γράφει στο log.

## Η γραμματεία (Assistant)

```
Twilio ──ήχος──▶ Deepgram (nova-3, el) ──κείμενο──▶ Claude (Haiku, σε ροή)
   ▲                                                    │ πρόταση-πρόταση
   └────────── ήχος μ-law 8 kHz ◀── ElevenLabs (Flash) ◀─┘
```

- **Σε ροή παντού.** Το Claude δεν περιμένει να τελειώσει όλη η απάντηση: κάθε πρόταση πάει αμέσως στη φωνή
  (`SentenceSplitter`, που ξέρει ότι το ελληνικό ερωτηματικό είναι `;`). Το ElevenLabs βγάζει απευθείας `ulaw_8000`,
  τη μορφή του Twilio, χωρίς μετατροπή.
- **Διακοπές.** Αν ο ασθενής μιλήσει όσο η γραμματεία σκέφτεται ή μιλάει, στέλνουμε `clear` στο Twilio και σωπαίνει αμέσως.
  Μετά από κάθε πρόταση στέλνουμε ένα `mark`, που επιστρέφει όταν παίξει, οπότε ξέρουμε ακριβώς τι πρόλαβε να ακουστεί.
  Μόνο αυτό μένει στο ιστορικό, για να ξέρει το Claude πού έμεινε.
- **Ο χαιρετισμός δεν κόβεται ποτέ**, γιατί λέει ότι ο καλών μιλάει με AI (Κανονισμός AI της ΕΕ). Αν ο ασθενής μιλήσει από πάνω,
  η απάντηση ετοιμάζεται κανονικά και ο ήχος της ξεκινά μόλις τελειώσει ο χαιρετισμός.
- **Κλείσιμο.** Το Claude λέει αντίο και καλεί το εργαλείο `end_call`. Περιμένουμε να ακουστεί το αντίο και κλείνουμε.
- **Σφάλματα.** Αν πέσει κάποια υπηρεσία, η γραμματεία λέει ότι θα καλέσει το ιατρείο και κλείνει. Η κλήση γράφεται ως `Failed`.
- **Ιστορικό.** Στο τέλος γράφονται στη βάση το αποτέλεσμα και η απομαγνητοφώνηση.
- Οι οδηγίες της γραμματείας φτιάχνονται από τα στοιχεία του ιατρείου (`PromptBuilder`): ωράριο, υπηρεσίες, αργίες, πληροφορίες.

Προς το παρόν η γραμματεία **δεν βλέπει το ημερολόγιο**: παίρνει όνομα, υπηρεσία και επιθυμητή ώρα και λέει ότι θα καλέσει το ιατρείο.
Επόμενο βήμα: Google Calendar.

### Λογαριασμοί που χρειάζεσαι

| Υπηρεσία | Τι κάνει | Ρύθμιση |
|---|---|---|
| [Deepgram](https://console.deepgram.com) | ακούει | `Deepgram:ApiKey` |
| [ElevenLabs](https://elevenlabs.io) | μιλάει | `ElevenLabs:ApiKey`, `ElevenLabs:VoiceId` (από τη βιβλιοθήκη φωνών) |
| [Anthropic](https://console.anthropic.com) | σκέφτεται | `Anthropic:ApiKey` |

```bash
cd src/Slotify.Voice
dotnet user-secrets set "Voice:Mode" "Assistant"
dotnet user-secrets set "Deepgram:ApiKey" "..."
dotnet user-secrets set "ElevenLabs:ApiKey" "..."
dotnet user-secrets set "ElevenLabs:VoiceId" "..."
dotnet user-secrets set "Anthropic:ApiKey" "..."
```

Σε `Assistant` η εφαρμογή δεν ξεκινά αν λείπει κάποιο κλειδί, και λέει ποιο. Σε `Echo` και `Silent` δεν χρειάζονται.

Ρυθμίσεις που αξίζει να πειράξεις μετά τις πρώτες κλήσεις:
- `Deepgram:EndpointingMs` (400): πόση σιωπή σημαίνει «τελείωσα». Μικρότερο = πιο γρήγορη απάντηση, αλλά κόβει όσους κάνουν παύσεις.
- `ElevenLabs:Speed`, `Stability`: ρυθμός και σταθερότητα της φωνής.
- `Anthropic:Model`: `claude-haiku-5-5` για ταχύτητα και κόστος.

## Τοπική εκκίνηση

Χρειάζεσαι .NET 10 SDK, Docker και [ngrok](https://ngrok.com).

```bash
# 1. Βάση
docker compose up -d db

# 2. Δημόσια διεύθυνση για το Twilio
ngrok http 8000          # κράτα το https://xxxx.ngrok-free.app

# 3. Μυστικά (μένουν στον υπολογιστή σου, όχι στο Git)
cd src/Slotify.Voice
dotnet user-secrets init
dotnet user-secrets set "Voice:PublicBaseUrl" "https://xxxx.ngrok-free.app"
dotnet user-secrets set "Voice:DemoClinicPhoneNumber" "+30xxxxxxxxxx"   # ο αριθμός Twilio σου
dotnet user-secrets set "Voice:StreamSecret" "$(openssl rand -base64 32)"
dotnet user-secrets set "Twilio:AuthToken" "το-auth-token-σου"

# 4. Εκκίνηση (http://localhost:8000)
dotnet run
```

Στην πρώτη εκκίνηση φτιάχνονται οι πίνακες και ένα «Οδοντιατρείο Δοκιμής» στον αριθμό που έδωσες.

**Στο Twilio Console:** Phone Numbers → ο αριθμός σου → Voice Configuration →
«A call comes in»: Webhook, `https://xxxx.ngrok-free.app/twilio/voice`, HTTP POST.

Πάρε τηλέφωνο. Με `Voice:Mode = Echo` πρέπει να ακούς τη φωνή σου πίσω. Όταν δουλέψει αυτό, βάλε τα κλειδιά
(παραπάνω) και γύρνα σε `Assistant`.

> Αν αλλάξει το URL του ngrok, άλλαξε και το `Voice:PublicBaseUrl` και το webhook στο Twilio.
> Η υπογραφή υπολογίζεται πάνω στο URL, και αν δεν ταιριάζουν, το webhook γυρνάει 403.

## Βάση δεδομένων

Όσο δεν υπάρχουν migrations, η εφαρμογή φτιάχνει τους πίνακες μόνη της (`EnsureCreated`).
Πριν από την παραγωγή, φτιάξε το πρώτο migration:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add Initial -p src/Slotify.Data
```

Από εκεί και πέρα η εφαρμογή τρέχει τα migrations μόνη της στην εκκίνηση.

## Ασφάλεια

- Κάθε webhook ελέγχεται με την υπογραφή του Twilio. Το `Twilio:ValidateSignature=false` υπάρχει μόνο για δοκιμές με curl.
- Το WebSocket δέχεται μόνο streams με token που έβγαλε το δικό μας webhook για τη συγκεκριμένη κλήση.
- Τα μυστικά μπαίνουν σε user-secrets ή στο `.env`, ποτέ στο `appsettings.json`.
