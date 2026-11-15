using Slotify.Core.Calls;

namespace Slotify.Core.Abstractions;

/// <summary>Γράφει το ιστορικό κλήσεων. Η υλοποίηση είναι στο Slotify.Data.</summary>
public interface ICallLog
{
    Task StartAsync(Call call, CancellationToken ct = default);

    Task CompleteAsync(
        string callSid,
        CallOutcome outcome,
        string? summary = null,
        string? transcript = null,
        CancellationToken ct = default);
}
