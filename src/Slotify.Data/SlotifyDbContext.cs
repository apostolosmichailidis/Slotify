using Microsoft.EntityFrameworkCore;
using Slotify.Core.Calls;
using Slotify.Core.Clinics;

namespace Slotify.Data;

public class SlotifyDbContext(DbContextOptions<SlotifyDbContext> options) : DbContext(options)
{
    public DbSet<Clinic> Clinics => Set<Clinic>();
    public DbSet<OpeningHours> OpeningHours => Set<OpeningHours>();
    public DbSet<ClinicService> Services => Set<ClinicService>();
    public DbSet<ClinicClosure> Closures => Set<ClinicClosure>();
    public DbSet<Call> Calls => Set<Call>();

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
