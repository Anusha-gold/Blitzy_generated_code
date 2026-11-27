using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using SmartHotel.Api.Common.Pricing;
using SmartHotel.Api.IntegrationTests.Infrastructure;
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

    private readonly SmartHotelApiFactory _factory;
    private readonly ApiTestClient _client;

    public CreateBookingTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
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

        using HttpResponseMessage response = await _client.CreateBookingAsync(body, session.Token, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string payload = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;

        string[] responseMembers =
        [
            "checkIn", "checkOut", "createdAt", "guests", "hotelId", "hotelName", "id", "nights", "roomNumber",
            "roomTypeId", "roomTypeName", "status", "totalPrice",
        ];
        Assert.Equal(
            responseMembers,
            root.EnumerateObject().Select(static member => member.Name).Order(StringComparer.Ordinal));

        string? createdAt = root.GetProperty("createdAt").GetString();
        Assert.NotNull(createdAt);
        Assert.EndsWith("Z", createdAt, StringComparison.Ordinal);

        ApiTestClient.Booking? booking = root.Deserialize<ApiTestClient.Booking>(ApiTestClient.JsonOptions);
        Assert.NotNull(booking);

        Assert.Equal(PriceCalculator.Total(suite.NightlyPrice, 3), booking.TotalPrice);
        Assert.Equal(1107.00m, booking.TotalPrice);
        Assert.Equal("Confirmed", booking.Status);
        Assert.Equal(suite.HotelId, booking.HotelId);
        Assert.Equal(ChicagoLoop, booking.HotelName);
        Assert.Equal(suite.Id, booking.RoomTypeId);
        Assert.Equal("Suite", booking.RoomTypeName);
        Assert.Equal("401", booking.RoomNumber);
        Assert.Equal(checkIn, booking.CheckIn);
        Assert.Equal(checkOut, booking.CheckOut);
        Assert.Equal(3, booking.Nights);
        Assert.Equal(2, booking.Guests);
        Assert.True(booking.Id > 0, "Expected the booking to have a positive id.");

        Assert.Equal(DateTimeKind.Utc, booking.CreatedAt.Kind);
        DateTime now = TimeProvider.System.GetUtcNow().UtcDateTime;
        Assert.InRange(booking.CreatedAt, now.AddMinutes(-5), now.AddMinutes(5));

        // The detail's stay total and the booked total both come from PriceCalculator, so they must agree.
        ApiTestClient.HotelDetail detail =
            await _client.GetHotelDetailAsync(suite.HotelId, checkIn, checkOut, guests: 2, ct);
        ApiTestClient.RoomTypeAvailability availability =
            Assert.Single(detail.RoomTypes, roomType => roomType.Id == suite.Id);
        Assert.Equal(booking.TotalPrice, availability.StayTotal);
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
}