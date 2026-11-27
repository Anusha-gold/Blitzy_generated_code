using System.Globalization;
using System.Net;

using SmartHotel.Api.IntegrationTests.Infrastructure;

namespace SmartHotel.Api.IntegrationTests.Hotels;

public sealed class HotelDetailTests : IClassFixture<SmartHotelApiFactory>
{
    private const string HotelName = "SmartHotel Seattle Downtown";
    private const string HotelCity = "Seattle";
    private const string BaselineGuests = "2";
    private const int UnknownHotelId = 999999;

    // Every room type of every seeded hotel has 10 rooms, and this class books nothing in its own database, so each
    // count is the full seeded inventory.
    private const int SeededRoomsPerType = 10;

    private readonly SmartHotelApiFactory _factory;
    private readonly ApiTestClient _client;

    public HotelDetailTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    [Fact]
    public async Task Detail_UnknownHotel_Returns404()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;

        using HttpResponseMessage response = await _client.GetHotelAsync(
            UnknownHotelId,
            ApiTestClient.Iso(today.AddDays(30)),
            ApiTestClient.Iso(today.AddDays(32)),
            BaselineGuests,
            ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.NotFound, cancellationToken: ct);
    }

    // Each row breaks exactly one input on a real hotel and keeps every other input at the valid baseline, so the
    // 400 can come only from the broken input. The dates are built from the label because DateOnly cannot be an
    // attribute argument.
    [Theory]
    [InlineData("checkOut-equals-checkIn", "checkOut")]
    [InlineData("checkOut-not-padded", "checkOut")]
    [InlineData("checkIn-missing", "checkIn")]
    [InlineData("guests-zero", "guests")]
    [InlineData("guests-not-integer", "guests")]
    public async Task Detail_InvalidInput_Returns400(string caseLabel, string expectedKey)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int hotelId = await _factory.GetHotelIdAsync(HotelName, ct);
        (string? checkIn, string? checkOut, string? guests) = InvalidQuery(caseLabel);

        using HttpResponseMessage response = await _client.GetHotelAsync(hotelId, checkIn, checkOut, guests, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedKey, ct);
    }

    [Fact]
    public async Task Detail_ReturnsFourRoomTypesWithCounts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly checkIn = today.AddDays(30);
        DateOnly checkOut = today.AddDays(32);

        int hotelId = await _factory.GetHotelIdAsync(HotelName, ct);
        SmartHotelApiFactory.SeededRoomType standard = await _factory.GetRoomTypeAsync(HotelName, "Standard", ct);
        SmartHotelApiFactory.SeededRoomType deluxe = await _factory.GetRoomTypeAsync(HotelName, "Deluxe", ct);
        SmartHotelApiFactory.SeededRoomType family = await _factory.GetRoomTypeAsync(HotelName, "Family", ct);
        SmartHotelApiFactory.SeededRoomType suite = await _factory.GetRoomTypeAsync(HotelName, "Suite", ct);

        ApiTestClient.HotelDetail detail = await _client.GetHotelDetailAsync(hotelId, checkIn, checkOut, 2, ct);

        Assert.Equal(hotelId, detail.Id);
        Assert.Equal(HotelName, detail.Name);
        Assert.Equal(HotelCity, detail.City);
        Assert.False(string.IsNullOrWhiteSpace(detail.Address), "Expected the hotel detail to carry an address.");
        Assert.False(
            string.IsNullOrWhiteSpace(detail.Description),
            "Expected the hotel detail to carry a description.");
        Assert.Equal(checkIn, detail.CheckIn);
        Assert.Equal(checkOut, detail.CheckOut);
        Assert.Equal(2, detail.Nights);
        Assert.Equal(2, detail.Guests);

        // Ordered by nightly price; each stay total is the nightly price for the 2 nights.
        Assert.Collection(
            detail.RoomTypes,
            item => AssertRoomType(item, standard, "Standard", 2, 129.00m, 258.00m),
            item => AssertRoomType(item, deluxe, "Deluxe", 2, 169.00m, 338.00m),
            item => AssertRoomType(item, family, "Family", 4, 219.00m, 438.00m),
            item => AssertRoomType(item, suite, "Suite", 4, 329.00m, 658.00m));

        // The counts are physical inventory: a party of 4 is only echoed, and the two room types that host at most 2
        // guests are still listed with every room free.
        ApiTestClient.HotelDetail largeParty = await _client.GetHotelDetailAsync(hotelId, checkIn, checkOut, 4, ct);
        int[] expectedRoomTypeIds = [standard.Id, deluxe.Id, family.Id, suite.Id];

        Assert.Equal(4, largeParty.Guests);
        Assert.Equal(expectedRoomTypeIds, largeParty.RoomTypes.Select(static roomType => roomType.Id));
        Assert.All(
            largeParty.RoomTypes,
            static roomType => Assert.Equal(SeededRoomsPerType, roomType.AvailableRooms));
    }

    private static (string? CheckIn, string? CheckOut, string? Guests) InvalidQuery(string caseLabel)
    {
        DateOnly today = ApiTestClient.TodayUtc;
        string checkIn = ApiTestClient.Iso(today.AddDays(30));
        string checkOut = ApiTestClient.Iso(today.AddDays(32));

        // The next year's dates are always in the future, so the unpadded check-out is the only broken input of its
        // row: written padded, Y-01-05 to Y-01-07 is a valid 2-night stay.
        int year = ApiTestClient.NextYear;

        return caseLabel switch
        {
            "checkOut-equals-checkIn" => (checkIn, checkIn, BaselineGuests),
            "checkOut-not-padded" => (
                string.Create(CultureInfo.InvariantCulture, $"{year}-01-05"),
                string.Create(CultureInfo.InvariantCulture, $"{year}-1-7"),
                BaselineGuests),
            "checkIn-missing" => (null, checkOut, BaselineGuests),
            "guests-zero" => (checkIn, checkOut, "0"),
            "guests-not-integer" => (checkIn, checkOut, "two"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseLabel), caseLabel, "Unknown invalid-input case."),
        };
    }

    private static void AssertRoomType(
        ApiTestClient.RoomTypeAvailability actual,
        SmartHotelApiFactory.SeededRoomType seeded,
        string expectedName,
        int expectedMaxGuests,
        decimal expectedNightlyPrice,
        decimal expectedStayTotal)
    {
        Assert.Equal(seeded.Id, actual.Id);
        Assert.Equal(expectedName, actual.Name);
        Assert.Equal(expectedMaxGuests, actual.MaxGuests);
        Assert.Equal(seeded.MaxGuests, actual.MaxGuests);
        Assert.Equal(expectedNightlyPrice, actual.NightlyPrice);
        Assert.Equal(seeded.NightlyPrice, actual.NightlyPrice);
        Assert.Equal(SeededRoomsPerType, actual.AvailableRooms);
        Assert.Equal(expectedStayTotal, actual.StayTotal);
    }
}