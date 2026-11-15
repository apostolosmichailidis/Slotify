using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slotify.Core.Abstractions;

namespace Slotify.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>Καταχωρεί την Postgres και τις υλοποιήσεις του Core.</summary>
    public static IServiceCollection AddSlotifyData(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Factory αντί για scoped DbContext: το WebSocket μιας κλήσης ζει λεπτά,
        // οπότε ανοίγουμε μικρά context μόνο όταν χρειάζεται.
        services.AddDbContextFactory<SlotifyDbContext>(o => o.UseNpgsql(connectionString));

        services.AddSingleton<IClinicDirectory, ClinicDirectory>();
        services.AddSingleton<ICallLog, CallLog>();
        return services;
    }
}
