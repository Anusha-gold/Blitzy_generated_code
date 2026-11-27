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

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ObservationLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(30);

    // How long before the drain limit the requests still running are cancelled, which leaves them that long to stop.
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

        // The holder takes the room type's row lock before any request starts, so every request blocks at its own
        // FOR UPDATE. Seeing all of them queued there proves that the requests passed authentication, validation and
        // the lookups and contend inside PostgreSQL at the critical section together, rather than one after another.
        await using NpgsqlConnection holder = await _factory.OpenDatabaseConnectionAsync(ct);
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
                    _factory,
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
                // Neither step throws, so the drain runs even when the release failed, and a request still running
                // into the next test or into the factory's database drop is always reported.
                await ReleaseLockAsync(holder, holderTx, failures);
                await DrainAsync(requests, requestCancellation, responses, failures);
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

    // Polls until exactly Concurrency sessions wait behind the holder's lock. One deadline covers opening the
    // observer, every poll and every pause, and a count read after it is not accepted. Returns whether the waiters
    // were seen in time, with the last count read in time (-1 for none).
    private static async Task<(bool Observed, long LastCount)> ObserveWaitersAsync(
        SmartHotelApiFactory factory,
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
            await using NpgsqlConnection observer = await factory.OpenDatabaseConnectionAsync(deadline.Token);
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
            // The deadline passed while opening the observer, polling or pausing. The test's own cancellation is
            // not caught.
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

    // Waits for every request, for at most DrainLimit from here and whatever the test's token says, so a cancelled
    // test still waits for its requests. Those still running CancellationGrace before the limit are cancelled, which
    // leaves them that long to stop. Then each response is collected for the caller to dispose, whatever happened to
    // the others, and each failed or cancelled request is recorded. A request still running at the limit is recorded
    // as one that did not stop, and disposes its response if it completes later. Failures are recorded for the
    // caller, never thrown.
    private static async Task DrainAsync(
        List<Task<HttpResponseMessage>> requests,
        CancellationTokenSource requestCancellation,
        List<HttpResponseMessage> responses,
        List<Exception> failures)
    {
        TimeSpan cancelAfter = DrainLimit - CancellationGrace;
        requestCancellation.CancelAfter(cancelAfter);
        try
        {
            // StoppedAsync never throws, so the wait ends only when every request has stopped or at the limit.
            await Task.WhenAll(requests.Select(StoppedAsync)).WaitAsync(DrainLimit, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // The requests still running at the limit are counted and reported below.
        }

        List<Exception> requestFailures = [];
        int stillRunning = 0;
        foreach (Task<HttpResponseMessage> request in requests)
        {
            if (!request.IsCompleted)
            {
                stillRunning++;
                _ = DisposeWhenStoppedAsync(request);
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

        if (stillRunning > 0)
        {
            failures.Add(FailException.ForFailure(string.Create(
                CultureInfo.InvariantCulture,
                $"{stillRunning} of {requests.Count} booking requests did not stop within {DrainLimit.TotalSeconds} s "
                + $"of the release, although cancelled after {cancelAfter.TotalSeconds} s; each disposes its response "
                + $"if it completes later.")));
        }

        // Completes when the request stops, however it stops, and never throws.
        static async Task StoppedAsync(Task request) =>
            await request.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    // Takes over a request still running when the drain ended: disposes its response if one arrives, and otherwise
    // observes its failure.
    private static async Task DisposeWhenStoppedAsync(Task<HttpResponseMessage> request)
    {
        try
        {
            (await request).Dispose();
        }
        catch (Exception)
        {
            // Already reported: the drain counted this request as one that did not stop in time.
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