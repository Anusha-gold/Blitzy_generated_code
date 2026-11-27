using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    // EF Core names each table by a short alias such as b. Both patterns capture the bookings table's alias, so the
    // caller filter and both sort keys must apply to the bookings themselves, not to a joined room.
    private const string CallerFilterPattern =
        @"(?s)\bFROM ""Bookings"" AS (?<booking>\w+)\b.*\bWHERE \k<booking>\.""GuestId"" = @\w+";
    private const string NewestFirstOrderPattern =
        @"(?s)\bFROM ""Bookings"" AS (?<booking>\w+)\b.*"
        + @"\bORDER BY \k<booking>\.""CreatedAt"" DESC, \k<booking>\.""Id"" DESC\s*\z";

    private readonly ApiTestClient _client;
    private readonly TestPostgres _postgres;

    public ListBookingsTests(SmartHotelApiFactory factory, TestPostgres postgres)
    {
        _client = factory.CreateApiClient();
        _postgres = postgres;
    }

    [Fact]
    public async Task List_NoToken_Returns401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using HttpResponseMessage response = await _client.ListBookingsAsync(token: null, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.Unauthorized, cancellationToken: ct);
        ProblemAssert.AssertBearerChallenge(response);
    }

    // Two of the bookings below share one createdAt, and PostgreSQL returns rows that tie on every ORDER BY key in no
    // particular order, so a list query without the id tie-break can still return the expected order by chance. The
    // test therefore also checks the SQL the list request executes, which states both sort keys on every run. That
    // needs a host of its own: a variant whose log provider records EF Core's executed commands, with a database of
    // its own, so this test books nothing in the class's shared one.
    [Fact]
    public async Task List_ReturnsOnlyCallersBookingsNewestFirst()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        CommandCapturingLogProvider commandLog = new();
        await using SmartHotelApiFactory variant = new(_postgres)
        {
            ServiceOverrides = services =>
            {
                services.AddSingleton<ILoggerProvider>(commandLog);

                // appsettings.json holds EF Core's command category at Warning for every provider, and an executed
                // command is an Information event, so only a rule for this provider lets it through. The console
                // provider keeps Warning.
                services.Configure<LoggerFilterOptions>(options => options.AddFilter<CommandCapturingLogProvider>(
                    DbLoggerCategory.Database.Command.Name,
                    LogLevel.Information));
            },
        };

        // A hand-built factory is not a fixture, so it is started here.
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();

        int hotelId = await variant.GetHotelIdAsync(HotelName, ct);
        SmartHotelApiFactory.SeededRoomType roomType = await variant.GetRoomTypeAsync(HotelName, RoomTypeName, ct);
        DateOnly today = ApiTestClient.TodayUtc;

        ApiTestClient.GuestSession guestA = await client.RegisterGuestAsync(ct);
        ApiTestClient.GuestSession guestB = await client.RegisterGuestAsync(ct);

        ApiTestClient.Booking first = await client.CreateBookingOkAsync(
            guestA.Token,
            hotelId,
            roomType.Id,
            today.AddDays(30),
            today.AddDays(30 + StayNights),
            PartySize,
            ct);
        ApiTestClient.Booking second = await client.CreateBookingOkAsync(
            guestA.Token,
            hotelId,
            roomType.Id,
            today.AddDays(34),
            today.AddDays(34 + StayNights),
            PartySize,
            ct);
        ApiTestClient.Booking third = await client.CreateBookingOkAsync(
            guestA.Token,
            hotelId,
            roomType.Id,
            today.AddDays(38),
            today.AddDays(38 + StayNights),
            PartySize,
            ct);
        Assert.True(
            first.Id < second.Id && second.Id < third.Id,
            "Expected the ids to rise in creation order, which the arrangement below relies on.");

        // Cancelling one booking puts both statuses in the list.
        using (HttpResponseMessage cancelled = await client.CancelBookingAsync(second.Id, guestA.Token, ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        }

        ApiTestClient.Booking other = await client.CreateBookingOkAsync(
            guestB.Token,
            hotelId,
            roomType.Id,
            today.AddDays(42),
            today.AddDays(42 + StayNights),
            PartySize,
            ct);

        // The API stamps createdAt from the real clock, so a later booking has both the later instant and the higher
        // id, and sorting by id alone would pass. The instants are rewritten so that they disagree with the ids: the
        // lowest id gets the latest instant, and the two others share one instant, which only the id tie-break can
        // order. The values are UTC, as timestamp with time zone requires, and whole microseconds, the column's
        // precision, with all six fractional digits in use, so the list must return them exactly.
        DateTime lastRealSecond = new(
            third.CreatedAt.Ticks - (third.CreatedAt.Ticks % TimeSpan.TicksPerSecond),
            DateTimeKind.Utc);
        DateTime tiedCreatedAt = lastRealSecond.AddMinutes(1).AddTicks(123_456 * TimeSpan.TicksPerMicrosecond);
        DateTime latestCreatedAt = tiedCreatedAt.AddMinutes(1);
        await variant.ExecuteDbAsync(
            async (db, token) =>
            {
                int latestRows = await db.Bookings
                    .Where(booking => booking.Id == first.Id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(booking => booking.CreatedAt, latestCreatedAt),
                        token);
                int tiedRows = await db.Bookings
                    .Where(booking => booking.Id == second.Id || booking.Id == third.Id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(booking => booking.CreatedAt, tiedCreatedAt),
                        token);
                Assert.Equal(1, latestRows);
                Assert.Equal(2, tiedRows);
            },
            ct);

        // Nothing else reaches the variant's database between clearing the capture and reading it, so the capture holds
        // exactly what guest A's list request executed.
        commandLog.Clear();
        List<ApiTestClient.Booking> listA = await ListOkAsync(client, guestA.Token, ct);
        List<string> listCommands = [.. commandLog.ExecutedCommands];

        // One query reads the caller's bookings, sorted by createdAt descending and then by id descending. The SQL
        // shows both keys on every run, whereas without the id key the tied pair below comes back in the expected
        // order only by chance. Sensitive-data logging is off, so the parameter shows as a placeholder and only the
        // SQL text is checked.
        string listCommand = Assert.Single(listCommands);
        Assert.Matches(CallerFilterPattern, listCommand);
        Assert.Matches(NewestFirstOrderPattern, listCommand);

        // Newest createdAt first, then highest id: first, then the tie in descending id order. Sorting by id alone
        // gives third, second, first, and breaking the tie by ascending id gives first, second, third, so either
        // fails here.
        int[] expectedIds = [first.Id, third.Id, second.Id];
        Assert.Equal(expectedIds, listA.Select(static booking => booking.Id));
        Assert.DoesNotContain(listA, booking => booking.Id == other.Id);

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

        // Apart from the arranged instant and the cancellation, each listed booking is exactly the one its 201 body
        // described.
        ApiTestClient.Booking listedFirst = listA[0];
        ApiTestClient.Booking listedThird = listA[1];
        ApiTestClient.Booking listedSecond = listA[2];
        Assert.Equal("Confirmed", listedFirst.Status);
        Assert.Equal("Confirmed", listedThird.Status);
        Assert.Equal("Cancelled", listedSecond.Status);
        Assert.Equal(first with { CreatedAt = latestCreatedAt }, listedFirst);
        Assert.Equal(third with { CreatedAt = tiedCreatedAt }, listedThird);
        Assert.Equal(second with { CreatedAt = tiedCreatedAt, Status = "Cancelled" }, listedSecond);

        List<ApiTestClient.Booking> listB = await ListOkAsync(client, guestB.Token, ct);

        ApiTestClient.Booking onlyB = Assert.Single(listB);
        Assert.Equal(other.Id, onlyB.Id);
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

    private static async Task<List<ApiTestClient.Booking>> ListOkAsync(
        ApiTestClient client,
        string token,
        CancellationToken ct)
    {
        using HttpResponseMessage response = await client.ListBookingsAsync(token, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await client.ReadAsync<List<ApiTestClient.Booking>>(response, ct);
    }

    // Hands EF Core's command category a logger that records the message of each executed command, which ends with
    // the command's SQL text; every other category gets the null logger, so the host starts and serves as usual.
    // Nothing is written to output.
    private sealed class CommandCapturingLogProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _executedCommands = new();
        private int _disposed;

        public IReadOnlyCollection<string> ExecutedCommands => _executedCommands;

        public void Clear()
        {
            _executedCommands.Clear();
        }

        public ILogger CreateLogger(string categoryName)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return string.Equals(categoryName, DbLoggerCategory.Database.Command.Name, StringComparison.Ordinal)
                ? new CommandCapturingLogger(_executedCommands)
                : NullLogger.Instance;
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposed, 1);
        }
    }

    private sealed class CommandCapturingLogger(ConcurrentQueue<string> executedCommands) : ILogger
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
            if (eventId.Id == RelationalEventId.CommandExecuted.Id)
            {
                executedCommands.Enqueue(formatter(state, exception));
            }
        }
    }
}