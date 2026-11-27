using System.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

using SmartHotel.Api.Common.Pricing;
using SmartHotel.Api.Common.Stays;
using SmartHotel.Api.Modules.Availability;
using SmartHotel.Data;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.Modules.Bookings;

/// <summary>
/// Creates, lists and cancels guests' bookings: the service behind <c>POST /api/bookings</c>,
/// <c>GET /api/bookings</c> and <c>DELETE /api/bookings/{id}</c>, and the only application code that writes
/// <c>Bookings</c> rows.
/// </summary>
/// <remarks>
/// <para>
/// Creation is atomic. Inside one READ COMMITTED transaction it locks the requested room type's row, assigns the
/// lowest-numbered room that is free for the stay and inserts a <see cref="BookingStatus.Confirmed"/> booking whose
/// total the server computes with <see cref="PriceCalculator.Total"/>. Payment is simulated, so a booking is confirmed
/// as soon as it is created. The exclusion constraint <c>EX_Bookings_Room_NoOverlap</c> guarantees that no room is ever
/// double booked; its violation is the only database error mapped to an outcome, and every other one propagates.
/// </para>
/// <para>
/// "Today" is the current UTC calendar date of the injected <see cref="TimeProvider"/>, whatever the hotel's city. A
/// stay may start today, and it may be cancelled until the end of its check-in day (UTC).
/// </para>
/// <para>
/// Registered scoped, beside the request's <see cref="SmartHotelDbContext"/>, which it shares with
/// <see cref="AvailabilityQueries"/>, so the free-room query runs inside the transaction that holds the lock. Its log
/// events carry booking ids, room type ids and dates only: never a guest id, an email, a price or an exception message.
/// </para>
/// </remarks>
/// <param name="db">The request's database session, shared with <paramref name="availability"/>.</param>
/// <param name="availability">The shared free-room queries of the Availability module.</param>
/// <param name="timeProvider">The clock that decides "today" and stamps <see cref="Booking.CreatedAt"/>.</param>
/// <param name="logger">Receives the booking-created, booking-conflict and booking-cancelled events.</param>
public sealed partial class BookingService(
    SmartHotelDbContext db,
    AvailabilityQueries availability,
    TimeProvider timeProvider,
    ILogger<BookingService> logger)
{
    // The hand-written exclusion constraint of the InitialCreate migration. Its violation (SqlState 23P01) means that a
    // concurrent writer took the room, which is a 409 and not a server error.
    private const string ExclusionConstraintName = "EX_Bookings_Room_NoOverlap";

    private const string UnvalidatedRequestMessage = "A booking request must pass model validation first.";

    /// <summary>
    /// Books a room of the requested type for the stay and confirms it, assigning the room and computing the total on
    /// the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checks run in this order, and the first failure decides the outcome: the stay rules, the hotel, the room type,
    /// which must belong to that hotel, and the room type's capacity. Only then does the transaction start.
    /// </para>
    /// <para>
    /// Under the room type's row lock the lowest-numbered free room is assigned. When no room is free, or when
    /// PostgreSQL reports a violation of <c>EX_Bookings_Room_NoOverlap</c> because a concurrent writer took the room,
    /// the transaction is rolled back and the result is <see cref="CreateBookingResult.NoRoomAvailable"/>. Any other
    /// database error propagates.
    /// </para>
    /// </remarks>
    /// <param name="guestId">The caller's guest id, from the bearer token's <c>sub</c> claim.</param>
    /// <param name="request">
    /// The request body after model validation, so <see cref="CreateBookingRequest.HotelId"/>,
    /// <see cref="CreateBookingRequest.RoomTypeId"/> and <see cref="CreateBookingRequest.Guests"/> are present and at
    /// least 1. The dates arrive as the client sent them and are validated here.
    /// </param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <returns>
    /// <see cref="CreateBookingResult.Created"/> with the confirmed booking; otherwise the first failing outcome:
    /// <see cref="CreateBookingResult.InvalidStay"/>, <see cref="CreateBookingResult.HotelNotFound"/>,
    /// <see cref="CreateBookingResult.RoomTypeNotFound"/>, <see cref="CreateBookingResult.CapacityExceeded"/> or
    /// <see cref="CreateBookingResult.NoRoomAvailable"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="request"/> has no hotel id, room type id or guest count, so it skipped model validation.
    /// </exception>
    public async Task<CreateBookingResult> CreateAsync(
        int guestId,
        CreateBookingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        int hotelId = request.HotelId ?? throw new ArgumentException(UnvalidatedRequestMessage, nameof(request));
        int roomTypeId = request.RoomTypeId ?? throw new ArgumentException(UnvalidatedRequestMessage, nameof(request));
        int guests = request.Guests ?? throw new ArgumentException(UnvalidatedRequestMessage, nameof(request));

        bool stayIsValid = StayPeriod.TryCreate(
            request.CheckIn,
            request.CheckOut,
            Today(),
            out StayPeriod period,
            out Dictionary<string, string[]> errors);
        if (!stayIsValid)
        {
            return new CreateBookingResult.InvalidStay(errors);
        }

        string? hotelName = await db.Hotels
            .AsNoTracking()
            .Where(h => h.Id == hotelId)
            .Select(h => h.Name)
            .FirstOrDefaultAsync(cancellationToken);
        if (hotelName is null)
        {
            return new CreateBookingResult.HotelNotFound();
        }

        var roomType = await db.RoomTypes
            .AsNoTracking()
            .Where(t => t.Id == roomTypeId && t.HotelId == hotelId)
            .Select(t => new { t.Name, t.MaxGuests, t.NightlyPrice })
            .FirstOrDefaultAsync(cancellationToken);
        if (roomType is null)
        {
            return new CreateBookingResult.RoomTypeNotFound();
        }

        if (guests > roomType.MaxGuests)
        {
            return new CreateBookingResult.CapacityExceeded(roomType.MaxGuests);
        }

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // The row lock gives predictable allocation. Concurrent creators for this room type take turns here, and under
        // READ COMMITTED each one's free-room query then sees every booking committed before it, so each is given a
        // distinct room and a 409 means that no room of the type is free. The lock is not the guarantee against double
        // booking: EX_Bookings_Room_NoOverlap alone guarantees that, whatever the interleaving. A booking row cannot be
        // locked before it exists, so the parent RoomTypes row is locked, and creators for other room types never wait
        // on it. SERIALIZABLE isolation was rejected because its 40001 failures need whole-transaction retries, which
        // make "exactly one success for the last room" harder to keep deterministic.
        // No operator may be composed onto this query: any would wrap the statement in a subquery, and it must reach
        // PostgreSQL exactly as written.
        await db.RoomTypes
            .FromSql($"SELECT * FROM \"RoomTypes\" WHERE \"Id\" = {roomTypeId} FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        Room? room = await availability.FindFreeRoomAsync(roomTypeId, period, cancellationToken);
        if (room is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            LogBookingConflict(logger, roomTypeId, period.CheckIn, period.CheckOut);
            return new CreateBookingResult.NoRoomAvailable(roomType.Name, period.CheckIn, period.CheckOut);
        }

        DateTime createdAt = UtcNowToMicroseconds();
        Booking booking = new()
        {
            GuestId = guestId,
            RoomId = room.Id,
            CheckIn = period.CheckIn,
            CheckOut = period.CheckOut,
            GuestCount = guests,
            TotalPrice = PriceCalculator.Total(roomType.NightlyPrice, period.Nights),
            Status = BookingStatus.Confirmed,
            CreatedAt = createdAt,
        };

        db.Bookings.Add(booking);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ExclusionViolation,
            ConstraintName: ExclusionConstraintName,
        })
        {
            // The rejected insert stays tracked as Added; detaching it keeps a later save in this scope from
            // retrying it. The transaction now holds nothing worth keeping, and rolling it back releases the lock.
            db.Entry(booking).State = EntityState.Detached;
            await transaction.RollbackAsync(cancellationToken);
            LogBookingConflict(logger, roomTypeId, period.CheckIn, period.CheckOut);
            return new CreateBookingResult.NoRoomAvailable(roomType.Name, period.CheckIn, period.CheckOut);
        }

        LogBookingCreated(logger, booking.Id, roomTypeId, period.CheckIn, period.CheckOut);
        return new CreateBookingResult.Created(new BookingResponse(
            booking.Id,
            hotelId,
            hotelName,
            roomTypeId,
            roomType.Name,
            room.RoomNumber,
            period.CheckIn,
            period.CheckOut,
            period.Nights,
            guests,
            booking.TotalPrice,
            BookingStatus.Confirmed,
            createdAt));
    }

    /// <summary>
    /// Lists a guest's own bookings, confirmed and cancelled, newest first.
    /// </summary>
    /// <remarks>
    /// One SQL statement reads the bookings with their room, room type and hotel, ordered by creation instant and then
    /// by id, both descending, which <c>IX_Bookings_GuestId_CreatedAt</c> serves. The nights are computed in memory.
    /// </remarks>
    /// <param name="guestId">The caller's guest id, from the bearer token's <c>sub</c> claim.</param>
    /// <param name="cancellationToken">Cancels the database query.</param>
    /// <returns>The guest's bookings, newest first; an empty list when the guest has none.</returns>
    public async Task<IReadOnlyList<BookingResponse>> ListAsync(int guestId, CancellationToken cancellationToken)
    {
        var rows = await db.Bookings
            .AsNoTracking()
            .Where(b => b.GuestId == guestId)
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id)
            .Select(b => new
            {
                b.Id,
                b.Room.RoomType.HotelId,
                HotelName = b.Room.RoomType.Hotel.Name,
                b.Room.RoomTypeId,
                RoomTypeName = b.Room.RoomType.Name,
                b.Room.RoomNumber,
                b.CheckIn,
                b.CheckOut,
                b.GuestCount,
                b.TotalPrice,
                b.Status,
                b.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => new BookingResponse(
                x.Id,
                x.HotelId,
                x.HotelName,
                x.RoomTypeId,
                x.RoomTypeName,
                x.RoomNumber,
                x.CheckIn,
                x.CheckOut,
                new StayPeriod(x.CheckIn, x.CheckOut).Nights,
                x.GuestCount,
                x.TotalPrice,
                x.Status,
                x.CreatedAt))
            .ToList();
    }

    /// <summary>
    /// Cancels one of the guest's own confirmed bookings, which frees its room for those dates again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checks run in this order, and the first failure decides the outcome: the booking exists, it belongs to the
    /// caller, it is not already cancelled, and its check-in date is not earlier than today (UTC). Ownership is checked
    /// before state, so another guest's request learns nothing about the booking's state.
    /// </para>
    /// <para>
    /// The status changes in one conditional update that only matches a confirmed booking. When it matches nothing, a
    /// concurrent cancellation won, and the result is <see cref="CancelResult.AlreadyCancelled"/>.
    /// </para>
    /// </remarks>
    /// <param name="guestId">The caller's guest id, from the bearer token's <c>sub</c> claim.</param>
    /// <param name="bookingId">The booking's id, from the route.</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <returns>
    /// <see cref="CancelResult.Cancelled"/>; otherwise <see cref="CancelResult.NotFound"/>,
    /// <see cref="CancelResult.Forbidden"/>, <see cref="CancelResult.AlreadyCancelled"/> or
    /// <see cref="CancelResult.CheckInPassed"/>.
    /// </returns>
    public async Task<CancelResult> CancelAsync(int guestId, int bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.GuestId, b.Status, b.CheckIn })
            .FirstOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            return new CancelResult.NotFound();
        }

        if (booking.GuestId != guestId)
        {
            return new CancelResult.Forbidden();
        }

        if (booking.Status == BookingStatus.Cancelled)
        {
            return new CancelResult.AlreadyCancelled();
        }

        if (booking.CheckIn < Today())
        {
            return new CancelResult.CheckInPassed();
        }

        int affected = await db.Bookings
            .Where(b => b.Id == bookingId && b.Status == BookingStatus.Confirmed)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Cancelled), cancellationToken);
        if (affected == 0)
        {
            return new CancelResult.AlreadyCancelled();
        }

        LogBookingCancelled(logger, bookingId);
        return new CancelResult.Cancelled();
    }

    private DateOnly Today()
    {
        return DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
    }

    private DateTime UtcNowToMicroseconds()
    {
        // PostgreSQL stores timestamps to the microsecond and a tick is 100 ns, so the instant is truncated before it
        // is stored: the createdAt of the 201 body then equals the value the booking list reads back.
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMicrosecond), DateTimeKind.Utc);
    }

    [LoggerMessage(
        EventId = 3001,
        EventName = "BookingCreated",
        Level = LogLevel.Information,
        Message = "Booking created. BookingId={BookingId} RoomTypeId={RoomTypeId} CheckIn={CheckIn} CheckOut={CheckOut}")]
    private static partial void LogBookingCreated(
        ILogger logger,
        int bookingId,
        int roomTypeId,
        DateOnly checkIn,
        DateOnly checkOut);

    [LoggerMessage(
        EventId = 3002,
        EventName = "BookingConflict",
        Level = LogLevel.Warning,
        Message = "Booking conflict. RoomTypeId={RoomTypeId} CheckIn={CheckIn} CheckOut={CheckOut}")]
    private static partial void LogBookingConflict(ILogger logger, int roomTypeId, DateOnly checkIn, DateOnly checkOut);

    [LoggerMessage(
        EventId = 3003,
        EventName = "BookingCancelled",
        Level = LogLevel.Information,
        Message = "Booking cancelled. BookingId={BookingId}")]
    private static partial void LogBookingCancelled(ILogger logger, int bookingId);
}