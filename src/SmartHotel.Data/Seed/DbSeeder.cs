using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using SmartHotel.Data.Entities;

namespace SmartHotel.Data.Seed;

/// <summary>
/// Inserts the <see cref="SeedCatalog"/> hotels, room types and rooms into a migrated SmartHotel database that holds
/// no hotel yet.
/// </summary>
public static class DbSeeder
{
    // Conflicts with the ROW EXCLUSIVE lock that inserts take, so it waits for every open transaction that has inserted
    // hotels.
    private const string LockHotelsSql = "LOCK TABLE \"Hotels\" IN SHARE ROW EXCLUSIVE MODE";

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
    /// from the persisted state: it skips when any hotel exists and otherwise inserts the whole catalogue. The
    /// transaction runs as one retriable unit of the context's execution strategy, so a transient failure, such as a
    /// session the database ended, rolls the attempt back, unless its commit was already under way, and runs the check
    /// and the inserts again. When the acknowledgement of the commit is lost, the call checks for the hotels and
    /// returns <see langword="true"/> if the commit took effect. Such a commit can also take effect only after that
    /// check, while the server is still completing it, so every attempt after a commit began first locks
    /// <c>Hotels</c> in <c>SHARE ROW EXCLUSIVE</c> mode, which waits for that commit's transaction to end, and then
    /// returns <see langword="true"/> if it finds the hotels. The first attempt takes no such lock. Only when the
    /// check and the retries all fail can the call fail even though the whole catalogue was committed; the next call
    /// then finds the hotels and skips. Once any hotel exists, every later call changes nothing.
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
    /// begins its own inside the context's execution strategy, retrying or not; such a context is likewise rejected
    /// before any database work. The first attempt's empty-table check is not guarded against a second seeder running
    /// at the same time; that is safe because startup migration and seeding assume a single API instance.
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

        // Checked here rather than left to BeginTransactionAsync, so the rejection is the same under any execution
        // strategy: a retrying one would otherwise reject the open transaction with a message about itself.
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "The seed requires a context with no transaction open, because it begins its own.");
        }

        // Set once an attempt has saved the whole catalogue and begins its commit, and never cleared: that commit can
        // take effect even after the check that follows its failure, so every later attempt allows for it.
        bool commitStarted = false;

        // The context's retrying execution strategy rejects a transaction begun outside it, so the transaction runs
        // inside the strategy as one retriable unit. A transient failure, such as a session the database ended,
        // abandons the attempt, whose rows roll back with its transaction unless its commit was already under way, and
        // the next attempt starts again from the check for an existing hotel.
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();
        bool inserted = await strategy.ExecuteAsync(
            db,
            async (context, token) =>
            {
                // A failed attempt leaves its seed entities tracked although their rows were rolled back; clearing the
                // tracker limits this attempt's saves to the graph it builds. The first attempt finds it empty.
                context.ChangeTracker.Clear();

                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(token);

                if (commitStarted)
                {
                    // An earlier attempt began its commit, which the server can still be completing although the
                    // check that followed the failure found no hotel. Its inserts hold ROW EXCLUSIVE on Hotels until
                    // its transaction ends, and this mode conflicts with it, so the lock waits for that transaction
                    // and the check below then sees its hotels if it committed. The first attempt has no earlier
                    // transaction to wait for, so it takes no lock.
                    await context.Database.ExecuteSqlRawAsync(LockHotelsSql, token);
                }

                if (await context.Hotels.AnyAsync(token))
                {
                    // Disposing the uncommitted transaction rolls it back, so a skipped seed writes nothing. Hotels
                    // found after an earlier attempt began its commit are the catalogue that commit inserted.
                    return commitStarted;
                }

                foreach (SeedCatalog.HotelEntry entry in SeedCatalog.Hotels)
                {
                    context.Hotels.Add(BuildHotel(entry));

                    // One save per hotel: rows saved together carry no ordering guarantee, and hotel ids must follow
                    // the catalogue.
                    await context.SaveChangesAsync(token);
                }

                commitStarted = true;
                await transaction.CommitAsync(token);
                return true;
            },
            async (context, token) =>
            {
                // Runs after a transient failure, before any retry. Only an attempt that began its commit can have
                // inserted anything, and the hotels then exist once that commit has taken effect, whether or not its
                // acknowledgement arrived. A commit the server is still completing is not visible yet; the next
                // attempt waits for it under the table lock. EF Core discards the result of an unsuccessful check.
                bool committed = commitStarted && await context.Hotels.AnyAsync(token);
                return new ExecutionResult<bool>(committed, committed);
            },
            cancellationToken);

        // The tracker holds at most the committed seed graph, which is not needed after the commit; a caller that
        // keeps using the context starts clean.
        db.ChangeTracker.Clear();

        return inserted;
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