using Microsoft.EntityFrameworkCore;
using Slotify.Core.Calls;
using Slotify.Core.Clinics;
using Slotify.Core.Notifications;

namespace Slotify.Data;

public class SlotifyDbContext(DbContextOptions<SlotifyDbContext> options) : DbContext(options)
{
    public DbSet<Clinic> Clinics => Set<Clinic>();
    public DbSet<OpeningHours> OpeningHours => Set<OpeningHours>();
    public DbSet<ClinicService> Services => Set<ClinicService>();
    public DbSet<ClinicClosure> Closures => Set<ClinicClosure>();
    public DbSet<Call> Calls => Set<Call>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Clinic>(e =>
        {
            e.ToTable("clinics");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.PhoneNumber).HasMaxLength(20);
            e.HasIndex(x => x.PhoneNumber).IsUnique();
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.GoogleCalendarId).HasMaxLength(256);
            e.Property(x => x.Greeting).HasMaxLength(1000);
            e.Property(x => x.NotificationEmail).HasMaxLength(500);
            e.Property(x => x.SmsSenderId).HasMaxLength(11);

            e.Ignore(x => x.EffectiveGreeting);
            e.Ignore(x => x.TimeZone);

            e.HasMany(x => x.OpeningHours).WithOne().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Services).WithOne().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Closures).WithOne().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OpeningHours>(e =>
        {
            e.ToTable("opening_hours");
            e.HasIndex(x => new { x.ClinicId, x.Day });
        });

        b.Entity<ClinicService>(e =>
        {
            e.ToTable("clinic_services");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
        });

        b.Entity<ClinicClosure>(e =>
        {
            e.ToTable("clinic_closures");
            e.HasIndex(x => new { x.ClinicId, x.Date }).IsUnique();
            e.Property(x => x.Reason).HasMaxLength(200);
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.To).HasMaxLength(500);
            e.Property(x => x.Subject).HasMaxLength(300);
            e.Property(x => x.Error).HasMaxLength(500);
            e.Property(x => x.ProviderId).HasMaxLength(100);
            e.Ignore(x => x.GaveUp);

            // Ο worker ψάχνει ό,τι δεν έχει σταλεί. Μερικό index: μένει μικρό όσο κι αν μεγαλώσει ο πίνακας.
            e.HasIndex(x => x.NextAttemptAt).HasFilter("\"SentAt\" IS NULL");
            e.HasIndex(x => new { x.ClinicId, x.CreatedAt });
            e.HasIndex(x => x.CallId);

            e.HasOne<Clinic>().WithMany().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Call>().WithMany().HasForeignKey(x => x.CallId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Call>(e =>
        {
            e.ToTable("calls");
            e.Property(x => x.CallSid).HasMaxLength(64);
            e.HasIndex(x => x.CallSid).IsUnique();
            e.Property(x => x.StreamSid).HasMaxLength(64);
            e.Property(x => x.FromNumber).HasMaxLength(20);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.ClinicId, x.StartedAt });
            e.Ignore(x => x.DurationSeconds);

            e.HasOne<Clinic>().WithMany().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
