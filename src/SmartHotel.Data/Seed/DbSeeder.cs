using Microsoft.EntityFrameworkCore;

using SmartHotel.Data.Entities;

namespace SmartHotel.Data.Seed;

/// <summary>
/// Inserts the <see cref="SeedCatalog"/> hotels, room types and rooms into a migrated SmartHotel database that holds
/// no hotel yet.
/// </summary>
public static class DbSeeder
{
    /// <summary>
    /// Inserts the whole catalogue in one transaction, unless the database already holds a hotel.
    /// </summary>
    /// <param name="db">
    /// The context over the migrated database to seed. It must be dedicated to the seed: tracking no entities and with
    /// no transaction open, such as a freshly resolved scoped context. After a successful return it tracks nothing.
    /// After an exception it must be discarded, because its tracker can still hold seed entities whose rows were
    /// rolled back.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the seed. A seed cancelled before its commit rolls back every row it inserted.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the catalogue was inserted; <see langword="false"/> when the seed was skipped
    /// because at least one hotel already exists, in which case nothing was written.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Idempotency: the check for an existing hotel and every insert run inside one transaction. A failure or
    /// cancellation before the commit rolls back every seed row, so no partial catalogue persists. Each call decides
    /// from the persisted state: it skips when any hotel exists and otherwise inserts the whole catalogue. When the
    /// outcome of the commit is uncertain, for example because its acknowledgement was lost, the call can fail even
    /// though the whole catalogue was committed; the next call then finds the hotels and skips. Once any hotel exists,
    /// every later call changes nothing.
    /// </para>
    /// <para>
    /// Each hotel is saved together with its room types and rooms before the next one is added, so the identity
    /// values follow catalogue order. A rollback does not reset identity sequences, so the ids start at 1, with
    /// hotel 1 as the first catalogue entry, only when no earlier attempt consumed identity values. No key is set
    /// here: EF Core propagates each saved hotel's id to its room types, and each room type's alternate key
    /// <c>(Id, HotelId)</c> to its rooms, where the composite foreign key to the room type checks it.
    /// </para>
    /// <para>
    /// Preconditions: <paramref name="db"/> must track no entities, because every save here writes all pending changes
    /// in the context and the final clear detaches everything it tracks; a context that tracks anything is rejected
    /// before any database work. The caller must not already have a transaction open on it, because this method
    /// begins its own, and the context must use no retrying execution strategy, which rejects user-initiated
    /// transactions. The empty-table check is not guarded against a second seeder running at the same time; that is
    /// safe because startup migration and seeding assume a single API instance.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="db"/> already tracks entities, or it already has a transaction open.
    /// </exception>
    public static async Task<bool> SeedAsync(SmartHotelDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        // SaveChangesAsync below writes every pending entry in the context and Clear() detaches every tracked entity,
        // so a context that already tracks anything could commit or lose state that is not part of the catalogue.
        if (db.ChangeTracker.Entries().Any())
        {
            throw new InvalidOperationException(
                "The seed requires a dedicated context that tracks no entities; use a freshly created context.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await db.Hotels.AnyAsync(cancellationToken))
        {
            // Disposing the uncommitted transaction rolls it back, so a skipped seed writes nothing.
            return false;
        }

        foreach (var entry in SeedCatalog.Hotels)
        {
            db.Hotels.Add(BuildHotel(entry));

            // One save per hotel: rows saved together carry no ordering guarantee, and hotel ids must follow the
            // catalogue.
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        // The tracker holds only the committed seed graph, which is not needed after the commit; a caller that keeps
        // using the context starts clean.
        db.ChangeTracker.Clear();

        return true;
    }

    private static Hotel BuildHotel(SeedCatalog.HotelEntry entry)
    {
        var hotel = new Hotel
        {
            Name = entry.Name,
            City = entry.City,
            Address = entry.Address,
            Description = entry.Description,
        };

        foreach (var type in SeedCatalog.RoomTypes)
        {
            var roomType = new RoomType
            {
                Name = type.Name,
                MaxGuests = type.MaxGuests,
                NightlyPrice = SeedCatalog.NightlyPrice(entry.Number, type.BasePrice),
            };

            for (var index = 1; index <= SeedCatalog.RoomsPerType; index++)
            {
                roomType.Rooms.Add(new Room { RoomNumber = SeedCatalog.RoomNumber(type.Position, index) });
            }

            hotel.RoomTypes.Add(roomType);
        }

        return hotel;
    }
}