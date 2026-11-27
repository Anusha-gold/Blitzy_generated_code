using System.Diagnostics;
using System.Globalization;
using System.Net;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.IntegrationTests.Bookings;

public sealed class ConcurrentBookingTests : IClassFixture<SmartHotelApiFactory>
{
    private const string HotelName = "SmartHotel Seattle Downtown";
    private const string RoomTypeName = "Standard";
    private const int RoomsPerType = 10;
    private const int Guests = 2;
    private const int Concurrency = 20;

    // The same lock the booking path takes, so the holder owns exactly the row lock that every request needs.
    private const string LockRoomTypeSql = "SELECT 1 FROM \"RoomTypes\" WHERE \"Id\" = @id FOR UPDATE";

    // Sessions of this class's database queued on a lock behind the holder. The first waiter waits on the holder's
    // transaction id and the rest on the tuple; both report wait_event_type 'Lock'. The holder and the observer itself
    // are excluded by backend PID.
    private const string CountWaitersSql =
        "SELECT count(*) FROM pg_stat_activity "
        + "WHERE datname = current_database() AND pid <> @holderPid AND pid <> pg_backend_pid() "
        + "AND wait_event_type = 'Lock' AND query LIKE '%FOR UPDATE%'";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ObservationLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(30);

    private readonly SmartHotelApiFactory _factory;
    private readonly ApiTestClient _client;

    public ConcurrentBookingTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    [Fact]
    public async Task TwentyConcurrentRequestsForLastRoom_ExactlyOneSucceeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        ApiTestClient.GuestSession session = await _client.RegisterGuestAsync(ct);
        int hotelId = await _factory.GetHotelIdAsync(HotelName, ct);
        SmartHotelApiFactory.SeededRoomType roomType = await _factory.GetRoomTypeAsync(HotelName, RoomTypeName, ct);
        int roomTypeId = roomType.Id;
        DateOnly today = ApiTestClient.TodayUtc;
        DateOnly checkIn = today.AddDays(40);
        DateOnly checkOut = today.AddDays(42);

        // One after another, so each booking takes the next free room and exactly one room of the type stays free.
        for (int booked = 0; booked < RoomsPerType - 1; booked++)
        {
            await _client.CreateBookingOkAsync(session.Token, hotelId, roomTypeId, checkIn, checkOut, Guests, ct);
        }

        Assert.Equal(1, await AvailableRoomsAsync(hotelId, roomTypeId, checkIn, checkOut, ct));

        // The holder takes the room type's row lock before any request starts, so every request blocks at its own
        // FOR UPDATE. Seeing all of them queued there proves that the requests passed authentication, validation and
        // the lookups and contend inside PostgreSQL at the critical section together, rather than one after another.
        await using NpgsqlConnection holder = new(_factory.DatabaseConnectionString);
        await holder.OpenAsync(ct);
        await using NpgsqlTransaction holderTx = await holder.BeginTransactionAsync(ct);
        await using NpgsqlCommand lockCommand = new(LockRoomTypeSql, holder, holderTx);
        lockCommand.Parameters.AddWithValue("id", roomTypeId);
        Assert.Equal(1, (int)(await lockCommand.ExecuteScalarAsync(ct))!);
        int holderPid = holder.ProcessID;

        // ToArray starts every request at once. BookingBody builds a new JSON node for each, so concurrent sends never
        // share one.
        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, Concurrency)
            .Select(_ => _client.CreateBookingAsync(
                ApiTestClient.BookingBody(hotelId, roomTypeId, checkIn, checkOut, Guests),
                session.Token,
                ct))
            .ToArray();

        bool observed = false;
        long lastCount = -1;
        HttpResponseMessage[] responses;
        try
        {
            // No transaction on the observer: each poll is its own autocommit statement and so reads a fresh
            // pg_stat_activity snapshot, while inside a transaction every poll would return the first one again.
            await using NpgsqlConnection observer = new(_factory.DatabaseConnectionString);
            await observer.OpenAsync(ct);
            await using NpgsqlCommand countWaiters = new(CountWaitersSql, observer);
            countWaiters.Parameters.AddWithValue("holderPid", holderPid);

            Stopwatch elapsed = Stopwatch.StartNew();
            do
            {
                lastCount = (long)(await countWaiters.ExecuteScalarAsync(ct))!;
                if (lastCount == Concurrency)
                {
                    observed = true;
                    break;
                }

                await Task.Delay(PollInterval, ct);
            }
            while (elapsed.Elapsed < ObservationLimit);
        }
        finally
        {
            // Runs on success, on the observation timeout and on any failure. The holder changed nothing, so the
            // rollback only releases the lock, and it does so even while the test is being cancelled. Draining every
            // request afterwards leaves none running into the next test or into the factory's database drop.
            await holderTx.RollbackAsync(CancellationToken.None);
            responses = await Task.WhenAll(requests).WaitAsync(DrainLimit, ct);
        }

        try
        {
            if (!observed)
            {
                Assert.Fail(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Concurrency} waiting requests were not observed within {ObservationLimit.TotalSeconds} s "
                    + $"(last count: {lastCount})."));
            }

            Assert.Single(responses, static response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(
                Concurrency - 1,
                responses.Count(static response => response.StatusCode == HttpStatusCode.Conflict));
            foreach (HttpResponseMessage conflict in responses.Where(
                static response => response.StatusCode == HttpStatusCode.Conflict))
            {
                await ProblemAssert.AssertProblemAsync(conflict, HttpStatusCode.Conflict, cancellationToken: ct);
            }

            // Every room of the type is now booked exactly once for the stay: no room was double booked.
            List<int> roomIds = await _factory.ExecuteDbAsync<List<int>>(
                (db, token) => db.Bookings
                    .AsNoTracking()
                    .Where(b => b.Room.RoomTypeId == roomTypeId
                        && b.CheckIn == checkIn
                        && b.CheckOut == checkOut
                        && b.Status == BookingStatus.Confirmed)
                    .Select(b => b.RoomId)
                    .ToListAsync(token),
                ct);
            Assert.Equal(RoomsPerType, roomIds.Count);
            Assert.Equal(RoomsPerType, roomIds.Distinct().Count());

            Assert.Equal(0, await AvailableRoomsAsync(hotelId, roomTypeId, checkIn, checkOut, ct));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    private async Task<int> AvailableRoomsAsync(
        int hotelId,
        int roomTypeId,
        DateOnly checkIn,
        DateOnly checkOut,
        CancellationToken cancellationToken)
    {
        ApiTestClient.HotelDetail detail = await _client.GetHotelDetailAsync(
            hotelId,
            checkIn,
            checkOut,
            Guests,
            cancellationToken);

        return Assert.Single(detail.RoomTypes, roomType => roomType.Id == roomTypeId).AvailableRooms;
    }
}