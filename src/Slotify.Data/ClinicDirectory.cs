using Microsoft.EntityFrameworkCore;
using Slotify.Core;
using Slotify.Core.Abstractions;
using Slotify.Core.Clinics;

namespace Slotify.Data;

internal sealed class ClinicDirectory(IDbContextFactory<SlotifyDbContext> dbFactory) : IClinicDirectory
{
    public async Task<Clinic?> FindByPhoneNumberAsync(string phoneNumber, CancellationToken ct = default)
    {
        var normalized = PhoneNumbers.Normalize(phoneNumber);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await WithDetails(db).FirstOrDefaultAsync(c => c.PhoneNumber == normalized && c.IsActive, ct);
    }

    public async Task<Clinic?> GetAsync(Guid clinicId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await WithDetails(db).FirstOrDefaultAsync(c => c.Id == clinicId, ct);
    }

    private static IQueryable<Clinic> WithDetails(SlotifyDbContext db)
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        return db.Clinics
            .AsNoTracking()
            .AsSplitQuery()
            .Include(c => c.OpeningHours)
            .Include(c => c.Services.Where(s => s.IsActive))
            .Include(c => c.Closures.Where(x => x.Date >= yesterday));
    }
}
