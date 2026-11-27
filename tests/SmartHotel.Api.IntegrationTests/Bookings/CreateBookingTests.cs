using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using SmartHotel.Api.Common.Pricing;
using SmartHotel.Api.Infrastructure;
using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Api.Modules.Availability;
using SmartHotel.Api.Modules.Bookings;
using SmartHotel.Data;
using SmartHotel.Data.Entities;
using SmartHotel.Data.Seed;

using Xunit.Sdk;

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

    // The request-body cases send POST api/bookings themselves, so they control its Content-Type, body and token.
    private const string BookingsRoute = "api/bookings";
    private const string BearerScheme = "Bearer";
    private const string JsonMediaType = "application/json";
    private const string TextPlainMediaType = "text/plain";

    // A JSON object cut off after its first member name, so the parser fails before any member binds.
    private const string MalformedBookingJson = "{\"hotelId\":";

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

    // Ends every other client session that the connected login role holds in the connected database, as a database
    // restart does, and waits up to 5 s for each one to exit. The role is not a superuser, so it may signal only its
    // own backends. The filter runs before the select list, so no other role's session, no background process and no
    // session of another database is ever signalled.
    private const string TerminateOtherSessionsSql =
        "SELECT pg_terminate_backend(pid, 5000) FROM pg_stat_activity "
        + "WHERE datname = current_database() AND usename = current_user AND backend_type = 'client backend' "
        + "AND pid <> pg_backend_pid()";

    private const string TerminateSessionSql = "SELECT pg_terminate_backend(@pid, 5000)";

    // Ends, of the sessions TerminateOtherSessionsSql ends, only those that carry the given application name and the
    // one with the given process id, which matches none when it is 0. Both values are bound, never part of the text.
    private const string TerminateNamedSessionsSql =
        "SELECT pg_terminate_backend(pid, 5000) FROM pg_stat_activity "
        + "WHERE datname = current_database() AND usename = current_user AND backend_type = 'client backend' "
        + "AND pid <> pg_backend_pid() AND (application_name = @applicationName OR pid = @holderPid)";

    // Fires the function RivalTriggerFunctionSql creates before each row inserted into Bookings.
    private const string RivalTriggerSql =
        "CREATE TRIGGER it_insert_rival_booking BEFORE INSERT ON \"Bookings\" "
        + "FOR EACH ROW EXECUTE FUNCTION it_insert_rival_booking()";

    // One row per booker whose insert the trigger precedes with a rival's booking. The guest ids reach the database
    // only as InsertRivalBookingSql's bound parameters, so every statement that defines the trigger is fixed text.
    private const string CreateRivalBookingTableSql =
        "CREATE TABLE it_rival_booking (booker_guest_id integer PRIMARY KEY, rival_guest_id integer NOT NULL)";

    private const string InsertRivalBookingSql =
        "INSERT INTO it_rival_booking (booker_guest_id, rival_guest_id) VALUES (@bookerGuestId, @rivalGuestId)";

    // pg_trigger_depth() is 1 for the API's own insert and 2 for the trigger's, so only the API's insert, and only one
    // whose guest has a row in it_rival_booking, is preceded by that row's rival's confirmed booking of the same room
    // and dates.
    private const string RivalTriggerFunctionSql =
        """
        CREATE FUNCTION it_insert_rival_booking() RETURNS trigger LANGUAGE plpgsql AS $function$
        DECLARE
            rival_id integer;
        BEGIN
            IF pg_trigger_depth() = 1 THEN
                SELECT rival_guest_id INTO rival_id FROM it_rival_booking WHERE booker_guest_id = NEW."GuestId";
                IF FOUND THEN
                    INSERT INTO "Bookings"
                        ("GuestId", "RoomId", "CheckIn", "CheckOut", "GuestCount", "TotalPrice", "Status", "CreatedAt")
                    VALUES
                        (rival_id, NEW."RoomId", NEW."CheckIn", NEW."CheckOut", 1, NEW."TotalPrice", 'Confirmed',
                         NEW."CreatedAt");
                END IF;
            END IF;
            RETURN NEW;
        END
        $function$
        """;

    // The InitialCreate migration's foreign key from a booking to its guest.
    private const string BookingGuestForeignKeyName = "FK_Bookings_Guests_GuestId";

    // Removes the booking's guest row just before each row is inserted into Bookings, after the request has passed
    // authentication, which rejects a token whose guest is already gone. The insert then violates
    // BookingGuestForeignKeyName. Only a guest with no bookings can be removed, and the request's rollback restores it.
    private const string DeleteBookingGuestFunctionSql =
        """
        CREATE FUNCTION it_delete_booking_guest() RETURNS trigger LANGUAGE plpgsql AS $function$
        BEGIN
            DELETE FROM "Guests" WHERE "Id" = NEW."GuestId";
            RETURN NEW;
        END
        $function$
        """;

    private const string DeleteBookingGuestTriggerSql =
        "CREATE TRIGGER it_delete_booking_guest BEFORE INSERT ON \"Bookings\" "
        + "FOR EACH ROW EXECUTE FUNCTION it_delete_booking_guest()";

    // A second exclusion constraint on Bookings, beside EX_Bookings_Room_NoOverlap: no guest holds two confirmed
    // bookings whose stays overlap. An insert that breaks it fails with the same SqlState, 23P01, under another name.
    private const string GuestNoOverlapConstraintName = "IT_Bookings_Guest_NoOverlap";

    private const string AddGuestNoOverlapSql =
        "ALTER TABLE \"Bookings\" ADD CONSTRAINT \"" + GuestNoOverlapConstraintName + "\" "
        + "EXCLUDE USING gist (\"GuestId\" WITH =, daterange(\"CheckIn\", \"CheckOut\", '[)') WITH &&) "
        + "WHERE (\"Status\" = 'Confirmed')";

    // BookingService's BookingCreated.
    private const int BookingCreatedEventId = 3001;

    // BookingService's BookingConflict.
    private const int BookingConflictEventId = 3002;

    // ApiExceptionHandler's sanitized UnhandledException.
    private const int UnhandledExceptionEventId = 5000;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ObservationLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(30);

    // How long before the drain limit work still running is cancelled, which leaves it that long to stop.
    private static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(5);

    // How long a host is given, once a request's drain has ended, to finish serving the request.
    private static readonly TimeSpan HostStopLimit = TimeSpan.FromSeconds(5);

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
    [InlineData("hotelId-missing", "hotelId")]
    [InlineData("hotelId-zero", "hotelId")]
    [InlineData("hotelId-null", "hotelId")]
    [InlineData("roomTypeId-zero", "roomTypeId")]
    [InlineData("guests-missing", "guests")]
    [InlineData("checkIn-missing", "checkIn")]
    [InlineData("checkOut-missing", "checkOut")]
    [InlineData("checkOut-not-padded", "checkOut")]
    [InlineData("checkOut-slashed", "checkOut")]
    [InlineData("roomTypeId-boolean", "$.roomTypeId")]
    [InlineData("guests-quoted", "$.guests")]
    [InlineData("checkIn-number", "$.checkIn")]
    public async Task Create_InvalidBodyField_Returns400(string caseLabel, string expectedKey)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType roomType =
            await _factory.GetRoomTypeAsync(SeattleWaterfront, "Deluxe", ct);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly today = ApiTestClient.TodayUtc;

        // Next year's dates are always in the future, so a format case fails only for its one nonconforming date. Both
        // dates use the one year derived from today, so a run that crosses midnight UTC cannot split them.
        int year = ApiTestClient.NextYear(today);
        string formatCheckIn = string.Create(CultureInfo.InvariantCulture, $"{year}-01-05");

        // The baseline is a valid stay; each case changes exactly one member, so it fails only for its stated reason.
        JsonObject body = ApiTestClient.BookingBody(
            roomType.HotelId,
            roomType.Id,
            today.AddDays(30),
            today.AddDays(32),
            guests: 2);

        switch (caseLabel)
        {
            case "hotelId-missing":
                body.Remove("hotelId");
                break;
            case "hotelId-zero":
                body["hotelId"] = 0;
                break;
            case "hotelId-null":
                // An explicit JSON null binds as null, as a missing member does, so the required check rejects it.
                body["hotelId"] = null;
                break;
            case "roomTypeId-zero":
                body["roomTypeId"] = 0;
                break;
            case "guests-missing":
                body.Remove("guests");
                break;
            case "checkIn-missing":
                body.Remove("checkIn");
                break;
            case "checkOut-missing":
                body.Remove("checkOut");
                break;
            case "checkOut-not-padded":
                body["checkIn"] = formatCheckIn;
                body["checkOut"] = string.Create(CultureInfo.InvariantCulture, $"{year}-1-7");
                break;
            case "checkOut-slashed":
                body["checkIn"] = formatCheckIn;
                body["checkOut"] = string.Create(CultureInfo.InvariantCulture, $"01/07/{year}");
                break;
            case "roomTypeId-boolean":
                body["roomTypeId"] = true;
                break;
            case "guests-quoted":
                // The numeric members accept only JSON numbers, so a quoted number is a value of the wrong JSON type.
                body["guests"] = "2";
                break;
            case "checkIn-number":
                // A JSON number is the wrong type for the date string, even one whose digits spell a valid date.
                body["checkIn"] = (year * 10_000) + 105;
                break;
            default:
                throw new ArgumentException($"Unknown case label '{caseLabel}'.", nameof(caseLabel));
        }

        using HttpResponseMessage response = await _client.CreateBookingAsync(body, session.Token, ct);

        // A member the input formatter cannot read is keyed by its JSON path. A member that binds and then fails
        // validation, a missing or null one included, is keyed by its member name.
        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedKey, ct);
    }

    [Theory]
    [InlineData("empty-body")]
    [InlineData("malformed-json")]
    [InlineData("json-array")]
    [InlineData("json-string")]
    public async Task Create_UnbindableJsonBody_Returns400(string caseLabel)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        string payload = caseLabel switch
        {
            "empty-body" => string.Empty,
            "malformed-json" => MalformedBookingJson,
            "json-array" => "[]",
            "json-string" => "\"booking\"",
            _ => throw new ArgumentException($"Unknown case label '{caseLabel}'.", nameof(caseLabel)),
        };

        using HttpResponseMessage response = await PostBookingAsync(
            _client,
            new StringContent(payload, Encoding.UTF8, JsonMediaType),
            session.Token,
            ct);

        // The token is valid, so the body decides the response, and binding rejects it before any booking check runs.
        // An empty body, malformed JSON and a JSON value other than an object are each a body-level failure that
        // names no field, so only the presence of the errors object is checked.
        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.BadRequest, cancellationToken: ct);
    }

    [Theory]
    [InlineData("no-content-type-no-body")]
    [InlineData("text-plain")]
    public async Task Create_NonJsonContentType_Returns415(string caseLabel)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType roomType =
            await _factory.GetRoomTypeAsync(SeattleWaterfront, "Deluxe", ct);
        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        DateOnly today = ApiTestClient.TodayUtc;

        // The text/plain body is a valid booking for a seeded room type, so only its media type can be rejected.
        JsonObject body = ApiTestClient.BookingBody(
            roomType.HotelId,
            roomType.Id,
            today.AddDays(30),
            today.AddDays(32),
            guests: 2);
        HttpContent? content = caseLabel switch
        {
            "no-content-type-no-body" => null,
            "text-plain" => new StringContent(body.ToJsonString(), Encoding.UTF8, TextPlainMediaType),
            _ => throw new ArgumentException($"Unknown case label '{caseLabel}'.", nameof(caseLabel)),
        };

        using HttpResponseMessage response = await PostBookingAsync(_client, content, session.Token, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, cancellationToken: ct);
    }

    [Theory]
    [InlineData("malformed-json")]
    [InlineData("text-plain")]
    [InlineData("no-content-type-no-body")]
    public async Task Create_NoTokenWithInvalidBody_Returns401(string caseLabel)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType roomType =
            await _factory.GetRoomTypeAsync(SeattleWaterfront, "Deluxe", ct);
        DateOnly today = ApiTestClient.TodayUtc;

        // With a valid token, each of these bodies would be rejected with 400 or 415.
        JsonObject body = ApiTestClient.BookingBody(
            roomType.HotelId,
            roomType.Id,
            today.AddDays(30),
            today.AddDays(32),
            guests: 2);
        HttpContent? content = caseLabel switch
        {
            "malformed-json" => new StringContent(MalformedBookingJson, Encoding.UTF8, JsonMediaType),
            "text-plain" => new StringContent(body.ToJsonString(), Encoding.UTF8, TextPlainMediaType),
            "no-content-type-no-body" => null,
            _ => throw new ArgumentException($"Unknown case label '{caseLabel}'.", nameof(caseLabel)),
        };

        using HttpResponseMessage response = await PostBookingAsync(_client, content, token: null, ct);

        // Authorization runs before model binding, so the missing token decides the response whatever the body is,
        // and the problem body keeps the challenge's WWW-Authenticate header.
        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.Unauthorized, cancellationToken: ct);
        ProblemAssert.AssertBearerChallenge(response);
    }

    // Sends POST api/bookings with exactly the given content, none included, and a bearer Authorization header only
    // when a token is given, so a case controls the Content-Type and the body that ApiTestClient would set. Disposing
    // the request also disposes its content; the caller disposes the response.
    private static async Task<HttpResponseMessage> PostBookingAsync(
        ApiTestClient client,
        HttpContent? content,
        string? token,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, BookingsRoute) { Content = content };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, token);
        }

        return await client.Http.SendAsync(request, cancellationToken);
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

        // The fixed title tells the two reasons for a 404 apart.
        (int hotelId, int roomTypeId, string expectedTitle) = caseLabel switch
        {
            "unknown-hotel" => (UnknownId, waterfrontStandard.Id, "Hotel not found"),
            "unknown-room-type" => (downtownId, UnknownId, "Room type not found"),
            "room-type-of-other-hotel" => (downtownId, waterfrontStandard.Id, "Room type not found"),
            _ => throw new ArgumentException($"Unknown case label '{caseLabel}'.", nameof(caseLabel)),
        };
        JsonObject body = ApiTestClient.BookingBody(
            hotelId,
            roomTypeId,
            today.AddDays(30),
            today.AddDays(32),
            guests: 2);

        using HttpResponseMessage response = await _client.CreateBookingAsync(body, session.Token, ct);

        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            cancellationToken: ct);

        // Compared in memory, with a message naming only the expected title, so no response value reaches the output.
        string? title = problem.GetProperty("title").GetString();
        Assert.True(
            string.Equals(expectedTitle, title, StringComparison.Ordinal),
            $"Expected the 404 problem's title to be \"{expectedTitle}\".");
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

    // Each request meets a session the database ended and succeeds only through the retry: the recorder counts the
    // transient command failure each one met. The variant host has a database of its own, so no other test's request
    // uses a session this test ends.
    [Fact]
    public async Task Create_AfterDatabaseTerminatesPooledConnections_SearchAndBookingSucceed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TransientCommandFailureRecorder failures = new();
        await using SmartHotelApiFactory variant = new(_postgres)
        {
            ServiceOverrides = services => AddContextInterceptors(services, failures),
        };

        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();

        SmartHotelApiFactory.SeededRoomType standard = await variant.GetRoomTypeAsync(ChicagoLoop, "Standard", ct);
        ApiTestClient.GuestSession session = await client.RegisterGuestAsync(ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(70);
        DateOnly checkOut = checkIn.AddDays(2);

        // The lookups, the registration and the login leave the API's connections idle in its pool. Ending their
        // sessions, as a database restart does, makes the next command on each of them fail with SqlState 57P01.
        Assert.True(await TerminateApiSessionsAsync(variant, ct) > 0, "Expected the API to hold pooled connections.");

        int mark = failures.Count;
        using (HttpResponseMessage search = await client.SearchAsync(
            "Chicago",
            ApiTestClient.Iso(checkIn),
            ApiTestClient.Iso(checkOut),
            "2",
            ct))
        {
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            List<ApiTestClient.HotelSummary> hotels =
                await client.ReadAsync<List<ApiTestClient.HotelSummary>>(search, ct);
            Assert.Equal(standard.HotelId, Assert.Single(hotels).Id);
        }

        Assert.True(failures.Count > mark, "The search did not meet an ended session before it succeeded.");

        // The search left a new connection in the pool; it is ended too, so the booking meets the same failure.
        Assert.True(
            await TerminateApiSessionsAsync(variant, ct) > 0,
            "Expected the search to leave a pooled connection.");

        mark = failures.Count;
        ApiTestClient.Booking booking = await client.CreateBookingOkAsync(
            session.Token,
            standard.HotelId,
            standard.Id,
            checkIn,
            checkOut,
            guests: 2,
            ct);

        Assert.True(failures.Count > mark, "The booking did not meet an ended session before it succeeded.");
        Assert.Equal("101", booking.RoomNumber);
        Assert.Equal("Confirmed", booking.Status);
    }

    [Fact]
    public async Task Create_SessionTerminatedInsideTransaction_RetriesAndReturns201()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A variant host with a database of its own, whose first middleware tracks the requests it serves and, once
        // closed, starts no other, so the test sees the booking request's handler return, not only its client task
        // end, before the variant is disposed and drops its database.
        ServedRequests served = new();
        await using SmartHotelApiFactory variant = new(_postgres)
        {
            ServiceOverrides = services => services.AddSingleton<IStartupFilter>(served),
        };

        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();

        SmartHotelApiFactory.SeededRoomType deluxe = await variant.GetRoomTypeAsync(ChicagoLoop, "Deluxe", ct);
        ApiTestClient.GuestSession session = await client.RegisterGuestAsync(ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(80);
        DateOnly checkOut = checkIn.AddDays(2);

        // The holder's lock blocks the request's insert, after it has locked the room type, picked a room and added
        // its booking inside the transaction it has begun, which is where its session is then ended.
        await using NpgsqlConnection holder = new(UnpooledConnectionString(variant));
        await holder.OpenAsync(ct);
        await using NpgsqlTransaction holderTx = await holder.BeginTransactionAsync(ct);
        await using (NpgsqlCommand lockBookings = new(LockBookingsSql, holder, holderTx))
        {
            await lockBookings.ExecuteNonQueryAsync(ct);
        }

        // For a request still running past its drain or its host's stop limit, an attempt is made to end every session
        // the API holds in the variant's database, so that the request is not left waiting there.
        Func<List<Exception>, Task> endApiSessions = cleanupFailures => EndApiSessionsAsync(variant, cleanupFailures);

        // The request's own token: the test's cancellation reaches it, and so does the drain's cancellation of a
        // request still running near its limit, which must not cancel the test's other work.
        using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<HttpResponseMessage> request = client.CreateBookingAsync(
            ApiTestClient.BookingBody(deluxe.HotelId, deluxe.Id, checkIn, checkOut, guests: 2),
            session.Token,
            requestCancellation.Token);

        int? firstWaiter = null;
        bool terminated = false;
        int? retryWaiter = null;
        HttpResponseMessage? response = null;
        List<Exception> failures = [];
        try
        {
            try
            {
                await using NpgsqlConnection observer = new(UnpooledConnectionString(variant));
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
            catch (Exception exception)
            {
                // Kept, and rethrown only once the lock is released and the request has stopped.
                failures.Add(exception);
            }
            finally
            {
                // Runs on every path, the test's cancellation included. The holder changed nothing, so the rollback
                // only releases the lock; the request is then drained, and the host is closed and seen to finish
                // serving it. Each wait is bounded first, and for work still running at its bound, an attempt is made
                // to end its sessions before it is awaited until it stops, so nothing is running into the next test or
                // into the variant's database drop, and a late response is disposed below. No step throws, so each
                // runs even when the one before it failed.
                await ReleaseHolderAsync(holder, holderTx, failures);
                response = await DrainAsync(request, "booking request", requestCancellation, endApiSessions, failures);
                await ObserveHostIdleAsync(served, endApiSessions, failures);
            }

            ThrowFailures(failures);

            Assert.True(firstWaiter is not null, "The booking request was not observed waiting to insert.");
            Assert.True(terminated, "The waiting booking session was not terminated.");
            Assert.True(retryWaiter is not null, "The booking request was not observed waiting again on a new session.");
            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            ApiTestClient.Booking booking = await client.ReadAsync<ApiTestClient.Booking>(response, ct);
            Assert.Equal("201", booking.RoomNumber);
        }
        finally
        {
            response?.Dispose();
        }

        // The ended attempt inserted nothing, and the retry inserted exactly one booking.
        int bookings = await variant.ExecuteDbAsync(
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

        // Read before the creation starts: from then on the injector may be using the holder session.
        int holderPid = holder.ProcessID;
        HoldFirstCommit interceptor = new(holder, suite.Id, session.GuestId);

        // The creation's sessions carry an application name of their own, so a creation still running past its wait
        // can have exactly its sessions, and the holder's, ended in the database the class's other tests share.
        string creationApplicationName = "it-creation-" + Guid.NewGuid().ToString("N");
        string creationConnectionString = new NpgsqlConnectionStringBuilder(UnpooledConnectionString(_factory))
        {
            ApplicationName = creationApplicationName,
        }.ConnectionString;
        Func<List<Exception>, Task> endCreationSessions = cleanupFailures => EndCreationSessionsAsync(
            _factory,
            creationApplicationName,
            holder,
            holderPid,
            cleanupFailures);
        DbContextOptions<SmartHotelDbContext> options = new DbContextOptionsBuilder<SmartHotelDbContext>()
            .UseNpgsql(
                creationConnectionString,
                npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), errorCodesToAdd: null))
            .AddInterceptors(interceptor)
            .Options;

        // The creation's own token: the test's cancellation reaches it, and so does the drain's cancellation of a
        // creation still running near its limit, which must not cancel the test's other work.
        using CancellationTokenSource creationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<CreateBookingResult> creation = CreateOnOwnContextAsync(
            options,
            session.GuestId,
            new CreateBookingRequest(
                suite.HotelId,
                suite.Id,
                ApiTestClient.Iso(checkIn),
                ApiTestClient.Iso(checkOut),
                Guests: 2),
            creationCancellation.Token);

        int? retryWaiter = null;
        bool lateCommitted = false;
        CreateBookingResult? result = null;
        List<Exception> failures = [];
        try
        {
            await using NpgsqlConnection observer = new(UnpooledConnectionString(_factory));
            await observer.OpenAsync(ct);

            // The retry begins only after the check, and its lock waits behind the held transaction.
            retryWaiter = await WaitForHolderWaiterAsync(observer, holderPid, creation, ct);
        }
        catch (Exception exception)
        {
            // Kept, and rethrown only once the held commit is released and the creation has stopped.
            failures.Add(exception);
        }
        finally
        {
            // Runs on every path, the test's cancellation included. A commit the server is completing takes effect
            // whatever the client does, so the held transaction commits once the injector is done with the holder
            // session; the creation is then drained. Each wait is bounded first, and for a creation still running at
            // its bound, an attempt is made to end its sessions and the holder's before it is awaited until it stops,
            // so none is running into the next test and no held transaction is left without an owner. Neither step
            // throws, so the drain runs even when the commit failed. The creation's context is disposed inside the
            // creation, so a stopped creation has no command left running.
            lateCommitted = await CommitHeldAsync(
                holder,
                interceptor,
                creation,
                creationCancellation,
                endCreationSessions,
                failures);
            result = await DrainAsync(creation, "creation", creationCancellation, endCreationSessions, failures);
        }

        ThrowFailures(failures);

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

        // The trigger and the table it reads live in the variant's own database, which is dropped with the variant. The
        // booker and rival ids are bound, never written into the SQL text.
        await ExecuteSqlAsync(variant.DatabaseConnectionString, CreateRivalBookingTableSql, ct);
        int configured = await ExecuteSqlAsync(
            variant.DatabaseConnectionString,
            InsertRivalBookingSql,
            [("bookerGuestId", booker.GuestId), ("rivalGuestId", rival.GuestId)],
            ct);
        Assert.Equal(1, configured);
        await ExecuteSqlAsync(variant.DatabaseConnectionString, RivalTriggerFunctionSql, ct);
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

    // Only a 23P01 on EX_Bookings_Room_NoOverlap is a booking conflict. In each case the room type has free rooms, so
    // the request passes the free-room check under its row lock, and its insert then fails with another database
    // error: a foreign-key violation (23503), because the booker's guest row is removed after authentication found it,
    // or a violation of a second exclusion constraint with the same SqlState, 23P01, because the booker already holds a
    // confirmed booking of another room type for the same stay. Either error propagates and becomes the sanitized 500,
    // whose one Error record is the exception handler's event naming that SqlState and constraint, and the rollback
    // leaves no booking of the request behind.
    [Theory]
    [InlineData("foreign-key-violation")]
    [InlineData("other-exclusion-constraint")]
    public async Task CreateBooking_OtherDatabaseErrorOnInsert_Returns500WithoutConflictRecord(string caseLabel)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.LogCapture capture = new();
        await using SmartHotelApiFactory variant = _factory.CreateLogCaptureVariant(
            capture,
            SmartHotelApiFactory.LogCapture.DataLayerRules,
            additionalOverrides: null);

        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();

        ApiTestClient.GuestSession booker = await client.RegisterGuestAsync(ct);
        SmartHotelApiFactory.SeededRoomType roomType = await variant.GetRoomTypeAsync(NewYorkMidtown, "Family", ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(56);
        DateOnly checkOut = checkIn.AddDays(2);

        // Everything arranged here lives in the variant's own database, which is dropped with the variant.
        string expectedSqlState;
        string expectedConstraint;
        List<int> arrangedBookingIds = [];
        switch (caseLabel)
        {
            case "foreign-key-violation":
                await ExecuteSqlAsync(variant.DatabaseConnectionString, DeleteBookingGuestFunctionSql, ct);
                await ExecuteSqlAsync(variant.DatabaseConnectionString, DeleteBookingGuestTriggerSql, ct);
                expectedSqlState = PostgresErrorCodes.ForeignKeyViolation;
                expectedConstraint = BookingGuestForeignKeyName;
                break;
            case "other-exclusion-constraint":
                await ExecuteSqlAsync(variant.DatabaseConnectionString, AddGuestNoOverlapSql, ct);

                // A room of another type, so every room of the requested type stays free for the stay.
                SmartHotelApiFactory.SeededRoomType otherType =
                    await variant.GetRoomTypeAsync(NewYorkMidtown, "Suite", ct);
                IReadOnlyList<SmartHotelApiFactory.SeededRoom> otherRooms =
                    await variant.GetRoomsAsync(otherType.Id, ct);
                Assert.Equal(SeedCatalog.RoomsPerType, otherRooms.Count);
                SmartHotelApiFactory.SeededRoom otherRoom = otherRooms[0];
                int arrangedId = await variant.ExecuteDbAsync(
                    async (db, token) =>
                    {
                        Booking arranged = new()
                        {
                            GuestId = booker.GuestId,
                            RoomId = otherRoom.Id,
                            CheckIn = checkIn,
                            CheckOut = checkOut,
                            GuestCount = 1,
                            TotalPrice = PriceCalculator.Total(otherType.NightlyPrice, 2),
                            Status = BookingStatus.Confirmed,
                            CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime,
                        };
                        db.Bookings.Add(arranged);
                        await db.SaveChangesAsync(token);
                        return arranged.Id;
                    },
                    ct);
                arrangedBookingIds.Add(arrangedId);
                expectedSqlState = PostgresErrorCodes.ExclusionViolation;
                expectedConstraint = GuestNoOverlapConstraintName;
                break;
            default:
                throw new ArgumentException($"Unknown case label '{caseLabel}'.", nameof(caseLabel));
        }

        ApiTestClient.HotelDetail detail = await client.GetHotelDetailAsync(
            roomType.HotelId,
            checkIn,
            checkOut,
            guests: 2,
            ct);
        Assert.Equal(
            SeedCatalog.RoomsPerType,
            Assert.Single(detail.RoomTypes, type => type.Id == roomType.Id).AvailableRooms);

        int mark = capture.Count;
        using HttpResponseMessage response = await client.CreateBookingAsync(
            ApiTestClient.BookingBody(roomType.HotelId, roomType.Id, checkIn, checkOut, guests: 2),
            booker.Token,
            ct);

        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            cancellationToken: ct);

        // The generic problem names neither the database error nor its source. The trace id is random hex that could
        // spell a numeric SqlState by chance, so it is left out of the search.
        string traceId = problem.GetProperty("traceId").GetString()!;
        string payload = (await response.Content.ReadAsStringAsync(ct))
            .Replace(traceId, string.Empty, StringComparison.Ordinal);
        Assert.False(
            payload.Contains(expectedSqlState, StringComparison.Ordinal),
            "Expected the problem body not to carry the SqlState.");
        Assert.False(
            payload.Contains(expectedConstraint, StringComparison.Ordinal),
            "Expected the problem body not to name the constraint.");
        Assert.False(
            payload.Contains("Npgsql", StringComparison.OrdinalIgnoreCase),
            "Expected the problem body not to name the database provider.");
        Assert.False(
            payload.Contains("Postgres", StringComparison.OrdinalIgnoreCase),
            "Expected the problem body not to name the database server.");

        IReadOnlyList<SmartHotelApiFactory.CapturedLog> records = capture.Since(mark);
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            static record => record.Category == BookingServiceCategory
                && record.EventId is BookingCreatedEventId or BookingConflictEventId,
            "booking-created or booking-conflict record");

        // The one Error record is the exception handler's, and it names this case's database error, so that error is
        // what made the response a 500.
        SmartHotelApiFactory.CapturedLog error = Assert.Single(
            records,
            static record => record.Level >= LogLevel.Error);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal(typeof(ApiExceptionHandler).FullName, error.Category);
        Assert.Equal(UnhandledExceptionEventId, error.EventId);
        Assert.True(
            error.Text.Contains("SqlState=" + expectedSqlState, StringComparison.Ordinal),
            "Expected the exception handler's record to name the case's SqlState.");
        Assert.True(
            error.Text.Contains("Constraint=" + expectedConstraint, StringComparison.Ordinal),
            "Expected the exception handler's record to name the case's constraint.");

        // The rollback left no booking of the request: the arranged booking, if any, is the only one stored.
        int requested = await variant.ExecuteDbAsync(
            (db, token) => db.Bookings
                .AsNoTracking()
                .CountAsync(
                    booking => booking.Room.RoomTypeId == roomType.Id
                        && booking.CheckIn == checkIn
                        && booking.CheckOut == checkOut,
                    token),
            ct);
        Assert.Equal(0, requested);
        List<int> storedIds = await variant.ExecuteDbAsync(
            (db, token) => db.Bookings
                .AsNoTracking()
                .OrderBy(static booking => booking.Id)
                .Select(static booking => booking.Id)
                .ToListAsync(token),
            ct);
        Assert.Equal(arrangedBookingIds, storedIds);
    }

    // Ending the API's pooled sessions, as a database restart does, makes the next command on one of them fail with
    // 57P01, which the retrying execution strategy runs again on a new connection. A failure the retry overcomes is
    // not the request's outcome, so it leaves no record at Warning or above and none that quotes the exception. The
    // recorder shows that the search met such a failure, and EF Core's record of an executed command, which is none of
    // the failure records the data layer ignores, shows that the capture received the search's records.
    [Fact]
    public async Task Search_AfterPooledSessionsAreTerminated_SucceedsWithoutFailureRecords()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.LogCapture capture = new();
        TransientCommandFailureRecorder failures = new();
        await using SmartHotelApiFactory variant = _factory.CreateLogCaptureVariant(
            capture,
            SmartHotelApiFactory.LogCapture.DataLayerRules,
            additionalOverrides: services => AddContextInterceptors(services, failures));
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();
        int downtownId = await variant.GetHotelIdAsync(SeattleDowntown, ct);
        int waterfrontId = await variant.GetHotelIdAsync(SeattleWaterfront, ct);
        DateOnly today = ApiTestClient.TodayUtc;

        // Startup's migration and seed, and the lookups, leave the API's connections idle in its pool.
        Assert.True(
            await TerminateApiSessionsAsync(variant, ct) > 0,
            "Expected the API to hold pooled connections.");

        int mark = capture.Count;
        int failureMark = failures.Count;
        using HttpResponseMessage response = await client.SearchAsync(
            "Seattle",
            ApiTestClient.Iso(today.AddDays(30)),
            ApiTestClient.Iso(today.AddDays(32)),
            "2",
            ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(failures.Count > failureMark, "The search did not meet an ended session before it succeeded.");

        // Standard, for 2 guests, is the cheapest type in both seeded Seattle hotels: 129.00 in hotel 1 and 10.00
        // more in hotel 2.
        List<ApiTestClient.HotelSummary> hotels =
            await client.ReadAsync<List<ApiTestClient.HotelSummary>>(response, ct);
        Assert.Collection(
            hotels,
            hotel => AssertHotel(hotel, downtownId, SeattleDowntown, 129.00m),
            hotel => AssertHotel(hotel, waterfrontId, SeattleWaterfront, 139.00m));

        IReadOnlyList<SmartHotelApiFactory.CapturedLog> records = capture.Since(mark);
        Assert.Contains(
            records,
            static record => record.Category == DbLoggerCategory.Database.Command.Name
                && record.EventId == RelationalEventId.CommandExecuted.Id);
        Assert.DoesNotContain(records, static record => record.Level >= LogLevel.Warning);
        Assert.DoesNotContain(
            records,
            static record => record.Message.Contains(PostgresErrorCodes.AdminShutdown, StringComparison.Ordinal));

        static void AssertHotel(ApiTestClient.HotelSummary hotel, int id, string name, decimal lowestNightlyPrice)
        {
            Assert.Equal(id, hotel.Id);
            Assert.Equal(name, hotel.Name);

            // Decimal equality ignores scale, so 129.0 and 129.00 compare equal.
            Assert.Equal(lowestNightlyPrice, hotel.LowestNightlyPrice);
        }
    }

    // The booking's session is ended after its insert and before its commit, as a database restart can do. The
    // commit then fails with Npgsql's disposed-transaction exception, which carries the session's failure inside it;
    // the server rolled the attempt back, so the whole unit runs again on a new session and books the lowest room once.
    // The variant host has a database of its own, so this test books nothing in the class's shared one.
    [Fact]
    public async Task Create_SessionEndedBeforeCommit_RetriesAndReturns201()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EndSessionAtFirstRelease interceptor = new();
        await using SmartHotelApiFactory variant = new(_postgres)
        {
            ServiceOverrides = services => AddContextInterceptors(services, interceptor),
        };

        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();

        SmartHotelApiFactory.SeededRoomType standard = await variant.GetRoomTypeAsync(ChicagoLoop, "Standard", ct);
        IReadOnlyList<SmartHotelApiFactory.SeededRoom> rooms = await variant.GetRoomsAsync(standard.Id, ct);
        Assert.Equal(SeedCatalog.RoomsPerType, rooms.Count);
        ApiTestClient.GuestSession session = await client.RegisterGuestAsync(ct);
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(70);
        DateOnly checkOut = checkIn.AddDays(2);

        // Armed only now, so the session ended is the booking's: startup and the registration are complete.
        interceptor.Arm(UnpooledConnectionString(variant));
        using HttpResponseMessage response = await client.CreateBookingAsync(
            ApiTestClient.BookingBody(standard.HotelId, standard.Id, checkIn, checkOut, guests: 2),
            session.Token,
            ct);

        Assert.True(interceptor.Ended, "The booking's session was not ended before its commit.");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        ApiTestClient.Booking booking = await client.ReadAsync<ApiTestClient.Booking>(response, ct);
        Assert.Equal(rooms[0].RoomNumber, booking.RoomNumber);
        Assert.Equal("Confirmed", booking.Status);

        // The ended attempt's insert never committed, so the retry's booking is the guest's only one.
        var stored = await variant.ExecuteDbAsync(
            (db, token) => db.Bookings
                .AsNoTracking()
                .Where(b => b.GuestId == session.GuestId)
                .Select(static b => new { b.Id, b.Status })
                .ToListAsync(token),
            ct);
        var only = Assert.Single(stored);
        Assert.Equal(booking.Id, only.Id);
        Assert.Equal(BookingStatus.Confirmed, only.Status);
    }

    private static async Task ExecuteSqlAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await ExecuteSqlAsync(connectionString, sql, [], cancellationToken);
    }

    // Runs one statement with its integer values bound as parameters, never written into the SQL text. Returns the
    // number of rows the statement affected.
    private static async Task<int> ExecuteSqlAsync(
        string connectionString,
        string sql,
        IReadOnlyList<(string Name, int Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, int value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // The test's own sessions bypass the pool the API draws from, so a session the test closes is never handed to the
    // API in place of one the test ended.
    private static string UnpooledConnectionString(SmartHotelApiFactory api)
    {
        return new NpgsqlConnectionStringBuilder(api.DatabaseConnectionString) { Pooling = false }
            .ConnectionString;
    }

    // Called only with a variant host, whose database is its own, so no other test's request is using a session this
    // ends. Returns how many sessions exited.
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

    // Asks the database to end the variant API's sessions, as TerminateApiSessionsAsync does, for a request that
    // outlived its drain or its host's stop limit, without checking whether each one exited. Not the test's token: the
    // sessions must end even while the test is being cancelled. Failures are recorded for the caller, never thrown.
    private static async Task EndApiSessionsAsync(SmartHotelApiFactory api, List<Exception> failures)
    {
        try
        {
            await TerminateApiSessionsAsync(api, CancellationToken.None);
        }
        catch (Exception terminateFailure)
        {
            failures.Add(terminateFailure);
        }
    }

    // Asks the database to end the sessions of this class's database that a direct creation which outlived its wait
    // can be using: its own, which carry its application name, and the holder's, on which its injector runs. Whether
    // each one exited is not checked. A closed holder's process id may since belong to another session, so it is
    // matched only while the holder is open. Not the test's token: the sessions must end even while the test is being
    // cancelled. Failures are recorded for the caller, never thrown.
    private static async Task EndCreationSessionsAsync(
        SmartHotelApiFactory api,
        string applicationName,
        NpgsqlConnection holder,
        int holderPid,
        List<Exception> failures)
    {
        try
        {
            await using NpgsqlConnection connection = new(UnpooledConnectionString(api));
            await connection.OpenAsync(CancellationToken.None);
            await using NpgsqlCommand terminate = new(TerminateNamedSessionsSql, connection);
            terminate.Parameters.AddWithValue("applicationName", applicationName);
            terminate.Parameters.AddWithValue("holderPid", holder.State == ConnectionState.Open ? holderPid : 0);
            await terminate.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception terminateFailure)
        {
            failures.Add(terminateFailure);
        }
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
    // The held transaction stays open, with the lock and the uncommitted booking, until the test commits it. It is
    // handed over only once the injector has finished with the holder session, so the injector and the test never use
    // that session at the same time.
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

        private readonly TaskCompletionSource<NpgsqlTransaction?> _handOver =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _intercepted;

        // Completes once the injector has finished with the holder session: with the held transaction, which the
        // caller then owns, or with null when the injection failed and the injector ended any transaction it began.
        // It never completes when no commit reaches the injector.
        public Task<NpgsqlTransaction?> HandedOver => _handOver.Task;

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

            NpgsqlTransaction? held = null;
            try
            {
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

                held = await holder.BeginTransactionAsync(cancellationToken);
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
            }
            catch (Exception injectionFailure)
            {
                // Nothing else touches the holder session before the hand-over, so a held transaction that did not get
                // its lock and its booking is ended here, and none is handed over.
                Exception? discardFailure = held is null ? null : await DiscardAsync(held);
                _handOver.TrySetResult(null);
                if (discardFailure is not null)
                {
                    throw new AggregateException(injectionFailure, discardFailure);
                }

                throw;
            }

            _handOver.TrySetResult(held);
            throw new NpgsqlException(
                "The connection was lost before the commit was acknowledged.",
                new IOException("The connection was reset."));
        }

        // Rolls back and disposes a held transaction the injection could not complete; when the rollback fails,
        // closing the holder session still ends it. Not the creation's token: the transaction must end even while the
        // creation is being cancelled. Returns what failed instead of throwing it over the injection's own failure.
        private async Task<Exception?> DiscardAsync(NpgsqlTransaction held)
        {
            List<Exception> failures = [];
            try
            {
                await held.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackFailure)
            {
                failures.Add(rollbackFailure);
                await CloseHolderAsync(holder, failures);
            }

            try
            {
                await held.DisposeAsync();
            }
            catch (Exception disposeFailure)
            {
                failures.Add(disposeFailure);
            }

            return failures.Count switch
            {
                0 => null,
                1 => failures[0],
                _ => new AggregateException(failures),
            };
        }
    }

    // Rolls back the holder's transaction, which changed nothing, so its lock is released. Not the test's token: the
    // lock must be released even while the test is being cancelled. When the rollback fails, closing the holder's
    // session still ends the transaction. Failures are recorded for the caller, never thrown.
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
            await CloseHolderAsync(holder, failures);
        }
    }

    // Commits the transaction the injector holds back, as a server completing the commit late would, and disposes it.
    // The holder session is touched only once the injector has handed the transaction over, or once the creation has
    // ended without reaching the injector, when nothing is held. That wait is bounded like the drain and not by the
    // test's token, so a cancelled test still releases the held lock. When the commit fails, closing the holder's
    // session still ends the transaction. When neither happens within the bound, the wait is recorded, endSessions
    // attempts to end every session the creation and the injector can be using, and the creation is awaited until it
    // stops; a transaction handed over by then is rolled back and disposed here, so it is never left without an owner
    // and never commits. Returns whether the held commit took effect. endSessions records its failures and never
    // throws. Failures are recorded for the caller, never thrown.
    private static async Task<bool> CommitHeldAsync(
        NpgsqlConnection holder,
        HoldFirstCommit injector,
        Task creation,
        CancellationTokenSource creationCancellation,
        Func<List<Exception>, Task> endSessions,
        List<Exception> failures)
    {
        Task<NpgsqlTransaction?> handOver = injector.HandedOver;
        if (!await WaitWithinDrainLimitAsync(Task.WhenAny(handOver, creation), creationCancellation))
        {
            failures.Add(FailException.ForFailure(string.Create(
                CultureInfo.InvariantCulture,
                $"The injector did not finish with the holder session within {DrainLimit.TotalSeconds} s, although "
                + $"the creation was cancelled after {(DrainLimit - CancellationGrace).TotalSeconds} s; the "
                + $"creation's and the holder's sessions were then ended.")));
            await endSessions(failures);

            // Deliberately not bounded: the creation's token is cancelled and ending its sessions and the holder's has
            // been attempted, but neither guarantees that it stops, and the holder session must not be touched or
            // disposed while it still runs. The injector runs inside the creation's commit and completes the hand-over
            // on every path before it returns, so once the creation has stopped the injector is done with the holder.
            await creation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await DiscardLateHandOverAsync(holder, handOver, failures);
            return false;
        }

        // The creation ends only after its interceptors have returned, so a hand-over is complete by now whenever the
        // injector ran. It never faults, so this does not wait.
        NpgsqlTransaction? held = handOver.IsCompleted ? await handOver : null;
        if (held is null)
        {
            return false;
        }

        bool committed = false;
        try
        {
            await held.CommitAsync(CancellationToken.None);
            committed = true;
        }
        catch (Exception commitFailure)
        {
            failures.Add(commitFailure);
            await CloseHolderAsync(holder, failures);
        }

        try
        {
            await held.DisposeAsync();
        }
        catch (Exception disposeFailure)
        {
            failures.Add(disposeFailure);
        }

        return committed;
    }

    // Rolls back and disposes a transaction the injector handed over after CommitHeldAsync stopped waiting for it, so
    // the held booking never commits. Called only once the creation has stopped, when the hand-over can no longer
    // change. Not the test's token: the transaction must end even while the test is being cancelled. When the rollback
    // fails, closing the holder's session still ends the transaction. Failures are recorded for the caller, never
    // thrown.
    private static async Task DiscardLateHandOverAsync(
        NpgsqlConnection holder,
        Task<NpgsqlTransaction?> handOver,
        List<Exception> failures)
    {
        // The hand-over never faults, so this does not wait or throw.
        NpgsqlTransaction? late = handOver.IsCompleted ? await handOver : null;
        if (late is null)
        {
            return;
        }

        try
        {
            await late.RollbackAsync(CancellationToken.None);
        }
        catch (Exception rollbackFailure)
        {
            failures.Add(rollbackFailure);
            await CloseHolderAsync(holder, failures);
        }

        try
        {
            await late.DisposeAsync();
        }
        catch (Exception disposeFailure)
        {
            failures.Add(disposeFailure);
        }
    }

    // Closes the holder's session once its transaction could not be ended by a rollback or a commit: the server then
    // ends the transaction itself. Failures are recorded for the caller, never thrown.
    private static async Task CloseHolderAsync(NpgsqlConnection holder, List<Exception> failures)
    {
        try
        {
            await holder.CloseAsync();
        }
        catch (Exception closeFailure)
        {
            failures.Add(closeFailure);
        }
    }

    // Waits for the work for at most DrainLimit from here, whatever the test's token says, so a cancelled test still
    // waits for it. Work still running CancellationGrace before the limit is cancelled through its own token source,
    // which leaves it that long to stop without cancelling the test's other work. Returns whether the work stopped,
    // and never throws.
    private static async Task<bool> WaitWithinDrainLimitAsync(Task work, CancellationTokenSource workCancellation)
    {
        workCancellation.CancelAfter(DrainLimit - CancellationGrace);
        await work
            .WaitAsync(DrainLimit, CancellationToken.None)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return work.IsCompleted;
    }

    // Drains one piece of work after its release, waiting as WaitWithinDrainLimitAsync does. Work still running at the
    // limit is recorded, endSessions attempts to end every database session it can be using, and the work is then
    // awaited until it stops, so it returns only once the work has stopped and its outcome has been observed. Returns
    // the work's result, a late one included, which the caller then owns, such as a response to dispose; or null when
    // it failed or was cancelled, which is recorded. endSessions records its failures and never throws. Failures are
    // recorded for the caller, never thrown.
    private static async Task<T?> DrainAsync<T>(
        Task<T> work,
        string workName,
        CancellationTokenSource workCancellation,
        Func<List<Exception>, Task> endSessions,
        List<Exception> failures)
        where T : class
    {
        TimeSpan cancelAfter = DrainLimit - CancellationGrace;
        if (!await WaitWithinDrainLimitAsync(work, workCancellation))
        {
            failures.Add(FailException.ForFailure(string.Create(
                CultureInfo.InvariantCulture,
                $"The {workName} did not stop within {DrainLimit.TotalSeconds} s of the release, although cancelled "
                + $"after {cancelAfter.TotalSeconds} s; its database sessions were then ended.")));
            await endSessions(failures);

            // Deliberately not bounded: the work's token is cancelled and ending its database sessions has been
            // attempted, but neither guarantees that it stops, and no caller may tear down what the work uses while it
            // still runs.
            await ((Task)work).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        try
        {
            // Already completed, so this does not wait.
            return await work;
        }
        catch (Exception exception)
        {
            failures.Add(new AggregateException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The {workName} failed or was cancelled; it is cancelled if still running "
                    + $"{cancelAfter.TotalSeconds} s after the release."),
                exception));
            return null;
        }
    }

    // A drained client task, ended with a response or a cancellation, does not show that the host has finished the
    // request: its handler and its database commands can still be running. Closes the host to new requests, so a
    // request it has not started yet never reaches the handler, then waits, after the drain and whatever the test's
    // token says, until the host serves none. A host still serving after HostStopLimit is recorded, endSessions
    // attempts to end every database session its requests can be using, and the host is then awaited until it serves
    // none, so this returns only once no handler is running or can start. endSessions records its failures and never
    // throws. Never throws.
    private static async Task ObserveHostIdleAsync(
        ServedRequests served,
        Func<List<Exception>, Task> endSessions,
        List<Exception> failures)
    {
        served.Close();
        Task idle = served.WhenIdleAsync();
        await idle
            .WaitAsync(HostStopLimit, CancellationToken.None)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!idle.IsCompleted)
        {
            failures.Add(FailException.ForFailure(string.Create(
                CultureInfo.InvariantCulture,
                $"The host was still serving requests {HostStopLimit.TotalSeconds} s after the booking request's "
                + $"drain ended; their database sessions were then ended.")));
            await endSessions(failures);

            // Deliberately not bounded: the closed host serves only requests it had already started, and ending their
            // database sessions has been attempted, but that does not guarantee that they stop, and the host must not
            // be torn down while one still runs. The wait never faults.
            await idle;
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

    // The first middleware of a host's pipeline, ahead of everything Program adds, counting the requests the host is
    // still serving. A request is served until its handler, and every middleware after this one, has returned, whether
    // or not its client is still waiting. Once the host is closed, a request it has not started is answered with 503 at
    // once, without the rest of the pipeline, so a closed host seen serving none never runs a handler again.
    private sealed class ServedRequests : IStartupFilter
    {
        private readonly object _gate = new();
        private bool _closed;
        private int _inFlight;

        // Completed and replaced, under the gate, each time a request finishes.
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(nextMiddleware => async context =>
                {
                    if (!TryStart())
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        return;
                    }

                    try
                    {
                        await nextMiddleware(context);
                    }
                    finally
                    {
                        Finish();
                    }
                });
                next(app);
            };
        }

        // Stops the host from starting any request from now on; the requests it has started run on.
        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
            }
        }

        // Completes once the host serves no request, which stays so once the host is closed. Never faults.
        public async Task WhenIdleAsync()
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_inFlight == 0)
                    {
                        return;
                    }

                    changed = _changed.Task;
                }

                await changed;
            }
        }

        // Checks for a closed host and counts the request under the same gate as Close, so no request starts once
        // Close has returned.
        private bool TryStart()
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return false;
                }

                _inFlight++;
                return true;
            }
        }

        private void Finish()
        {
            lock (_gate)
            {
                _inFlight--;

                // Its continuations run asynchronously, so none runs under the gate.
                TaskCompletionSource changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.SetResult();
            }
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

    // Adds interceptors to the context options the host builds from the data layer's own registration, keeping its
    // lifetime, so every context the host creates reports to them and its configuration is otherwise unchanged.
    private static void AddContextInterceptors(IServiceCollection services, params IInterceptor[] interceptors)
    {
        ServiceDescriptor[] registrations =
        [
            .. services.Where(static descriptor =>
                descriptor.ServiceType == typeof(DbContextOptions<SmartHotelDbContext>) && !descriptor.IsKeyedService),
        ];
        if (registrations is not [{ ImplementationFactory: { } configure } registration])
        {
            throw new InvalidOperationException(
                "The data layer did not register its context options exactly once, through a factory.");
        }

        services.Remove(registration);
        services.Add(new ServiceDescriptor(
            typeof(DbContextOptions<SmartHotelDbContext>),
            provider => new DbContextOptionsBuilder<SmartHotelDbContext>(
                    (DbContextOptions<SmartHotelDbContext>)configure(provider))
                .AddInterceptors(interceptors)
                .Options,
            registration.Lifetime));
    }

    // Ends the attempt's session once, at the first savepoint release after Arm, as a database restart does. EF Core
    // wraps a save inside a transaction in a savepoint and releases it once the save's commands have run, so the
    // booking is inserted and its id assigned before the release meets the ended session. EF Core does not report
    // that failure; Npgsql breaks the connection and disposes the transaction, so the attempt's commit then throws
    // Npgsql's disposed-transaction exception, with the session's failure as its inner exception.
    private sealed class EndSessionAtFirstRelease : DbTransactionInterceptor
    {
        private string? _connectionString;
        private int _ended;

        public bool Ended => Volatile.Read(ref _ended) == 1;

        // Startup's seed saves inside a transaction of its own, so nothing is ended until the test arms the
        // interceptor. The session is ended through the given connection string, which must bypass the host's pool.
        public void Arm(string connectionString)
        {
            Volatile.Write(ref _connectionString, connectionString);
        }

        public override async ValueTask<InterceptionResult> ReleasingSavepointAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            string? connectionString = Interlocked.Exchange(ref _connectionString, null);
            if (connectionString is null)
            {
                return result;
            }

            int pid = ((NpgsqlConnection)transaction.Connection!).ProcessID;
            await using NpgsqlConnection terminator = new(connectionString);
            await terminator.OpenAsync(cancellationToken);
            await using NpgsqlCommand terminate = new(TerminateSessionSql, terminator);
            terminate.Parameters.AddWithValue("pid", pid);
            if (await terminate.ExecuteScalarAsync(cancellationToken) is true)
            {
                Volatile.Write(ref _ended, 1);
            }

            // Unchanged, so EF Core sends the release on the ended session.
            return result;
        }
    }

    // Counts the commands that fail with an error Npgsql classes as transient, such as a session the database ended
    // (SqlState 57P01) or a dropped connection, so a test can tell that a request met such a failure before the retry
    // that let it succeed. EF Core calls command interceptors whether or not its logging records the failure.
    private sealed class TransientCommandFailureRecorder : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        {
            Record(eventData);
        }

        public override Task CommandFailedAsync(
            DbCommand command,
            CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Record(eventData);
            return Task.CompletedTask;
        }

        private void Record(CommandErrorEventData eventData)
        {
            if (eventData.Exception is NpgsqlException { IsTransient: true })
            {
                Interlocked.Increment(ref _count);
            }
        }
    }
}