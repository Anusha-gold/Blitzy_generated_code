using System.Net;

using Microsoft.EntityFrameworkCore;

using SmartHotel.Api.Common.Pricing;
using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.IntegrationTests.Bookings;

/// <summary>
/// <c>DELETE /api/bookings/{id}</c> against the real API pipeline and PostgreSQL: the 401, 403, 404 and both 409
/// cases of the interface table, and a successful cancellation that frees the room again.
/// </summary>
/// <remarks>
/// The tests share one seeded database and run in any order, so each one books a hotel and room type that no other
/// test in this class touches, registers its own guests and resolves every id by name.
/// </remarks>
public sealed class CancelBookingTests : IClassFixture<SmartHotelApiFactory>
{
    private const string SeattleDowntown = "SmartHotel Seattle Downtown";
    private const string SeattleWaterfront = "SmartHotel Seattle Waterfront";
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

    [Fact]
    public async Task Cancel_OtherGuestsBooking_Returns403()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        ApiTestClient.GuestSession owner = await _client.RegisterGuestAsync(cancellationToken);
        ApiTestClient.GuestSession otherGuest = await _client.RegisterGuestAsync(cancellationToken);
        ApiTestClient.Booking booking = await BookAsync(
            owner,
            SeattleDowntown,
            Standard,
            today.AddDays(30),
            today.AddDays(32),
            cancellationToken);

        using HttpResponseMessage response = await _client.CancelBookingAsync(
            booking.Id,
            otherGuest.Token,
            cancellationToken);

        await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            cancellationToken: cancellationToken);

        ApiTestClient.Booking listed = await GetListedBookingAsync(owner.Token, booking.Id, cancellationToken);
        Assert.Equal(ConfirmedStatus, listed.Status);
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

        await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            cancellationToken: cancellationToken);
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

        await ProblemAssert.AssertProblemAsync(
            second,
            HttpStatusCode.Conflict,
            cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task Cancel_CheckInPassed_Returns409()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly checkIn = today.AddDays(-1);
        DateOnly checkOut = today.AddDays(1);
        int nights = checkOut.DayNumber - checkIn.DayNumber;

        ApiTestClient.GuestSession guest = await _client.RegisterGuestAsync(cancellationToken);
        SmartHotelApiFactory.SeededRoomType suite = await _factory.GetRoomTypeAsync(
            ChicagoLoop,
            Suite,
            cancellationToken);
        IReadOnlyList<SmartHotelApiFactory.SeededRoom> rooms = await _factory.GetRoomsAsync(
            suite.Id,
            cancellationToken);
        Assert.NotEmpty(rooms);
        SmartHotelApiFactory.SeededRoom room = rooms[^1];

        // The API refuses a check-in in the past, so a stay that has already started can only be arranged directly
        // in the database.
        int bookingId = await _factory.ExecuteDbAsync(
            async (db, ct) =>
            {
                Booking started = new()
                {
                    GuestId = guest.GuestId,
                    RoomId = room.Id,
                    CheckIn = checkIn,
                    CheckOut = checkOut,
                    GuestCount = 1,
                    TotalPrice = PriceCalculator.Total(suite.NightlyPrice, nights),
                    Status = BookingStatus.Confirmed,
                    CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime,
                };
                db.Bookings.Add(started);
                await db.SaveChangesAsync(ct);
                return started.Id;
            },
            cancellationToken);

        using HttpResponseMessage response = await _client.CancelBookingAsync(
            bookingId,
            guest.Token,
            cancellationToken);

        await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            cancellationToken: cancellationToken);

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

        ApiTestClient.Booking booking = await _client.CreateBookingOkAsync(
            owner.Token,
            family.HotelId,
            family.Id,
            checkIn,
            checkOut,
            PartySize,
            cancellationToken);
        Assert.Equal(initial - 1, await GetAvailableRoomsAsync(family, checkIn, checkOut, cancellationToken));

        using HttpResponseMessage response = await _client.CancelBookingAsync(
            booking.Id,
            owner.Token,
            cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        ApiTestClient.Booking listed = await GetListedBookingAsync(owner.Token, booking.Id, cancellationToken);
        Assert.Equal(CancelledStatus, listed.Status);

        // The availability count runs the shared confirmed-overlap predicate as SQL, so the cancelled booking must
        // no longer block its room.
        Assert.Equal(initial, await GetAvailableRoomsAsync(family, checkIn, checkOut, cancellationToken));
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
}