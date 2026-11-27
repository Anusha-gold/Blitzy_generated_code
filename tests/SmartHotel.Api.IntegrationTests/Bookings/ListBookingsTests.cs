using System.Net;
using System.Text.Json;

using SmartHotel.Api.IntegrationTests.Infrastructure;

namespace SmartHotel.Api.IntegrationTests.Bookings;

public sealed class ListBookingsTests : IClassFixture<SmartHotelApiFactory>
{
    // Every booking in this class uses one seeded room type, and each gets its own stay window, so no booking here
    // can block another whatever order the tests run in: the type has 10 rooms.
    private const string HotelName = "SmartHotel Seattle Waterfront";
    private const string RoomTypeName = "Standard";
    private const int PartySize = 2;
    private const int StayNights = 2;

    private readonly SmartHotelApiFactory _factory;
    private readonly ApiTestClient _client;

    public ListBookingsTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    [Fact]
    public async Task List_NoToken_Returns401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using HttpResponseMessage response = await _client.ListBookingsAsync(token: null, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.Unauthorized, cancellationToken: ct);
        ProblemAssert.AssertBearerChallenge(response);
    }

    [Fact]
    public async Task List_ReturnsOnlyCallersBookingsNewestFirst()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int hotelId = await _factory.GetHotelIdAsync(HotelName, ct);
        SmartHotelApiFactory.SeededRoomType roomType = await _factory.GetRoomTypeAsync(HotelName, RoomTypeName, ct);
        DateOnly today = ApiTestClient.TodayUtc;

        ApiTestClient.GuestSession guestA = await _client.RegisterGuestAsync(ct);
        ApiTestClient.GuestSession guestB = await _client.RegisterGuestAsync(ct);

        ApiTestClient.Booking first = await _client.CreateBookingOkAsync(
            guestA.Token,
            hotelId,
            roomType.Id,
            today.AddDays(30),
            today.AddDays(30 + StayNights),
            PartySize,
            ct);
        ApiTestClient.Booking second = await _client.CreateBookingOkAsync(
            guestA.Token,
            hotelId,
            roomType.Id,
            today.AddDays(34),
            today.AddDays(34 + StayNights),
            PartySize,
            ct);

        // Cancelling the older booking puts both statuses in the list.
        using (HttpResponseMessage cancelled = await _client.CancelBookingAsync(first.Id, guestA.Token, ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        }

        ApiTestClient.Booking other = await _client.CreateBookingOkAsync(
            guestB.Token,
            hotelId,
            roomType.Id,
            today.AddDays(38),
            today.AddDays(38 + StayNights),
            PartySize,
            ct);

        List<ApiTestClient.Booking> listA = await ListOkAsync(guestA.Token, ct);

        // second was created after first, so it has the later createdAt and the higher id.
        int[] expectedIds = [second.Id, first.Id];
        Assert.Equal(expectedIds, listA.Select(static booking => booking.Id));
        Assert.DoesNotContain(listA, booking => booking.Id == other.Id);

        for (int index = 1; index < listA.Count; index++)
        {
            Assert.True(
                IsListedBefore(listA[index - 1], listA[index]),
                $"Expected the booking at position {index - 1} to be newer than the one at position {index}: "
                + "createdAt descending, then id descending.");
        }

        foreach (ApiTestClient.Booking booking in listA)
        {
            Assert.Equal(hotelId, booking.HotelId);
            Assert.Equal(HotelName, booking.HotelName);
            Assert.Equal(roomType.Id, booking.RoomTypeId);
            Assert.Equal(RoomTypeName, booking.RoomTypeName);
            Assert.False(string.IsNullOrWhiteSpace(booking.RoomNumber), "Expected every booking to name its room.");
            Assert.Equal(StayNights, booking.Nights);
            Assert.Equal(PartySize, booking.Guests);
        }

        // Apart from the cancellation, each listed booking is exactly the one its 201 body described.
        ApiTestClient.Booking listedSecond = listA[0];
        ApiTestClient.Booking listedFirst = listA[1];
        Assert.Equal("Confirmed", listedSecond.Status);
        Assert.Equal("Cancelled", listedFirst.Status);
        Assert.Equal(second, listedSecond);
        Assert.Equal(first with { Status = "Cancelled" }, listedFirst);

        List<ApiTestClient.Booking> listB = await ListOkAsync(guestB.Token, ct);

        ApiTestClient.Booking onlyB = Assert.Single(listB);
        Assert.Equal(other.Id, onlyB.Id);

        static bool IsListedBefore(ApiTestClient.Booking newer, ApiTestClient.Booking older)
        {
            return newer.CreatedAt > older.CreatedAt
                || (newer.CreatedAt == older.CreatedAt && newer.Id > older.Id);
        }
    }

    [Fact]
    public async Task List_NoBookings_ReturnsEmptyList()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient.GuestSession guest = await _client.RegisterGuestAsync(ct);

        using HttpResponseMessage response = await _client.ListBookingsAsync(guest.Token, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Empty(document.RootElement.EnumerateArray());
    }

    private async Task<List<ApiTestClient.Booking>> ListOkAsync(string token, CancellationToken ct)
    {
        using HttpResponseMessage response = await _client.ListBookingsAsync(token, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await _client.ReadAsync<List<ApiTestClient.Booking>>(response, ct);
    }
}