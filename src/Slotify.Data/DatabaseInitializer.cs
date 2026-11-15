using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Slotify.Core;
using Slotify.Core.Clinics;

namespace Slotify.Data;

public static class DatabaseInitializer
{
    /// <summary>
    /// Φέρνει τη βάση στην τελευταία μορφή. Όσο δεν υπάρχουν migrations
    /// (στην αρχή του project), φτιάχνει τους πίνακες απευθείας.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<SlotifyDbContext>>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Slotify.Database");
        await using var db = await factory.CreateDbContextAsync(ct);

        if (db.Database.GetMigrations().Any())
        {
            await db.Database.MigrateAsync(ct);
            logger.LogInformation("Η βάση ενημερώθηκε με migrations");
        }
        else
        {
            await db.Database.EnsureCreatedAsync(ct);
            logger.LogWarning("Δεν υπάρχουν migrations ακόμα, οι πίνακες φτιάχτηκαν με EnsureCreated");
        }
    }

    /// <summary>Φτιάχνει ένα δοκιμαστικό οδοντιατρείο στον αριθμό που δίνεις, αν δεν υπάρχει ήδη.</summary>
    public static async Task SeedDemoClinicAsync(IServiceProvider services, string phoneNumber, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<SlotifyDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        var normalized = PhoneNumbers.Normalize(phoneNumber);
        if (await db.Clinics.AnyAsync(c => c.PhoneNumber == normalized, ct)) return;

        var clinic = new Clinic { Name = "Οδοντιατρείο Δοκιμής", PhoneNumber = normalized };

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
