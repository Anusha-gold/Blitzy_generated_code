using System.Data;
using System.Globalization;

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
/// events carry booking ids, room type ids and <c>yyyy-MM-dd</c> dates only: never a guest id, an email, a price or an
/// exception message. They are written once the outcome is final. A log provider that fails while writing one loses
/// only its own copy of the event, the failure goes unreported, and the outcome stands.
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
    /// PostgreSQL reports a violation of <c>EX_Bookings_Room_NoOverlap</c> because a writer that did not take the lock
    /// took that room first, the transaction is rolled back and the result is
    /// <see cref="CreateBookingResult.NoRoomAvailable"/>; in the second case another room of the type may still be
    /// free. Any other database error propagates.
    /// </para>
    /// <para>
    /// The transaction runs as one retriable unit of the context's execution strategy. A transient failure inside it,
    /// such as a session the database ended, abandons the attempt and runs the whole unit again from the start: a new
    /// transaction, a new lock and a new free-room query. A commit whose acknowledgement was lost is recognized by its
    /// booking's row. The check that follows the failure looks for it; so does every retry once it holds the room
    /// type's lock, which waits for the failed attempt's transaction to end, so a commit that takes effect only after
    /// that check is found as well. A found booking is the result, and nothing more is inserted, so a retry never
    /// creates a second booking. A transient failure that outlasts the strategy's retries propagates like any other
    /// database error.
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

        // The context's retrying execution strategy rejects a transaction begun outside it, so the transaction runs
        // inside the strategy as one retriable unit. A transient failure anywhere in an attempt, such as a session the
        // database ended, abandons the attempt, whose transaction the server then rolls back unless its commit was
        // already under way, and the next attempt starts again from BEGIN: under a new lock and with a new free-room
        // query, never with a decision taken under a lock it no longer holds.
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();
        CreationAttempt attempt = new();
        CreateBookingResult result = await strategy.ExecuteAsync(
            attempt,
            async (state, token) =>
            {
                // A failed attempt can leave its booking tracked: Added when its save failed, Unchanged when its
                // commit did. Detaching it limits this attempt's save to its own insert.
                if (state.Booking is not null)
                {
                    db.Entry(state.Booking).State = EntityState.Detached;
                    state.Booking = null;
                }

                await using IDbContextTransaction transaction =
                    await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);

                // The row lock gives predictable allocation. Concurrent creators for this room type take turns here,
                // and under READ COMMITTED each one's free-room query then sees every booking committed before it.
                // Among creators that take this lock, each is therefore given a distinct room, and a 409 from the
                // free-room check below means that no room of the type was free in that query's snapshot. The lock is
                // not the guarantee against double booking: EX_Bookings_Room_NoOverlap alone guarantees that, whatever
                // the interleaving. It is also the safety net for a writer that does not take this lock: when such a
                // writer takes the chosen room first, the constraint rejects the insert and the catch below returns
                // 409, even though another room of the type may still be free. A booking row cannot be locked before
                // it exists, so the parent RoomTypes row is locked, and creators for other room types never wait on
                // it. SERIALIZABLE isolation was rejected because its 40001 failures need whole-transaction retries,
                // which make "exactly one success for the last room" harder to keep deterministic.
                // No operator may be composed onto this query: any would wrap the statement in a subquery, and it must
                // reach PostgreSQL exactly as written.
                await db.RoomTypes
                    .FromSql($"SELECT * FROM \"RoomTypes\" WHERE \"Id\" = {roomTypeId} FOR UPDATE")
                    .AsNoTracking()
                    .ToListAsync(token);

                // After a transient failure, the commit of an earlier attempt's saved booking can take effect after
                // the check that followed the failure, while the server was still completing it. That attempt held
                // this lock until its transaction ended, so taking the lock waited for it, and this query's snapshot
                // shows whether the booking was committed. A committed booking is this request's: nothing more is
                // inserted, and rolling back releases the lock.
                if (state.FailedTransiently && state.Saved is BookingResponse earlier)
                {
                    if (await db.Bookings.AsNoTracking().AnyAsync(b => b.Id == earlier.Id, token))
                    {
                        await transaction.RollbackAsync(token);
                        return new CreateBookingResult.Created(earlier);
                    }

                    // Its transaction ended without a commit, so its booking can never appear.
                    state.Saved = null;
                }

                Room? room = await availability.FindFreeRoomAsync(roomTypeId, period, token);
                if (room is null)
                {
                    await transaction.RollbackAsync(token);
                    WriteBookingConflict(roomTypeId, period);
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
                state.Booking = booking;
                try
                {
                    await db.SaveChangesAsync(token);

                    // The insert is saved and its id assigned. Should the commit's acknowledgement be lost, the check
                    // below, or a retry under the lock, finds this booking's row if the commit took effect, and returns
                    // this response.
                    BookingResponse saved = new(
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
                        createdAt);
                    state.Saved = saved;
                    await transaction.CommitAsync(token);
                    return new CreateBookingResult.Created(saved);
                }
                catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.ExclusionViolation,
                    ConstraintName: ExclusionConstraintName,
                })
                {
                    // The rejected insert stays tracked as Added; detaching it keeps a later save in this scope from
                    // retrying it. The transaction now holds nothing worth keeping, and rolling it back releases the
                    // lock. The violation is not transient, so the strategy never retries it.
                    db.Entry(booking).State = EntityState.Detached;
                    state.Booking = null;
                    await transaction.RollbackAsync(token);
                    WriteBookingConflict(roomTypeId, period);
                    return new CreateBookingResult.NoRoomAvailable(roomType.Name, period.CheckIn, period.CheckOut);
                }
            },
            async (state, token) =>
            {
                // Runs after a transient failure, before any retry. Only an attempt whose insert was saved can have
                // committed, and its booking's row exists once that commit has taken effect, whether or not its
                // acknowledgement arrived. A commit the server is still completing is not visible yet; the retry looks
                // for its booking again under the lock.
                state.FailedTransiently = true;
                BookingResponse? saved = state.Saved;
                if (saved is not null
                    && await db.Bookings.AsNoTracking().AnyAsync(b => b.Id == saved.Id, token))
                {
                    return new ExecutionResult<CreateBookingResult>(true, new CreateBookingResult.Created(saved));
                }

                // EF Core discards the result of an unsuccessful check and retries the unit, or rethrows the failure
                // once no retry is left.
                return new ExecutionResult<CreateBookingResult>(false, null!);
            },
            cancellationToken);

        // Logged once the strategy has returned, so a booking whose commit the check or a retry confirmed is logged
        // too, and every created booking is logged exactly once, after its commit.
        if (result is CreateBookingResult.Created created)
        {
            WriteBookingCreated(created.Booking.Id, roomTypeId, period);
        }

        return result;
    }

    /// <summary>
    /// Lists a guest's own bookings, confirmed and cancelled, newest first.
    /// </summary>
    /// <remarks>
    /// One SQL statement reads the bookings with their room, room type and hotel, ordered by creation instant and then
    /// by id, both descending, which <c>IX_Bookings_GuestId_CreatedAt</c> serves. Each row is materialized straight
    /// into its <see cref="BookingResponse"/>, so the result is the only list allocated. The nights are not a column:
    /// EF Core evaluates that part of the projection in memory, from the dates the row read, with
    /// <see cref="StayPeriod.Nights"/>.
    /// </remarks>
    /// <param name="guestId">The caller's guest id, from the bearer token's <c>sub</c> claim.</param>
    /// <param name="cancellationToken">Cancels the database query.</param>
    /// <returns>The guest's bookings, newest first; an empty list when the guest has none.</returns>
    public async Task<IReadOnlyList<BookingResponse>> ListAsync(int guestId, CancellationToken cancellationToken)
    {
        // The projection calls no instance member of this service: EF Core rejects a client projection that captures
        // the instance, because the cached query would keep it alive.
        return await db.Bookings
            .AsNoTracking()
            .Where(b => b.GuestId == guestId)
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id)
            .Select(b => new BookingResponse(
                b.Id,
                b.Room.RoomType.HotelId,
                b.Room.RoomType.Hotel.Name,
                b.Room.RoomTypeId,
                b.Room.RoomType.Name,
                b.Room.RoomNumber,
                b.CheckIn,
                b.CheckOut,
                new StayPeriod(b.CheckIn, b.CheckOut).Nights,
                b.GuestCount,
                b.TotalPrice,
                b.Status,
                b.CreatedAt))
            .ToListAsync(cancellationToken);
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
    /// <para>
    /// The update runs as one retriable unit of the context's execution strategy, so a transient failure, such as a
    /// session the database ended, runs it again. An update whose commit took effect although its acknowledgement was
    /// lost is recognized by the booking's status: once an attempt has failed transiently, a booking found cancelled,
    /// by the check that follows the failure or by a retry whose update matches nothing, gives
    /// <see cref="CancelResult.Cancelled"/>. The only other way to that status, a concurrent cancellation of the same
    /// booking that races the failure, leaves the booking in the same state. A transient failure that outlasts the
    /// strategy's retries propagates like any other database error.
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

        // Left to itself, the context's retrying execution strategy replays the update after a transient failure, and
        // the replay of an update whose commit took effect matches no row, which would report a concurrent
        // cancellation. Run as one retriable unit with a check of its own, the committed update is recognized.
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();
        CancellationAttempt attempt = new();
        bool cancelled = await strategy.ExecuteAsync(
            attempt,
            async (state, token) =>
            {
                int affected = await db.Bookings
                    .Where(b => b.Id == bookingId && b.Status == BookingStatus.Confirmed)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Cancelled), token);

                // Outside a transient failure, matching no row means that a concurrent cancellation won. After one,
                // the booking, which exists and was confirmed, can only have been cancelled since: by an earlier
                // attempt whose commit took effect after the check that followed the failure, or by a concurrent
                // cancellation, which leaves the same final state.
                return affected > 0 || state.FailedTransiently;
            },
            async (state, token) =>
            {
                // Runs after a transient failure, before any retry. A booking's status only ever moves from Confirmed
                // to Cancelled, so the update took effect exactly when the booking is now cancelled, whether or not its
                // acknowledgement arrived. The only other explanation, a concurrent cancellation of the same booking
                // racing the failure, leaves the same final state. EF Core discards the result of an unsuccessful
                // check and retries the unit, or rethrows the failure once no retry is left.
                state.FailedTransiently = true;
                bool isCancelled = await db.Bookings
                    .AsNoTracking()
                    .AnyAsync(b => b.Id == bookingId && b.Status == BookingStatus.Cancelled, token);
                return new ExecutionResult<bool>(isCancelled, isCancelled);
            },
            cancellationToken);

        if (!cancelled)
        {
            return new CancelResult.AlreadyCancelled();
        }

        WriteBookingCancelled(bookingId);
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

    // Each booking event is written through one of the three methods below, so that a failing log provider cannot turn
    // a 201, 204 or 409 into a 500 once the outcome is final. The aggregate logger behind ILogger<T> passes the event
    // to every provider and only then throws their failures as one AggregateException, so that is the only type caught,
    // and the providers that did not fail have already written it; a provider whose IsEnabled throws can instead stop
    // the write for all of them. Each try block holds only the event's own formatting and write: never a database call,
    // whose errors must keep reaching the exception handler.
    private void WriteBookingCreated(int bookingId, int roomTypeId, StayPeriod period)
    {
        try
        {
            LogBookingCreated(logger, bookingId, roomTypeId, FormatDate(period.CheckIn), FormatDate(period.CheckOut));
        }
        catch (AggregateException)
        {
            // The booking is already committed. A failed write loses only the failing providers' copies of the event,
            // and the failure goes unreported because the logger is the channel that failed.
        }
    }

    private void WriteBookingConflict(int roomTypeId, StayPeriod period)
    {
        try
        {
            LogBookingConflict(logger, roomTypeId, FormatDate(period.CheckIn), FormatDate(period.CheckOut));
        }
        catch (AggregateException)
        {
            // The transaction is already rolled back. A failed write loses only the failing providers' copies of the
            // event, and the failure goes unreported because the logger is the channel that failed.
        }
    }

    private void WriteBookingCancelled(int bookingId)
    {
        try
        {
            LogBookingCancelled(logger, bookingId);
        }
        catch (AggregateException)
        {
            // The cancellation is already committed. A failed write loses only the failing providers' copies of the
            // event, and the failure goes unreported because the logger is the channel that failed.
        }
    }

    // The events carry dates in the API's own exact form, the one the 409 detail uses, as their message text and as
    // their structured values; DateOnly's invariant ToString() would write the ambiguous MM/dd/yyyy.
    private static string FormatDate(DateOnly date)
    {
        return date.ToString(IsoDate.Format, CultureInfo.InvariantCulture);
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
        string checkIn,
        string checkOut);

    [LoggerMessage(
        EventId = 3002,
        EventName = "BookingConflict",
        Level = LogLevel.Warning,
        Message = "Booking conflict. RoomTypeId={RoomTypeId} CheckIn={CheckIn} CheckOut={CheckOut}")]
    private static partial void LogBookingConflict(ILogger logger, int roomTypeId, string checkIn, string checkOut);

    [LoggerMessage(
        EventId = 3003,
        EventName = "BookingCancelled",
        Level = LogLevel.Information,
        Message = "Booking cancelled. BookingId={BookingId}")]
    private static partial void LogBookingCancelled(ILogger logger, int bookingId);

    // What the attempts of the retriable creation unit share, with each other and with the commit check that runs after
    // a transient failure: the booking the latest one added, the saved booking whose commit can still take effect, and
    // whether one has failed transiently.
    private sealed class CreationAttempt
    {
        // The booking the latest attempt added, while the context still tracks it.
        public Booking? Booking { get; set; }

        // The confirmed booking as the response describes it, set once an attempt's insert is saved and before its
        // commit. Its row exists only if that commit took effect, possibly after the check that followed the failure,
        // so it is kept until a later attempt, holding the room type's lock, finds that its row does not exist.
        public BookingResponse? Saved { get; set; }

        // Set by the check that follows a transient failure. From then on an earlier attempt's commit can take effect
        // although no attempt has seen it acknowledged.
        public bool FailedTransiently { get; set; }
    }

    // What the attempts of the retriable cancellation unit share: whether one has failed transiently.
    private sealed class CancellationAttempt
    {
        // Set by the check that follows a transient failure. From then on the update can have taken effect although no
        // attempt has seen it match the booking.
        public bool FailedTransiently { get; set; }
    }
}