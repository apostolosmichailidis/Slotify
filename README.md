# Slotify

Τηλεφωνική AI γραμματεία για ιατρεία: σηκώνει το τηλέφωνο, μιλάει ελληνικά και κλείνει ραντεβού.

## Δομή

```
Slotify.slnx
src/
  Slotify.Core    Μοντέλα (ιατρείο, ωράριο, υπηρεσίες, κλήσεις) και λογική ελεύθερων ωρών. Χωρίς εξαρτήσεις.
  Slotify.Data    Postgres με Entity Framework Core.
  Slotify.Voice   ASP.NET Core API: webhook του Twilio + WebSocket για τον ζωντανό ήχο.
  Slotify.Admin   Διαχειριστικό (Blazor Server): ιατρεία, ωράριο, υπηρεσίες, αργίες, κλήσεις, στατιστικά.
docker-compose.yml       Postgres + Voice + Admin (τοπικά)
docker-compose.prod.yml  Παραγωγή: + Caddy (HTTPS) + backup
deploy/                  Caddyfile, script του backup
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
- **Σιωπή.** Αν ο καλών δεν μιλήσει για `Voice:SilencePromptSeconds` (12) αφού τελειώσει η γραμματεία, ρωτάει «Είστε ακόμα εκεί;».
  Αν συνεχίσει η σιωπή για `Voice:SilenceHangUpSeconds` (10), λέει αντίο και κλείνει. Η σιωπή μετράει μόνο όταν η γραμματεία
  δεν μιλάει και δεν σκέφτεται.
- **Όριο διάρκειας.** Στα `Voice:MaxCallMinutes` (10) η γραμματεία κλείνει ευγενικά, για να μην καίγονται λεπτά σε ξεχασμένη γραμμή.
  Ένα λεπτό μετά υπάρχει και σκληρό όριο σε κάθε λειτουργία (και σε Echo/Silent), σε περίπτωση που κάτι κολλήσει.
- **Σφάλματα.** Αν πέσει κάποια υπηρεσία, η γραμματεία λέει ότι θα καλέσει το ιατρείο και κλείνει. Η κλήση γράφεται ως `Failed`.
- **Ιστορικό.** Στο τέλος γράφονται στη βάση το αποτέλεσμα και η απομαγνητοφώνηση.
- Οι οδηγίες της γραμματείας φτιάχνονται από τα στοιχεία του ιατρείου (`PromptBuilder`): ωράριο, υπηρεσίες, αργίες, πληροφορίες.

Αν το ιατρείο έχει συνδέσει **Google Calendar**, η γραμματεία κλείνει ραντεβού κατευθείαν (δες παρακάτω).
Αλλιώς παίρνει όνομα, υπηρεσία και επιθυμητή ώρα και λέει ότι θα καλέσει το ιατρείο.

## Ραντεβού στο Google Calendar

Το Claude έχει δύο εργαλεία:
- `find_available_slots`: οι ελεύθερες ώρες υπολογίζονται από **τον δικό μας κώδικα**, όχι από το μοντέλο:
  ωράριο του ιατρείου + κλειστές μέρες + πιασμένα από το Google (freeBusy) + ελάχιστη προειδοποίηση.
  Επιστρέφει λίγες ώρες απλωμένες μέσα στη μέρα, για πραγματική επιλογή. «Πρωί» είναι ως τις 15:00 και «απόγευμα» από τις 15:00,
  όπως οι βάρδιες των ιατρείων.
- `book_appointment`: ξαναελέγχει ότι η ώρα είναι ακόμα ελεύθερη τη στιγμή του κλεισίματος (με κλείδωμα ανά ημερολόγιο,
  ώστε δύο ταυτόχρονες κλήσεις να μην πάρουν την ίδια ώρα) και γράφει το event με όνομα, τηλέφωνο και υπηρεσία.
  Αν η ώρα πιάστηκε στο μεταξύ, επιστρέφει εναλλακτικές.

Αν το ημερολόγιο δεν απαντά, η κλήση δεν κόβεται: η γραμματεία κρατάει το αίτημα και λέει ότι θα καλέσει το ιατρείο.
Η κλήση γράφεται στη βάση ως `AppointmentBooked`, με περίληψη του ραντεβού.

### Σύνδεση (μία φορά για σένα, μία κοινή χρήση για κάθε γιατρό)

1. [Google Cloud Console](https://console.cloud.google.com): νέο project → ενεργοποίησε το **Google Calendar API**.
2. IAM & Admin → Service Accounts → νέο service account → Keys → Add key → **JSON**. Κατεβαίνει ένα αρχείο.
3. Ο γιατρός (ή εσύ για δοκιμή) ανοίγει το Google Calendar → Ρυθμίσεις ημερολογίου → **Κοινή χρήση με συγκεκριμένα άτομα** →
   βάζει το email του service account (`...@...iam.gserviceaccount.com`) με δικαίωμα **«Αλλαγές σε εκδηλώσεις»**.
4. Το **Calendar ID** βρίσκεται στις ίδιες ρυθμίσεις, στο «Ενσωμάτωση ημερολογίου» (για το κύριο ημερολόγιο είναι το email σου).

```bash
cd src/Slotify.Voice
dotnet user-secrets set "Google:ServiceAccountFile" "/πλήρης/διαδρομή/service-account.json"
dotnet user-secrets set "Voice:DemoClinicCalendarId" "το-calendar-id"
```

Στην επόμενη εκκίνηση το δοκιμαστικό ιατρείο παίρνει το ημερολόγιο. Πάρε τηλέφωνο και κλείσε ένα ραντεβού.

> Για δοκιμές, φτιάξε ένα **ξεχωριστό ημερολόγιο** στο Google Calendar σου, για να μη γεμίσει το κανονικό με δοκιμαστικά ραντεβού.

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

## Email στον γιατρό και SMS στον ασθενή

Στο τέλος κάθε κλήσης γράφονται στη βάση (πίνακας `notifications`) όσα πρέπει να σταλούν, και ένας worker
του Voice τα στέλνει. Αν πέσει ο πάροχος ή ο server, ξαναδοκιμάζει (30″, 2′, 4′ … ως 8 φορές, για 24 ώρες).
Ό,τι δεν φτάσει, φαίνεται στο διαχειριστικό: στην κλήση και ως προειδοποίηση στην αρχική.

| Πότε | Τι | Σε ποιον |
|---|---|---|
| Κλείστηκε ραντεβού | Email «Νέο ραντεβού» | γιατρό, αν έχει email |
| Κλείστηκε ραντεβού | SMS επιβεβαίωσης | ασθενή, αν το ιατρείο έχει ενεργά SMS και ο αριθμός είναι ελληνικό κινητό |
| Η γραμματεία κράτησε μήνυμα | Email «Καλέστε πίσω» με όνομα, τηλέφωνο και τι θέλει | γιατρό |
| Η κλήση κόπηκε από σφάλμα | Email με τον αριθμό για να καλέσει πίσω | γιατρό |

Κλήσεις μόνο για πληροφορίες δεν στέλνουν τίποτα.

Η γραμματεία κρατάει μηνύματα με το εργαλείο `take_message`: για ραντεβού όταν το ιατρείο δεν έχει ημερολόγιο,
για ακύρωση ή αλλαγή ραντεβού, ή όταν ο ασθενής θέλει να μιλήσει με τον γιατρό. Στο διαχειριστικό φαίνονται ως «Μήνυμα».

**Ρυθμίσεις ανά ιατρείο** (Διαχειριστικό → Στοιχεία → Ειδοποιήσεις): email γιατρού, SMS ναι ή όχι,
αποστολέας SMS (έως 11 λατινικά, π.χ. `DrPapadop`). Υπάρχουν κουμπιά για δοκιμαστικό email και SMS.

**Χρέωση SMS.** Η αρχική δείχνει ανά ιατρείο τα SMS του μήνα και τα κομμάτια τους. Τα ελληνικά χωράνε 70 χαρακτήρες
στο κομμάτι, οπότε το SMS επιβεβαίωσης πιάνει συνήθως 2. Τα δοκιμαστικά δεν μετράνε.

### Τι στήνεις εσύ

**Email** (π.χ. MailerSend, δωρεάν πακέτο):
1. Λογαριασμός και **επιβεβαίωση domain**: βάζεις στο DNS τις εγγραφές SPF/DKIM που σου δίνει. Χωρίς αυτό
   τα email πάνε στα ανεπιθύμητα ή δεν φεύγουν καθόλου.
2. Domains → SMTP → νέος SMTP user. Παίρνεις username και password.
3. Στο `.env`: `SMTP_HOST=smtp.mailersend.net`, `SMTP_PORT=587`, `SMTP_USERNAME`, `SMTP_PASSWORD`,
   `EMAIL_FROM=noreply@domain.gr`. Με άλλον πάροχο αλλάζεις μόνο αυτά.

**SMS** (με τον ίδιο λογαριασμό Twilio):
1. Βάλε το `TWILIO_ACCOUNT_SID` στο `.env`.
2. Twilio Console → Messaging → Settings → Geo permissions: ενεργοποίησε την **Ελλάδα**.
3. Τα SMS φεύγουν με αποστολέα κείμενο (alphanumeric sender ID). Η Ελλάδα το δέχεται, αλλά καλό είναι
   να στείλεις πρώτα ένα δοκιμαστικό από το διαχειριστικό στο κινητό σου.

Τοπικά, με user-secrets στο `src/Slotify.Voice`: `Email:SmtpHost`, `Email:SmtpPort`, `Email:Username`,
`Email:Password`, `Email:FromAddress`, `Twilio:AccountSid`.

> Τα email περιέχουν όνομα και τηλέφωνο του ασθενή και τι ζήτησε. Η απομαγνητοφώνηση **δεν** μπαίνει στο email
> και μένει μόνο στη βάση.

## Διαχειριστικό

Στο `http://localhost:8001`. Ένας λογαριασμός διαχειριστή, από τις ρυθμίσεις:

```bash
cd src/Slotify.Admin
dotnet user-secrets init
dotnet user-secrets set "Admin:Password" "ένας-μεγάλος-κωδικός"   # τουλάχιστον 12 χαρακτήρες
dotnet run
```

Όνομα χρήστη `admin` (αλλάζει με `Admin:Username`). Στο Docker μπαίνουν από το `.env`: `ADMIN_USERNAME`, `ADMIN_PASSWORD`.
Χωρίς κωδικό η εφαρμογή δεν ξεκινά.

Τι έχει:
- **Αρχική**: κλήσεις σήμερα, 7 ημερών και μήνα, ραντεβού, σφάλματα, και λεπτά ανά ιατρείο
  (στρογγυλεμένα προς τα πάνω ανά κλήση, όπως χρεώνει το Twilio).
- **Ιατρεία**: νέο ιατρείο, και για καθένα:
  - *Στοιχεία*: όνομα, αριθμός Twilio, χαιρετισμός, πληροφορίες για τη γραμματεία, Google Calendar ID,
    κανόνες ραντεβού (προειδοποίηση, βήμα, μέρες μπροστά). Ανενεργό ιατρείο = η γραμματεία δεν σηκώνει.
  - *Ωράριο*: διαστήματα ανά μέρα (πρωί και απόγευμα), με έλεγχο για επικαλύψεις.
  - *Υπηρεσίες*: όνομα, διάρκεια, περιγραφή. Οι ανενεργές δεν προτείνονται.
  - *Κλειστές μέρες*: διάστημα από–έως (άδεια, συνέδριο) και οι επίσημες αργίες της χρονιάς με ένα κλικ
    (μαζί με Καθαρά Δευτέρα, Πάσχα και Αγίου Πνεύματος).
- **Κλήσεις**: φίλτρα ανά ιατρείο, αποτέλεσμα, ημερομηνίες και τηλέφωνο. Κάθε κλήση με περίληψη και απομαγνητοφώνηση.

Οι αλλαγές ισχύουν από την επόμενη κλήση, χωρίς επανεκκίνηση του Voice.

Ασφάλεια:
- Σύνδεση με cookie (HttpOnly, SameSite=Strict, 7 μέρες). Ως 5 προσπάθειες σύνδεσης το λεπτό.
- Στην παραγωγή βάλ' το πίσω από HTTPS (Caddy/nginx). Το `X-Forwarded-Proto` αναγνωρίζεται για το secure cookie.
- Τα κλειδιά του cookie μένουν στο `Admin:KeysDirectory` (στο Docker σε volume). Αλλιώς κάθε επανεκκίνηση σε αποσυνδέει.

## Βάση δεδομένων

Η μορφή της βάσης αλλάζει μόνο με migrations (`src/Slotify.Data/Migrations`). Voice και Admin τα τρέχουν μόνα τους
στην εκκίνηση. Αν ξεκινήσουν μαζί, δεν πειράζει: το EF κλειδώνει τη βάση όσο τρέχουν τα migrations.

Αν αλλάξεις κάποιο μοντέλο στο Core:

```bash
dotnet tool install --global dotnet-ef        # μία φορά
dotnet ef migrations add ΠεριγραφήΑλλαγής -p src/Slotify.Data -o Migrations
```

> Βάσεις από παλιότερη έκδοση (φτιαγμένες με `EnsureCreated`, χωρίς migrations) περνούν μόνες τους στα migrations
> στην πρώτη εκκίνηση, χωρίς να χαθούν δεδομένα.
> Σε ολοκαίνουργια βάση το log δείχνει μία φορά `fail ... __EFMigrationsHistory`. Είναι αναμενόμενο.

## Ανέβασμα σε server

Όλα τρέχουν με Docker Compose. Το Caddy βγάζει μόνο του πιστοποιητικά HTTPS, και μόνο αυτό ανοίγει θύρες (80/443).
Η βάση, το Voice και το Admin δεν φαίνονται απ' έξω.

```
Twilio ──https/wss──▶ voice.domain.gr ─┐
                                       ├─ Caddy ─▶ voice / admin ─▶ Postgres ◀─ backup (κάθε μέρα)
εσύ ────────https───▶ admin.domain.gr ─┘
```

### Τι κάνεις εσύ (μία φορά)

1. **Server.** Ένα VPS με Ubuntu 24.04 (π.χ. Hetzner CX22, 2 vCPU / 4 GB, σε Ευρώπη). Μπαίνεις με SSH key.
2. **Domain.** Στο DNS του domain σου βάζεις δύο εγγραφές `A` προς την IP του server:
   `voice.domain.gr` και `admin.domain.gr`. Περίμενε να «πιάσουν» (`ping voice.domain.gr` δείχνει την IP).
3. **Docker και firewall** στον server:
   ```bash
   curl -fsSL https://get.docker.com | sh
   ufw allow OpenSSH && ufw allow 80 && ufw allow 443 && ufw enable
   ```
4. **Κώδικας και ρυθμίσεις:**
   ```bash
   git clone https://github.com/apostolosmichailidis/Slotify.git && cd Slotify
   cp .env.example .env && nano .env
   ```
   Συμπλήρωσε: `VOICE_DOMAIN`, `ADMIN_DOMAIN`, `ADMIN_PASSWORD`, `POSTGRES_PASSWORD` (νέο, δυνατό),
   `STREAM_SECRET` (`openssl rand -base64 32`), `TWILIO_AUTH_TOKEN`, `VOICE_MODE=Assistant` και τα κλειδιά
   Deepgram / ElevenLabs / Anthropic / Google. Το `PUBLIC_BASE_URL` δεν χρειάζεται εδώ, βγαίνει από το `VOICE_DOMAIN`.
5. **Εκκίνηση:**
   ```bash
   docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
   docker compose -f docker-compose.yml -f docker-compose.prod.yml logs -f caddy voice admin
   ```
6. **Διαχειριστικό:** άνοιξε `https://admin.domain.gr` και φτιάξε το ιατρείο με τον αριθμό Twilio, το ωράριο και τις υπηρεσίες.
   Στην παραγωγή δεν φτιάχνεται δοκιμαστικό ιατρείο.
7. **Twilio Console:** στον αριθμό, «A call comes in» → Webhook `https://voice.domain.gr/twilio/voice`, HTTP POST.
8. Πάρε τηλέφωνο.

### Ενημέρωση σε νέα έκδοση

```bash
git pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
```

Τα migrations τρέχουν μόνα τους. Όσο ξαναξεκινά το Voice, όποια κλήση είναι σε εξέλιξη κόβεται, οπότε κάν' το εκτός ωραρίου.

### Backup

Το container `backup` κρατάει ένα αντίγραφο της βάσης την ημέρα στον φάκελο `backups/` του server
(τελευταίες 14 μέρες, αλλάζει με `BACKUP_KEEP_DAYS`). **Ο φάκελος είναι στον ίδιο server**: αν χαθεί ο server,
χάνονται κι αυτά. Κατέβαζέ τα πού και πού στον υπολογιστή σου, ή ενεργοποίησε τα backups του VPS
(στη Hetzner κοστίζουν 20% της τιμής του server).

```bash
scp root@server:Slotify/backups/slotify-*.dump .        # κατέβασμα
```

Επαναφορά (σβήνει ό,τι υπάρχει στη βάση):

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml stop voice admin
docker compose exec -T db pg_restore -U slotify -d slotify --clean --if-exists < backups/slotify-ΗΜΕΡΟΜΗΝΙΑ.dump
docker compose -f docker-compose.yml -f docker-compose.prod.yml start voice admin
```

## Ασφάλεια

- Κάθε webhook ελέγχεται με την υπογραφή του Twilio. Το `Twilio:ValidateSignature=false` υπάρχει μόνο για δοκιμές με curl.
- Το WebSocket δέχεται μόνο streams με token που έβγαλε το δικό μας webhook για τη συγκεκριμένη κλήση.
- Τα μυστικά μπαίνουν σε user-secrets ή στο `.env`, ποτέ στο `appsettings.json`.
