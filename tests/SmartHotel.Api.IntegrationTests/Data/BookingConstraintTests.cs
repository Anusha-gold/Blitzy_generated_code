using Microsoft.EntityFrameworkCore;

using Npgsql;

using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.IntegrationTests.Data;

/// <summary>
/// Proves that PostgreSQL itself holds the booking, room and guest invariants, whatever the application does: every
/// row is inserted directly through EF Core, so no API rule runs and no HTTP request is sent.
/// </summary>
public sealed class BookingConstraintTests : IClassFixture<SmartHotelApiFactory>
{
    private const string HotelName = "SmartHotel Seattle Downtown";
    private const string RoomTypeName = "Standard";
    private const string OtherHotelName = "SmartHotel Seattle Waterfront";
    private const string OtherRoomTypeName = "Deluxe";

    // The database object names written out as literals rather than read from the EF model or the data library, so a
    // renamed or missing constraint fails these tests.
    private const string ExclusionConstraintName = "EX_Bookings_Room_NoOverlap";
    private const string DatesCheckName = "CK_Bookings_Dates";
    private const string RoomTypeForeignKeyName = "FK_Rooms_RoomTypes_RoomTypeId_HotelId";
    private const string RoomNumberUniqueIndexName = "IX_Rooms_HotelId_RoomNumber";
    private const string NormalizedEmailCheckName = "CK_Guests_Email_Normalized";
    private const string EmailUniqueIndexName = "UX_Guests_Email";

    private const decimal NightlyPrice = 129.00m;

    // A number no seeded room carries in any hotel: the seed numbers each hotel's rooms 101-110 to 401-410.
    private const string UnusedRoomNumber = "999";

    // The database has no "check-in not in the past" rule (that is the API's StayPeriod), so a fixed date keeps every
    // stay deterministic.
    private static readonly DateOnly BaseDate = new(2031, 3, 10);

    private readonly SmartHotelApiFactory _factory;

    public BookingConstraintTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
    }

    // The tests of this class share one seeded database and run in no fixed order, so each booking test, and each row
    // of a booking theory, works on a room of its own and never sees another test's bookings.

    [Fact]
    public async Task Insert_OverlappingConfirmedBooking_FailsOnExclusionConstraint()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int guestId = await InsertGuestAsync(ct);
        int roomId = await GetRoomIdAsync("101", ct);
        Booking first = NewBooking(guestId, roomId, BaseDate, BaseDate.AddDays(3), BookingStatus.Confirmed);
        await InsertBookingAsync(first, ct);

        Booking overlapping = NewBooking(
            guestId,
            roomId,
            BaseDate.AddDays(1),
            BaseDate.AddDays(2),
            BookingStatus.Confirmed);
        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => InsertBookingAsync(overlapping, ct));

        AssertPostgresError(exception, PostgresErrorCodes.ExclusionViolation, ExclusionConstraintName);
        Booking stored = Assert.Single(await GetBookingsForRoomAsync(roomId, ct));
        Assert.Equal(first.Id, stored.Id);
    }

    [Fact]
    public async Task Insert_CancelledOverlappingBooking_Succeeds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int guestId = await InsertGuestAsync(ct);
        int roomId = await GetRoomIdAsync("102", ct);

        await InsertBookingAsync(
            NewBooking(guestId, roomId, BaseDate, BaseDate.AddDays(3), BookingStatus.Confirmed),
            ct);
        await InsertBookingAsync(
            NewBooking(guestId, roomId, BaseDate.AddDays(1), BaseDate.AddDays(2), BookingStatus.Cancelled),
            ct);

        (BookingStatus Status, DateOnly CheckIn, DateOnly CheckOut)[] expected =
        [
            (BookingStatus.Confirmed, BaseDate, BaseDate.AddDays(3)),
            (BookingStatus.Cancelled, BaseDate.AddDays(1), BaseDate.AddDays(2)),
        ];
        Assert.Equal(expected, ToStays(await GetBookingsForRoomAsync(roomId, ct)));
    }

    [Fact]
    public async Task Insert_BackToBackConfirmedStays_Succeed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int guestId = await InsertGuestAsync(ct);
        int roomId = await GetRoomIdAsync("103", ct);

        // The middle stay first, then one starting on its check-out day and one ending on its check-in day. Adjacent
        // stays can share an endpoint because the earlier stay does not occupy its check-out date.
        await InsertBookingAsync(
            NewBooking(guestId, roomId, BaseDate, BaseDate.AddDays(2), BookingStatus.Confirmed),
            ct);
        await InsertBookingAsync(
            NewBooking(guestId, roomId, BaseDate.AddDays(2), BaseDate.AddDays(4), BookingStatus.Confirmed),
            ct);
        await InsertBookingAsync(
            NewBooking(guestId, roomId, BaseDate.AddDays(-2), BaseDate, BookingStatus.Confirmed),
            ct);

        (BookingStatus Status, DateOnly CheckIn, DateOnly CheckOut)[] expected =
        [
            (BookingStatus.Confirmed, BaseDate, BaseDate.AddDays(2)),
            (BookingStatus.Confirmed, BaseDate.AddDays(2), BaseDate.AddDays(4)),
            (BookingStatus.Confirmed, BaseDate.AddDays(-2), BaseDate),
        ];
        Assert.Equal(expected, ToStays(await GetBookingsForRoomAsync(roomId, ct)));
    }

    // Each row's room already holds a Confirmed stay from BaseDate to BaseDate + 3, and the rejected stay's dates lie
    // inside it. Only the check rejects either row with SqlState 23514: without it, the empty stay would be stored,
    // because an empty range overlaps nothing, and the reversed stay would make daterange() fail with SqlState 22000.
    [Theory]
    [InlineData("104", 1, 1)]
    [InlineData("105", 2, 1)]
    public async Task Insert_EmptyOrReversedStay_FailsOnDatesCheck(
        string roomNumber,
        int checkInOffset,
        int checkOutOffset)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int guestId = await InsertGuestAsync(ct);
        int roomId = await GetRoomIdAsync(roomNumber, ct);
        Booking existing = NewBooking(guestId, roomId, BaseDate, BaseDate.AddDays(3), BookingStatus.Confirmed);
        await InsertBookingAsync(existing, ct);

        Booking invalid = NewBooking(
            guestId,
            roomId,
            BaseDate.AddDays(checkInOffset),
            BaseDate.AddDays(checkOutOffset),
            BookingStatus.Confirmed);

        // One night's price, so the dates are the only value the row gets wrong.
        invalid.TotalPrice = NightlyPrice;

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => InsertBookingAsync(invalid, ct));

        AssertPostgresError(exception, PostgresErrorCodes.CheckViolation, DatesCheckName);
        Booking stored = Assert.Single(await GetBookingsForRoomAsync(roomId, ct));
        Assert.Equal(existing.Id, stored.Id);
    }

    // The room number is free in the other hotel, so the unique index, which is checked as the row is inserted, has
    // nothing to reject, and the composite foreign key, checked at the end of the statement, is what fails.
    [Fact]
    public async Task Insert_RoomWithAnotherHotelsRoomType_FailsOnCompositeForeignKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.SeededRoomType roomType = await _factory.GetRoomTypeAsync(HotelName, RoomTypeName, ct);
        int otherHotelId = await _factory.GetHotelIdAsync(OtherHotelName, ct);
        Assert.NotEqual(roomType.HotelId, otherHotelId);
        Assert.Empty(await GetRoomIdsAsync(otherHotelId, UnusedRoomNumber, ct));
        IReadOnlyList<SmartHotelApiFactory.SeededRoom> roomsBefore = await _factory.GetRoomsAsync(roomType.Id, ct);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => InsertRoomAsync(roomType.Id, otherHotelId, UnusedRoomNumber, ct));

        AssertPostgresError(exception, PostgresErrorCodes.ForeignKeyViolation, RoomTypeForeignKeyName);
        Assert.Empty(await GetRoomIdsAsync(otherHotelId, UnusedRoomNumber, ct));
        Assert.Equal(roomsBefore, await _factory.GetRoomsAsync(roomType.Id, ct));
    }

    // The new room reuses a Standard room's number under the Deluxe type of the same hotel, so the rejection shows that
    // room numbers are unique per hotel, not per room type.
    [Fact]
    public async Task Insert_DuplicateRoomNumberInHotel_FailsOnUniqueIndex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string roomNumber = "101";
        int takenRoomId = await GetRoomIdAsync(roomNumber, ct);
        SmartHotelApiFactory.SeededRoomType otherRoomType =
            await _factory.GetRoomTypeAsync(HotelName, OtherRoomTypeName, ct);
        IReadOnlyList<SmartHotelApiFactory.SeededRoom> otherRoomsBefore =
            await _factory.GetRoomsAsync(otherRoomType.Id, ct);
        Assert.DoesNotContain(otherRoomsBefore, room => room.RoomNumber == roomNumber);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => InsertRoomAsync(otherRoomType.Id, otherRoomType.HotelId, roomNumber, ct));

        AssertPostgresError(exception, PostgresErrorCodes.UniqueViolation, RoomNumberUniqueIndexName);
        Assert.Equal(takenRoomId, Assert.Single(await GetRoomIdsAsync(otherRoomType.HotelId, roomNumber, ct)));
        Assert.Equal(otherRoomsBefore, await _factory.GetRoomsAsync(otherRoomType.Id, ct));
    }

    // Each email is unique (it carries a new GUID) and breaks the normalized form in one way only: an upper-case
    // letter, a character above printable ASCII, or a space, which is below it.
    [Theory]
    [InlineData("C+", "@Example.test")]
    [InlineData("g\u00e4st+", "@example.test")]
    [InlineData("c +", "@example.test")]
    public async Task Insert_GuestWithUnnormalizedEmail_FailsOnNormalizedCheck(string localPrefix, string domain)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = localPrefix + Guid.NewGuid().ToString("N") + domain;

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => InsertGuestAsync(email, ct));

        AssertPostgresError(exception, PostgresErrorCodes.CheckViolation, NormalizedEmailCheckName);
    }

    [Fact]
    public async Task Insert_DuplicateNormalizedEmail_FailsOnUniqueIndex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = NewNormalizedEmail();
        await InsertGuestAsync(email, ct);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => InsertGuestAsync(email, ct));

        AssertPostgresError(exception, PostgresErrorCodes.UniqueViolation, EmailUniqueIndexName);
    }

    private Task<int> InsertGuestAsync(CancellationToken cancellationToken)
    {
        return InsertGuestAsync(NewNormalizedEmail(), cancellationToken);
    }

    // Every insert runs in a scope of its own: a failed SaveChangesAsync leaves its entity tracked in that context, so
    // neither the context nor the entity is ever used again.
    private Task<int> InsertGuestAsync(string email, CancellationToken cancellationToken)
    {
        return _factory.ExecuteDbAsync(
            async (db, ct) =>
            {
                Guest guest = new()
                {
                    Email = email,
                    PasswordHash = "not-a-real-hash",
                    FullName = "Constraint Guest",
                    CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime,
                };
                db.Guests.Add(guest);
                await db.SaveChangesAsync(ct);
                return guest.Id;
            },
            cancellationToken);
    }

    private Task InsertBookingAsync(Booking booking, CancellationToken cancellationToken)
    {
        return _factory.ExecuteDbAsync(
            async (db, ct) =>
            {
                db.Bookings.Add(booking);
                await db.SaveChangesAsync(ct);
            },
            cancellationToken);
    }

    // Only scalar properties are set: with the RoomType navigation left unset, EF inserts no related row.
    private Task InsertRoomAsync(
        int roomTypeId,
        int hotelId,
        string roomNumber,
        CancellationToken cancellationToken)
    {
        return _factory.ExecuteDbAsync(
            async (db, ct) =>
            {
                db.Rooms.Add(new Room { RoomTypeId = roomTypeId, HotelId = hotelId, RoomNumber = roomNumber });
                await db.SaveChangesAsync(ct);
            },
            cancellationToken);
    }

    private Task<List<int>> GetRoomIdsAsync(int hotelId, string roomNumber, CancellationToken cancellationToken)
    {
        return _factory.ExecuteDbAsync(
            (db, ct) => db.Rooms
                .AsNoTracking()
                .Where(room => room.HotelId == hotelId && room.RoomNumber == roomNumber)
                .Select(static room => room.Id)
                .ToListAsync(ct),
            cancellationToken);
    }

    private async Task<int> GetRoomIdAsync(string roomNumber, CancellationToken cancellationToken)
    {
        SmartHotelApiFactory.SeededRoomType roomType =
            await _factory.GetRoomTypeAsync(HotelName, RoomTypeName, cancellationToken);
        IReadOnlyList<SmartHotelApiFactory.SeededRoom> rooms =
            await _factory.GetRoomsAsync(roomType.Id, cancellationToken);

        return Assert.Single(rooms, room => room.RoomNumber == roomNumber).Id;
    }

    private Task<List<Booking>> GetBookingsForRoomAsync(int roomId, CancellationToken cancellationToken)
    {
        return _factory.ExecuteDbAsync(
            (db, ct) => db.Bookings
                .AsNoTracking()
                .Where(booking => booking.RoomId == roomId)
                .OrderBy(static booking => booking.Id)
                .ToListAsync(ct),
            cancellationToken);
    }

    private static string NewNormalizedEmail()
    {
        return "c+" + Guid.NewGuid().ToString("N") + "@example.test";
    }

    // Only scalar properties are set: with the Guest and Room navigations left unset, EF inserts no related rows.
    private static Booking NewBooking(
        int guestId,
        int roomId,
        DateOnly checkIn,
        DateOnly checkOut,
        BookingStatus status)
    {
        return new Booking
        {
            GuestId = guestId,
            RoomId = roomId,
            CheckIn = checkIn,
            CheckOut = checkOut,
            GuestCount = 1,
            TotalPrice = NightlyPrice * (checkOut.DayNumber - checkIn.DayNumber),
            Status = status,
            CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime,
        };
    }

    private static (BookingStatus Status, DateOnly CheckIn, DateOnly CheckOut)[] ToStays(List<Booking> bookings)
    {
        return [.. bookings.Select(static booking => (booking.Status, booking.CheckIn, booking.CheckOut))];
    }

    // ConstraintName is reported even though Include Error Detail is off; only the DETAIL text is redacted.
    private static void AssertPostgresError(
        DbUpdateException exception,
        string expectedSqlState,
        string expectedConstraintName)
    {
        PostgresException postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(expectedSqlState, postgres.SqlState);
        Assert.Equal(expectedConstraintName, postgres.ConstraintName);
    }
}