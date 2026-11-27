using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data;
using SmartHotel.Data.Seed;

namespace SmartHotel.Api.IntegrationTests.Data;

// The factory starts the API host during fixture setup, and the host migrates and seeds this class's database before
// it starts, so every test here reads the catalogue the startup seed inserted. No test writes a row to that database;
// each retry test seeds a database of its own.
public sealed class SeedTests : IClassFixture<SmartHotelApiFactory>
{
    private const int RoomsPerType = 10;
    private const int FloorNumberMultiplier = 100;
    private const decimal HotelPriceStep = 10.00m;
    private const string TestDatabasePrefix = "smarthotel_it_";

    // SHARE mode lets the seed read Hotels, so its check for an existing hotel passes, and blocks its first insert.
    private const string LockHotelsSql = "LOCK TABLE \"Hotels\" IN SHARE MODE";

    // A session of the seeded database, other than the holder, the observer and an excluded earlier session, that is
    // queued on a lock at an insert into Hotels.
    private const string FindInsertWaiterSql =
        "SELECT pid FROM pg_stat_activity "
        + "WHERE datname = current_database() AND pid <> @holderPid AND pid <> @excludedPid "
        + "AND pid <> pg_backend_pid() AND wait_event_type = 'Lock' AND query LIKE '%INSERT INTO \"Hotels\"%'";

    // A session of the seeded database, other than the observer, that waits on a lock the holder's transaction holds.
    private const string FindHolderWaiterSql =
        "SELECT pid FROM pg_stat_activity "
        + "WHERE datname = current_database() AND pid <> pg_backend_pid() AND @holderPid = ANY(pg_blocking_pids(pid))";

    // Ends the session and waits up to 5 s for it to exit; its next read then fails with SqlState 57P01.
    private const string TerminateSessionSql = "SELECT pg_terminate_backend(@pid, 5000)";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ObservationLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(30);

    // The seed-data tables written out as literals rather than read from SeedCatalog: expectations built from the
    // seeder's own data would pass with a wrong catalogue.
    private static readonly ExpectedHotel[] ExpectedHotels =
    [
        new(1, "SmartHotel Seattle Downtown", "Seattle"),
        new(2, "SmartHotel Seattle Waterfront", "Seattle"),
        new(3, "SmartHotel New York Midtown", "New York"),
        new(4, "SmartHotel New York Brooklyn", "New York"),
        new(5, "SmartHotel Chicago Loop", "Chicago"),
    ];

    private static readonly ExpectedRoomType[] ExpectedRoomTypes =
    [
        new(1, "Standard", 2, 129.00m),
        new(2, "Deluxe", 2, 169.00m),
        new(3, "Family", 4, 219.00m),
        new(4, "Suite", 4, 329.00m),
    ];

    private static readonly (string City, int HotelCount)[] ExpectedHotelsPerCity =
    [
        ("Chicago", 1),
        ("New York", 2),
        ("Seattle", 2),
    ];

    private readonly SmartHotelApiFactory _factory;
    private readonly TestPostgres _postgres;

    public SeedTests(SmartHotelApiFactory factory, TestPostgres postgres)
    {
        _factory = factory;
        _postgres = postgres;
    }

    [Fact]
    public async Task Seed_Catalogue_HasFiveHotelsInThreeCitiesWithTwentyRoomTypesAndTwoHundredRooms()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        CatalogueSnapshot catalogue = await _factory.ExecuteDbAsync(
            static (db, token) => ReadCatalogueAsync(db, token),
            ct);
        (bool anyGuest, bool anyBooking) = await _factory.ExecuteDbAsync(
            static async (db, token) => (
                await db.Guests.AsNoTracking().AnyAsync(token),
                await db.Bookings.AsNoTracking().AnyAsync(token)),
            ct);

        Assert.Equal(5, catalogue.Hotels.Count);
        Assert.Equal(
            ExpectedHotels
                .Select(static hotel => (hotel.Name, hotel.City))
                .OrderBy(static hotel => hotel.Name, StringComparer.Ordinal)
                .ToArray(),
            catalogue.Hotels
                .Select(static hotel => (hotel.Name, hotel.City))
                .OrderBy(static hotel => hotel.Name, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ExpectedHotelsPerCity,
            catalogue.Hotels
                .GroupBy(static hotel => hotel.City, StringComparer.Ordinal)
                .Select(static city => (City: city.Key, HotelCount: city.Count()))
                .OrderBy(static city => city.City, StringComparer.Ordinal)
                .ToArray());
        Assert.All(catalogue.Hotels, static hotel =>
        {
            Assert.False(string.IsNullOrWhiteSpace(hotel.Address));
            Assert.False(string.IsNullOrWhiteSpace(hotel.Description));
        });

        Assert.Equal(20, catalogue.RoomTypes.Count);
        Assert.Equal(200, catalogue.Rooms.Count);

        // The seed inserts the catalogue only: no personal data and no bookings.
        Assert.False(anyGuest);
        Assert.False(anyBooking);
    }

    [Fact]
    public async Task Seed_RoomTypes_HaveExpectedNamesCapacitiesAndPrices()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        CatalogueSnapshot catalogue = await _factory.ExecuteDbAsync(
            static (db, token) => ReadCatalogueAsync(db, token),
            ct);

        string[] expectedNames =
            [.. ExpectedRoomTypes.Select(static type => type.Name).Order(StringComparer.Ordinal)];

        foreach (ExpectedHotel hotel in ExpectedHotels)
        {
            int hotelId = Assert.Single(catalogue.Hotels, row => row.Name == hotel.Name).Id;
            RoomTypeRow[] types = [.. catalogue.RoomTypes.Where(row => row.HotelId == hotelId)];

            Assert.Equal(4, types.Length);
            Assert.Equal(expectedNames, types.Select(static row => row.Name).Order(StringComparer.Ordinal).ToArray());

            foreach (ExpectedRoomType expected in ExpectedRoomTypes)
            {
                RoomTypeRow actual = Assert.Single(types, row => row.Name == expected.Name);
                Assert.Equal(expected.MaxGuests, actual.MaxGuests);
                Assert.Equal(ExpectedNightlyPrice(hotel, expected), actual.NightlyPrice);
            }
        }

        // The two worked examples of the price rule: the first hotel's base price, and the last hotel's top price.
        Assert.Equal(
            129.00m,
            Assert.Single(
                catalogue.RoomTypes,
                static row => row.HotelName == "SmartHotel Seattle Downtown" && row.Name == "Standard").NightlyPrice);
        Assert.Equal(
            369.00m,
            Assert.Single(
                catalogue.RoomTypes,
                static row => row.HotelName == "SmartHotel Chicago Loop" && row.Name == "Suite").NightlyPrice);
    }

    [Fact]
    public async Task Seed_RoomNumbers_AreFloorNumberedAndUniquePerHotel()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        CatalogueSnapshot catalogue = await _factory.ExecuteDbAsync(
            static (db, token) => ReadCatalogueAsync(db, token),
            ct);
        IReadOnlyList<RoomRow> rooms = catalogue.Rooms;

        Assert.Equal(200, rooms.Count);

        IGrouping<int, RoomRow>[] roomsByType = [.. rooms.GroupBy(static room => room.RoomTypeId)];
        Assert.Equal(20, roomsByType.Length);
        Assert.All(roomsByType, static type => Assert.Equal(RoomsPerType, type.Count()));

        // Every hotel has the same 40 numbers: 101-110, 201-210, 301-310 and 401-410.
        string[] expectedNumbers =
        [
            .. ExpectedRoomTypes
                .SelectMany(static type => Enumerable
                    .Range(1, RoomsPerType)
                    .Select(index => ((type.Position * FloorNumberMultiplier) + index)
                        .ToString(CultureInfo.InvariantCulture)))
                .Order(StringComparer.Ordinal),
        ];

        foreach (ExpectedHotel hotel in ExpectedHotels)
        {
            int hotelId = Assert.Single(catalogue.Hotels, row => row.Name == hotel.Name).Id;

            Assert.Equal(
                expectedNumbers,
                rooms
                    .Where(room => room.HotelId == hotelId)
                    .Select(static room => room.RoomNumber)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
        }

        // The floor is the room type's position, and each floor holds rooms 1 to 10.
        Assert.All(rooms, static room =>
        {
            ExpectedRoomType type = Assert.Single(ExpectedRoomTypes, expected => expected.Name == room.RoomTypeName);
            int number = int.Parse(room.RoomNumber, CultureInfo.InvariantCulture);

            Assert.Equal(type.Position, number / FloorNumberMultiplier);
            Assert.InRange(number % FloorNumberMultiplier, 1, RoomsPerType);
        });

        // IX_Rooms_HotelId_RoomNumber: no room number repeats within a hotel.
        Assert.Equal(200, rooms.Select(static room => (room.HotelId, room.RoomNumber)).Distinct().Count());

        // The denormalized hotel id always equals the room type's hotel (FK_Rooms_RoomTypes_RoomTypeId_HotelId).
        Assert.All(rooms, static room => Assert.Equal(room.RoomTypeHotelId, room.HotelId));
    }

    [Fact]
    public async Task Seed_SecondRun_ReturnsFalseAndChangesNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        CatalogueSnapshot before = await _factory.ExecuteDbAsync(
            static (db, token) => ReadCatalogueAsync(db, token),
            ct);

        // A fresh scope with no surrounding transaction: the seeder requires an untracked context and opens its own
        // transaction.
        bool inserted = await _factory.ExecuteDbAsync(
            static (db, token) => DbSeeder.SeedAsync(db, token),
            ct);

        CatalogueSnapshot after = await _factory.ExecuteDbAsync(
            static (db, token) => ReadCatalogueAsync(db, token),
            ct);

        Assert.False(inserted);

        Assert.Equal(5, after.Hotels.Count);
        Assert.Equal(20, after.RoomTypes.Count);
        Assert.Equal(200, after.Rooms.Count);

        // Row by row, ordered by id: the second run neither added, removed nor altered a hotel, room type or room.
        Assert.Equal(before.Hotels, after.Hotels);
        Assert.Equal(before.RoomTypes, after.RoomTypes);
        Assert.Equal(before.Rooms, after.Rooms);
    }

    [Fact]
    public async Task Seed_SessionTerminatedDuringInsert_RetriesAndInsertsCatalogueOnce()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A migrated database that no seed has run on yet, so this seed inserts for real and the class's database is
        // left untouched.
        string connectionString = _postgres.CreateDatabaseConnectionString(
            TestDatabasePrefix + Guid.NewGuid().ToString("N"));
        await _postgres.CreateDatabaseAsync(connectionString, ct);
        try
        {

            // The data layer's own registration, so the seed runs under the execution strategy the API uses.
            await using ServiceProvider services = new ServiceCollection()
                .AddSmartHotelData(_ => connectionString)
                .BuildServiceProvider();
            await using (AsyncServiceScope scope = services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<SmartHotelDbContext>().Database.MigrateAsync(ct);
            }

            // The test's own sessions bypass the pool the seed draws from.
            string unpooled = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

            // The holder's lock blocks the seed's first insert inside the transaction the seed has begun, which is
            // where its session is then ended.
            await using NpgsqlConnection holder = new(unpooled);
            await holder.OpenAsync(ct);
            await using NpgsqlTransaction holderTx = await holder.BeginTransactionAsync(ct);
            await using (NpgsqlCommand lockHotels = new(LockHotelsSql, holder, holderTx))
            {
                await lockHotels.ExecuteNonQueryAsync(ct);
            }

            Task<bool> seed = SeedInNewScopeAsync(services, ct);

            int? firstWaiter = null;
            bool terminated = false;
            int? retryWaiter = null;
            bool inserted;
            try
            {
                await using NpgsqlConnection observer = new(unpooled);
                await observer.OpenAsync(ct);

                firstWaiter = await WaitForInsertWaiterAsync(observer, holder.ProcessID, excludedPid: 0, seed, ct);
                if (firstWaiter is int waiterPid)
                {
                    await using NpgsqlCommand terminate = new(TerminateSessionSql, observer);
                    terminate.Parameters.AddWithValue("pid", waiterPid);
                    terminated = (bool)(await terminate.ExecuteScalarAsync(ct))!;

                    // A retry runs the whole unit again on a new session, which queues behind the holder once more.
                    retryWaiter = await WaitForInsertWaiterAsync(observer, holder.ProcessID, waiterPid, seed, ct);
                }
            }
            finally
            {
                // Runs on every path. The holder changed nothing, so the rollback only releases the lock; the seed is
                // then drained, so none is left running into the database drop.
                await holderTx.RollbackAsync(CancellationToken.None);
                inserted = await seed.WaitAsync(DrainLimit, ct);
            }

            Assert.True(firstWaiter is not null, "The seed was not observed waiting to insert a hotel.");
            Assert.True(terminated, "The waiting seed session was not terminated.");
            Assert.True(retryWaiter is not null, "The seed was not observed waiting again on a new session.");
            Assert.True(inserted, "Expected the retried seed to report that it inserted the catalogue.");

            // Exactly one catalogue: the ended attempt's rows rolled back, and its entities did not ride along with the
            // retry's saves.
            CatalogueSnapshot catalogue;
            await using (AsyncServiceScope scope = services.CreateAsyncScope())
            {
                catalogue = await ReadCatalogueAsync(
                    scope.ServiceProvider.GetRequiredService<SmartHotelDbContext>(),
                    ct);
            }

            Assert.Equal(
                ExpectedHotels.Select(static hotel => hotel.Name),
                catalogue.Hotels.Select(static hotel => hotel.Name));
            Assert.Equal(20, catalogue.RoomTypes.Count);
            Assert.Equal(200, catalogue.Rooms.Count);
        }
        finally
        {
            // Not the test's token: the database must be dropped even when the test run is being cancelled.
            await _postgres.DropDatabaseAsync(connectionString, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Seed_CommitTakesEffectAfterCheck_ReturnsTrueAndInsertsCatalogueOnce()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A migrated database that no seed has run on yet, so this seed inserts for real and the class's database is
        // left untouched.
        string connectionString = _postgres.CreateDatabaseConnectionString(
            TestDatabasePrefix + Guid.NewGuid().ToString("N"));
        await _postgres.CreateDatabaseAsync(connectionString, ct);
        try
        {
            // The test's sessions and the seed's bypass the pool, so no session is shared between them.
            string unpooled = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
            }.ConnectionString;
            DbContextOptions<SmartHotelDbContext> plain = new DbContextOptionsBuilder<SmartHotelDbContext>()
                .UseNpgsql(unpooled)
                .Options;
            await using (SmartHotelDbContext migration = new(plain))
            {
                await migration.Database.MigrateAsync(ct);
            }

            // No seed can have its commit completed late on purpose, so it runs on a context of its own, under the
            // API's retrying strategy, whose interceptor moves the first commit's transaction to the holder session and
            // reports a transient failure. The holder keeps that transaction open, with the uncommitted catalogue,
            // past the check that follows the failure, as a server still completing the commit would.
            await using NpgsqlConnection holder = new(unpooled);
            await holder.OpenAsync(ct);
            HoldFirstSeedCommit interceptor = new(holder);
            DbContextOptions<SmartHotelDbContext> options = new DbContextOptionsBuilder<SmartHotelDbContext>()
                .UseNpgsql(
                    unpooled,
                    npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), errorCodesToAdd: null))
                .AddInterceptors(interceptor)
                .Options;

            Task<bool> seed = SeedOnOwnContextAsync(options, ct);

            int? retryWaiter = null;
            bool lateCommitted = false;
            bool inserted;
            try
            {
                await using NpgsqlConnection observer = new(unpooled);
                await observer.OpenAsync(ct);

                // The retry begins only after the check, and it waits behind the held transaction.
                retryWaiter = await WaitForHolderWaiterAsync(observer, holder.ProcessID, seed, ct);
            }
            finally
            {
                // Runs on every path. A commit the server is completing takes effect whatever the client does, so the
                // held transaction commits; the seed is then drained, so none is left running into the database drop.
                if (interceptor.HeldTransaction is NpgsqlTransaction held)
                {
                    await using (held)
                    {
                        await held.CommitAsync(CancellationToken.None);
                    }

                    lateCommitted = true;
                }

                inserted = await seed.WaitAsync(DrainLimit, ct);
            }

            Assert.True(lateCommitted, "The first commit was not held back.");
            Assert.True(inserted, "Expected the seed to report the catalogue its late commit inserted.");

            // Exactly one catalogue: the late commit's, which the retry found instead of inserting another.
            CatalogueSnapshot catalogue;
            await using (SmartHotelDbContext db = new(plain))
            {
                catalogue = await ReadCatalogueAsync(db, ct);
            }

            Assert.Equal(
                ExpectedHotels.Select(static hotel => hotel.Name),
                catalogue.Hotels.Select(static hotel => hotel.Name));
            Assert.Equal(20, catalogue.RoomTypes.Count);
            Assert.Equal(200, catalogue.Rooms.Count);
            Assert.True(retryWaiter is not null, "The retried seed was not observed waiting for the held transaction.");
        }
        finally
        {
            // Not the test's token: the database must be dropped even when the test run is being cancelled.
            await _postgres.DropDatabaseAsync(connectionString, CancellationToken.None);
        }
    }

    private static decimal ExpectedNightlyPrice(ExpectedHotel hotel, ExpectedRoomType type)
    {
        return type.BasePrice + ((hotel.Number - 1) * HotelPriceStep);
    }

    // One query per table, each ordered by id so that two reads of an unchanged catalogue compare equal row by row.
    private static async Task<CatalogueSnapshot> ReadCatalogueAsync(SmartHotelDbContext db, CancellationToken token)
    {
        List<HotelRow> hotels = await db.Hotels
            .AsNoTracking()
            .OrderBy(static hotel => hotel.Id)
            .Select(static hotel => new HotelRow(hotel.Id, hotel.Name, hotel.City, hotel.Address, hotel.Description))
            .ToListAsync(token);

        List<RoomTypeRow> roomTypes = await db.RoomTypes
            .AsNoTracking()
            .OrderBy(static type => type.Id)
            .Select(static type => new RoomTypeRow(
                type.Id,
                type.HotelId,
                type.Hotel.Name,
                type.Name,
                type.MaxGuests,
                type.NightlyPrice))
            .ToListAsync(token);

        List<RoomRow> rooms = await db.Rooms
            .AsNoTracking()
            .OrderBy(static room => room.Id)
            .Select(static room => new RoomRow(
                room.Id,
                room.HotelId,
                room.RoomTypeId,
                room.RoomNumber,
                room.RoomType.HotelId,
                room.RoomType.Name))
            .ToListAsync(token);

        return new CatalogueSnapshot(hotels, roomTypes, rooms);
    }

    // A fresh scope gives the seed the dedicated context it requires: tracking nothing, with no transaction open.
    private static async Task<bool> SeedInNewScopeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await DbSeeder.SeedAsync(
            scope.ServiceProvider.GetRequiredService<SmartHotelDbContext>(),
            cancellationToken);
    }

    // Polls with autocommit statements, so each poll reads a fresh pg_stat_activity snapshot. Returns null when the
    // seed completes, or the observation limit passes, before a waiter appears.
    private static async Task<int?> WaitForInsertWaiterAsync(
        NpgsqlConnection observer,
        int holderPid,
        int excludedPid,
        Task seed,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand findWaiter = new(FindInsertWaiterSql, observer);
        findWaiter.Parameters.AddWithValue("holderPid", holderPid);
        findWaiter.Parameters.AddWithValue("excludedPid", excludedPid);

        Stopwatch elapsed = Stopwatch.StartNew();
        do
        {
            if (await findWaiter.ExecuteScalarAsync(cancellationToken) is int pid)
            {
                return pid;
            }

            if (seed.IsCompleted)
            {
                return null;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
        while (elapsed.Elapsed < ObservationLimit);

        return null;
    }

    // Polls like WaitForInsertWaiterAsync, for a session that waits on a lock the holder's transaction holds.
    private static async Task<int?> WaitForHolderWaiterAsync(
        NpgsqlConnection observer,
        int holderPid,
        Task seed,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand findWaiter = new(FindHolderWaiterSql, observer);
        findWaiter.Parameters.AddWithValue("holderPid", holderPid);

        Stopwatch elapsed = Stopwatch.StartNew();
        do
        {
            if (await findWaiter.ExecuteScalarAsync(cancellationToken) is int pid)
            {
                return pid;
            }

            if (seed.IsCompleted)
            {
                return null;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
        while (elapsed.Elapsed < ObservationLimit);

        return null;
    }

    // The seed on a context of its own, which tracks nothing and has no transaction open, under the given options.
    private static async Task<bool> SeedOnOwnContextAsync(
        DbContextOptions<SmartHotelDbContext> options,
        CancellationToken cancellationToken)
    {
        await using SmartHotelDbContext db = new(options);
        return await DbSeeder.SeedAsync(db, cancellationToken);
    }

    // Holds the first commit back, as a server still completing a commit when the connection is lost would. The
    // attempt's catalogue, ids included, moves to a transaction on the holder session; the attempt's own transaction is
    // rolled back; and a transient failure is reported. The held transaction stays open, with the uncommitted catalogue
    // and the locks its inserts took, until the test commits it.
    private sealed class HoldFirstSeedCommit(NpgsqlConnection holder) : DbTransactionInterceptor
    {
        // Read inside the attempt's transaction, the only one that sees its uncommitted rows.
        private const string ReadCatalogueSql =
            "SELECT json_build_object("
            + "'hotels', (SELECT json_agg(h) FROM \"Hotels\" h), "
            + "'roomTypes', (SELECT json_agg(t) FROM \"RoomTypes\" t), "
            + "'rooms', (SELECT json_agg(r) FROM \"Rooms\" r))::text";

        // Every column, the ids included, in foreign-key order, so the held rows are the attempt's catalogue itself.
        private static readonly string[] InsertCatalogueSql =
        [
            "INSERT INTO \"Hotels\" "
                + "SELECT * FROM json_populate_recordset(NULL::\"Hotels\", @catalogue::json -> 'hotels')",
            "INSERT INTO \"RoomTypes\" "
                + "SELECT * FROM json_populate_recordset(NULL::\"RoomTypes\", @catalogue::json -> 'roomTypes')",
            "INSERT INTO \"Rooms\" "
                + "SELECT * FROM json_populate_recordset(NULL::\"Rooms\", @catalogue::json -> 'rooms')",
        ];

        private int _intercepted;
        private NpgsqlTransaction? _held;

        public NpgsqlTransaction? HeldTransaction => Volatile.Read(ref _held);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _intercepted, 1) == 1)
            {
                return result;
            }

            string catalogue;
            await using (NpgsqlCommand read = new(
                ReadCatalogueSql,
                (NpgsqlConnection)transaction.Connection!,
                (NpgsqlTransaction)transaction))
            {
                catalogue = await read.ExecuteScalarAsync(cancellationToken) as string
                    ?? throw new InvalidOperationException("The attempt's catalogue was not found.");
            }

            await transaction.RollbackAsync(cancellationToken);

            NpgsqlTransaction held = await holder.BeginTransactionAsync(cancellationToken);
            Volatile.Write(ref _held, held);
            foreach (string sql in InsertCatalogueSql)
            {
                await using NpgsqlCommand insert = new(sql, holder, held);
                insert.Parameters.AddWithValue("catalogue", catalogue);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            throw new NpgsqlException(
                "The connection was lost before the commit was acknowledged.",
                new IOException("The connection was reset."));
        }
    }

    private sealed record ExpectedHotel(int Number, string Name, string City);

    private sealed record ExpectedRoomType(int Position, string Name, int MaxGuests, decimal BasePrice);

    private sealed record HotelRow(int Id, string Name, string City, string Address, string Description);

    private sealed record RoomTypeRow(
        int Id,
        int HotelId,
        string HotelName,
        string Name,
        int MaxGuests,
        decimal NightlyPrice);

    private sealed record RoomRow(
        int Id,
        int HotelId,
        int RoomTypeId,
        string RoomNumber,
        int RoomTypeHotelId,
        string RoomTypeName);

    // Compared list by list, never as a whole: the lists compare by reference inside a record's equality.
    private sealed record CatalogueSnapshot(
        IReadOnlyList<HotelRow> Hotels,
        IReadOnlyList<RoomTypeRow> RoomTypes,
        IReadOnlyList<RoomRow> Rooms);
}