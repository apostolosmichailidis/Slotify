using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Slotify.Core.Abstractions;
using Slotify.Core.Calls;

namespace Slotify.Data;

internal sealed class CallLog(IDbContextFactory<SlotifyDbContext> dbFactory, ILogger<CallLog> logger) : ICallLog
{
    public async Task StartAsync(Call call, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Calls.Add(call);
        await db.SaveChangesAsync(ct);
    }

    public async Task CompleteAsync(
        string callSid,
        CallOutcome outcome,
        string? summary = null,
        string? transcript = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var call = await db.Calls.FirstOrDefaultAsync(c => c.CallSid == callSid, ct);
        if (call is null)
        {
            logger.LogWarning("Δεν βρέθηκε κλήση {CallSid} για να κλείσει", callSid);
            return;
        }

        call.EndedAt = DateTimeOffset.UtcNow;
        call.Outcome = outcome;
        call.Summary = summary ?? call.Summary;
        call.Transcript = transcript ?? call.Transcript;
        await db.SaveChangesAsync(ct);
    }
}
