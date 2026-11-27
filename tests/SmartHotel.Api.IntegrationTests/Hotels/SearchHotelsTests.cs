using System.Globalization;
using System.Net;

using Microsoft.EntityFrameworkCore;

using SmartHotel.Api.Common.Pricing;
using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.IntegrationTests.Hotels;

// The tests share this class's database, so each one writes only where no other test searches, and the seeded Seattle
// hotels are only read:
// - Search_NoAvailability_ReturnsEmptyList books every room of the seeded Chicago Loop hotel for one night, 90 days
//   ahead; no other test searches Chicago or that night.
// - Search_CityWithLocaleSensitiveCase_ReturnsStoredHotel adds a hotel in IzmirCity.
// - Search_GuestsOmitted_SearchesForOneGuest adds two hotels in DefaultGuestsCity.
// - Search_CheapestEligibleTypeSoldOut_PricesFromFreeTypesAndOrdersByPriceThenName adds four hotels in PriceOrderCity
//   and books every room of one of their room types.
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

    // Cities outside the seed, each used by one test only, so the hotels and bookings a test adds there are invisible
    // to every other search.
    private const string PriceOrderCity = "Price Order Test City";
    private const string DefaultGuestsCity = "Default Guests Test City";

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
        string downtownAddress = await GetStoredAddressAsync(downtownId, cancellationToken);
        string waterfrontAddress = await GetStoredAddressAsync(waterfrontId, cancellationToken);

        // Standard, for 2 guests, is the cheapest type everywhere: 129.00 in hotel 1 and 10.00 more in hotel 2.
        IReadOnlyList<ApiTestClient.HotelSummary> forTwo =
            await SearchOkAsync(BaselineCity, checkIn, checkOut, 2, cancellationToken);
        AssertSeattleHotels(forTwo, downtownId, downtownAddress, 129.00m, waterfrontId, waterfrontAddress, 139.00m);

        // Standard and Deluxe host only 2 guests, so a party of 3 is priced from Family, the cheapest type that fits.
        IReadOnlyList<ApiTestClient.HotelSummary> forThree =
            await SearchOkAsync(BaselineCity, checkIn, checkOut, 3, cancellationToken);
        AssertSeattleHotels(forThree, downtownId, downtownAddress, 219.00m, waterfrontId, waterfrontAddress, 229.00m);

        // The city matches whatever its letter case, and the stored spelling comes back.
        IReadOnlyList<ApiTestClient.HotelSummary> lowerCase =
            await SearchOkAsync("seattle", checkIn, checkOut, 2, cancellationToken);
        AssertSeattleHotels(lowerCase, downtownId, downtownAddress, 129.00m, waterfrontId, waterfrontAddress, 139.00m);
        Assert.True(
            forTwo.SequenceEqual(lowerCase),
            "Expected the lower-case search to list the same hotels, in the same order, as the stored-case search.");
    }

    [Fact]
    public async Task Search_GuestsOmitted_SearchesForOneGuest()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly checkIn = today.AddDays(30);
        DateOnly checkOut = today.AddDays(32);

        // Every seeded room type hosts at least 2 guests, so these hotels add room types for 1 guest: a party of 1 and
        // a party of 2 get different results here, and the default decides which one an omitted guests gets.
        Hotel solo = await AddHotelAsync(
            "Solo Inn",
            DefaultGuestsCity,
            [new("Single", 1, 90.00m, 1)],
            cancellationToken);
        Hotel duo = await AddHotelAsync(
            "Duo Inn",
            DefaultGuestsCity,
            [new("Single", 1, 80.00m, 1), new("Double", 2, 120.00m, 1)],
            cancellationToken);

        // A party of 1 fits both Singles. A party of 2 fits only Duo's Double, so Solo is not listed.
        IReadOnlyList<ApiTestClient.HotelSummary> forOne =
            await SearchOkAsync(DefaultGuestsCity, checkIn, checkOut, 1, cancellationToken);
        ApiTestClient.HotelSummary[] expectedForOne = [Summary(duo, 80.00m), Summary(solo, 90.00m)];
        AssertSummaries("search for 1 guest", expectedForOne, forOne);

        IReadOnlyList<ApiTestClient.HotelSummary> forTwo =
            await SearchOkAsync(DefaultGuestsCity, checkIn, checkOut, 2, cancellationToken);
        ApiTestClient.HotelSummary[] expectedForTwo = [Summary(duo, 120.00m)];
        AssertSummaries("search for 2 guests", expectedForTwo, forTwo);

        // Only an omitted guests parameter means a party of 1; a supplied blank one is a 400 in the theory above.
        IReadOnlyList<ApiTestClient.HotelSummary> omitted =
            await SearchOkAsync(DefaultGuestsCity, checkIn, checkOut, null, cancellationToken);
        AssertSummaries("search with guests omitted", expectedForOne, omitted);
        Assert.False(
            forTwo.SequenceEqual(omitted),
            "Expected the search with guests omitted to differ from the search for 2 guests.");
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
        AssertOnlyHotel("search before the bookings", beforeBookings, hotelId, ChicagoLoop);

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
        Assert.True(soldOut.Count == 0, "Expected the search for the sold-out night to list no hotel.");

        // The check-out day is not occupied, so a stay that starts on it finds the hotel again.
        IReadOnlyList<ApiTestClient.HotelSummary> backToBack =
            await SearchOkAsync("Chicago", soldOutCheckOut, soldOutCheckOut.AddDays(1), 1, cancellationToken);
        AssertOnlyHotel("search from the check-out day", backToBack, hotelId, ChicagoLoop);

        IReadOnlyList<ApiTestClient.HotelSummary> unknownCity =
            await SearchOkAsync("Atlantis", today.AddDays(30), today.AddDays(32), 2, cancellationToken);
        Assert.True(unknownCity.Count == 0, "Expected the search for an unknown city to list no hotel.");

        // 100 characters is within the bound even when each one takes two UTF-16 code units, so an unknown city of
        // that length is searched too.
        IReadOnlyList<ApiTestClient.HotelSummary> outsideBmpCity =
            await SearchOkAsync(OutsideBmpCity(100), today.AddDays(30), today.AddDays(32), 2, cancellationToken);
        Assert.True(
            outsideBmpCity.Count == 0,
            "Expected the search for an unknown 100-character city outside the BMP to list no hotel.");
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
        AssertOnlyHotel("search for the stored city", identical, hotelId, IzmirHotel);
        ApiTestClient.HotelSummary found = identical[0];
        Assert.True(
            string.Equals(IzmirHotel, found.Name, StringComparison.Ordinal),
            $"Expected the search for the stored city to return the stored name {IzmirHotel}.");
        Assert.True(
            string.Equals(IzmirCity, found.City, StringComparison.Ordinal),
            $"Expected the search for the stored city to return the stored city {IzmirCity}.");
        Assert.True(
            found.LowestNightlyPrice == 99.00m,
            $"Expected the search for the stored city to price {IzmirHotel} from 99.00.");

        // The other-case spelling upper-cases only z, m and r, which every collation lowers alike, and keeps U+0130 and
        // the small i as stored, because lower() folds U+0130 and "I" differently from one locale to another: C leaves
        // U+0130 unchanged, ICU's en-US rules give "i" followed by U+0307, and Turkish rules lower "I" to the dotless
        // U+0131. Upper-casing the i, to U+0130 or to "I", would make the match depend on the test server's locale;
        // this spelling folds to the stored city's lower() under each of them.
        IReadOnlyList<ApiTestClient.HotelSummary> otherCase =
            await SearchOkAsync("\u0130ZMiR", checkIn, checkOut, 2, cancellationToken);
        Assert.True(
            identical.SequenceEqual(otherCase),
            "Expected the other-case search to list the same hotels, in the same order, as the stored-city search.");
    }

    [Fact]
    public async Task Search_CheapestEligibleTypeSoldOut_PricesFromFreeTypesAndOrdersByPriceThenName()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly checkIn = today.AddDays(30);
        DateOnly checkOut = today.AddDays(32);

        // The hotels are added one at a time, so their ids ascend in this order, which is neither their name order nor
        // their price order. Zephyr and Maple share a price, and their name order is the reverse of their id order.
        Hotel zephyr = await AddHotelAsync(
            "Zephyr Lodge",
            PriceOrderCity,
            [new("Double", 2, 150.00m, 1), new("Suite", 4, 250.00m, 1)],
            cancellationToken);
        Hotel maple = await AddHotelAsync(
            "Maple Lodge",
            PriceOrderCity,
            [new("Double", 2, 150.00m, 1)],
            cancellationToken);
        Hotel alder = await AddHotelAsync(
            "Alder Lodge",
            PriceOrderCity,
            [new("Single", 1, 60.00m, 1), new("Double", 2, 100.00m, 2), new("Family", 4, 200.00m, 1)],
            cancellationToken);
        Hotel birch = await AddHotelAsync(
            "Birch Lodge",
            PriceOrderCity,
            [new("Double", 2, 180.00m, 1)],
            cancellationToken);
        int[] insertionOrder = [zephyr.Id, maple.Id, alder.Id, birch.Id];
        Assert.Equal(insertionOrder.Order(), insertionOrder);

        // Alder's Single is its cheapest type and is free, but it hosts 1 guest, so for a party of 2 Alder is priced
        // from its Double, the lowest price in the city. Maple comes before Zephyr at the same price on name alone.
        IReadOnlyList<ApiTestClient.HotelSummary> beforeBooking =
            await SearchOkAsync(PriceOrderCity, checkIn, checkOut, 2, cancellationToken);
        ApiTestClient.HotelSummary[] expectedBeforeBooking =
            [Summary(alder, 100.00m), Summary(maple, 150.00m), Summary(zephyr, 150.00m), Summary(birch, 180.00m)];
        AssertSummaries("search before the booking", expectedBeforeBooking, beforeBooking);

        // Every room of Alder's Double is taken for the stay, while its Family stays free.
        RoomType alderDouble = Assert.Single(alder.RoomTypes, static roomType => roomType.Name == "Double");
        await BookEveryRoomAsync(alderDouble, checkIn, checkOut, 2, cancellationToken);

        // Alder is still listed, priced from Family, its cheapest type that fits the party and has a free room, and
        // that price moves it from first to last.
        IReadOnlyList<ApiTestClient.HotelSummary> afterBooking =
            await SearchOkAsync(PriceOrderCity, checkIn, checkOut, 2, cancellationToken);
        ApiTestClient.HotelSummary[] expectedAfterBooking =
            [Summary(maple, 150.00m), Summary(zephyr, 150.00m), Summary(birch, 180.00m), Summary(alder, 200.00m)];
        AssertSummaries("search after the booking", expectedAfterBooking, afterBooking);

        // A party of 1 fits Alder's free Single, which then sets Alder's price: only its capacity kept it out above.
        IReadOnlyList<ApiTestClient.HotelSummary> forOne =
            await SearchOkAsync(PriceOrderCity, checkIn, checkOut, 1, cancellationToken);
        ApiTestClient.HotelSummary[] expectedForOne =
            [Summary(alder, 60.00m), Summary(maple, 150.00m), Summary(zephyr, 150.00m), Summary(birch, 180.00m)];
        AssertSummaries("search for 1 guest", expectedForOne, forOne);
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
        string downtownAddress,
        decimal downtownPrice,
        int waterfrontId,
        string waterfrontAddress,
        decimal waterfrontPrice)
    {
        // Each element is checked directly rather than through Assert.Collection, whose failure message prints every
        // element, the response's addresses included. Every message names only the expected hotel, never a value the
        // response carried.
        Assert.True(
            hotels.Count == 2,
            $"Expected the {BaselineCity} search to list exactly {SeattleDowntown} and {SeattleWaterfront}.");
        AssertHotel(hotels[0], downtownId, SeattleDowntown, downtownAddress, downtownPrice);
        AssertHotel(hotels[1], waterfrontId, SeattleWaterfront, waterfrontAddress, waterfrontPrice);

        static void AssertHotel(
            ApiTestClient.HotelSummary hotel,
            int id,
            string name,
            string storedAddress,
            decimal lowestNightlyPrice)
        {
            Assert.True(hotel.Id == id, $"Expected the {name} search result to carry that hotel's id.");
            Assert.True(
                string.Equals(name, hotel.Name, StringComparison.Ordinal),
                $"Expected the {name} search result to carry that hotel's name.");
            Assert.True(
                string.Equals(BaselineCity, hotel.City, StringComparison.Ordinal),
                $"Expected the {name} search result to carry the city {BaselineCity}.");

            // The address is the text the database stores for this hotel, read by its id before the searches, so
            // another hotel's address or any other text fails. The message names only the expected hotel, never the
            // stored text or the text the response carried.
            Assert.True(
                string.Equals(storedAddress, hotel.Address, StringComparison.Ordinal),
                $"Expected the {name} search result to carry that hotel's stored address.");

            // Decimal equality ignores scale; ApiTestClient.ReadAsync has already required exactly two decimals.
            Assert.True(
                hotel.LowestNightlyPrice == lowestNightlyPrice,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Expected the {name} search result to be priced from {lowestNightlyPrice}."));
        }
    }

    // Checks that a search listed exactly the expected hotels in the expected order, comparing each one's fields as
    // record equality does: the id, the text by ordinal comparison and the price by value. Assert.Equal would print
    // both sequences on a mismatch, the response's addresses included, so every message names only the search, the
    // index and the expected hotel, never a value the response carried.
    private static void AssertSummaries(
        string search,
        IReadOnlyList<ApiTestClient.HotelSummary> expected,
        IReadOnlyList<ApiTestClient.HotelSummary> actual)
    {
        string expectedNames = string.Join(", ", expected.Select(static hotel => hotel.Name));
        Assert.True(actual.Count == expected.Count, $"Expected the {search} to list exactly: {expectedNames}.");
        for (int index = 0; index < expected.Count; index++)
        {
            ApiTestClient.HotelSummary want = expected[index];
            ApiTestClient.HotelSummary hotel = actual[index];
            string position = string.Create(CultureInfo.InvariantCulture, $"index {index} of the {search}");
            Assert.True(hotel.Id == want.Id, $"Expected {want.Name} at {position}.");
            Assert.True(
                string.Equals(want.Name, hotel.Name, StringComparison.Ordinal),
                $"Expected the hotel at {position} to carry the name {want.Name}.");
            Assert.True(
                string.Equals(want.City, hotel.City, StringComparison.Ordinal),
                $"Expected {want.Name} at {position} to carry the city {want.City}.");
            Assert.True(
                string.Equals(want.Address, hotel.Address, StringComparison.Ordinal),
                $"Expected {want.Name} at {position} to carry that hotel's stored address.");
            Assert.True(
                hotel.LowestNightlyPrice == want.LowestNightlyPrice,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Expected {want.Name} at {position} to be priced from {want.LowestNightlyPrice}."));
        }
    }

    // Checks that a search listed one hotel only, the expected one. Assert.Single would print every listed hotel on a
    // mismatch, so the messages name only the search and the expected hotel.
    private static void AssertOnlyHotel(
        string search,
        IReadOnlyList<ApiTestClient.HotelSummary> hotels,
        int hotelId,
        string hotelName)
    {
        Assert.True(hotels.Count == 1, $"Expected the {search} to list {hotelName} only.");
        Assert.True(hotels[0].Id == hotelId, $"Expected the {search} to list {hotelName}.");
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

    // Reads a hotel's address as the database stores it, the text its search result must carry.
    private async Task<string> GetStoredAddressAsync(int hotelId, CancellationToken cancellationToken)
    {
        return await _factory.ExecuteDbAsync(
            (db, token) => db.Hotels
                .AsNoTracking()
                .Where(hotel => hotel.Id == hotelId)
                .Select(static hotel => hotel.Address)
                .SingleAsync(token),
            cancellationToken);
    }

    // The search result expected for a hotel this class added, with the stored name, city and address.
    private static ApiTestClient.HotelSummary Summary(Hotel hotel, decimal lowestNightlyPrice)
    {
        return new ApiTestClient.HotelSummary(hotel.Id, hotel.Name, hotel.City, hotel.Address, lowestNightlyPrice);
    }

    // Adds a hotel through the same navigations as the seed, so EF Core fills each room's composite foreign key
    // (RoomTypeId, HotelId) from its room type. The rooms of the n-th room type are numbered n01, n02 and so on, unique
    // within the hotel. The hotel is saved on its own, so hotels added one after another get ascending ids. The
    // returned hotel carries the generated ids of its room types and rooms.
    private async Task<Hotel> AddHotelAsync(
        string name,
        string city,
        IReadOnlyList<FixtureRoomType> roomTypes,
        CancellationToken cancellationToken)
    {
        return await _factory.ExecuteDbAsync(
            async (db, ct) =>
            {
                Hotel hotel = new()
                {
                    Name = name,
                    City = city,
                    Address = name + ", 1 Fixture Street",
                    Description = "A test hotel added by the search tests.",
                };
                for (int floor = 1; floor <= roomTypes.Count; floor++)
                {
                    FixtureRoomType fixture = roomTypes[floor - 1];
                    RoomType roomType = new()
                    {
                        Name = fixture.Name,
                        MaxGuests = fixture.MaxGuests,
                        NightlyPrice = fixture.NightlyPrice,
                    };
                    for (int number = 1; number <= fixture.Rooms; number++)
                    {
                        roomType.Rooms.Add(new Room
                        {
                            RoomNumber = string.Create(CultureInfo.InvariantCulture, $"{floor}{number:00}"),
                        });
                    }

                    hotel.RoomTypes.Add(roomType);
                }

                db.Hotels.Add(hotel);
                await db.SaveChangesAsync(ct);

                return hotel;
            },
            cancellationToken);
    }

    // Takes every room of the room type for the stay with Confirmed bookings inserted directly, each priced by
    // PriceCalculator as the API would price it, so no room of the type is free for any stay that overlaps it.
    private async Task BookEveryRoomAsync(
        RoomType roomType,
        DateOnly checkIn,
        DateOnly checkOut,
        int guests,
        CancellationToken cancellationToken)
    {
        int roomTypeId = roomType.Id;
        int expectedRooms = roomType.Rooms.Count;
        decimal total = PriceCalculator.Total(roomType.NightlyPrice, checkOut.DayNumber - checkIn.DayNumber);

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

                List<int> roomIds = await db.Rooms
                    .AsNoTracking()
                    .Where(room => room.RoomTypeId == roomTypeId)
                    .Select(room => room.Id)
                    .ToListAsync(ct);
                Assert.Equal(expectedRooms, roomIds.Count);

                DateTime createdAt = TimeProvider.System.GetUtcNow().UtcDateTime;
                db.Bookings.AddRange(roomIds.Select(roomId => new Booking
                {
                    GuestId = guest.Id,
                    RoomId = roomId,
                    CheckIn = checkIn,
                    CheckOut = checkOut,
                    GuestCount = guests,
                    TotalPrice = total,
                    Status = BookingStatus.Confirmed,
                    CreatedAt = createdAt,
                }));
                await db.SaveChangesAsync(ct);
            },
            cancellationToken);
    }

    // One room type of a hotel AddHotelAsync creates, with the number of rooms it gets.
    private sealed record FixtureRoomType(string Name, int MaxGuests, decimal NightlyPrice, int Rooms);
}