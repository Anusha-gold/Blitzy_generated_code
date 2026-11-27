using System.Data.Common;
using System.Globalization;
using System.Runtime.ExceptionServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data;
using SmartHotel.Data.Seed;

using Xunit.Sdk;

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

    // What the retry tests' observations can miss, each reported by its assertion or, when cleanup fails as well,
    // ahead of the cleanup failures.
    private const string FirstWaiterMissed = "The seed was not observed waiting to insert a hotel.";
    private const string TerminationMissed = "The waiting seed session was not terminated.";
    private const string RetryWaiterMissed = "The seed was not observed waiting again on a new session.";
    private const string HolderWaiterMissed = "The retried seed was not observed waiting for the held transaction.";

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

        // The seed satisfies IX_Rooms_HotelId_RoomNumber: no room number repeats within a hotel. BookingConstraintTests
        // proves that the database enforces the index.
        Assert.Equal(200, rooms.Select(static room => (room.HotelId, room.RoomNumber)).Distinct().Count());

        // The seed satisfies FK_Rooms_RoomTypes_RoomTypeId_HotelId: every room's denormalized hotel id equals its room
        // type's hotel. BookingConstraintTests proves that the database enforces the key.
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

        // The body's failure and each teardown step's, in the order they occur, thrown only once the database is
        // dropped. Each resource is disposed as a step of its own, so one that fails to close is recorded rather than
        // replacing the body's failure.
        List<Exception> failures = [];
        ServiceProvider? services = null;
        NpgsqlConnection? holder = null;
        NpgsqlTransaction? holderTx = null;
        try
        {

            // The data layer's own registration, so the seed runs under the execution strategy the API uses.
            services = new ServiceCollection()
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
            holder = new(unpooled);
            await holder.OpenAsync(ct);
            holderTx = await holder.BeginTransactionAsync(ct);
            await using (NpgsqlCommand lockHotels = new(LockHotelsSql, holder, holderTx))
            {
                await lockHotels.ExecuteNonQueryAsync(ct);
            }

            // The seed's own token: the test's cancellation reaches it, and so does the drain's, which must not cancel
            // the test's other work.
            using CancellationTokenSource seedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<bool> seed = SeedInNewScopeAsync(services, seedCancellation.Token);

            // One deadline covers the whole observation: opening the observer, every poll and every pause, and the
            // termination.
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ObservationLimit);
            NpgsqlConnection observer = new(unpooled);
            List<Exception> scenarioFailures = [];
            int? firstWaiter = null;
            bool terminated = false;
            int? retryWaiter = null;
            bool? inserted;
            try
            {
                await observer.OpenAsync(deadline.Token);

                firstWaiter = await WaitForInsertWaiterAsync(
                    observer,
                    holder.ProcessID,
                    excludedPid: 0,
                    seed,
                    deadline.Token);
                if (firstWaiter is int waiterPid)
                {
                    await using NpgsqlCommand terminate = new(TerminateSessionSql, observer);
                    terminate.Parameters.AddWithValue("pid", waiterPid);
                    terminated = (bool)(await terminate.ExecuteScalarAsync(deadline.Token))!;

                    // A retry runs the whole unit again on a new session, which queues behind the holder once more.
                    retryWaiter = await WaitForInsertWaiterAsync(
                        observer,
                        holder.ProcessID,
                        waiterPid,
                        seed,
                        deadline.Token);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The deadline passed: what was not seen in time stays unset, and is reported once cleanup has run.
            }
            catch (Exception exception)
            {
                // Kept, and rethrown only once the lock is released and the seed is drained.
                scenarioFailures.Add(exception);
            }
            finally
            {
                // Runs on every path, and no step throws, so each runs whatever the ones before it did. The holder
                // changed nothing, so the rollback only releases the lock; the seed is then drained, and cancelled
                // first when the observation failed, so none is left running into the database drop. Last, what the
                // observation missed is recorded ahead of any cleanup failure, which would otherwise be thrown alone.
                bool observationFailed = scenarioFailures.Count > 0;
                await DisposeRecordedAsync(observer, scenarioFailures);
                await EndHolderTransactionAsync(holder, holderTx, commit: false, scenarioFailures);
                inserted = await DrainSeedAsync(
                    seed,
                    seedCancellation,
                    cancelFirst: observationFailed,
                    scenarioFailures);
                RecordMissedObservation(
                    scenarioFailures,
                    observationFailed,
                    (firstWaiter is not null, FirstWaiterMissed),
                    (terminated, TerminationMissed),
                    (retryWaiter is not null, RetryWaiterMissed));
            }

            ThrowFailures(scenarioFailures);

            Assert.True(firstWaiter is not null, FirstWaiterMissed);
            Assert.True(terminated, TerminationMissed);
            Assert.True(retryWaiter is not null, RetryWaiterMissed);
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
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            // No step throws, so each runs whatever the ones before it did.
            await DisposeRecordedAsync(holderTx, failures);
            await DisposeRecordedAsync(holder, failures);
            await DisposeRecordedAsync(services, failures);

            // Not the test's token: the database must be dropped even when the test run is being cancelled.
            await RunRecordedAsync(
                () => _postgres.DropDatabaseAsync(connectionString, CancellationToken.None),
                failures);
        }

        ThrowFailures(failures);
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

        // The body's failure and each teardown step's, in the order they occur, thrown only once the database is
        // dropped. The holder is disposed as a step of its own, so a failure to close it is recorded rather than
        // replacing the body's failure.
        List<Exception> failures = [];
        NpgsqlConnection? holder = null;
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
            holder = new(unpooled);
            await holder.OpenAsync(ct);
            HoldFirstSeedCommit interceptor = new(holder);
            DbContextOptions<SmartHotelDbContext> options = new DbContextOptionsBuilder<SmartHotelDbContext>()
                .UseNpgsql(
                    unpooled,
                    npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), errorCodesToAdd: null))
                .AddInterceptors(interceptor)
                .Options;

            // The seed's own token, which the interceptor's statements on the holder session run under as well: the
            // test's cancellation reaches it, and so does the drain's, which must not cancel the test's other work.
            using CancellationTokenSource seedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<bool> seed = SeedOnOwnContextAsync(options, seedCancellation.Token);

            // One deadline covers the whole observation: opening the observer, every poll and every pause.
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ObservationLimit);
            NpgsqlConnection observer = new(unpooled);
            List<Exception> scenarioFailures = [];
            int? retryWaiter = null;
            bool lateCommitted;
            bool? inserted;
            try
            {
                await observer.OpenAsync(deadline.Token);

                // The retry begins only after the check, and it waits behind the held transaction.
                retryWaiter = await WaitForHolderWaiterAsync(observer, holder.ProcessID, seed, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The deadline passed: the retry was not seen in time, which is reported once cleanup has run.
            }
            catch (Exception exception)
            {
                // Kept, and rethrown only once the held transaction is settled and the seed is drained.
                scenarioFailures.Add(exception);
            }
            finally
            {
                // Runs on every path, and no step throws, so each runs whatever the ones before it did. A commit the
                // server is completing takes effect whatever the client does, so the held transaction commits. Once
                // the interceptor has stopped using the holder session, and unless the observation failed, the commit
                // comes first and releases any retry waiting behind it, and the seed is then drained. Otherwise the
                // seed is cancelled, which cancels the interceptor's statements too, and drained before the held
                // transaction is touched. Either way no seed is left running into the database drop. Last, what the
                // observation missed is recorded ahead of any cleanup failure, which would otherwise be thrown alone.
                bool observationFailed = scenarioFailures.Count > 0;
                bool commitFirst = !observationFailed && interceptor.HolderSessionFree;
                await DisposeRecordedAsync(observer, scenarioFailures);
                if (commitFirst)
                {
                    lateCommitted = await CommitHeldAsync(holder, interceptor, scenarioFailures);
                    inserted = await DrainSeedAsync(seed, seedCancellation, cancelFirst: false, scenarioFailures);
                }
                else
                {
                    inserted = await DrainSeedAsync(seed, seedCancellation, cancelFirst: true, scenarioFailures);
                    lateCommitted = await CommitHeldAsync(holder, interceptor, scenarioFailures);
                }

                RecordMissedObservation(
                    scenarioFailures,
                    observationFailed,
                    (retryWaiter is not null, HolderWaiterMissed));
            }

            ThrowFailures(scenarioFailures);

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
            Assert.True(retryWaiter is not null, HolderWaiterMissed);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            // No step throws, so each runs whatever the ones before it did.
            await DisposeRecordedAsync(holder, failures);

            // Not the test's token: the database must be dropped even when the test run is being cancelled.
            await RunRecordedAsync(
                () => _postgres.DropDatabaseAsync(connectionString, CancellationToken.None),
                failures);
        }

        ThrowFailures(failures);
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
    // seed completes before a waiter appears. The token is the caller's observation deadline: it alone ends a search
    // that finds nothing, by cancelling the poll or the pause in progress.
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

        while (true)
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

        while (true)
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
    }

    // The seed on a context of its own, which tracks nothing and has no transaction open, under the given options.
    private static async Task<bool> SeedOnOwnContextAsync(
        DbContextOptions<SmartHotelDbContext> options,
        CancellationToken cancellationToken)
    {
        await using SmartHotelDbContext db = new(options);
        return await DbSeeder.SeedAsync(db, cancellationToken);
    }

    // Commits and disposes the transaction the interceptor holds, if it holds one yet: a commit the server is
    // completing takes effect whatever the client does. Returns whether it committed. Failures are recorded for the
    // caller, never thrown.
    private static async Task<bool> CommitHeldAsync(
        NpgsqlConnection holder,
        HoldFirstSeedCommit interceptor,
        List<Exception> failures)
    {
        if (interceptor.HeldTransaction is not NpgsqlTransaction held)
        {
            return false;
        }

        bool committed = await EndHolderTransactionAsync(holder, held, commit: true, failures);
        await DisposeRecordedAsync(held, failures);
        return committed;
    }

    // Commits or rolls back a transaction on the holder session, which releases its locks. Not the test's token: the
    // locks must be released even while the test is being cancelled. When ending the transaction fails, closing the
    // holder's connection still ends it. Returns whether it ended as asked. Failures are recorded for the caller, never
    // thrown.
    private static async Task<bool> EndHolderTransactionAsync(
        NpgsqlConnection holder,
        NpgsqlTransaction transaction,
        bool commit,
        List<Exception> failures)
    {
        try
        {
            if (commit)
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            else
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            return true;
        }
        catch (Exception endFailure)
        {
            failures.Add(endFailure);
            await RunRecordedAsync(holder.CloseAsync, failures);
            return false;
        }
    }

    // Waits for the seed to end, whatever the test's token says, so a cancelled test still waits for its seed, and
    // returns only once it has ended, so nothing the seed uses is disposed and its database is not dropped while it
    // runs. cancelFirst cancels the seed before the wait. A seed still running after DrainLimit is recorded as one that
    // did not end in time, then cancelled and waited for again; one still running DrainLimit after that is recorded as
    // well, and then waited for until it ends. Its cancellation bounds that wait: the seed passes its token to every
    // database call and to its execution strategy, which waits between retries under it, and a cancelled command ends
    // even when the server does not answer, because the driver then breaks the connection. The two limits only detect
    // and report a slow seed. Returns the seed's result, or null when it failed or ended by a cancellation requested
    // here. Every failure, other than ending by that cancellation, is recorded for the caller, never thrown.
    private static async Task<bool?> DrainSeedAsync(
        Task<bool> seed,
        CancellationTokenSource seedCancellation,
        bool cancelFirst,
        List<Exception> failures)
    {
        bool cancelled = cancelFirst && CancelSeed(seed, seedCancellation, failures);
        if (!await EndsWithinAsync(seed, DrainLimit))
        {
            failures.Add(FailException.ForFailure(string.Create(
                CultureInfo.InvariantCulture,
                $"The seed did not end within {DrainLimit.TotalSeconds} s; it is cancelled and waited for again.")));
            cancelled = CancelSeed(seed, seedCancellation, failures) || cancelled;
            if (!await EndsWithinAsync(seed, DrainLimit))
            {
                failures.Add(FailException.ForFailure(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The seed did not end within {DrainLimit.TotalSeconds} s of its cancellation either; teardown "
                    + $"waits for it to end.")));
            }
        }

        try
        {
            // Ended already, unless it outlasted both limits, in which case this waits until it ends.
            return await seed;
        }
        catch (OperationCanceledException) when (cancelled)
        {
            // Ended by the cancellation requested here, which is no failure of the seed's own: what led to it is
            // reported instead.
            return null;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            return null;
        }
    }

    // Cancels the seed unless it has already ended. Returns whether the cancellation was requested here, so that the
    // seed's ending by it is expected rather than a failure. A cancellation callback's failure is recorded for the
    // caller, never thrown.
    private static bool CancelSeed(Task seed, CancellationTokenSource seedCancellation, List<Exception> failures)
    {
        if (seed.IsCompleted)
        {
            return false;
        }

        try
        {
            seedCancellation.Cancel();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        return true;
    }

    // Whether the task ends within the limit, however it ends and whatever the test's token says. Never throws.
    private static async Task<bool> EndsWithinAsync(Task task, TimeSpan limit)
    {
        await task.WaitAsync(limit, CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return task.IsCompleted;
    }

    // Disposes the resource, if there is one, as a teardown step of its own.
    private static async Task DisposeRecordedAsync(IAsyncDisposable? resource, List<Exception> failures)
    {
        if (resource is not null)
        {
            await RunRecordedAsync(() => resource.DisposeAsync().AsTask(), failures);
        }
    }

    // Runs one teardown step and records its failure for the caller instead of throwing it, so the steps after it
    // still run and no failure recorded before it is replaced.
    private static async Task RunRecordedAsync(Func<Task> step, List<Exception> failures)
    {
        try
        {
            await step();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    // Called once an observation's cleanup has run. When the observation did not fail but its cleanup did, the cleanup
    // failures are thrown before the assertions on what was observed can run, so the first expectation not met is
    // recorded ahead of them, under its assertion's message, as the failure that came first. Otherwise nothing is
    // recorded: the observation's own failure already comes first, or the assertions report what was missed.
    private static void RecordMissedObservation(
        List<Exception> failures,
        bool observationFailed,
        params (bool Met, string Message)[] expectations)
    {
        if (observationFailed || failures.Count == 0)
        {
            return;
        }

        foreach ((bool met, string message) in expectations)
        {
            if (!met)
            {
                failures.Insert(0, FailException.ForFailure(message));
                return;
            }
        }
    }

    // Rethrows a single failure as it was first thrown, or reports several together in the order they occurred.
    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
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
        private int _holderSessionFree;
        private NpgsqlTransaction? _held;

        public NpgsqlTransaction? HeldTransaction => Volatile.Read(ref _held);

        // True once the interceptor has stopped using the holder session, so the held transaction can be ended
        // without racing its inserts.
        public bool HolderSessionFree => Volatile.Read(ref _holderSessionFree) == 1;

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
            try
            {
                foreach (string sql in InsertCatalogueSql)
                {
                    await using NpgsqlCommand insert = new(sql, holder, held);
                    insert.Parameters.AddWithValue("catalogue", catalogue);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            finally
            {
                // No statement runs on the holder session after this, however the inserts ended.
                Volatile.Write(ref _holderSessionFree, 1);
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