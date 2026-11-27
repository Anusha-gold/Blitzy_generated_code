using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data.Entities;

using Xunit.Sdk;

namespace SmartHotel.Api.IntegrationTests.Bookings;

public sealed class ConcurrentBookingTests : IClassFixture<SmartHotelApiFactory>
{
    private const string HotelName = "SmartHotel Seattle Downtown";
    private const string RoomTypeName = "Standard";
    private const int RoomsPerType = 10;
    private const int Guests = 2;
    private const int Concurrency = 20;

    // The same lock the booking path takes, so the holder owns exactly the row lock that every request needs.
    private const string LockRoomTypeSql = "SELECT 1 FROM \"RoomTypes\" WHERE \"Id\" = @id FOR UPDATE";

    // Sessions of this class's database queued on a lock behind the holder. The first waiter waits on the holder's
    // transaction id and the rest on the tuple; both report wait_event_type 'Lock'. The holder and the observer itself
    // are excluded by backend PID.
    private const string CountWaitersSql =
        "SELECT count(*) FROM pg_stat_activity "
        + "WHERE datname = current_database() AND pid <> @holderPid AND pid <> pg_backend_pid() "
        + "AND wait_event_type = 'Lock' AND query LIKE '%FOR UPDATE%'";

    // The holder's session and the observer's, both outside the API's pool.
    private const int ControlSessions = 2;

    // How many sessions the server lets this class's login role have open at once: the slots left to a role that is
    // neither a superuser nor a member of pg_use_reserved_connections, capped by the role's and the database's own
    // connection limits, where -1 means none. reserved_connections arrived in PostgreSQL 16; an older server reserves
    // no such slots. LEAST ignores the NULL that a limit of -1 becomes.
    private const string SessionCapacitySql =
        "SELECT least("
        + "current_setting('max_connections')::int - current_setting('superuser_reserved_connections')::int "
        + "- coalesce(current_setting('reserved_connections', true)::int, 0), "
        + "(SELECT nullif(rolconnlimit, -1) FROM pg_roles WHERE rolname = current_user), "
        + "(SELECT nullif(datconnlimit, -1) FROM pg_database WHERE datname = current_database()))";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ObservationLimit = TimeSpan.FromSeconds(15);
    // The whole drain, from the release: the wait for the client tasks, the factory's drain of the server's requests,
    // its abort and grace included, and the last wait for the client tasks still pending.
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(30);

    // The least of DrainLimit left for the factory's drain once the wait for the client tasks ends: time to wait, to
    // abort the requests still running and to give them its grace.
    private static readonly TimeSpan ServerDrainReserve = TimeSpan.FromSeconds(4);

    // The end of DrainLimit, kept for the client tasks still pending once the factory's drain has ended, so they can
    // settle independently of the server's drain: a drained server does not mean every client task, with its response
    // buffering and continuations, has completed.
    private static readonly TimeSpan ClientSettleLimit = TimeSpan.FromSeconds(1);

    // How long before the wait for the client tasks ends the requests still running are cancelled, which leaves them
    // that long to stop.
    private static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(5);

    private readonly SmartHotelApiFactory _factory;
    private readonly ApiTestClient _client;

    public ConcurrentBookingTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    [Fact]
    public async Task TwentyConcurrentRequestsForLastRoom_ExactlyOneSucceeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Before any lock is taken, so a server or pool too small for the test fails it here with that reason, rather
        // than as waiters not observed in time.
        await AssertSessionCapacityAsync(_factory, ct);

        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        int hotelId = await _factory.GetHotelIdAsync(HotelName, ct);
        SmartHotelApiFactory.SeededRoomType roomType = await _factory.GetRoomTypeAsync(HotelName, RoomTypeName, ct);
        int roomTypeId = roomType.Id;
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly checkIn = today.AddDays(40);
        DateOnly checkOut = today.AddDays(42);

        // One after another, so each booking takes the next free room and exactly one room of the type stays free.
        for (int booked = 0; booked < RoomsPerType - 1; booked++)
        {
            await _client.CreateBookingOkAsync(session.Token, hotelId, roomTypeId, checkIn, checkOut, Guests, ct);
        }

        Assert.Equal(1, await AvailableRoomsAsync(hotelId, roomTypeId, checkIn, checkOut, ct));

        // Every session the contention needs is open before the lock is taken and before any request starts: the
        // observer's and the holder's, outside the API's pool, and Concurrency sessions in the API's pool, which the
        // requests then find idle there. A server whose free slots other sessions hold fails the test here, with that
        // reason, rather than as a request or the observer left without a session and the waiters not observed in
        // time. Each connection is disposed on every path, and the holder has no transaction yet.
        await using NpgsqlConnection observer = await OpenControlSessionAsync(_factory, ct);
        await using NpgsqlConnection holder = await OpenControlSessionAsync(_factory, ct);
        await ReserveRequestSessionsAsync(_factory, ct);

        // The holder takes the room type's row lock before any request starts, so every request blocks at its own
        // FOR UPDATE. Seeing all of them queued there proves that the requests passed authentication, validation and
        // the lookups and contend inside PostgreSQL at the critical section together, rather than one after another.
        await using NpgsqlTransaction holderTx = await holder.BeginTransactionAsync(ct);
        await using NpgsqlCommand lockCommand = new(LockRoomTypeSql, holder, holderTx);
        lockCommand.Parameters.AddWithValue("id", roomTypeId);
        Assert.Equal(1, (int)(await lockCommand.ExecuteScalarAsync(ct))!);
        int holderPid = holder.ProcessID;

        // The requests' own token: the test's cancellation reaches them, and so does the drain's cancellation of the
        // requests still running near its limit, which must not cancel the test's other work.
        using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        List<Task<HttpResponseMessage>> requests = new(Concurrency);
        List<HttpResponseMessage> responses = new(Concurrency);
        List<Exception> failures = [];
        try
        {
            try
            {
                // Started without awaiting, so every request is in flight at once, and inside the try, so the ones
                // already started are drained even when starting another fails. BookingBody builds a new JSON node
                // for each, so concurrent sends never share one.
                for (int started = 0; started < Concurrency; started++)
                {
                    requests.Add(_client.CreateBookingAsync(
                        ApiTestClient.BookingBody(hotelId, roomTypeId, checkIn, checkOut, Guests),
                        session.Token,
                        requestCancellation.Token));
                }

                (bool observed, long lastCount) = await ObserveWaitersAsync(
                    observer,
                    holderPid,
                    ct);
                if (!observed)
                {
                    failures.Add(FailException.ForFailure(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{Concurrency} waiting requests were not observed within {ObservationLimit.TotalSeconds} s "
                        + $"(last count: {lastCount}).")));
                }
            }
            catch (Exception exception)
            {
                // Kept, and rethrown only once the lock is released and the requests are drained.
                failures.Add(exception);
            }
            finally
            {
                // Runs on success, on the observation timeout and on any failure, the test's cancellation included.
                // Neither step throws, so the drain runs even when the release failed, and a request whose server work
                // is still running into the next test or into the factory's database drop is always reported.
                await ReleaseLockAsync(holder, holderTx, failures);
                await DrainAsync(_factory, requests, requestCancellation, responses, failures);
            }

            ThrowFailures(failures);

            Assert.Single(responses, static response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(
                Concurrency - 1,
                responses.Count(static response => response.StatusCode == HttpStatusCode.Conflict));
            foreach (HttpResponseMessage conflict in responses.Where(
                static response => response.StatusCode == HttpStatusCode.Conflict))
            {
                await ProblemAssert.AssertProblemAsync(conflict, HttpStatusCode.Conflict, cancellationToken: ct);
            }

            // Every room of the type is now booked exactly once for the stay: no room was double booked.
            List<int> roomIds = await _factory.ExecuteDbAsync<List<int>>(
                (db, token) => db.Bookings
                    .AsNoTracking()
                    .Where(b => b.Room.RoomTypeId == roomTypeId
                        && b.CheckIn == checkIn
                        && b.CheckOut == checkOut
                        && b.Status == BookingStatus.Confirmed)
                    .Select(b => b.RoomId)
                    .ToListAsync(token),
                ct);
            Assert.Equal(RoomsPerType, roomIds.Count);
            Assert.Equal(RoomsPerType, roomIds.Distinct().Count());

            Assert.Equal(0, await AvailableRoomsAsync(hotelId, roomTypeId, checkIn, checkOut, ct));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    private async Task<int> AvailableRoomsAsync(
        int hotelId,
        int roomTypeId,
        DateOnly checkIn,
        DateOnly checkOut,
        CancellationToken cancellationToken)
    {
        ApiTestClient.HotelDetail detail = await _client.GetHotelDetailAsync(
            hotelId,
            checkIn,
            checkOut,
            Guests,
            cancellationToken);

        return Assert.Single(detail.RoomTypes, roomType => roomType.Id == roomTypeId).AvailableRooms;
    }

    // Fails the test unless every session it needs can be open at once: the API's pool must allow Concurrency
    // sessions, and the server must let this class's login role have those and the ControlSessions together. Otherwise
    // some requests would queue for a pool slot or fail for a server slot instead of waiting at the lock, and the
    // observation would only time out. These are the configured limits, not the sessions open right now, which
    // OpenControlSessionAsync and ReserveRequestSessionsAsync settle by opening them. The server's are read on one
    // short-lived session outside the API's pool. The messages name the limits, never their values.
    private static async Task AssertSessionCapacityAsync(
        SmartHotelApiFactory factory,
        CancellationToken cancellationToken)
    {
        if (new NpgsqlConnectionStringBuilder(factory.DatabaseConnectionString).MaxPoolSize < Concurrency)
        {
            Assert.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"The test database's connection string lets the API's pool open fewer than {Concurrency} sessions "
                + $"(Maximum Pool Size), so the {Concurrency} concurrent booking requests cannot all reach PostgreSQL."));
        }

        int capacity;
        await using (NpgsqlConnection connection = await OpenControlSessionAsync(factory, cancellationToken))
        await using (NpgsqlCommand command = new(SessionCapacitySql, connection))
        {
            capacity = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        if (capacity < Concurrency + ControlSessions)
        {
            Assert.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"The PostgreSQL server cannot give this test class's login role {Concurrency + ControlSessions} "
                + $"sessions at once, {Concurrency} for the booking requests and {ControlSessions} for the lock holder "
                + $"and the observer: max_connections less superuser_reserved_connections and reserved_connections, "
                + $"the role's connection limit or the database's connection limit is lower. Select a server with more "
                + $"connection slots through SMARTHOTEL_TEST_POSTGRES."));
        }
    }

    // Opens a session of the test's own outside the API's pool, or fails the test when the server has no connection
    // slot free for it.
    private static async Task<NpgsqlConnection> OpenControlSessionAsync(
        SmartHotelApiFactory factory,
        CancellationToken cancellationToken) =>
        await factory.TryOpenDatabaseConnectionAsync(pooled: false, cancellationToken) ?? throw SessionRefused();

    // Opens Concurrency sessions of the API's own pool at once, each held open until every open has ended, and then
    // returns them to the pool, so that many sessions stay open on the server, idle in the pool, for the requests to
    // take instead of opening sessions of their own. The pool keeps an idle session for its connection idle lifetime,
    // minutes rather than the seconds the test takes. Fails the test when the server refused one of them for want of a
    // free slot, and rethrows any other failure; either way only once every connection that opened is disposed.
    private static async Task ReserveRequestSessionsAsync(
        SmartHotelApiFactory factory,
        CancellationToken cancellationToken)
    {
        List<Task<NpgsqlConnection?>> opens = new(Concurrency);
        for (int opened = 0; opened < Concurrency; opened++)
        {
            opens.Add(factory.TryOpenDatabaseConnectionAsync(pooled: true, cancellationToken));
        }

        // Ends only when every open has, however it ended, so each session that opened is held until then.
        Task allEnded = Task.WhenAll(opens);
        await allEnded.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        List<Exception> failures = [];
        bool refused = false;
        foreach (Task<NpgsqlConnection?> open in opens)
        {
            try
            {
                // Already ended, so this does not wait.
                NpgsqlConnection? connection = await open;
                if (connection is null)
                {
                    refused = true;
                    continue;
                }

                await connection.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        // The test's cancellation is reported once, rather than as each open it cancelled.
        cancellationToken.ThrowIfCancellationRequested();
        if (refused)
        {
            failures.Add(SessionRefused());
        }

        ThrowFailures(failures);
    }

    // The server refused a session the test needs, before the lock is taken. The message names the limit, never a
    // value of the connection string.
    private static FailException SessionRefused() =>
        FailException.ForFailure(string.Create(
            CultureInfo.InvariantCulture,
            $"The PostgreSQL server refused a session this test needs (SQLSTATE 53300, too_many_connections): fewer "
            + $"than {Concurrency + ControlSessions} connection slots are free for this test class's login role, "
            + $"{Concurrency} for the booking requests and {ControlSessions} for the lock holder and the observer, "
            + $"because other sessions hold the rest. Run the test when the server has more free connection slots, or "
            + $"select another server through SMARTHOTEL_TEST_POSTGRES."));

    // Polls the observer until exactly Concurrency sessions wait behind the holder's lock. One deadline covers every
    // poll and every pause, and a count read after it is not accepted. Returns whether the waiters were seen in time,
    // with the last count read in time (-1 for none).
    private static async Task<(bool Observed, long LastCount)> ObserveWaitersAsync(
        NpgsqlConnection observer,
        int holderPid,
        CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ObservationLimit);
        long lastCount = -1;
        try
        {
            // No transaction on the observer: each poll is its own autocommit statement and so reads a fresh
            // pg_stat_activity snapshot, while inside a transaction every poll would return the first one again.
            await using NpgsqlCommand countWaiters = new(CountWaitersSql, observer);
            countWaiters.Parameters.AddWithValue("holderPid", holderPid);

            while (true)
            {
                long count = (long)(await countWaiters.ExecuteScalarAsync(deadline.Token))!;
                if (elapsed.Elapsed > ObservationLimit)
                {
                    // Read after the deadline, so it cannot show the waiters in time.
                    return (false, lastCount);
                }

                lastCount = count;
                if (count == Concurrency)
                {
                    return (true, count);
                }

                await Task.Delay(PollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            // The deadline passed while polling or pausing. The test's own cancellation is not caught.
            return (false, lastCount);
        }
    }

    // Rolls back the holder's transaction, which changed nothing, so the lock is released. Not the test's token: the
    // lock must be released even while the test is being cancelled. When the rollback fails, closing the holder's
    // connection still ends the transaction. Failures are recorded for the caller, never thrown.
    private static async Task ReleaseLockAsync(
        NpgsqlConnection holder,
        NpgsqlTransaction holderTx,
        List<Exception> failures)
    {
        try
        {
            await holderTx.RollbackAsync(CancellationToken.None);
        }
        catch (Exception rollbackFailure)
        {
            failures.Add(rollbackFailure);
            try
            {
                await holder.CloseAsync();
            }
            catch (Exception closeFailure)
            {
                failures.Add(closeFailure);
            }
        }
    }

    // Waits for every request, on the client and on the server, for at most DrainLimit from here, the release, in all,
    // whatever the test's token says, so a cancelled test still waits for its requests. The client tasks are waited
    // for first, until ServerDrainReserve and ClientSettleLimit are all that is left of DrainLimit at the latest, and
    // the requests still running CancellationGrace before that are cancelled, which leaves them that long to stop. A
    // client task can complete while its server handler and database commands go on, once the response has started
    // or the request is aborted. So the factory's drain then waits for the server's own requests until only
    // ClientSettleLimit is left, aborting those still running when only its grace is left of that. The client tasks
    // still pending after it are given what is left of DrainLimit. Then each response is collected for the caller to
    // dispose, whatever happened to the others, and each failed or cancelled request, each request whose server work
    // did not stop and each client task still pending is recorded. A request whose client task is still pending can
    // still reach the server, but it can start work only until the factory's dispose begins, which refuses it without
    // passing it to the API. Failures are recorded for the caller, never thrown.
    private static async Task DrainAsync(
        SmartHotelApiFactory factory,
        List<Task<HttpResponseMessage>> requests,
        CancellationTokenSource requestCancellation,
        List<HttpResponseMessage> responses,
        List<Exception> failures)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        TimeSpan clientWaitLimit = DrainLimit - ServerDrainReserve - ClientSettleLimit;
        TimeSpan cancelAfter = clientWaitLimit - CancellationGrace;
        requestCancellation.CancelAfter(cancelAfter);

        // StoppedAsync never throws, so this completes only when every client task has stopped.
        Task clientsStopped = Task.WhenAll(requests.Select(StoppedAsync));
        await CompletesWithinAsync(clientsStopped, clientWaitLimit);

        try
        {
            int serverRunning = await factory.DrainRequestsAsync(RemainingUntil(DrainLimit - ClientSettleLimit));
            if (serverRunning > 0)
            {
                failures.Add(FailException.ForFailure(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{serverRunning} requests were still running on the server at the end of the "
                    + $"{DrainLimit.TotalSeconds} s drain after the release, although aborted; the factory's dispose "
                    + $"ends their database access before it drops the database.")));
            }
        }
        catch (Exception exception)
        {
            // Aborting the server's requests failed. Recorded, never thrown, so the responses are still collected.
            failures.Add(exception);
        }

        // Even a drain that left nothing running on the server does not show that every client task has settled, so
        // those still pending are given what is left of DrainLimit.
        if (!clientsStopped.IsCompleted)
        {
            await CompletesWithinAsync(clientsStopped, RemainingUntil(DrainLimit));
        }

        List<Exception> requestFailures = [];
        int stillPending = 0;
        foreach (Task<HttpResponseMessage> request in requests)
        {
            if (!request.IsCompleted)
            {
                // Recorded below. Should its request reach the server later, it can start work only until the
                // factory's dispose begins, which refuses it.
                stillPending++;
                continue;
            }

            try
            {
                // Already completed, so this does not wait.
                responses.Add(await request);
            }
            catch (Exception exception)
            {
                requestFailures.Add(exception);
            }
        }

        if (requestFailures.Count > 0)
        {
            failures.Add(new AggregateException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{requestFailures.Count} of {requests.Count} booking requests failed or were cancelled; those "
                    + $"still running {cancelAfter.TotalSeconds} s after the release are cancelled."),
                requestFailures));
        }

        if (stillPending > 0)
        {
            failures.Add(FailException.ForFailure(string.Create(
                CultureInfo.InvariantCulture,
                $"{stillPending} of {requests.Count} booking requests' client tasks were still pending at the end of "
                + $"the {DrainLimit.TotalSeconds} s drain after the release, although cancelled "
                + $"{cancelAfter.TotalSeconds} s after it; the factory's dispose refuses their requests from the "
                + $"moment it begins.")));
        }

        // What is left from now until mark, measured from the release; zero once mark has passed.
        TimeSpan RemainingUntil(TimeSpan mark)
        {
            TimeSpan remaining = mark - elapsed.Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        // Completes when the request stops, however it stops, and never throws.
        static async Task StoppedAsync(Task request) =>
            await request.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Whether task completed within limit. Not the test's token: the drain must run in full while the test is
        // being cancelled.
        static async Task<bool> CompletesWithinAsync(Task task, TimeSpan limit)
        {
            try
            {
                await task.WaitAsync(limit, CancellationToken.None);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
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
}