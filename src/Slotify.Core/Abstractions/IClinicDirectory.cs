using Slotify.Core.Clinics;

namespace Slotify.Core.Abstractions;

/// <summary>Βρίσκει ιατρεία. Η υλοποίηση είναι στο Slotify.Data.</summary>
public interface IClinicDirectory
{
    /// <summary>Το ιατρείο που έχει αυτόν τον αριθμό Twilio, μαζί με ωράριο, υπηρεσίες και κλειστές μέρες.</summary>
    Task<Clinic?> FindByPhoneNumberAsync(string phoneNumber, CancellationToken ct = default);

    Task<Clinic?> GetAsync(Guid clinicId, CancellationToken ct = default);
}
