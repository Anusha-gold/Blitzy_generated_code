using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using SmartHotel.Api.Common.Pricing;
using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Api.Modules.Availability;
using SmartHotel.Api.Modules.Bookings;
using SmartHotel.Data;
using SmartHotel.Data.Entities;
using SmartHotel.Data.Seed;

namespace SmartHotel.Api.IntegrationTests.Bookings;

// The tests share one seeded database and run in any order, so every test that can create a booking owns a hotel,
// room type and date window that no other test in this class books. Ids are always looked up by catalogue name.
public sealed class CreateBookingTests : IClassFixture<SmartHotelApiFactory>
{
    private const string SeattleDowntown = "SmartHotel Seattle Downtown";
    private const string SeattleWaterfront = "SmartHotel Seattle Waterfront";
    private const string NewYorkMidtown = "SmartHotel New York Midtown";
    private const string ChicagoLoop = "SmartHotel Chicago Loop";

    private const int UnknownId = 999_999;

    // SHARE mode lets a booking request lock its room type and read Bookings for a free room, and blocks its insert.
    private const string LockBookingsSql = "LOCK TABLE \"Bookings\" IN SHARE MODE";

    // A session of this class's database, other than the holder, the observer and an excluded earlier session, that is
    // queued on a lock at an insert into Bookings.
    private const string FindInsertWaiterSql =
        "SELECT pid FROM pg_stat_activity "
        + "WHERE datname = current_database() AND pid <> @holderPid AND pid <> @excludedPid "
        + "AND pid <> pg_backend_pid() AND wait_event_type = 'Lock' AND query LIKE '%INSERT INTO \"Bookings\"%'";

    // A session of this class's database, other than the observer, that waits on a lock the holder's transaction holds.
    private const string FindHolderWaiterSql =
        "SELECT pid FROM pg_stat_activity "
        + "WHERE datname = current_database() AND pid <> pg_backend_pid() AND @holderPid = ANY(pg_blocking_pids(pid))";

    // Ends every other session of this class's database, as a database restart does, and waits up to 5 s for each one
    // to exit. The filter runs before the select list, so no session of another database is ever signalled.
    private const string TerminateOtherSessionsSql =
        "SELECT pg_terminate_backend(pid, 5000) FROM pg_stat_activity "
        + "WHERE datname = current_database() AND pid <> pg_backend_pid()";

    private const string TerminateSessionSql = "SELECT pg_terminate_backend(@pid, 5000)";

    // Fires the function RivalTriggerFunctionSql creates before each row inserted into Bookings.
    private const string RivalTriggerSql =
        "CREATE TRIGGER it_insert_rival_booking BEFORE INSERT ON \"Bookings\" "
        + "FOR EACH ROW EXECUTE FUNCTION it_insert_rival_booking()";

    // BookingService's BookingConflict.
    private const int BookingConflictEventId = 3002;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ObservationLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(30);

    private static readonly string BookingServiceCategory = typeof(BookingService).FullName!;

    private readonly SmartHotelApiFactory _factory;
    private readonly ApiTestClient _client;
    private readonly TestPostgres _postgres;

    public CreateBookingTests(SmartHotelApiFactory factory, TestPostgres postgres)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
        _postgres = postgres;
    }

    [Theory]
    [InlineData("roomTypeId-missing", "roomTypeId")]
    [InlineData("guests-zero", "guests")]
    [InlineData("checkOut-equals-checkIn", "checkOut")]
    [InlineData("checkIn-yesterday", "checkIn")]
    [InlineData("stay-31-nights", "checkOut")]
    [InlineData("checkIn-not-padded", "checkIn")]
    public async Task Create_InvalidInput_Returns400(string caseLabel, string expectedKey)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType roomType =
            await _factory.GetRoomTypeAsync(SeattleWaterfront, "Deluxe", ct);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly today = ApiTestClient.TodayUtc;

        // The baseline is a valid stay; each case breaks exactly one field, so it fails only for its stated reason.
        JsonObject body = ApiTestClient.BookingBody(
            roomType.HotelId,
            roomType.Id,
            today.AddDays(30),
            today.AddDays(32),
            guests: 2);

        switch (caseLabel)
        {
            case "roomTypeId-missing":
                body.Remove("roomTypeId");
                break;
            case "guests-zero":
                body["guests"] = 0;
                break;
            case "checkOut-equals-checkIn":
                body["checkOut"] = ApiTestClient.Iso(today.AddDays(30));
                break;
            case "checkIn-yesterday":
                body["checkIn"] = ApiTestClient.Iso(today.AddDays(-1));
                body["checkOut"] = ApiTestClient.Iso(today.AddDays(1));
                break;
            case "stay-31-nights":
                body["checkOut"] = ApiTestClient.Iso(today.AddDays(61));
                break;
            case "checkIn-not-padded":
                // Next year's dates are always in the future, so only the non-padded form can fail. Both dates use
                // the one year derived from today, so a run that crosses midnight UTC cannot split them.
                int year = ApiTestClient.NextYear(today);
                body["checkIn"] = string.Create(CultureInfo.InvariantCulture, $"{year}-1-5");
                body["checkOut"] = string.Create(CultureInfo.InvariantCulture, $"{year}-01-07");
                break;
            default:
                throw new ArgumentException($"Unknown case label '{caseLabel}'.", nameof(caseLabel));
        }

        using HttpResponseMessage response = await _client.CreateBookingAsync(body, session.Token, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedKey, ct);
    }

    [Fact]
    public async Task Create_GuestsAboveCapacity_Returns400()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType standard =
            await _factory.GetRoomTypeAsync(SeattleWaterfront, "Standard", ct);
        Assert.Equal(2, standard.MaxGuests);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly today = ApiTestClient.TodayUtc;

        // Valid for model validation and the stay rules, so only the room type's capacity rejects it.
        JsonObject body = ApiTestClient.BookingBody(
            standard.HotelId,
            standard.Id,
            today.AddDays(30),
            today.AddDays(32),
            guests: 3);

        using HttpResponseMessage response = await _client.CreateBookingAsync(body, session.Token, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.BadRequest, "guests", ct);
    }

    [Fact]
    public async Task Create_NoToken_Returns401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType family =
            await _factory.GetRoomTypeAsync(SeattleWaterfront, "Family", ct);
        DateOnly today = ApiTestClient.TodayUtc;
        JsonObject body = ApiTestClient.BookingBody(
            family.HotelId,
            family.Id,
            today.AddDays(30),
            today.AddDays(32),
            guests: 2);

        using HttpResponseMessage response = await _client.CreateBookingAsync(body, token: null, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.Unauthorized, cancellationToken: ct);
        ProblemAssert.AssertBearerChallenge(response);
    }

    [Theory]
    [InlineData("unknown-hotel")]
    [InlineData("unknown-room-type")]
    [InlineData("room-type-of-other-hotel")]
    public async Task Create_UnknownHotelOrRoomType_Returns404(string caseLabel)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int downtownId = await _factory.GetHotelIdAsync(SeattleDowntown, ct);
        SmartHotelApiFactory.SeededRoomType waterfrontStandard =
            await _factory.GetRoomTypeAsync(SeattleWaterfront, "Standard", ct);
        Assert.NotEqual(downtownId, waterfrontStandard.HotelId);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly today = ApiTestClient.TodayUtc;

        (int hotelId, int roomTypeId) = caseLabel switch
        {
            "unknown-hotel" => (UnknownId, waterfrontStandard.Id),
            "unknown-room-type" => (downtownId, UnknownId),
            "room-type-of-other-hotel" => (downtownId, waterfrontStandard.Id),
            _ => throw new ArgumentException($"Unknown case label '{caseLabel}'.", nameof(caseLabel)),
        };
        JsonObject body = ApiTestClient.BookingBody(
            hotelId,
            roomTypeId,
            today.AddDays(30),
            today.AddDays(32),
            guests: 2);

        using HttpResponseMessage response = await _client.CreateBookingAsync(body, session.Token, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.NotFound, cancellationToken: ct);
    }

    [Fact]
    public async Task Create_NoRoomAvailable_Returns409()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType deluxe = await _factory.GetRoomTypeAsync(NewYorkMidtown, "Deluxe", ct);
        IReadOnlyList<SmartHotelApiFactory.SeededRoom> rooms = await _factory.GetRoomsAsync(deluxe.Id, ct);
        Assert.Equal(SeedCatalog.RoomsPerType, rooms.Count);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(50);
        DateOnly checkOut = checkIn.AddDays(2);

        List<string> bookedRoomNumbers = [];
        for (int i = 0; i < rooms.Count; i++)
        {
            ApiTestClient.Booking booking = await _client.CreateBookingOkAsync(
                session.Token,
                deluxe.HotelId,
                deluxe.Id,
                checkIn,
                checkOut,
                guests: 2,
                ct);
            bookedRoomNumbers.Add(booking.RoomNumber);
        }

        // Room numbers are unique within a hotel, so matching the seeded set also proves no room was given twice.
        Assert.Equal(
            rooms.Select(static room => room.RoomNumber),
            bookedRoomNumbers.Order(StringComparer.Ordinal));

        JsonObject body = ApiTestClient.BookingBody(deluxe.HotelId, deluxe.Id, checkIn, checkOut, guests: 2);
        using HttpResponseMessage response = await _client.CreateBookingAsync(body, session.Token, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.Conflict, cancellationToken: ct);

        ApiTestClient.HotelDetail detail =
            await _client.GetHotelDetailAsync(deluxe.HotelId, checkIn, checkOut, guests: 2, ct);
        ApiTestClient.RoomTypeAvailability availability =
            Assert.Single(detail.RoomTypes, roomType => roomType.Id == deluxe.Id);
        Assert.Equal(0, availability.AvailableRooms);
    }

    [Fact]
    public async Task Create_Valid_Returns201WithServerComputedTotal()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType suite = await _factory.GetRoomTypeAsync(ChicagoLoop, "Suite", ct);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(60);
        DateOnly checkOut = checkIn.AddDays(3);

        JsonObject body = ApiTestClient.BookingBody(suite.HotelId, suite.Id, checkIn, checkOut, guests: 2);
        string[] requestMembers = ["checkIn", "checkOut", "guests", "hotelId", "roomTypeId"];
        Assert.Equal(requestMembers, body.Select(static member => member.Key).Order(StringComparer.Ordinal));

        // From here on every assertion carries a fixed message that names only expected values and members, never
        // what the response holds, because test output is logged.
        using HttpResponseMessage response = await _client.CreateBookingAsync(body, session.Token, ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, "Expected POST api/bookings to return 201.");

        // The booking is one guest's own data, so no cache may store it.
        ApiTestClient.AssertNoStore(response, "POST api/bookings");

        // The strict reader checks the raw body before it binds, because a bound value cannot tell 1107.00 from
        // "1107.00", a date from a date-time, or totalPrice from TotalPrice: Content-Type application/json in UTF-8,
        // exactly the wire record's camelCase members, each member's JSON kind, money with exactly two decimals,
        // exact yyyy-MM-dd dates and a UTC createdAt ending in Z.
        ApiTestClient.Booking booking = await _client.ReadAsync<ApiTestClient.Booking>(response, ct);

        // The reader accepted the buffered body as UTF-8 JSON, so its raw members are read again for exact values.
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        JsonElement root = document.RootElement;

        // The contract's 13 members, stated here independently of the wire record the reader checks against.
        string[] responseMembers =
        [
            "checkIn", "checkOut", "createdAt", "guests", "hotelId", "hotelName", "id", "nights", "roomNumber",
            "roomTypeId", "roomTypeName", "status", "totalPrice",
        ];
        Assert.True(
            root.EnumerateObject()
                .Select(static member => member.Name)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(responseMembers, StringComparer.Ordinal),
            $"Expected the 201 body to have exactly the members {string.Join(", ", responseMembers)}, each once.");

        JsonElement totalPrice = root.GetProperty("totalPrice");
        Assert.True(
            totalPrice.ValueKind == JsonValueKind.Number
                && string.Equals(totalPrice.GetRawText(), "1107.00", StringComparison.Ordinal),
            "Expected totalPrice to be the JSON number 1107.00.");

        string expectedCheckIn = ApiTestClient.Iso(checkIn);
        string expectedCheckOut = ApiTestClient.Iso(checkOut);
        Assert.True(
            root.GetProperty("checkIn") is { ValueKind: JsonValueKind.String } checkInMember
                && string.Equals(checkInMember.GetString(), expectedCheckIn, StringComparison.Ordinal),
            $"Expected checkIn to be the JSON string {expectedCheckIn}.");
        Assert.True(
            root.GetProperty("checkOut") is { ValueKind: JsonValueKind.String } checkOutMember
                && string.Equals(checkOutMember.GetString(), expectedCheckOut, StringComparison.Ordinal),
            $"Expected checkOut to be the JSON string {expectedCheckOut}.");
        Assert.True(
            root.GetProperty("status") is { ValueKind: JsonValueKind.String } status
                && string.Equals(status.GetString(), "Confirmed", StringComparison.Ordinal),
            "Expected status to be the JSON string Confirmed.");
        Assert.True(
            root.GetProperty("createdAt") is { ValueKind: JsonValueKind.String } createdAt
                && createdAt.GetString() is string instant
                && instant.EndsWith('Z'),
            "Expected createdAt to be a JSON string ending in Z.");

        Assert.True(
            booking.TotalPrice == PriceCalculator.Total(suite.NightlyPrice, 3),
            "Expected totalPrice to be the Suite's nightly price times 3 nights.");
        Assert.True(booking.TotalPrice == 1107.00m, "Expected totalPrice to bind to 1107.00.");
        Assert.True(
            string.Equals(booking.Status, "Confirmed", StringComparison.Ordinal),
            "Expected status to bind to Confirmed.");
        Assert.True(booking.HotelId == suite.HotelId, $"Expected hotelId to be the id of {ChicagoLoop}.");
        Assert.True(
            string.Equals(booking.HotelName, ChicagoLoop, StringComparison.Ordinal),
            $"Expected hotelName to be {ChicagoLoop}.");
        Assert.True(booking.RoomTypeId == suite.Id, $"Expected roomTypeId to be the id of {ChicagoLoop}'s Suite.");
        Assert.True(
            string.Equals(booking.RoomTypeName, "Suite", StringComparison.Ordinal),
            "Expected roomTypeName to be Suite.");
        Assert.True(
            string.Equals(booking.RoomNumber, "401", StringComparison.Ordinal),
            "Expected roomNumber to be 401.");
        Assert.True(booking.CheckIn == checkIn, $"Expected checkIn to bind to {expectedCheckIn}.");
        Assert.True(booking.CheckOut == checkOut, $"Expected checkOut to bind to {expectedCheckOut}.");
        Assert.True(booking.Nights == 3, "Expected nights to be 3.");
        Assert.True(booking.Guests == 2, "Expected guests to be 2.");
        Assert.True(booking.Id > 0, "Expected the booking to have a positive id.");

        Assert.True(booking.CreatedAt.Kind == DateTimeKind.Utc, "Expected createdAt to bind to a UTC instant.");
        DateTime now = TimeProvider.System.GetUtcNow().UtcDateTime;
        Assert.True(
            booking.CreatedAt >= now.AddMinutes(-5) && booking.CreatedAt <= now.AddMinutes(5),
            "Expected createdAt to be within five minutes of the test's UTC clock.");

        // The detail's stay total and the booked total both come from PriceCalculator, so they must agree.
        ApiTestClient.HotelDetail detail =
            await _client.GetHotelDetailAsync(suite.HotelId, checkIn, checkOut, guests: 2, ct);
        ApiTestClient.RoomTypeAvailability[] suiteTypes = [.. detail.RoomTypes.Where(type => type.Id == suite.Id)];
        Assert.True(suiteTypes.Length == 1, $"Expected the detail of {ChicagoLoop} to list its Suite exactly once.");
        Assert.True(
            suiteTypes[0].StayTotal == booking.TotalPrice,
            "Expected the detail's Suite stayTotal for the same stay to equal the booked totalPrice.");
    }

    [Fact]
    public async Task Create_BackToBackStay_SharesRoom()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType standard =
            await _factory.GetRoomTypeAsync(SeattleDowntown, "Standard", ct);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly arrival = ApiTestClient.TodayUtc.AddDays(44);

        ApiTestClient.Booking first = await _client.CreateBookingOkAsync(
            session.Token,
            standard.HotelId,
            standard.Id,
            arrival,
            arrival.AddDays(2),
            guests: 2,
            ct);

        // The second stay starts on the first one's check-out day, which is not occupied, so the lowest room is
        // still free for it.
        ApiTestClient.Booking second = await _client.CreateBookingOkAsync(
            session.Token,
            standard.HotelId,
            standard.Id,
            arrival.AddDays(2),
            arrival.AddDays(4),
            guests: 2,
            ct);

        Assert.Equal("101", first.RoomNumber);
        Assert.Equal(first.RoomNumber, second.RoomNumber);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Create_AfterDatabaseTerminatesPooledConnections_SearchAndBookingSucceed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType standard = await _factory.GetRoomTypeAsync(ChicagoLoop, "Standard", ct);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(70);
        DateOnly checkOut = checkIn.AddDays(2);

        // The lookups, the registration and the login leave the API's connections idle in its pool. Ending their
        // sessions, as a database restart does, makes the next command on each of them fail with SqlState 57P01.
        Assert.True(await TerminateApiSessionsAsync(_factory, ct) > 0, "Expected the API to hold pooled connections.");

        using (HttpResponseMessage search = await _client.SearchAsync(
            "Chicago",
            ApiTestClient.Iso(checkIn),
            ApiTestClient.Iso(checkOut),
            "2",
            ct))
        {
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            List<ApiTestClient.HotelSummary> hotels =
                await _client.ReadAsync<List<ApiTestClient.HotelSummary>>(search, ct);
            Assert.Equal(standard.HotelId, Assert.Single(hotels).Id);
        }

        // The search left a new connection in the pool; it is ended too, so the booking meets the same failure.
        Assert.True(
            await TerminateApiSessionsAsync(_factory, ct) > 0,
            "Expected the search to leave a pooled connection.");

        ApiTestClient.Booking booking = await _client.CreateBookingOkAsync(
            session.Token,
            standard.HotelId,
            standard.Id,
            checkIn,
            checkOut,
            guests: 2,
            ct);

        Assert.Equal("101", booking.RoomNumber);
        Assert.Equal("Confirmed", booking.Status);
    }

    [Fact]
    public async Task Create_SessionTerminatedInsideTransaction_RetriesAndReturns201()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType deluxe = await _factory.GetRoomTypeAsync(ChicagoLoop, "Deluxe", ct);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(80);
        DateOnly checkOut = checkIn.AddDays(2);

        // The holder's lock blocks the request's insert, after it has locked the room type, picked a room and added
        // its booking inside the transaction it has begun, which is where its session is then ended.
        await using NpgsqlConnection holder = new(UnpooledConnectionString(_factory));
        await holder.OpenAsync(ct);
        await using NpgsqlTransaction holderTx = await holder.BeginTransactionAsync(ct);
        await using (NpgsqlCommand lockBookings = new(LockBookingsSql, holder, holderTx))
        {
            await lockBookings.ExecuteNonQueryAsync(ct);
        }

        Task<HttpResponseMessage> request = _client.CreateBookingAsync(
            ApiTestClient.BookingBody(deluxe.HotelId, deluxe.Id, checkIn, checkOut, guests: 2),
            session.Token,
            ct);

        int? firstWaiter = null;
        bool terminated = false;
        int? retryWaiter = null;
        HttpResponseMessage response;
        try
        {
            await using NpgsqlConnection observer = new(UnpooledConnectionString(_factory));
            await observer.OpenAsync(ct);

            firstWaiter = await WaitForInsertWaiterAsync(observer, holder.ProcessID, excludedPid: 0, request, ct);
            if (firstWaiter is int waiterPid)
            {
                await using NpgsqlCommand terminate = new(TerminateSessionSql, observer);
                terminate.Parameters.AddWithValue("pid", waiterPid);
                terminated = (bool)(await terminate.ExecuteScalarAsync(ct))!;

                // A retry runs the whole unit again on a new session, which queues behind the holder once more.
                retryWaiter = await WaitForInsertWaiterAsync(observer, holder.ProcessID, waiterPid, request, ct);
            }
        }
        finally
        {
            // Runs on every path. The holder changed nothing, so the rollback only releases the lock; the request is
            // then drained, so none is left running into the next test or into the factory's database drop.
            await holderTx.RollbackAsync(CancellationToken.None);
            response = await request.WaitAsync(DrainLimit, ct);
        }

        using (response)
        {
            Assert.True(firstWaiter is not null, "The booking request was not observed waiting to insert.");
            Assert.True(terminated, "The waiting booking session was not terminated.");
            Assert.True(retryWaiter is not null, "The booking request was not observed waiting again on a new session.");
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            ApiTestClient.Booking booking = await _client.ReadAsync<ApiTestClient.Booking>(response, ct);
            Assert.Equal("201", booking.RoomNumber);
        }

        // The ended attempt inserted nothing, and the retry inserted exactly one booking.
        int bookings = await _factory.ExecuteDbAsync(
            (db, token) => db.Bookings.AsNoTracking().CountAsync(b => b.GuestId == session.GuestId, token),
            ct);
        Assert.Equal(1, bookings);
    }

    [Fact]
    public async Task Create_CommitAcknowledgementLost_ReturnsCommittedBookingWithoutSecondInsert()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType family = await _factory.GetRoomTypeAsync(ChicagoLoop, "Family", ct);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(90);
        DateOnly checkOut = checkIn.AddDays(2);

        // No request can lose a commit's acknowledgement on purpose, so the service runs here on a context of its own,
        // under a retrying strategy, whose interceptor reports a transient failure once, after the first commit has
        // taken effect.
        LoseFirstCommitAcknowledgement interceptor = new();
        DbContextOptions<SmartHotelDbContext> options = new DbContextOptionsBuilder<SmartHotelDbContext>()
            .UseNpgsql(
                UnpooledConnectionString(_factory),
                npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), errorCodesToAdd: null))
            .AddInterceptors(interceptor)
            .Options;

        CreateBookingResult result;
        await using (SmartHotelDbContext db = new(options))
        {
            BookingService service = new(
                db,
                new AvailabilityQueries(db),
                TimeProvider.System,
                NullLogger<BookingService>.Instance);
            result = await service.CreateAsync(
                session.GuestId,
                new CreateBookingRequest(
                    family.HotelId,
                    family.Id,
                    ApiTestClient.Iso(checkIn),
                    ApiTestClient.Iso(checkOut),
                    Guests: 2),
                ct);
        }

        Assert.True(interceptor.Failed, "The first commit's acknowledgement was not lost.");
        CreateBookingResult.Created created = Assert.IsType<CreateBookingResult.Created>(result);
        Assert.Equal("301", created.Booking.RoomNumber);

        // The check after the failure found the committed booking, so no retry inserted a second one.
        List<int> bookingIds = await _factory.ExecuteDbAsync(
            (db, token) => db.Bookings
                .AsNoTracking()
                .Where(b => b.GuestId == session.GuestId)
                .Select(static b => b.Id)
                .ToListAsync(token),
            ct);
        Assert.Equal(created.Booking.Id, Assert.Single(bookingIds));
    }

    [Fact]
    public async Task Create_CommitTakesEffectAfterCheck_ReturnsCommittedBookingWithoutSecondInsert()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType suite = await _factory.GetRoomTypeAsync(NewYorkMidtown, "Suite", ct);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(100);
        DateOnly checkOut = checkIn.AddDays(2);

        // No request can have its commit completed late on purpose, so the service runs here on a context of its own,
        // under a retrying strategy, whose interceptor moves the first commit's transaction to the holder session and
        // reports a transient failure. The holder keeps that transaction open, with the room type's lock and the
        // booking, past the check that follows the failure, as a server still completing the commit would.
        await using NpgsqlConnection holder = new(UnpooledConnectionString(_factory));
        await holder.OpenAsync(ct);
        HoldFirstCommit interceptor = new(holder, suite.Id, session.GuestId);
        DbContextOptions<SmartHotelDbContext> options = new DbContextOptionsBuilder<SmartHotelDbContext>()
            .UseNpgsql(
                UnpooledConnectionString(_factory),
                npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), errorCodesToAdd: null))
            .AddInterceptors(interceptor)
            .Options;

        Task<CreateBookingResult> creation = CreateOnOwnContextAsync(
            options,
            session.GuestId,
            new CreateBookingRequest(
                suite.HotelId,
                suite.Id,
                ApiTestClient.Iso(checkIn),
                ApiTestClient.Iso(checkOut),
                Guests: 2),
            ct);

        int? retryWaiter = null;
        bool lateCommitted = false;
        CreateBookingResult result;
        try
        {
            await using NpgsqlConnection observer = new(UnpooledConnectionString(_factory));
            await observer.OpenAsync(ct);

            // The retry begins only after the check, and its lock waits behind the held transaction.
            retryWaiter = await WaitForHolderWaiterAsync(observer, holder.ProcessID, creation, ct);
        }
        finally
        {
            // Runs on every path. A commit the server is completing takes effect whatever the client does, so the held
            // transaction commits; the creation is then drained, so none is left running into the next test.
            if (interceptor.HeldTransaction is NpgsqlTransaction held)
            {
                await using (held)
                {
                    await held.CommitAsync(CancellationToken.None);
                }

                lateCommitted = true;
            }

            result = await creation.WaitAsync(DrainLimit, ct);
        }

        Assert.True(lateCommitted, "The first commit was not held back.");
        CreateBookingResult.Created created = Assert.IsType<CreateBookingResult.Created>(result);

        // The retry found the late booking under the lock and inserted no second one.
        List<int> bookingIds = await _factory.ExecuteDbAsync(
            (db, token) => db.Bookings
                .AsNoTracking()
                .Where(b => b.GuestId == session.GuestId)
                .Select(static b => b.Id)
                .ToListAsync(token),
            ct);
        Assert.Equal(created.Booking.Id, Assert.Single(bookingIds));
        Assert.Equal("401", created.Booking.RoomNumber);
        Assert.True(retryWaiter is not null, "The retry was not observed waiting for the held transaction's lock.");
    }

    // The room type has free rooms, so the request passes the free-room check under its row lock. A trigger then
    // plays a writer that ignores that lock: just before the API's row is inserted, it inserts another guest's
    // confirmed booking of the same room and dates in the same transaction, so the API's insert fails with 23P01 on
    // EX_Bookings_Room_NoOverlap, the conflict BookingService translates to 409.
    [Fact]
    public async Task CreateBooking_ExclusionViolationOnInsert_Returns409WithOneConflictWarningAndNoErrorRecord()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.LogCapture capture = new();
        await using SmartHotelApiFactory variant = _factory.CreateLogCaptureVariant(
            capture,
            SmartHotelApiFactory.LogCapture.DataLayerRules,
            additionalOverrides: null);

        // A hand-built factory is not a fixture, so it is started here.
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();

        ApiTestClient.GuestSession booker = await client.RegisterGuestAsync(ct);
        ApiTestClient.GuestSession rival = await client.RegisterGuestAsync(ct);
        SmartHotelApiFactory.SeededRoomType roomType = await variant.GetRoomTypeAsync(SeattleDowntown, "Standard", ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(50);
        DateOnly checkOut = checkIn.AddDays(2);

        ApiTestClient.HotelDetail detail = await client.GetHotelDetailAsync(
            roomType.HotelId,
            checkIn,
            checkOut,
            guests: 2,
            ct);
        Assert.Equal(
            SeedCatalog.RoomsPerType,
            Assert.Single(detail.RoomTypes, type => type.Id == roomType.Id).AvailableRooms);

        // The trigger lives in the variant's own database, which is dropped with the variant.
        await ExecuteSqlAsync(
            variant.DatabaseConnectionString,
            RivalTriggerFunctionSql(booker.GuestId, rival.GuestId),
            ct);
        await ExecuteSqlAsync(variant.DatabaseConnectionString, RivalTriggerSql, ct);

        int mark = capture.Count;
        using HttpResponseMessage response = await client.CreateBookingAsync(
            ApiTestClient.BookingBody(roomType.HotelId, roomType.Id, checkIn, checkOut, guests: 2),
            booker.Token,
            ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.Conflict, cancellationToken: ct);

        IReadOnlyList<SmartHotelApiFactory.CapturedLog> records = capture.Since(mark);
        SmartHotelApiFactory.CapturedLog conflict = Assert.Single(
            records,
            static record => record.Category == BookingServiceCategory && record.EventId == BookingConflictEventId);
        Assert.Equal(LogLevel.Warning, conflict.Level);
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            static record => record.Level >= LogLevel.Error,
            "Error or Critical record");

        // The rollback discarded the rival's booking together with the rejected one.
        int stored = await variant.ExecuteDbAsync(
            (db, token) => db.Bookings
                .AsNoTracking()
                .CountAsync(
                    booking => booking.Room.RoomTypeId == roomType.Id
                        && booking.CheckIn == checkIn
                        && booking.CheckOut == checkOut,
                    token),
            ct);
        Assert.Equal(0, stored);
    }

    // Ending the API's pooled sessions, as a database restart does, makes the next command on one of them fail with
    // 57P01, which the retrying execution strategy runs again on a new connection. A failure the retry overcomes is
    // not the request's outcome, so it leaves no record at Warning or above and none that quotes the exception.
    [Fact]
    public async Task Search_AfterPooledSessionsAreTerminated_SucceedsWithoutFailureRecords()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.LogCapture capture = new();
        await using SmartHotelApiFactory variant = _factory.CreateLogCaptureVariant(
            capture,
            SmartHotelApiFactory.LogCapture.DataLayerRules,
            additionalOverrides: null);
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();
        string checkIn = ApiTestClient.Iso(ApiTestClient.TodayUtc.AddDays(30));
        string checkOut = ApiTestClient.Iso(ApiTestClient.TodayUtc.AddDays(32));

        // Startup's migration and seed leave the API's connections idle in its pool.
        Assert.True(
            await TerminateApiSessionsAsync(variant, ct) > 0,
            "Expected the API to hold pooled connections.");

        int mark = capture.Count;
        using HttpResponseMessage response = await client.SearchAsync("Seattle", checkIn, checkOut, "2", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        IReadOnlyList<SmartHotelApiFactory.CapturedLog> records = capture.Since(mark);
        Assert.DoesNotContain(records, static record => record.Level >= LogLevel.Warning);
        Assert.DoesNotContain(
            records,
            static record => record.Message.Contains(PostgresErrorCodes.AdminShutdown, StringComparison.Ordinal));
    }

    // pg_trigger_depth() is 1 for the API's own insert and 2 for the trigger's, so only the API's insert, and only
    // the booker's, is preceded by a rival booking. The guest ids are integers written in invariant form.
    private static string RivalTriggerFunctionSql(int bookerGuestId, int rivalGuestId)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            CREATE FUNCTION it_insert_rival_booking() RETURNS trigger LANGUAGE plpgsql AS $function$
            BEGIN
                IF pg_trigger_depth() = 1 AND NEW."GuestId" = {{bookerGuestId}} THEN
                    INSERT INTO "Bookings"
                        ("GuestId", "RoomId", "CheckIn", "CheckOut", "GuestCount", "TotalPrice", "Status", "CreatedAt")
                    VALUES
                        ({{rivalGuestId}}, NEW."RoomId", NEW."CheckIn", NEW."CheckOut", 1, NEW."TotalPrice",
                         'Confirmed', NEW."CreatedAt");
                END IF;
                RETURN NEW;
            END
            $function$
            """);
    }

    private static async Task ExecuteSqlAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // The test's own sessions bypass the pool the API draws from, so a session the test closes is never handed to the
    // API in place of one the test ended.
    private static string UnpooledConnectionString(SmartHotelApiFactory api)
    {
        return new NpgsqlConnectionStringBuilder(api.DatabaseConnectionString) { Pooling = false }
            .ConnectionString;
    }

    // Tests in a class run one at a time, and a variant host has a database of its own, so no other test's request is
    // using a session this ends. Returns how many sessions exited.
    private static async Task<int> TerminateApiSessionsAsync(
        SmartHotelApiFactory api,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(UnpooledConnectionString(api));
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand terminate = new(TerminateOtherSessionsSql, connection);
        await using NpgsqlDataReader reader = await terminate.ExecuteReaderAsync(cancellationToken);

        int terminated = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetBoolean(0))
            {
                terminated++;
            }
        }

        return terminated;
    }

    // Polls with autocommit statements, so each poll reads a fresh pg_stat_activity snapshot. Returns null when the
    // request completes, or the observation limit passes, before a waiter appears.
    private static async Task<int?> WaitForInsertWaiterAsync(
        NpgsqlConnection observer,
        int holderPid,
        int excludedPid,
        Task request,
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

            if (request.IsCompleted)
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
        Task request,
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

            if (request.IsCompleted)
            {
                return null;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
        while (elapsed.Elapsed < ObservationLimit);

        return null;
    }

    // The service on a context of its own, so the test's interceptor sees every transaction of the creation.
    private static async Task<CreateBookingResult> CreateOnOwnContextAsync(
        DbContextOptions<SmartHotelDbContext> options,
        int guestId,
        CreateBookingRequest request,
        CancellationToken cancellationToken)
    {
        await using SmartHotelDbContext db = new(options);
        BookingService service = new(
            db,
            new AvailabilityQueries(db),
            TimeProvider.System,
            NullLogger<BookingService>.Instance);
        return await service.CreateAsync(guestId, request, cancellationToken);
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

    // Holds the first commit back, as a server still completing a commit when the connection is lost would. The
    // attempt's booking, its id included, moves to a transaction on the holder session, which first locks the room
    // type's row as the attempt did; the attempt's own transaction is rolled back; and a transient failure is reported.
    // The held transaction stays open, with the lock and the uncommitted booking, until the test commits it.
    private sealed class HoldFirstCommit(NpgsqlConnection holder, int roomTypeId, int guestId)
        : DbTransactionInterceptor
    {
        // Read inside the attempt's transaction, the only one that sees its uncommitted booking.
        private const string ReadBookingSql =
            "SELECT row_to_json(b)::text FROM \"Bookings\" b WHERE b.\"GuestId\" = @guestId";

        private const string LockRoomTypeSql = "SELECT 1 FROM \"RoomTypes\" WHERE \"Id\" = @id FOR UPDATE";

        // Every column, the id included, so the held row is the attempt's booking itself.
        private const string InsertBookingSql =
            "INSERT INTO \"Bookings\" SELECT * FROM json_populate_record(NULL::\"Bookings\", @booking::json)";

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

            string booking;
            await using (NpgsqlCommand read = new(
                ReadBookingSql,
                (NpgsqlConnection)transaction.Connection!,
                (NpgsqlTransaction)transaction))
            {
                read.Parameters.AddWithValue("guestId", guestId);
                booking = await read.ExecuteScalarAsync(cancellationToken) as string
                    ?? throw new InvalidOperationException("The attempt's booking was not found.");
            }

            // Releases the room type's lock, which the held transaction takes next.
            await transaction.RollbackAsync(cancellationToken);

            NpgsqlTransaction held = await holder.BeginTransactionAsync(cancellationToken);
            Volatile.Write(ref _held, held);
            await using (NpgsqlCommand lockRoomType = new(LockRoomTypeSql, holder, held))
            {
                lockRoomType.Parameters.AddWithValue("id", roomTypeId);
                await lockRoomType.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (NpgsqlCommand insert = new(InsertBookingSql, holder, held))
            {
                insert.Parameters.AddWithValue("booking", booking);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            throw new NpgsqlException(
                "The connection was lost before the commit was acknowledged.",
                new IOException("The connection was reset."));
        }
    }

    // Every booking event is written after its outcome is final, so a log provider that fails each of them must leave
    // the 201s, the sold-out 409 and the 204 unchanged, with their effects in the database. The variant host has a
    // database of its own, so this test books nothing in the class's shared one.
    [Fact]
    public async Task CreateAndCancel_LoggingFailure_KeepsOutcomes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        FailingBookingLogProvider logProvider = new();
        await using SmartHotelApiFactory variant = new(_postgres)
        {
            ServiceOverrides = services => services.AddSingleton<ILoggerProvider>(logProvider),
        };

        // A hand-built factory is not a fixture, so it is started here.
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();

        SmartHotelApiFactory.SeededRoomType standard = await variant.GetRoomTypeAsync(ChicagoLoop, "Standard", ct);
        IReadOnlyList<SmartHotelApiFactory.SeededRoom> rooms = await variant.GetRoomsAsync(standard.Id, ct);
        Assert.Equal(SeedCatalog.RoomsPerType, rooms.Count);
        ApiTestClient.GuestSession session = await client.RegisterGuestAsync(ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(70);
        DateOnly checkOut = checkIn.AddDays(2);

        List<int> bookingIds = [];
        for (int i = 0; i < rooms.Count; i++)
        {
            ApiTestClient.Booking booking = await client.CreateBookingOkAsync(
                session.Token,
                standard.HotelId,
                standard.Id,
                checkIn,
                checkOut,
                guests: 2,
                ct);
            bookingIds.Add(booking.Id);
        }

        int confirmed = await variant.ExecuteDbAsync(
            (db, token) => db.Bookings
                .AsNoTracking()
                .CountAsync(b => b.GuestId == session.GuestId && b.Status == BookingStatus.Confirmed, token),
            ct);
        Assert.Equal(rooms.Count, confirmed);

        JsonObject body = ApiTestClient.BookingBody(standard.HotelId, standard.Id, checkIn, checkOut, guests: 2);
        using HttpResponseMessage conflict = await client.CreateBookingAsync(body, session.Token, ct);
        await ProblemAssert.AssertProblemAsync(conflict, HttpStatusCode.Conflict, cancellationToken: ct);

        int cancelledId = bookingIds[0];
        using HttpResponseMessage cancelled = await client.CancelBookingAsync(cancelledId, session.Token, ct);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);

        BookingStatus status = await variant.ExecuteDbAsync(
            (db, token) => db.Bookings
                .AsNoTracking()
                .Where(b => b.Id == cancelledId)
                .Select(static b => b.Status)
                .SingleAsync(token),
            ct);
        Assert.Equal(BookingStatus.Cancelled, status);

        // The provider was handed every event, so each outcome above survived a failed write; the dates are the API's
        // exact yyyy-MM-dd form.
        string dates = $"CheckIn={ApiTestClient.Iso(checkIn)} CheckOut={ApiTestClient.Iso(checkOut)}";
        List<(int EventId, string Message)> expectedEvents =
        [
            .. bookingIds.Select(id => (3001, string.Create(
                CultureInfo.InvariantCulture,
                $"Booking created. BookingId={id} RoomTypeId={standard.Id} {dates}"))),
            (3002, string.Create(CultureInfo.InvariantCulture, $"Booking conflict. RoomTypeId={standard.Id} {dates}")),
            (3003, string.Create(CultureInfo.InvariantCulture, $"Booking cancelled. BookingId={cancelledId}")),
        ];
        Assert.Equal(expectedEvents, logProvider.FailedEvents);
    }

    // Hands BookingService's category a logger that records each event and then throws, as a broken log sink does;
    // every other category gets the null logger, so the host starts and serves as usual.
    private sealed class FailingBookingLogProvider : ILoggerProvider
    {
        private static readonly string? BookingServiceCategory = typeof(BookingService).FullName;

        private readonly ConcurrentQueue<(int EventId, string Message)> _failedEvents = new();
        private int _disposed;

        public IReadOnlyCollection<(int EventId, string Message)> FailedEvents => _failedEvents;

        public ILogger CreateLogger(string categoryName)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return string.Equals(categoryName, BookingServiceCategory, StringComparison.Ordinal)
                ? new FailingLogger(_failedEvents)
                : NullLogger.Instance;
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposed, 1);
        }
    }

    private sealed class FailingLogger(ConcurrentQueue<(int EventId, string Message)> failedEvents) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullLogger.Instance.BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            failedEvents.Enqueue((eventId.Id, formatter(state, exception)));
            throw new InvalidOperationException("The log sink is unavailable.");
        }
    }
}