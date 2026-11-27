using System.Globalization;
using System.Net;

using Microsoft.EntityFrameworkCore;

using SmartHotel.Api.Common.Pricing;
using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.IntegrationTests.Hotels;

// The tests share this class's database, so each one searches its own stay window: only
// Search_NoAvailability_ReturnsEmptyList writes bookings, and only for a window no other test reads.
public sealed class SearchHotelsTests : IClassFixture<SmartHotelApiFactory>
{
    private const string BaselineCity = "Seattle";
    private const string BaselineGuests = "2";

    private const string SeattleDowntown = "SmartHotel Seattle Downtown";
    private const string SeattleWaterfront = "SmartHotel Seattle Waterfront";
    private const string ChicagoLoop = "SmartHotel Chicago Loop";

    // The seed gives every hotel 4 room types with 10 rooms each.
    private const int RoomsPerHotel = 40;

    private readonly SmartHotelApiFactory _factory;
    private readonly ApiTestClient _client;

    public SearchHotelsTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    // The valid baseline stay, today+30 to today+32. Computed on every read, like ApiTestClient.TodayUtc, so a run
    // that crosses midnight UTC still agrees with the server's "today".
    private static DateOnly CheckIn => ApiTestClient.TodayUtc.AddDays(30);

    private static DateOnly CheckOut => ApiTestClient.TodayUtc.AddDays(32);

    [Theory]
    [InlineData("checkIn-missing", "checkIn")]
    [InlineData("checkOut-missing", "checkOut")]
    [InlineData("checkIn-invalid-date", "checkIn")]
    [InlineData("checkIn-not-padded", "checkIn")]
    [InlineData("checkIn-us-format", "checkIn")]
    [InlineData("checkOut-equals-checkIn", "checkOut")]
    [InlineData("checkOut-before-checkIn", "checkOut")]
    [InlineData("checkIn-yesterday", "checkIn")]
    [InlineData("stay-31-nights", "checkOut")]
    [InlineData("city-missing", "city")]
    [InlineData("city-blank", "city")]
    [InlineData("city-too-long", "city")]
    [InlineData("guests-zero", "guests")]
    [InlineData("guests-not-integer", "guests")]
    public async Task Search_InvalidInput_Returns400(string caseLabel, string expectedKey)
    {
        (string? city, string? checkIn, string? checkOut, string? guests) = InvalidQuery(caseLabel);

        using HttpResponseMessage response = await _client.SearchAsync(
            city,
            checkIn,
            checkOut,
            guests,
            TestContext.Current.CancellationToken);

        await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            expectedKey,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Search_SeededCity_ReturnsHotelsWithLowestPrice()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly checkIn = CheckIn;
        DateOnly checkOut = CheckOut;
        int downtownId = await _factory.GetHotelIdAsync(SeattleDowntown, cancellationToken);
        int waterfrontId = await _factory.GetHotelIdAsync(SeattleWaterfront, cancellationToken);

        // Standard, for 2 guests, is the cheapest type everywhere: 129.00 in hotel 1 and 10.00 more in hotel 2.
        IReadOnlyList<ApiTestClient.HotelSummary> forTwo =
            await SearchOkAsync(BaselineCity, checkIn, checkOut, 2, cancellationToken);
        AssertSeattleHotels(forTwo, downtownId, 129.00m, waterfrontId, 139.00m);

        // Standard and Deluxe host only 2 guests, so a party of 3 is priced from Family, the cheapest type that fits.
        IReadOnlyList<ApiTestClient.HotelSummary> forThree =
            await SearchOkAsync(BaselineCity, checkIn, checkOut, 3, cancellationToken);
        AssertSeattleHotels(forThree, downtownId, 219.00m, waterfrontId, 229.00m);

        // The city matches whatever its letter case, and the stored spelling comes back.
        IReadOnlyList<ApiTestClient.HotelSummary> lowerCase =
            await SearchOkAsync("seattle", checkIn, checkOut, 2, cancellationToken);
        AssertSeattleHotels(lowerCase, downtownId, 129.00m, waterfrontId, 139.00m);
        Assert.Equal(forTwo, lowerCase);
    }

    [Fact]
    public async Task Search_NoAvailability_ReturnsEmptyList()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly soldOutCheckIn = ApiTestClient.TodayUtc.AddDays(90);
        DateOnly soldOutCheckOut = soldOutCheckIn.AddDays(1);
        int hotelId = await _factory.GetHotelIdAsync(ChicagoLoop, cancellationToken);

        // Chicago Loop is the city's only hotel. Listing it before any booking exists shows that the empty list below
        // comes from the bookings and not from the query.
        IReadOnlyList<ApiTestClient.HotelSummary> beforeBookings =
            await SearchOkAsync("Chicago", soldOutCheckIn, soldOutCheckOut, 1, cancellationToken);
        Assert.Equal(hotelId, Assert.Single(beforeBookings).Id);

        await _factory.ExecuteDbAsync(
            async (db, ct) =>
            {
                // The email is already in the stored form CK_Guests_Email_Normalized requires: lower-case ASCII.
                Guest guest = new()
                {
                    Email = "search+" + Guid.NewGuid().ToString("N") + "@example.test",
                    PasswordHash = "not-a-real-hash",
                    FullName = "Test Guest",
                    CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime,
                };
                db.Guests.Add(guest);
                await db.SaveChangesAsync(ct);

                var rooms = await db.Rooms
                    .AsNoTracking()
                    .Where(room => room.HotelId == hotelId)
                    .Select(room => new { room.Id, room.RoomType.NightlyPrice })
                    .ToListAsync(ct);
                Assert.Equal(RoomsPerHotel, rooms.Count);

                DateTime createdAt = TimeProvider.System.GetUtcNow().UtcDateTime;
                db.Bookings.AddRange(rooms.Select(room => new Booking
                {
                    GuestId = guest.Id,
                    RoomId = room.Id,
                    CheckIn = soldOutCheckIn,
                    CheckOut = soldOutCheckOut,
                    GuestCount = 1,
                    TotalPrice = PriceCalculator.Total(room.NightlyPrice, 1),
                    Status = BookingStatus.Confirmed,
                    CreatedAt = createdAt,
                }));
                await db.SaveChangesAsync(ct);
            },
            cancellationToken);

        // Every room is taken for the night, so no room type qualifies even for a single guest.
        IReadOnlyList<ApiTestClient.HotelSummary> soldOut =
            await SearchOkAsync("Chicago", soldOutCheckIn, soldOutCheckOut, 1, cancellationToken);
        Assert.Empty(soldOut);

        // The check-out day is not occupied, so a stay that starts on it finds the hotel again.
        IReadOnlyList<ApiTestClient.HotelSummary> backToBack =
            await SearchOkAsync("Chicago", soldOutCheckOut, soldOutCheckOut.AddDays(1), 1, cancellationToken);
        Assert.Equal(hotelId, Assert.Single(backToBack).Id);

        IReadOnlyList<ApiTestClient.HotelSummary> unknownCity =
            await SearchOkAsync("Atlantis", CheckIn, CheckOut, 2, cancellationToken);
        Assert.Empty(unknownCity);
    }

    // DateOnly values cannot be attribute arguments, so each theory row names its case and the query is built here.
    // Every case breaks exactly one input and keeps the others at the valid baseline, so it fails only for its stated
    // reason. The format cases pair their broken check-in with Y-01-07, the check-out of the valid stay Y-01-05 to
    // Y-01-07 in the following year Y.
    private static (string? City, string? CheckIn, string? CheckOut, string? Guests) InvalidQuery(string caseLabel)
    {
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly baselineCheckIn = CheckIn;
        string checkIn = ApiTestClient.Iso(baselineCheckIn);
        string checkOut = ApiTestClient.Iso(CheckOut);
        int year = ApiTestClient.NextYear;
        string formatCaseCheckOut = string.Create(CultureInfo.InvariantCulture, $"{year}-01-07");

        return caseLabel switch
        {
            "checkIn-missing" => (BaselineCity, null, checkOut, BaselineGuests),
            "checkOut-missing" => (BaselineCity, checkIn, null, BaselineGuests),
            "checkIn-invalid-date" => (
                BaselineCity,
                string.Create(CultureInfo.InvariantCulture, $"{year}-13-40"),
                formatCaseCheckOut,
                BaselineGuests),
            "checkIn-not-padded" => (
                BaselineCity,
                string.Create(CultureInfo.InvariantCulture, $"{year}-1-5"),
                formatCaseCheckOut,
                BaselineGuests),
            "checkIn-us-format" => (
                BaselineCity,
                string.Create(CultureInfo.InvariantCulture, $"01/05/{year}"),
                formatCaseCheckOut,
                BaselineGuests),
            "checkOut-equals-checkIn" => (BaselineCity, checkIn, checkIn, BaselineGuests),
            "checkOut-before-checkIn" => (BaselineCity, checkOut, checkIn, BaselineGuests),

            // Yesterday to tomorrow is a valid 2-night length, so only the past check-in is wrong.
            "checkIn-yesterday" => (
                BaselineCity,
                ApiTestClient.Iso(today.AddDays(-1)),
                ApiTestClient.Iso(today.AddDays(1)),
                BaselineGuests),
            "stay-31-nights" => (
                BaselineCity,
                checkIn,
                ApiTestClient.Iso(baselineCheckIn.AddDays(31)),
                BaselineGuests),
            "city-missing" => (null, checkIn, checkOut, BaselineGuests),
            "city-blank" => ("   ", checkIn, checkOut, BaselineGuests),
            "city-too-long" => (new string('a', 101), checkIn, checkOut, BaselineGuests),
            "guests-zero" => (BaselineCity, checkIn, checkOut, "0"),
            "guests-not-integer" => (BaselineCity, checkIn, checkOut, "two"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(caseLabel),
                caseLabel,
                "No query is defined for this case."),
        };
    }

    private static void AssertSeattleHotels(
        IReadOnlyList<ApiTestClient.HotelSummary> hotels,
        int downtownId,
        decimal downtownPrice,
        int waterfrontId,
        decimal waterfrontPrice)
    {
        Assert.Collection(
            hotels,
            hotel => AssertHotel(hotel, downtownId, SeattleDowntown, downtownPrice),
            hotel => AssertHotel(hotel, waterfrontId, SeattleWaterfront, waterfrontPrice));

        static void AssertHotel(ApiTestClient.HotelSummary hotel, int id, string name, decimal lowestNightlyPrice)
        {
            Assert.Equal(id, hotel.Id);
            Assert.Equal(name, hotel.Name);
            Assert.Equal(BaselineCity, hotel.City);
            Assert.False(string.IsNullOrWhiteSpace(hotel.Address));

            // Decimal equality ignores scale, so 129.0 and 129.00 compare equal.
            Assert.Equal(lowestNightlyPrice, hotel.LowestNightlyPrice);
        }
    }

    // Sends a valid search, asserts 200 and returns the parsed list; the response is disposed before returning.
    private async Task<IReadOnlyList<ApiTestClient.HotelSummary>> SearchOkAsync(
        string city,
        DateOnly checkIn,
        DateOnly checkOut,
        int guests,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.SearchAsync(
            city,
            ApiTestClient.Iso(checkIn),
            ApiTestClient.Iso(checkOut),
            guests.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await _client.ReadAsync<IReadOnlyList<ApiTestClient.HotelSummary>>(response, cancellationToken);
    }
}