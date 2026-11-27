using System.Globalization;
using System.Net;

using Microsoft.EntityFrameworkCore;

using SmartHotel.Api.Common.Pricing;
using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.IntegrationTests.Hotels;

// The tests share this class's database, so each one searches its own stay window: only
// Search_NoAvailability_ReturnsEmptyList writes bookings, and only for a window no other test reads. Only
// Search_CityWithLocaleSensitiveCase_ReturnsStoredHotel adds a hotel, in a city no other test searches.
public sealed class SearchHotelsTests : IClassFixture<SmartHotelApiFactory>
{
    private const string BaselineCity = "Seattle";
    private const string BaselineGuests = "2";

    private const string SeattleDowntown = "SmartHotel Seattle Downtown";
    private const string SeattleWaterfront = "SmartHotel Seattle Waterfront";
    private const string ChicagoLoop = "SmartHotel Chicago Loop";

    // A city outside the seed whose first letter is U+0130, the capital dotted I. PostgreSQL's lower() maps it to "i"
    // under collations such as en_US.utf8, while .NET's invariant casing leaves it unchanged.
    private const string IzmirCity = "\u0130zmir";
    private const string IzmirHotel = "SmartHotel \u0130zmir Konak";

    // The seed gives every hotel 4 room types with 10 rooms each.
    private const int RoomsPerHotel = 40;

    // U+1D538, a letter outside the Basic Multilingual Plane: one character, but two UTF-16 code units.
    private const string OutsideBmpLetter = "\U0001D538";

    private readonly SmartHotelApiFactory _factory;
    private readonly ApiTestClient _client;

    public SearchHotelsTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

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
    [InlineData("city-too-long-outside-bmp", "city")]
    [InlineData("guests-zero", "guests")]
    [InlineData("guests-not-integer", "guests")]
    [InlineData("guests-blank", "guests")]
    [InlineData("guests-white-space", "guests")]
    [InlineData("guests-nul-suffixed", "guests")]
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
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly checkIn = today.AddDays(30);
        DateOnly checkOut = today.AddDays(32);
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
    public async Task Search_GuestsOmitted_SearchesForOneGuest()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly checkIn = today.AddDays(30);
        DateOnly checkOut = today.AddDays(32);
        int downtownId = await _factory.GetHotelIdAsync(SeattleDowntown, cancellationToken);
        int waterfrontId = await _factory.GetHotelIdAsync(SeattleWaterfront, cancellationToken);

        // Only an omitted guests parameter means a party of 1; a supplied blank one is a 400 in the theory above.
        IReadOnlyList<ApiTestClient.HotelSummary> omitted =
            await SearchOkAsync(BaselineCity, checkIn, checkOut, null, cancellationToken);
        IReadOnlyList<ApiTestClient.HotelSummary> forOne =
            await SearchOkAsync(BaselineCity, checkIn, checkOut, 1, cancellationToken);

        AssertSeattleHotels(omitted, downtownId, 129.00m, waterfrontId, 139.00m);
        Assert.Equal(forOne, omitted);
    }

    [Fact]
    public async Task Search_NoAvailability_ReturnsEmptyList()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly soldOutCheckIn = today.AddDays(90);
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
            await SearchOkAsync("Atlantis", today.AddDays(30), today.AddDays(32), 2, cancellationToken);
        Assert.Empty(unknownCity);

        // 100 characters is within the bound even when each one takes two UTF-16 code units, so an unknown city of
        // that length is searched too.
        IReadOnlyList<ApiTestClient.HotelSummary> outsideBmpCity =
            await SearchOkAsync(OutsideBmpCity(100), today.AddDays(30), today.AddDays(32), 2, cancellationToken);
        Assert.Empty(outsideBmpCity);
    }

    [Fact]
    public async Task Search_CityWithLocaleSensitiveCase_ReturnsStoredHotel()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly checkIn = today.AddDays(30);
        DateOnly checkOut = today.AddDays(32);

        // The hotel is built through the same navigations as the seed, so EF Core fills the room's composite foreign
        // key (RoomTypeId, HotelId) from its room type.
        int hotelId = await _factory.ExecuteDbAsync(
            async (db, ct) =>
            {
                RoomType roomType = new()
                {
                    Name = "Standard",
                    MaxGuests = 2,
                    NightlyPrice = 99.00m,
                };
                roomType.Rooms.Add(new Room { RoomNumber = "101" });

                Hotel hotel = new()
                {
                    Name = IzmirHotel,
                    City = IzmirCity,
                    Address = "1 Kordon Boyu, Konak",
                    Description = "A test hotel in a city whose name PostgreSQL and .NET fold to different text.",
                };
                hotel.RoomTypes.Add(roomType);
                db.Hotels.Add(hotel);
                await db.SaveChangesAsync(ct);

                return hotel.Id;
            },
            cancellationToken);

        // The stored text itself finds the hotel, and the stored spelling comes back. A search that folded only its
        // input in .NET would keep U+0130 while a database whose collation lowers U+0130 to "i", en_US.utf8 among them,
        // folds the stored city to "izmir", and would find nothing.
        IReadOnlyList<ApiTestClient.HotelSummary> identical =
            await SearchOkAsync(IzmirCity, checkIn, checkOut, 2, cancellationToken);
        ApiTestClient.HotelSummary found = Assert.Single(identical);
        Assert.Equal(hotelId, found.Id);
        Assert.Equal(IzmirHotel, found.Name);
        Assert.Equal(IzmirCity, found.City);
        Assert.Equal(99.00m, found.LowestNightlyPrice);

        // The other-case spelling upper-cases only z, m and r, which every collation lowers alike, and keeps U+0130 and
        // the small i as stored, because lower() folds U+0130 and "I" differently from one locale to another: C leaves
        // U+0130 unchanged, ICU's en-US rules give "i" followed by U+0307, and Turkish rules lower "I" to the dotless
        // U+0131. Upper-casing the i, to U+0130 or to "I", would make the match depend on the test server's locale;
        // this spelling folds to the stored city's lower() under each of them.
        IReadOnlyList<ApiTestClient.HotelSummary> otherCase =
            await SearchOkAsync("\u0130ZMiR", checkIn, checkOut, 2, cancellationToken);
        Assert.Equal(identical, otherCase);
    }

    // DateOnly values cannot be attribute arguments, so each theory row names its case and the query is built here.
    // Every case breaks exactly one input and keeps the others at the valid baseline, so it fails only for its stated
    // reason. The format cases pair their broken check-in with Y-01-07, the check-out of the valid stay Y-01-05 to
    // Y-01-07 in the following year Y. Every date and the year come from one read of today, so a run that crosses
    // midnight UTC cannot build a stay of another length or another year.
    private static (string? City, string? CheckIn, string? CheckOut, string? Guests) InvalidQuery(string caseLabel)
    {
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly baselineCheckIn = today.AddDays(30);
        string checkIn = ApiTestClient.Iso(baselineCheckIn);
        string checkOut = ApiTestClient.Iso(today.AddDays(32));
        int year = ApiTestClient.NextYear(today);
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
            "city-too-long-outside-bmp" => (OutsideBmpCity(101), checkIn, checkOut, BaselineGuests),
            "guests-zero" => (BaselineCity, checkIn, checkOut, "0"),
            "guests-not-integer" => (BaselineCity, checkIn, checkOut, "two"),

            // Supplied but blank, guests is not an integer; only an omitted guests defaults to 1.
            "guests-blank" => (BaselineCity, checkIn, checkOut, string.Empty),
            "guests-white-space" => (BaselineCity, checkIn, checkOut, "  "),

            // Sent as 2%00. .NET's integer parsing ignores trailing NULs, but guests allows only a sign and digits.
            "guests-nul-suffixed" => (BaselineCity, checkIn, checkOut, "2\0"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(caseLabel),
                caseLabel,
                "No query is defined for this case."),
        };
    }

    // A city of the given number of characters, each outside the Basic Multilingual Plane, so its UTF-16 length is
    // twice that number. It matches no hotel.
    private static string OutsideBmpCity(int characters)
    {
        return string.Concat(Enumerable.Repeat(OutsideBmpLetter, characters));
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

    // Sends a valid search, asserts 200 and returns the parsed list; the response is disposed before returning. A null
    // guests omits the parameter.
    private async Task<IReadOnlyList<ApiTestClient.HotelSummary>> SearchOkAsync(
        string city,
        DateOnly checkIn,
        DateOnly checkOut,
        int? guests,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.SearchAsync(
            city,
            ApiTestClient.Iso(checkIn),
            ApiTestClient.Iso(checkOut),
            guests?.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await _client.ReadAsync<IReadOnlyList<ApiTestClient.HotelSummary>>(response, cancellationToken);
    }
}