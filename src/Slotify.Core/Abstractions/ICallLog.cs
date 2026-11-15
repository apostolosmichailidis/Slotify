using Slotify.Core.Calls;
using Slotify.Core.Notifications;

namespace Slotify.Core.Abstractions;

/// <summary>Γράφει το ιστορικό κλήσεων. Η υλοποίηση είναι στο Slotify.Data.</summary>
public interface ICallLog
{
    Task StartAsync(Call call, CancellationToken ct = default);

    /// <summary>Κλείνει την κλήση και γράφει, μαζί και ατομικά, τα email/SMS που πρέπει να σταλούν.</summary>
    Task CompleteAsync(
        string callSid,
        CallOutcome outcome,
        string? summary = null,
        string? transcript = null,
        IReadOnlyList<Notification>? notifications = null,
        CancellationToken ct = default);
}
