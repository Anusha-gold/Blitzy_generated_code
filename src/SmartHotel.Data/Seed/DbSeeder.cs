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
    /// The context over the migrated database to seed. It must not have a transaction open, and any entities it
    /// tracks are detached once the catalogue is inserted.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the seed. A seed cancelled before its commit rolls back and leaves the catalogue tables unchanged.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the catalogue was inserted; <see langword="false"/> when the seed was skipped
    /// because at least one hotel already exists, in which case nothing was written.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Idempotency: the check for an existing hotel and every insert run inside one transaction, so the seed writes
    /// either the whole catalogue or nothing. A failed or cancelled seed leaves the tables empty and the next start
    /// seeds again; once any hotel exists, every later call changes nothing.
    /// </para>
    /// <para>
    /// Each hotel is saved together with its room types and rooms before the next one is added, so on a fresh
    /// database the identity values follow catalogue order and hotel 1 is the first catalogue entry. No key is set
    /// here: EF Core propagates each saved hotel's id to its room types, and each room type's alternate key
    /// <c>(Id, HotelId)</c> to its rooms, where the composite foreign key to the room type checks it.
    /// </para>
    /// <para>
    /// Preconditions: the caller must not already have a transaction open on <paramref name="db"/>, because this
    /// method begins its own, and the context must use no retrying execution strategy, which rejects user-initiated
    /// transactions. The empty-table check is not guarded against a second seeder running at the same time; that is
    /// safe because startup migration and seeding assume a single API instance.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> is <see langword="null"/>.</exception>
    public static async Task<bool> SeedAsync(SmartHotelDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

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

        // The inserted graph is not needed after the commit; a caller that keeps using the context starts clean.
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