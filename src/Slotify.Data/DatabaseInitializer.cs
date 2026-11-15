using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Slotify.Core;
using Slotify.Core.Clinics;

namespace Slotify.Data;

public static class DatabaseInitializer
{
    /// <summary>
    /// Φέρνει τη βάση στην τελευταία μορφή με τα migrations. Το τρέχουν και το Voice και το Admin στην εκκίνηση·
    /// το EF κρατάει κλείδωμα στη βάση, οπότε δεν πειράζει αν ξεκινήσουν μαζί.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<SlotifyDbContext>>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Slotify.Database");
        await using var db = await factory.CreateDbContextAsync(ct);

        await AdoptEnsureCreatedDatabaseAsync(db, logger, ct);

        // Σε ολοκαίνουργια βάση το EF γράφει μία φορά «fail ... __EFMigrationsHistory» πριν φτιάξει τον πίνακα. Δεν πειράζει.
        var before = (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet();
        if (db.Database.GetMigrations().All(before.Contains)) return;

        // Αν ξεκινήσουν μαζί Voice και Admin, η μία περιμένει το κλείδωμα του EF και η άλλη περνάει τα migrations.
        // Το log λέει μόνο σε ποια μορφή είναι η βάση μετά, όχι ποια εφαρμογή την ενημέρωσε.
        await db.Database.MigrateAsync(ct);
        logger.LogInformation("Η βάση είναι στο {Migration}", db.Database.GetMigrations().Last());
    }

    /// <summary>
    /// Πριν από τα migrations οι πίνακες φτιάχνονταν με EnsureCreated, χωρίς ιστορικό migrations.
    /// Σε τέτοια βάση γράφουμε ότι το πρώτο migration έχει ήδη εφαρμοστεί, αλλιώς θα προσπαθούσε
    /// να ξαναφτιάξει πίνακες που υπάρχουν. Τα δεδομένα μένουν ως έχουν.
    /// </summary>
    private static async Task AdoptEnsureCreatedDatabaseAsync(SlotifyDbContext db, ILogger logger, CancellationToken ct)
    {
        var first = db.Database.GetMigrations().First();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Το ίδιο κλείδωμα για Voice και Admin, ώστε να μην το κάνουν και οι δύο ταυτόχρονα.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(4711001)", ct);

        var hasTables = await ScalarAsync(db, "SELECT to_regclass('public.clinics') IS NOT NULL", ct);
        var hasHistory = await ScalarAsync(db, "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL", ct);

        if (hasTables && !hasHistory)
        {
            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
            await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(first, ProductInfo.GetVersion())), ct);
            logger.LogWarning("Η βάση είχε φτιαχτεί με EnsureCreated. Σημειώθηκε ως {Migration} και συνεχίζει με migrations", first);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<bool> ScalarAsync(SlotifyDbContext db, string sql, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<bool>(sql + " AS \"Value\"").SingleAsync(ct);

    /// <summary>Φτιάχνει ένα δοκιμαστικό οδοντιατρείο στον αριθμό που δίνεις, αν δεν υπάρχει ήδη.</summary>
    public static async Task SeedDemoClinicAsync(IServiceProvider services, string phoneNumber, string? calendarId = null, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<SlotifyDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        var normalized = PhoneNumbers.Normalize(phoneNumber);
        var existing = await db.Clinics.FirstOrDefaultAsync(c => c.PhoneNumber == normalized, ct);
        if (existing is not null)
        {
            // Αν προστέθηκε ημερολόγιο μετά την πρώτη εκκίνηση, το περνάμε στο υπάρχον ιατρείο.
            if (!string.IsNullOrWhiteSpace(calendarId) && existing.GoogleCalendarId != calendarId)
            {
                existing.GoogleCalendarId = calendarId;
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        var clinic = new Clinic { Name = "Οδοντιατρείο Δοκιμής", PhoneNumber = normalized, GoogleCalendarId = calendarId };

        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
        {
            clinic.OpeningHours.Add(new OpeningHours { Day = day, Opens = new TimeOnly(9, 0), Closes = new TimeOnly(14, 0) });
            if (day is DayOfWeek.Tuesday or DayOfWeek.Thursday)
                clinic.OpeningHours.Add(new OpeningHours { Day = day, Opens = new TimeOnly(17, 30), Closes = new TimeOnly(21, 0) });
        }

        clinic.Services.Add(new ClinicService { Name = "Εξέταση", DurationMinutes = 30, Description = "Πρώτη επίσκεψη ή έλεγχος" });
        clinic.Services.Add(new ClinicService { Name = "Καθαρισμός", DurationMinutes = 45 });
        clinic.Services.Add(new ClinicService { Name = "Σφράγισμα", DurationMinutes = 60 });

        db.Clinics.Add(clinic);
        await db.SaveChangesAsync(ct);
    }
}
