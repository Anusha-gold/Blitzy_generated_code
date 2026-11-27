using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

using SmartHotel.Api.Common.Pricing;
using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Api.Modules.Availability;
using SmartHotel.Api.Modules.Bookings;
using SmartHotel.Data;
using SmartHotel.Data.Entities;

using Xunit.Sdk;

namespace SmartHotel.Api.IntegrationTests.Bookings;

/// <summary>
/// <c>DELETE /api/bookings/{id}</c> against the real API pipeline and PostgreSQL: the 401, 403, 404 and both 409
/// cases of the interface table, a successful cancellation that frees the room again, a cancellation whose commit
/// loses its acknowledgement or completes only after the check that follows the failure, the cancellation of a stay
/// that starts today (UTC), and two concurrent owner cancellations of which exactly one succeeds.
/// </summary>
/// <remarks>
/// The tests share one seeded database and run in any order, so each one books a hotel and room type that no other
/// test in this class touches, registers its own guests and resolves every id by name. A test that installs a trigger
/// limits it to its own booking and drops it before it ends.
/// </remarks>
public sealed class CancelBookingTests : IClassFixture<SmartHotelApiFactory>
{
    private const string SeattleDowntown = "SmartHotel Seattle Downtown";
    private const string SeattleWaterfront = "SmartHotel Seattle Waterfront";
    private const string NewYorkMidtown = "SmartHotel New York Midtown";
    private const string NewYorkBrooklyn = "SmartHotel New York Brooklyn";
    private const string ChicagoLoop = "SmartHotel Chicago Loop";

    private const string Standard = "Standard";
    private const string Deluxe = "Deluxe";
    private const string Family = "Family";
    private const string Suite = "Suite";

    // The wire form of BookingStatus, compared as the strings the API serializes.
    private const string ConfirmedStatus = "Confirmed";
    private const string CancelledStatus = "Cancelled";

    // Every seeded room type can host two guests.
    private const int PartySize = 2;
    private const int SeededRoomsPerType = 10;

    // Identities start at 1 in each class's fresh database, so no booking has this id.
    private const int UnknownBookingId = 999999;

    // The API's fixed problem titles, which tell apart the failures that share a status.
    private const string BookingNotFoundTitle = "Booking not found";
    private const string OtherGuestsBookingTitle = "Booking belongs to another guest";
    private const string AlreadyCancelledTitle = "Booking already cancelled";
    private const string CheckInPassedTitle = "Check-in date has passed";

    // Two owner requests race to cancel one booking.
    private const int ConcurrentCancellations = 2;

    // No backend has PID 0, so excluding it excludes no session.
    private const int NoSession = 0;

    // The row lock every cancelling update needs. A plain read, such as the service's ownership and state check, does
    // not wait for it.
    private const string LockBookingSql = "SELECT 1 FROM \"Bookings\" WHERE \"Id\" = @id FOR UPDATE";

    // Sessions of this class's database, other than the holder, the observer and an excluded session, whose cancelling
    // update is queued on a lock. The first waits for the holder's transaction and the next for the row; both report
    // wait_event_type 'Lock'.
    private const string FindUpdateWaitersSql =
        "SELECT pid FROM pg_stat_activity "
        + "WHERE datname = current_database() AND pid <> @holderPid AND pid <> @excludedPid "
        + "AND pid <> pg_backend_pid() AND wait_event_type = 'Lock' AND query LIKE 'UPDATE \"Bookings\"%'";

    // Ends one session, as a database restart does, and waits up to 5 s for it to exit.
    private const string TerminateSessionSql = "SELECT pg_terminate_backend(@pid, 5000)";

    // An advisory lock key of this class's own. Advisory locks are scoped to their database, so no other class's test
    // can hold it.
    private const long CommitGateKey = 7_400_001;

    // The gate session holds the advisory lock while it is closed.
    private const string CloseCommitGateSql = "SELECT pg_advisory_lock(@key)";
    private const string OpenCommitGateSql = "SELECT pg_advisory_unlock(@key)";

    private const string DropCommitGateSql =
        "DROP TRIGGER IF EXISTS it_gate_cancel_commit ON \"Bookings\"; DROP FUNCTION IF EXISTS it_gate_cancel_commit()";

    // A session of this class's database, other than the observer, whose COMMIT waits for a lock the gate session
    // holds. Npgsql sends a commit as exactly this statement.
    private const string FindGatedCommitSql =
        "SELECT pid FROM pg_stat_activity "
        + "WHERE datname = current_database() AND pid <> pg_backend_pid() "
        + "AND query = 'COMMIT' AND @gatePid = ANY(pg_blocking_pids(pid))";

    // The id of the transaction a session has open.
    private const string ReadSessionTransactionSql = "SELECT backend_xid::text FROM pg_stat_activity WHERE pid = @pid";

    // A session of this class's database, other than the observer, whose cancelling update waits for a lock another
    // session holds.
    private const string FindBlockedUpdateSql =
        "SELECT pid FROM pg_stat_activity "
        + "WHERE datname = current_database() AND pid <> pg_backend_pid() "
        + "AND query LIKE 'UPDATE \"Bookings\"%' AND @blockerPid = ANY(pg_blocking_pids(pid))";

    // The transaction that wrote the booking's current row version.
    private const string ReadRowWriterSql = "SELECT xmin::text FROM \"Bookings\" WHERE \"Id\" = @id";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ObservationLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(30);

    // How long before the drain limit the work still running is cancelled, which leaves it that long to stop.
    private static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(5);

    // Runs at COMMIT, through the deferred trigger CommitGateTriggerSql creates, and waits there for as long as the
    // gate session holds the advisory lock.
    private static readonly string CommitGateFunctionSql = string.Create(
        CultureInfo.InvariantCulture,
        $$"""
        CREATE FUNCTION it_gate_cancel_commit() RETURNS trigger LANGUAGE plpgsql AS $function$
        BEGIN
            PERFORM pg_advisory_xact_lock({{CommitGateKey}});
            RETURN NULL;
        END
        $function$
        """);

    private readonly SmartHotelApiFactory _factory;
    private readonly ApiTestClient _client;

    public CancelBookingTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    [Fact]
    public async Task Cancel_NoToken_Returns401()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        ApiTestClient.GuestSession owner = await _client.RegisterGuestAsync(cancellationToken);
        ApiTestClient.Booking booking = await BookAsync(
            owner,
            SeattleWaterfront,
            Standard,
            today.AddDays(30),
            today.AddDays(32),
            cancellationToken);

        using HttpResponseMessage response = await _client.CancelBookingAsync(
            booking.Id,
            token: null,
            cancellationToken);

        await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.Unauthorized,
            cancellationToken: cancellationToken);
        ProblemAssert.AssertBearerChallenge(response);

        ApiTestClient.Booking listed = await GetListedBookingAsync(owner.Token, booking.Id, cancellationToken);
        Assert.Equal(ConfirmedStatus, listed.Status);
    }

    [Theory]
    [InlineData("confirmed-future")]
    [InlineData("cancelled")]
    [InlineData("check-in-passed")]
    public async Task Cancel_OtherGuestsBooking_Returns403(string caseLabel)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        ApiTestClient.GuestSession owner = await _client.RegisterGuestAsync(cancellationToken);
        ApiTestClient.GuestSession otherGuest = await _client.RegisterGuestAsync(cancellationToken);

        // Ownership is checked before the booking's state, so even a booking that its owner could no longer cancel
        // answers another guest with 403. A 409 would tell that guest the booking is cancelled or has started.
        (int bookingId, string originalStatus) = caseLabel switch
        {
            "confirmed-future" => (
                (await BookAsync(
                    owner,
                    SeattleDowntown,
                    Standard,
                    today.AddDays(30),
                    today.AddDays(32),
                    cancellationToken)).Id,
                ConfirmedStatus),
            "cancelled" => (
                await BookAndCancelAsync(
                    owner,
                    SeattleWaterfront,
                    Deluxe,
                    today.AddDays(110),
                    today.AddDays(112),
                    cancellationToken),
                CancelledStatus),
            "check-in-passed" => (
                await InsertStartedBookingAsync(owner, SeattleWaterfront, Suite, cancellationToken),
                ConfirmedStatus),
            _ => throw new ArgumentOutOfRangeException(nameof(caseLabel), caseLabel, "Unknown ownership case."),
        };

        using HttpResponseMessage response = await _client.CancelBookingAsync(
            bookingId,
            otherGuest.Token,
            cancellationToken);

        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            cancellationToken: cancellationToken);
        AssertTitle(problem, OtherGuestsBookingTitle);

        ApiTestClient.Booking listed = await GetListedBookingAsync(owner.Token, bookingId, cancellationToken);
        Assert.Equal(originalStatus, listed.Status);
    }

    [Fact]
    public async Task Cancel_UnknownBooking_Returns404()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ApiTestClient.GuestSession guest = await _client.RegisterGuestAsync(cancellationToken);

        using HttpResponseMessage response = await _client.CancelBookingAsync(
            UnknownBookingId,
            guest.Token,
            cancellationToken);

        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            cancellationToken: cancellationToken);
        AssertTitle(problem, BookingNotFoundTitle);
    }

    [Fact]
    public async Task Cancel_AlreadyCancelled_Returns409()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        ApiTestClient.GuestSession owner = await _client.RegisterGuestAsync(cancellationToken);
        ApiTestClient.Booking booking = await BookAsync(
            owner,
            SeattleDowntown,
            Deluxe,
            today.AddDays(30),
            today.AddDays(32),
            cancellationToken);

        using HttpResponseMessage first = await _client.CancelBookingAsync(
            booking.Id,
            owner.Token,
            cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        using HttpResponseMessage second = await _client.CancelBookingAsync(
            booking.Id,
            owner.Token,
            cancellationToken);

        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            second,
            HttpStatusCode.Conflict,
            cancellationToken: cancellationToken);
        AssertTitle(problem, AlreadyCancelledTitle);
    }

    [Fact]
    public async Task Cancel_CheckInPassed_Returns409()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ApiTestClient.GuestSession guest = await _client.RegisterGuestAsync(cancellationToken);
        int bookingId = await InsertStartedBookingAsync(guest, ChicagoLoop, Suite, cancellationToken);

        using HttpResponseMessage response = await _client.CancelBookingAsync(
            bookingId,
            guest.Token,
            cancellationToken);

        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            cancellationToken: cancellationToken);
        AssertTitle(problem, CheckInPassedTitle);

        BookingStatus status = await _factory.ExecuteDbAsync(
            (db, ct) => db.Bookings
                .AsNoTracking()
                .Where(b => b.Id == bookingId)
                .Select(static b => b.Status)
                .SingleAsync(ct),
            cancellationToken);
        Assert.Equal(BookingStatus.Confirmed, status);
    }

    [Fact]
    public async Task Cancel_Own_Returns204AndFreesRoom()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly checkIn = today.AddDays(50);
        DateOnly checkOut = today.AddDays(52);

        ApiTestClient.GuestSession owner = await _client.RegisterGuestAsync(cancellationToken);
        SmartHotelApiFactory.SeededRoomType family = await _factory.GetRoomTypeAsync(
            NewYorkBrooklyn,
            Family,
            cancellationToken);

        int initial = await GetAvailableRoomsAsync(family, checkIn, checkOut, cancellationToken);
        Assert.Equal(SeededRoomsPerType, initial);

        // With every room of the type free, the allocator picks the lowest-numbered one.
        IReadOnlyList<SmartHotelApiFactory.SeededRoom> rooms = await _factory.GetRoomsAsync(
            family.Id,
            cancellationToken);
        Assert.Equal(SeededRoomsPerType, rooms.Count);
        SmartHotelApiFactory.SeededRoom lowest = rooms[0];

        ApiTestClient.Booking booking = await _client.CreateBookingOkAsync(
            owner.Token,
            family.HotelId,
            family.Id,
            checkIn,
            checkOut,
            PartySize,
            cancellationToken);
        Assert.Equal(lowest.RoomNumber, booking.RoomNumber);
        Assert.Equal(initial - 1, await GetAvailableRoomsAsync(family, checkIn, checkOut, cancellationToken));

        using HttpResponseMessage response = await _client.CancelBookingAsync(
            booking.Id,
            owner.Token,
            cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        ApiTestClient.Booking listed = await GetListedBookingAsync(owner.Token, booking.Id, cancellationToken);
        Assert.Equal(CancelledStatus, listed.Status);

        // The availability count and the allocator both run the shared confirmed-overlap predicate as SQL, so the
        // cancelled booking must block its room in neither: the count is back to every room, and another guest booking
        // the exact same stay gets the cancelled booking's room, still the lowest-numbered one. A different guest
        // shows the room is released to everyone, not held for its last guest.
        Assert.Equal(initial, await GetAvailableRoomsAsync(family, checkIn, checkOut, cancellationToken));

        ApiTestClient.GuestSession nextGuest = await _client.RegisterGuestAsync(cancellationToken);
        ApiTestClient.Booking rebooked = await _client.CreateBookingOkAsync(
            nextGuest.Token,
            family.HotelId,
            family.Id,
            checkIn,
            checkOut,
            PartySize,
            cancellationToken);
        Assert.Equal(lowest.RoomNumber, rebooked.RoomNumber);
        Assert.Equal(initial - 1, await GetAvailableRoomsAsync(family, checkIn, checkOut, cancellationToken));

        // The database agrees: the cancelled row and the new confirmed row hold the same seeded room.
        List<int> roomIds = await _factory.ExecuteDbAsync(
            (db, ct) => db.Bookings
                .AsNoTracking()
                .Where(b => b.Id == booking.Id || b.Id == rebooked.Id)
                .Select(static b => b.RoomId)
                .ToListAsync(ct),
            cancellationToken);
        Assert.Equal(2, roomIds.Count);
        Assert.All(roomIds, roomId => Assert.Equal(lowest.Id, roomId));
    }

    [Theory]
    [InlineData("acknowledgement-lost")]
    [InlineData("commit-after-check")]
    public async Task Cancel_CommitAcknowledgementLost_ReturnsCancelled(string caseLabel)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        ApiTestClient.GuestSession owner = await _client.RegisterGuestAsync(cancellationToken);
        ApiTestClient.Booking booking = await BookAsync(
            owner,
            ChicagoLoop,
            Deluxe,
            today.AddDays(60),
            today.AddDays(62),
            cancellationToken);

        // No request can lose a commit's acknowledgement on purpose, so the service runs here on a context of its own,
        // under the API's retrying strategy. Either the first attempt's commit has taken effect when the failure is
        // reported, or the server completes that same transaction's commit only after the check that follows the
        // failure, while the retry's update waits for the booking's row.
        CancelResult result = caseLabel switch
        {
            "acknowledgement-lost" => await CancelWithLostCommitAcknowledgementAsync(
                owner.GuestId,
                booking.Id,
                cancellationToken),
            "commit-after-check" => await CancelWithCommitCompletedAfterCheckAsync(
                owner.GuestId,
                booking.Id,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(caseLabel), caseLabel, "Unknown acknowledgement case."),
        };

        // The controller answers Cancelled with 204, where a replayed update matching no row would have given 409.
        Assert.IsType<CancelResult.Cancelled>(result);
        Assert.Equal(BookingStatus.Cancelled, await GetStoredStatusAsync(booking.Id, cancellationToken));
    }

    // "Today" is the UTC calendar date, and a stay may be cancelled until the end of its check-in day, so the owner of a
    // stay that starts today can still cancel it. The test runs on the real clock, and a run during which midnight UTC
    // passes is repeated once with the new date: two consecutive runs, each far shorter than a day, cannot both
    // straddle midnight.
    [Fact]
    public async Task Cancel_CheckInToday_Returns204()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ApiTestClient.GuestSession owner = await _client.RegisterGuestAsync(cancellationToken);
        SmartHotelApiFactory.SeededRoomType standard = await _factory.GetRoomTypeAsync(
            NewYorkMidtown,
            Standard,
            cancellationToken);

        bool ranWithinOneDay = await TryCancelCheckInTodayAsync(owner, standard, cancellationToken)
            || await TryCancelCheckInTodayAsync(owner, standard, cancellationToken);

        Assert.True(ranWithinOneDay, "Two consecutive runs both straddled midnight UTC.");
    }

    // Two cancellations by the owner both find the booking confirmed and reach the conditional update together, so
    // exactly one may succeed. In the second case the database ends one request's session while its update waits, as
    // a restart would; the retry on a new session then loses to the other request, and only a commit of its own could
    // make it report success.
    [Theory]
    [InlineData("both-reach-update")]
    [InlineData("one-session-terminated-before-update")]
    public async Task Cancel_ConcurrentOwnerCancels_ExactlyOneSucceeds(string caseLabel)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        bool terminateOne = caseLabel switch
        {
            "both-reach-update" => false,
            "one-session-terminated-before-update" => true,
            _ => throw new ArgumentOutOfRangeException(nameof(caseLabel), caseLabel, "Unknown race case."),
        };
        DateOnly today = ApiTestClient.TodayUtc;
        ApiTestClient.GuestSession owner = await _client.RegisterGuestAsync(cancellationToken);
        ApiTestClient.Booking booking = await BookAsync(
            owner,
            NewYorkMidtown,
            Deluxe,
            today.AddDays(80),
            today.AddDays(82),
            cancellationToken);

        // The holder takes the booking's row lock before either request starts. The service's ownership and state
        // check is a plain read, which the lock does not block, so both requests find the booking confirmed and queue
        // at their update. Seeing both queued there proves that each passed every check and that they contend for the
        // same row inside PostgreSQL.
        await using NpgsqlConnection holder = new(UnpooledConnectionString());
        await holder.OpenAsync(cancellationToken);
        await using NpgsqlTransaction holderTx = await holder.BeginTransactionAsync(cancellationToken);
        await using (NpgsqlCommand lockBooking = new(LockBookingSql, holder, holderTx))
        {
            lockBooking.Parameters.AddWithValue("id", booking.Id);
            Assert.Equal(1, (int)(await lockBooking.ExecuteScalarAsync(cancellationToken))!);
        }

        int holderPid = holder.ProcessID;

        // The requests' own token: the test's cancellation reaches them, and so does the drain's cancellation of the
        // requests still running near its limit, which must not cancel the test's other work.
        using CancellationTokenSource requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<Task<HttpResponseMessage>> requests = new(ConcurrentCancellations);
        List<HttpResponseMessage> responses = new(ConcurrentCancellations);
        List<Exception> failures = [];
        bool bothQueued = false;
        bool terminatedSessionExited = false;
        bool retryQueued = false;
        try
        {
            try
            {
                // Started without awaiting, so both are in flight at once, and inside the try, so a request already
                // started is drained even when starting the other fails.
                for (int started = 0; started < ConcurrentCancellations; started++)
                {
                    requests.Add(_client.CancelBookingAsync(booking.Id, owner.Token, requestCancellation.Token));
                }

                await using NpgsqlConnection observer = new(UnpooledConnectionString());
                await observer.OpenAsync(cancellationToken);
                await using NpgsqlCommand findWaiters = new(FindUpdateWaitersSql, observer);
                findWaiters.Parameters.AddWithValue("holderPid", holderPid);
                findWaiters.Parameters.AddWithValue("excludedPid", NoSession);

                Task anyRequestEnded = Task.WhenAny(requests);
                IReadOnlyList<int>? waiters = await WaitForSessionsAsync(
                    findWaiters,
                    ConcurrentCancellations,
                    anyRequestEnded,
                    cancellationToken);
                bothQueued = waiters is not null;
                if (waiters is not null && terminateOne)
                {
                    int terminated = waiters[0];
                    await using (NpgsqlCommand terminate = new(TerminateSessionSql, observer))
                    {
                        terminate.Parameters.AddWithValue("pid", terminated);
                        terminatedSessionExited = (bool)(await terminate.ExecuteScalarAsync(cancellationToken))!;
                    }

                    // The ended request's retry queues behind the other request, which still waits for the holder, so
                    // the other request is the one that updates the booking once the lock is released.
                    findWaiters.Parameters["excludedPid"].Value = terminated;
                    retryQueued = await WaitForSessionsAsync(
                        findWaiters,
                        ConcurrentCancellations,
                        anyRequestEnded,
                        cancellationToken) is not null;
                }
            }
            catch (Exception exception)
            {
                // Kept, and rethrown only once the lock is released and the requests are drained.
                failures.Add(exception);
            }
            finally
            {
                // Runs on success, on an observation timeout and on any failure, the test's cancellation included.
                await ReleaseHolderAsync(holder, holderTx, failures);
                await DrainAsync(requests, requestCancellation, responses, failures);
            }

            ThrowFailures(failures);
            Assert.True(
                bothQueued,
                "Both cancellations were not observed waiting at their update within the observation limit.");
            if (terminateOne)
            {
                Assert.True(terminatedSessionExited, "The terminated request's session did not exit.");
                Assert.True(
                    retryQueued,
                    "The terminated request's retry was not observed waiting behind the other request.");
            }

            Assert.Single(responses, static response => response.StatusCode == HttpStatusCode.NoContent);
            HttpResponseMessage conflict = Assert.Single(
                responses,
                static response => response.StatusCode == HttpStatusCode.Conflict);
            JsonElement problem = await ProblemAssert.AssertProblemAsync(
                conflict,
                HttpStatusCode.Conflict,
                cancellationToken: cancellationToken);
            AssertTitle(problem, AlreadyCancelledTitle);

            Assert.Equal(BookingStatus.Cancelled, await GetStoredStatusAsync(booking.Id, cancellationToken));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    private async Task<ApiTestClient.Booking> BookAsync(
        ApiTestClient.GuestSession guest,
        string hotelName,
        string roomTypeName,
        DateOnly checkIn,
        DateOnly checkOut,
        CancellationToken cancellationToken)
    {
        SmartHotelApiFactory.SeededRoomType roomType = await _factory.GetRoomTypeAsync(
            hotelName,
            roomTypeName,
            cancellationToken);

        return await _client.CreateBookingOkAsync(
            guest.Token,
            roomType.HotelId,
            roomType.Id,
            checkIn,
            checkOut,
            PartySize,
            cancellationToken);
    }

    // Books a stay through the API and cancels it as its own guest, which leaves the booking Cancelled.
    private async Task<int> BookAndCancelAsync(
        ApiTestClient.GuestSession guest,
        string hotelName,
        string roomTypeName,
        DateOnly checkIn,
        DateOnly checkOut,
        CancellationToken cancellationToken)
    {
        ApiTestClient.Booking booking = await BookAsync(
            guest,
            hotelName,
            roomTypeName,
            checkIn,
            checkOut,
            cancellationToken);

        using HttpResponseMessage response = await _client.CancelBookingAsync(
            booking.Id,
            guest.Token,
            cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        return booking.Id;
    }

    // One run of Cancel_CheckInToday_Returns204: books a one-night stay from today (UTC) through the API and cancels it
    // as its owner. The date is read before the booking, after the cancel response and, when the create is not 201,
    // after the create response. A create other than 201 while the date has changed returns false with nothing
    // asserted, so the caller can run again with the new date; the refusal is taken to be midnight passing, not shown
    // to be. Otherwise the run requires the 201 and a booking body, cancels, and returns false if the date has changed
    // by the cancel response, without checking the check-in, the 204 or the stored status. With the date unchanged it
    // requires a check-in on that date, the 204 and the stored Cancelled status, and returns true.
    private async Task<bool> TryCancelCheckInTodayAsync(
        ApiTestClient.GuestSession owner,
        SmartHotelApiFactory.SeededRoomType roomType,
        CancellationToken cancellationToken)
    {
        DateOnly today = ApiTestClient.TodayUtc;
        using HttpResponseMessage created = await _client.CreateBookingAsync(
            ApiTestClient.BookingBody(roomType.HotelId, roomType.Id, today, today.AddDays(1), PartySize),
            owner.Token,
            cancellationToken);
        if (created.StatusCode != HttpStatusCode.Created && ApiTestClient.TodayUtc != today)
        {
            // A refusal during a date change is taken to be the stay starting yesterday; its cause is not checked.
            return false;
        }

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        ApiTestClient.Booking booking = await _client.ReadAsync<ApiTestClient.Booking>(created, cancellationToken);

        using HttpResponseMessage cancelled = await _client.CancelBookingAsync(
            booking.Id,
            owner.Token,
            cancellationToken);
        if (ApiTestClient.TodayUtc != today)
        {
            return false;
        }

        Assert.Equal(today, booking.CheckIn);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal(BookingStatus.Cancelled, await GetStoredStatusAsync(booking.Id, cancellationToken));
        return true;
    }

    // The API refuses a check-in in the past, so a stay that has already started can only be arranged directly in the
    // database: a Confirmed booking from yesterday to tomorrow (UTC) in the room type's highest-numbered room.
    private async Task<int> InsertStartedBookingAsync(
        ApiTestClient.GuestSession guest,
        string hotelName,
        string roomTypeName,
        CancellationToken cancellationToken)
    {
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly checkIn = today.AddDays(-1);
        DateOnly checkOut = today.AddDays(1);
        int nights = checkOut.DayNumber - checkIn.DayNumber;

        SmartHotelApiFactory.SeededRoomType roomType = await _factory.GetRoomTypeAsync(
            hotelName,
            roomTypeName,
            cancellationToken);
        IReadOnlyList<SmartHotelApiFactory.SeededRoom> rooms = await _factory.GetRoomsAsync(
            roomType.Id,
            cancellationToken);
        Assert.NotEmpty(rooms);
        SmartHotelApiFactory.SeededRoom room = rooms[^1];

        return await _factory.ExecuteDbAsync(
            async (db, ct) =>
            {
                Booking started = new()
                {
                    GuestId = guest.GuestId,
                    RoomId = room.Id,
                    CheckIn = checkIn,
                    CheckOut = checkOut,
                    GuestCount = 1,
                    TotalPrice = PriceCalculator.Total(roomType.NightlyPrice, nights),
                    Status = BookingStatus.Confirmed,
                    CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime,
                };
                db.Bookings.Add(started);
                await db.SaveChangesAsync(ct);
                return started.Id;
            },
            cancellationToken);
    }

    private async Task<ApiTestClient.Booking> GetListedBookingAsync(
        string token,
        int bookingId,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.ListBookingsAsync(token, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        List<ApiTestClient.Booking> bookings = await _client.ReadAsync<List<ApiTestClient.Booking>>(
            response,
            cancellationToken);

        return Assert.Single(bookings, booking => booking.Id == bookingId);
    }

    private async Task<int> GetAvailableRoomsAsync(
        SmartHotelApiFactory.SeededRoomType roomType,
        DateOnly checkIn,
        DateOnly checkOut,
        CancellationToken cancellationToken)
    {
        ApiTestClient.HotelDetail detail = await _client.GetHotelDetailAsync(
            roomType.HotelId,
            checkIn,
            checkOut,
            PartySize,
            cancellationToken);

        return Assert.Single(detail.RoomTypes, type => type.Id == roomType.Id).AvailableRooms;
    }

    // ProblemAssert has already required one non-empty string title. The comparison runs in memory and the message
    // names only the expected title, so no response value reaches the test output.
    private static void AssertTitle(JsonElement problem, string expectedTitle)
    {
        string? title = problem.GetProperty("title").GetString();
        Assert.True(
            string.Equals(expectedTitle, title, StringComparison.Ordinal),
            $"Expected the problem's title to be \"{expectedTitle}\".");
    }

    // The test's own sessions bypass the pool the API draws from.
    private string UnpooledConnectionString()
    {
        return new NpgsqlConnectionStringBuilder(_factory.DatabaseConnectionString) { Pooling = false }
            .ConnectionString;
    }

    // The booking's status as stored, read on a session of its own.
    private async Task<BookingStatus> GetStoredStatusAsync(int bookingId, CancellationToken cancellationToken)
    {
        return await _factory.ExecuteDbAsync(
            (db, ct) => db.Bookings
                .AsNoTracking()
                .Where(b => b.Id == bookingId)
                .Select(static b => b.Status)
                .SingleAsync(ct),
            cancellationToken);
    }

    // Reports a transient failure once, after the first commit has taken effect, as a connection lost before the
    // commit's acknowledgement arrived would.
    private async Task<CancelResult> CancelWithLostCommitAcknowledgementAsync(
        int guestId,
        int bookingId,
        CancellationToken cancellationToken)
    {
        LoseFirstCommitAcknowledgement interceptor = new();
        CancelResult result = await CancelOnOwnContextAsync(
            RetryingOptions(UnpooledConnectionString(), interceptor),
            guestId,
            bookingId,
            cancellationToken);

        Assert.True(interceptor.Failed, "The first commit's acknowledgement was not lost.");
        return result;
    }

    // Holds the first attempt's COMMIT inside the server and then cuts the client side of its connection, as a network
    // that fails while the server is still completing the commit would. A deferred trigger on this booking's row makes
    // the COMMIT wait at the end of its transaction for an advisory lock the gate session holds, and the service
    // reaches the server through a relay, which cuts the connection once that COMMIT is seen waiting. The check that
    // follows the failure finds the transaction still in progress, the retry's update waits for the row it holds, and
    // only once the gate opens does the same transaction commit.
    private async Task<CancelResult> CancelWithCommitCompletedAfterCheckAsync(
        int guestId,
        int bookingId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection gate = new(UnpooledConnectionString());
            await gate.OpenAsync(cancellationToken);
            await ExecuteAsync(gate, CommitGateFunctionSql, cancellationToken);
            await ExecuteAsync(gate, CommitGateTriggerSql(bookingId), cancellationToken);
            await using (NpgsqlCommand close = new(CloseCommitGateSql, gate))
            {
                close.Parameters.AddWithValue("key", CommitGateKey);
                await close.ExecuteNonQueryAsync(cancellationToken);
            }

            NpgsqlConnectionStringBuilder server = new(UnpooledConnectionString());
            (string serverHost, int serverPort) = RelayTarget(server, gate);
            await using SeveringRelay relay = SeveringRelay.Start(serverHost, serverPort);
            await using NpgsqlDataSource relayed = RelayedDataSource(server, serverHost, relay.Port);

            // The cancellation's own token, cancelled near the drain limit, so a cancellation still running then stops.
            using CancellationTokenSource workCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<CancelResult> cancellation = CancelOnOwnContextAsync(
                RetryingOptions(relayed),
                guestId,
                bookingId,
                workCancellation.Token);

            IReadOnlyList<int>? gatedCommit = null;
            string? gatedTransactionId = null;
            int severed = 0;
            IReadOnlyList<int>? retryWaiter = null;
            CancelResult? result = null;
            List<Exception> failures = [];
            try
            {
                await using NpgsqlConnection observer = new(UnpooledConnectionString());
                await observer.OpenAsync(cancellationToken);

                await using NpgsqlCommand findGatedCommit = new(FindGatedCommitSql, observer);
                findGatedCommit.Parameters.AddWithValue("gatePid", gate.ProcessID);
                gatedCommit = await WaitForSessionsAsync(findGatedCommit, 1, cancellation, cancellationToken);
                if (gatedCommit is not null)
                {
                    // The transaction is still open, so its id is stable while it is read.
                    await using (NpgsqlCommand readTransactionId = new(ReadSessionTransactionSql, observer))
                    {
                        readTransactionId.Parameters.AddWithValue("pid", gatedCommit[0]);
                        gatedTransactionId = await readTransactionId.ExecuteScalarAsync(cancellationToken) as string;
                    }

                    severed = relay.SeverOpenClients();

                    await using NpgsqlCommand findRetry = new(FindBlockedUpdateSql, observer);
                    findRetry.Parameters.AddWithValue("blockerPid", gatedCommit[0]);
                    retryWaiter = await WaitForSessionsAsync(findRetry, 1, cancellation, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                // Kept, and rethrown only once the gate is open and the cancellation is drained.
                failures.Add(exception);
            }
            finally
            {
                // Runs on every path. The server completes the gated commit whatever happened to its client, and the
                // cancellation is drained, so none is left running into the next test.
                await OpenCommitGateAsync(gate, failures);
                workCancellation.CancelAfter(DrainLimit - CancellationGrace);
                try
                {
                    result = await cancellation.WaitAsync(DrainLimit, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            ThrowFailures(failures);
            Assert.True(gatedCommit is not null, "The first attempt's COMMIT was not observed waiting in the server.");
            Assert.True(gatedTransactionId is not null, "The gated COMMIT's transaction id was not read.");
            Assert.Equal(1, severed);
            Assert.True(retryWaiter is not null, "The retry was not observed waiting for the gated transaction.");

            // The booking's current row version was written by the transaction whose COMMIT lost its connection.
            await using (NpgsqlConnection reader = new(UnpooledConnectionString()))
            {
                await reader.OpenAsync(cancellationToken);
                await using NpgsqlCommand readWriter = new(ReadRowWriterSql, reader);
                readWriter.Parameters.AddWithValue("id", bookingId);
                Assert.Equal(gatedTransactionId, await readWriter.ExecuteScalarAsync(cancellationToken) as string);
            }

            Assert.NotNull(result);
            return result;
        }
        finally
        {
            // On a session of its own, whatever happened to the gate's, and bounded even while the test is cancelled.
            using CancellationTokenSource cleanupLimit = new(DrainLimit);
            await using NpgsqlConnection cleanup = new(UnpooledConnectionString());
            await cleanup.OpenAsync(cleanupLimit.Token);
            await ExecuteAsync(cleanup, DropCommitGateSql, cleanupLimit.Token);
        }
    }

    // The service on a context of its own, under the API's retrying strategy, with the connection the test chooses and
    // the system clock the API uses.
    private static async Task<CancelResult> CancelOnOwnContextAsync(
        DbContextOptions<SmartHotelDbContext> options,
        int guestId,
        int bookingId,
        CancellationToken cancellationToken)
    {
        await using SmartHotelDbContext db = new(options);
        BookingService service = new(
            db,
            new AvailabilityQueries(db),
            TimeProvider.System,
            NullLogger<BookingService>.Instance);
        return await service.CancelAsync(guestId, bookingId, cancellationToken);
    }

    // The API's retry settings: up to three retries, with delays capped at two seconds.
    private static DbContextOptions<SmartHotelDbContext> RetryingOptions(
        string connectionString,
        params IInterceptor[] interceptors)
    {
        return new DbContextOptionsBuilder<SmartHotelDbContext>()
            .UseNpgsql(connectionString, EnableApiRetries)
            .AddInterceptors(interceptors)
            .Options;
    }

    // The same settings over a data source, which can carry a certificate validation callback.
    private static DbContextOptions<SmartHotelDbContext> RetryingOptions(DbDataSource dataSource)
    {
        return new DbContextOptionsBuilder<SmartHotelDbContext>()
            .UseNpgsql(dataSource, EnableApiRetries)
            .Options;
    }

    private static void EnableApiRetries(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), errorCodesToAdd: null);
    }

    // The one TCP server the relay forwards to: the host and port the gate session reached, which are Npgsql's own
    // reading of Host, with a single host:port form split. Npgsql treats a Host with a comma as a list of servers and a
    // rooted path, or a name starting with '@', as a Unix-domain socket, and neither can be relayed over one TCP
    // connection. The messages are fixed, because the connection string can carry a password.
    private static (string Host, int Port) RelayTarget(NpgsqlConnectionStringBuilder server, NpgsqlConnection gate)
    {
        string configuredHost = server.Host
            ?? throw new InvalidOperationException("The test database's connection string sets no host.");
        if (configuredHost.Contains(','))
        {
            throw new InvalidOperationException(
                "The commit-after-check case relays the service's connection to a single TCP server, so it cannot run "
                + "when the test server's connection string lists several hosts.");
        }

        if (Path.IsPathRooted(configuredHost) || configuredHost.StartsWith('@'))
        {
            throw new InvalidOperationException(
                "The commit-after-check case relays the service's connection over TCP, so it cannot run when the test "
                + "server's connection string selects a Unix-domain socket.");
        }

        string host = gate.Host
            ?? throw new InvalidOperationException("The gate session is not connected, so the server is unknown.");
        return (host, gate.Port);
    }

    // The service's connection through the relay, validating the server certificate against the configured server's
    // identity and as strictly as the configured SSL Mode. Npgsql checks the certificate's name against Host, here the
    // relay's loopback address, and refuses a validation callback under VerifyCA or VerifyFull, or with a Root
    // Certificate. A relayed VerifyCA or VerifyFull connection therefore runs as Require, which like them sends the SSL
    // request, fails when the server declines SSL and, with a callback set, honours Check Certificate Revocation, and
    // the callback validates as the configured mode does on a direct connection, so no check is weakened. Every other
    // mode validates nothing and is relayed unchanged.
    private static NpgsqlDataSource RelayedDataSource(
        NpgsqlConnectionStringBuilder server,
        string serverHost,
        int relayPort)
    {
        NpgsqlConnectionStringBuilder relayed = new(server.ConnectionString)
        {
            Host = IPAddress.Loopback.ToString(),
            Port = relayPort,
        };

        if (server.SslMode is not (SslMode.VerifyCA or SslMode.VerifyFull))
        {
            return NpgsqlDataSource.Create(relayed.ConnectionString);
        }

        ConfiguredServerCertificateValidation validation = new(
            serverHost,
            server.SslMode == SslMode.VerifyFull,
            server.RootCertificate);
        relayed.SslMode = SslMode.Require;
        relayed.RootCertificate = null;
        return new NpgsqlDataSourceBuilder(relayed.ConnectionString)
            .UseUserCertificateValidationCallback(validation.Validate)
            .Build();
    }

    // Created for one booking only. Being deferred, the trigger fires at COMMIT, after every statement of a transaction
    // that updated that booking's row, and its function then waits for the gate's advisory lock.
    private static string CommitGateTriggerSql(int bookingId)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            CREATE CONSTRAINT TRIGGER it_gate_cancel_commit AFTER UPDATE ON "Bookings"
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW."Id" = {bookingId})
            EXECUTE FUNCTION it_gate_cancel_commit()
            """);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Polls with autocommit statements, so each poll reads a fresh pg_stat_activity snapshot, until the query returns
    // exactly the expected number of sessions, and returns their backend PIDs. Returns null when the observation limit
    // passes first, or when the work those sessions belong to has ended, since they can no longer appear.
    private static async Task<IReadOnlyList<int>?> WaitForSessionsAsync(
        NpgsqlCommand findSessions,
        int expected,
        Task work,
        CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        do
        {
            List<int> pids = [];
            await using (NpgsqlDataReader reader = await findSessions.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    pids.Add(reader.GetInt32(0));
                }
            }

            if (pids.Count == expected)
            {
                return pids;
            }

            if (work.IsCompleted)
            {
                return null;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
        while (elapsed.Elapsed < ObservationLimit);

        return null;
    }

    // Releases the advisory lock the gated COMMIT waits for. Not the test's token: the gate must open even while the
    // test is being cancelled. When the release fails, closing the gate's session releases the lock as well. Failures
    // are recorded for the caller, never thrown.
    private static async Task OpenCommitGateAsync(NpgsqlConnection gate, List<Exception> failures)
    {
        try
        {
            await using NpgsqlCommand open = new(OpenCommitGateSql, gate);
            open.Parameters.AddWithValue("key", CommitGateKey);
            await open.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception openFailure)
        {
            failures.Add(openFailure);
            try
            {
                await gate.CloseAsync();
            }
            catch (Exception closeFailure)
            {
                failures.Add(closeFailure);
            }
        }
    }

    // Rolls back the holder's transaction, which changed nothing, so the row lock is released. Not the test's token:
    // the lock must be released even while the test is being cancelled. When the rollback fails, closing the holder's
    // connection still ends the transaction. Failures are recorded for the caller, never thrown.
    private static async Task ReleaseHolderAsync(
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
    // leaves them that long to stop. Each response is then collected for the caller to dispose, each failed or
    // cancelled request is recorded, and a request still running at the limit is recorded as one that did not stop
    // and disposes its response if it completes later. Failures are recorded for the caller, never thrown.
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
                failures.Add(exception);
            }
        }

        if (stillRunning > 0)
        {
            failures.Add(FailException.ForFailure(string.Create(
                CultureInfo.InvariantCulture,
                $"{stillRunning} of {requests.Count} cancellation requests did not stop within "
                + $"{DrainLimit.TotalSeconds} s of the release, although cancelled after {cancelAfter.TotalSeconds} s; "
                + $"each disposes its response if it completes later.")));
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

    // Reports a transient failure once, after the first commit has taken effect, as a connection lost before the
    // commit's acknowledgement arrived would.
    private sealed class LoseFirstCommitAcknowledgement : DbTransactionInterceptor
    {
        private int _failed;

        public bool Failed => Volatile.Read(ref _failed) == 1;

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
            {
                throw new NpgsqlException(
                    "The connection was lost before the commit was acknowledged.",
                    new IOException("The connection was reset."));
            }

            return Task.CompletedTask;
        }
    }

    // Relays each connection the service opens to the database server over loopback, so the test can cut the client
    // side of a connection, as a failing network would, while the server side stays open and the server carries on
    // with the statement it received.
    private sealed class SeveringRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly object _connectionsLock = new();
        private readonly List<RelayedConnection> _connections = [];
        private readonly string _serverHost;
        private readonly int _serverPort;
        private Task _accepting = Task.CompletedTask;

        private SeveringRelay(string serverHost, int serverPort)
        {
            _serverHost = serverHost;
            _serverPort = serverPort;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static SeveringRelay Start(string serverHost, int serverPort)
        {
            SeveringRelay relay = new(serverHost, serverPort);
            relay._listener.Start();
            relay._accepting = relay.AcceptAsync(relay._stopping.Token);
            return relay;
        }

        // Cuts the client side of every connection still relaying, and returns how many were cut.
        public int SeverOpenClients()
        {
            lock (_connectionsLock)
            {
                return _connections.Count(static connection => connection.SeverClient());
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            await _accepting;

            RelayedConnection[] connections;
            lock (_connectionsLock)
            {
                connections = [.. _connections];
            }

            foreach (RelayedConnection connection in connections)
            {
                await connection.DisposeAsync();
            }

            _stopping.Dispose();
        }

        // Never throws: it ends when the relay stops.
        private async Task AcceptAsync(CancellationToken stopping)
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(stopping);
                }
                catch (Exception exception) when (exception is OperationCanceledException
                    or SocketException
                    or ObjectDisposedException)
                {
                    return;
                }

                TcpClient server = new();
                try
                {
                    await server.ConnectAsync(_serverHost, _serverPort, stopping);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException)
                {
                    // The client sees its connection closed, as it would if the server could not be reached.
                    server.Dispose();
                    client.Dispose();
                    continue;
                }

                lock (_connectionsLock)
                {
                    _connections.Add(new RelayedConnection(client, server, stopping));
                }
            }
        }
    }

    // One relayed connection, with a pump for each direction. When either side closes, both are closed, as on a direct
    // connection, unless the client side was severed: the server side then stays open until the relay is disposed.
    private sealed class RelayedConnection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly TcpClient _server;
        private readonly Task _relaying;
        private int _severed;
        private int _closed;

        public RelayedConnection(TcpClient client, TcpClient server, CancellationToken stopping)
        {
            _client = client;
            _server = server;
            _relaying = RelayAsync(stopping);
        }

        // Returns false when the connection had already ended or been severed.
        public bool SeverClient()
        {
            if (Volatile.Read(ref _closed) == 1 || Interlocked.Exchange(ref _severed, 1) == 1)
            {
                return false;
            }

            // A zero linger time resets the connection, so the client's pending read fails at once.
            _client.Client.LingerState = new LingerOption(true, 0);
            _client.Dispose();
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            Close();
            await _relaying;
        }

        private async Task RelayAsync(CancellationToken stopping)
        {
            NetworkStream clientStream = _client.GetStream();
            NetworkStream serverStream = _server.GetStream();
            Task toServer = PumpAsync(clientStream, serverStream, stopping);
            Task toClient = PumpAsync(serverStream, clientStream, stopping);
            await Task.WhenAny(toServer, toClient);
            if (Volatile.Read(ref _severed) == 0)
            {
                Close();
            }

            await Task.WhenAll(toServer, toClient);
        }

        private void Close()
        {
            Volatile.Write(ref _closed, 1);
            _client.Dispose();
            _server.Dispose();
        }

        // Never throws: a pump ends when its source closes, when a side is cut or closed, or when the relay stops.
        private static async Task PumpAsync(NetworkStream source, NetworkStream destination, CancellationToken stopping)
        {
            try
            {
                await source.CopyToAsync(destination, stopping);
            }
            catch (Exception exception) when (exception is IOException
                or SocketException
                or ObjectDisposedException
                or OperationCanceledException)
            {
                // The connection is over for this direction.
            }
        }
    }

    // Validates a relayed connection's server certificate as Npgsql 8 validates it under VerifyCA or VerifyFull on a
    // direct connection to the configured host. The TLS layer reports its errors with the name checked against the
    // relay's loopback address, so the name is checked again against the configured host. Without a root certificate
    // those errors decide, the chain having been built against the system's trust store; with one, a chain that the
    // system does not trust is built again with the root certificate as its only trust anchor. Under VerifyCA the name
    // never counts.
    private sealed class ConfiguredServerCertificateValidation(
        string serverHost,
        bool verifyFull,
        string? rootCertificate)
    {
        // Where Npgsql looks for a root certificate when the connection string sets none.
        private const string RootCertificateVariable = "PGSSLROOTCERT";
        private const string RootCertificateFileName = "root.crt";

        private static readonly IdnMapping HostNameMapping = new();

        public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            X509Certificate2? serverCertificate = certificate switch
            {
                null => null,
                X509Certificate2 certificate2 => certificate2,
                _ => new X509Certificate2(certificate),
            };

            errors &= ~SslPolicyErrors.RemoteCertificateNameMismatch;
            if (serverCertificate is not null && !MatchesServerHost(serverCertificate))
            {
                errors |= SslPolicyErrors.RemoteCertificateNameMismatch;
            }

            string? rootCertificatePath = rootCertificate
                ?? Environment.GetEnvironmentVariable(RootCertificateVariable)
                ?? DefaultRootCertificatePath();
            if (rootCertificatePath is null)
            {
                return errors == SslPolicyErrors.None
                    || (!verifyFull && errors == SslPolicyErrors.RemoteCertificateNameMismatch);
            }

            if (serverCertificate is null || chain is null)
            {
                return false;
            }

            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            if (!verifyFull && errors == SslPolicyErrors.RemoteCertificateNameMismatch)
            {
                return true;
            }

            if (verifyFull && errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                return false;
            }

            // Read on every handshake, as Npgsql reads it: as PEM unless its extension is .pfx, and as a single
            // certificate file when that finds none.
            X509Certificate2Collection roots = [];
            if (!string.Equals(Path.GetExtension(rootCertificatePath), ".pfx", StringComparison.OrdinalIgnoreCase))
            {
                roots.ImportFromPemFile(rootCertificatePath);
            }

            if (roots.Count == 0)
            {
                roots.Add(new X509Certificate2(rootCertificatePath));
            }

            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.ExtraStore.AddRange(roots);
            return chain.Build(serverCertificate);
        }

        // The configured host in the form the TLS layer checks: without a trailing dot, and either as an IP address or
        // in IDNA ASCII form. A host the check cannot read, or a certificate whose names cannot be decoded, does not
        // match, so the check never accepts what it cannot verify.
        private bool MatchesServerHost(X509Certificate2 certificate)
        {
            string hostName = serverHost.TrimEnd('.');
            if (IPAddress.TryParse(hostName, out IPAddress? address))
            {
                hostName = address.ToString();
            }
            else
            {
                try
                {
                    hostName = HostNameMapping.GetAscii(hostName);
                }
                catch (ArgumentException)
                {
                    // A name IDNA cannot map stays as written, and MatchesHostname rejects it if it cannot read it.
                }
            }

            try
            {
                return certificate.MatchesHostname(hostName);
            }
            catch (Exception exception) when (exception is ArgumentException or CryptographicException)
            {
                return false;
            }
        }

        // root.crt in the user's PostgreSQL directory, %APPDATA%\postgresql on Windows and ~/.postgresql elsewhere,
        // when that file exists.
        private static string? DefaultRootCertificatePath()
        {
            bool windows = OperatingSystem.IsWindows();
            string? home = Environment.GetEnvironmentVariable(windows ? "APPDATA" : "HOME");
            if (home is null)
            {
                return null;
            }

            string path = Path.Combine(home, windows ? "postgresql" : ".postgresql", RootCertificateFileName);
            return File.Exists(path) ? path : null;
        }
    }
}