using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Slotify.Data;

/// <summary>
/// Για το `dotnet ef migrations add ...`. Παίρνει το connection string από τη
/// μεταβλητή SLOTIFY_DB, αλλιώς χρησιμοποιεί την τοπική βάση του docker-compose.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SlotifyDbContext>
{
    public SlotifyDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("SLOTIFY_DB")
                 ?? "Host=localhost;Port=5432;Database=slotify;Username=slotify;Password=slotify";
        return new SlotifyDbContext(new DbContextOptionsBuilder<SlotifyDbContext>().UseNpgsql(cs).Options);
    }
}
