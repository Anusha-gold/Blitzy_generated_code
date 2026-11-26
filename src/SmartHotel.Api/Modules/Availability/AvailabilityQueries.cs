using Microsoft.EntityFrameworkCore;

using SmartHotel.Api.Common.Stays;
using SmartHotel.Data;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.Modules.Availability;

/// <summary>
/// The shared "which rooms are free" queries: the hotel search and the per-type counts of the Hotels module, and the
/// room allocation of the Bookings module.
/// </summary>
/// <remarks>
/// <para>
/// A stay is the half-open date range [checkIn, checkOut). A confirmed booking blocks a requested stay when
/// <c>existing.CheckIn &lt; requested.CheckOut &amp;&amp; requested.CheckIn &lt; existing.CheckOut</c>; a cancelled
/// booking never blocks. The check-out day is free, so a stay from the 10th to the 12th and a stay from the 12th to
/// the 14th share a room.
/// </para>
/// <para>
/// Every method composes that rule from one place, <see cref="StayRules.ConfirmedOverlap"/>: it filters
/// <see cref="SmartHotelDbContext.Bookings"/> with the predicate and correlates the result with each room by
/// <see cref="Booking.RoomId"/>, which EF Core translates into a correlated <c>NOT EXISTS</c> subquery. The SQL and
/// the unit-tested rule therefore share one predicate, served by the index
/// <c>IX_Bookings_RoomId_CheckIn_CheckOut</c>.
/// </para>
/// <para>
/// These queries decide availability. The database exclusion constraint <c>EX_Bookings_Room_NoOverlap</c> is the
/// guarantee against double booking, whatever the interleaving of concurrent requests.
/// </para>
/// <para>
/// The class is registered scoped and shares the request's <see cref="SmartHotelDbContext"/>, so
/// <see cref="FindFreeRoomAsync"/> runs on the connection, and inside the transaction, in which
/// <c>BookingService</c> holds the room type's row lock. It never opens a transaction, saves changes or reads the
/// clock: the stay arrives already validated.
/// </para>
/// </remarks>
/// <param name="db">The request's database context, shared with the module services that call these queries.</param>
public sealed class AvailabilityQueries(SmartHotelDbContext db)
{
    /// <summary>
    /// A hotel that can host the party for the stay, with the lowest nightly price among its qualifying room types.
    /// </summary>
    /// <param name="HotelId">The hotel's identifier.</param>
    /// <param name="Name">The hotel's display name.</param>
    /// <param name="City">The city the hotel is in, as stored.</param>
    /// <param name="Address">The hotel's street address.</param>
    /// <param name="LowestNightlyPrice">
    /// The lowest nightly price in USD among the hotel's room types that host at least the requested number of guests
    /// and have at least one room free for the whole stay.
    /// </param>
    public sealed record HotelAvailability(
        int HotelId,
        string Name,
        string City,
        string Address,
        decimal LowestNightlyPrice);

    /// <summary>
    /// The number of rooms of one room type that are free for the whole stay.
    /// </summary>
    /// <param name="RoomTypeId">The room type's identifier.</param>
    /// <param name="AvailableRooms">
    /// The rooms of the type with no confirmed booking overlapping the stay; zero when every room is taken. It counts
    /// physical rooms and does not depend on the party size.
    /// </param>
    public sealed record RoomTypeAvailability(int RoomTypeId, int AvailableRooms);

    /// <summary>
    /// Finds the hotels in a city that can host the party for the stay, each with its lowest qualifying nightly price,
    /// in one SQL query.
    /// </summary>
    /// <remarks>
    /// A hotel qualifies when at least one of its room types hosts at least <paramref name="guests"/> guests and has
    /// at least one room free for the whole stay. Only those room types contribute to the lowest price.
    /// </remarks>
    /// <param name="normalizedCity">
    /// The requested city, already trimmed and folded with <see cref="string.ToLowerInvariant"/> by the caller. It is
    /// compared with <c>lower("City")</c>.
    /// </param>
    /// <param name="period">The validated stay; its check-out day is exclusive.</param>
    /// <param name="guests">The validated party size, at least 1.</param>
    /// <param name="cancellationToken">A token that cancels the database query.</param>
    /// <returns>
    /// The qualifying hotels ordered by lowest nightly price, then name, then identifier. The list is empty when the
    /// city is unknown or no hotel qualifies.
    /// </returns>
    public async Task<IReadOnlyList<HotelAvailability>> SearchHotelsAsync(
        string normalizedCity,
        StayPeriod period,
        int guests,
        CancellationToken cancellationToken)
    {
        IQueryable<Booking> blocking = db.Bookings.Where(StayRules.ConfirmedOverlap(period));

        // ToLower translates to lower("City"), the expression IX_Hotels_City_Lower indexes. The StringComparison
        // overload that CA1862 suggests would not translate to SQL. The rows are projected to an anonymous type
        // first, because filtering and ordering on a record constructor would not translate either.
        var rows = await db.Hotels
            .AsNoTracking()
            .Where(h => h.City.ToLower() == normalizedCity)
            .Select(h => new
            {
                h.Id,
                h.Name,
                h.City,
                h.Address,
                LowestNightlyPrice = h.RoomTypes
                    .Where(t => t.MaxGuests >= guests && t.Rooms.Any(r => !blocking.Any(b => b.RoomId == r.Id)))
                    .Min(t => (decimal?)t.NightlyPrice),
            })
            .Where(x => x.LowestNightlyPrice != null)
            .OrderBy(x => x.LowestNightlyPrice)
            .ThenBy(x => x.Name)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        // The query keeps only hotels whose minimum exists, so every materialized price has a value.
        return rows
            .Select(x => new HotelAvailability(x.Id, x.Name, x.City, x.Address, x.LowestNightlyPrice!.Value))
            .ToList();
    }

    /// <summary>
    /// Counts, for every room type of a hotel, the rooms that are free for the whole stay, in one SQL query.
    /// </summary>
    /// <remarks>
    /// The counts are physical inventory and ignore the party size, so one booking lowers its room type's count by
    /// exactly one and cancelling it restores the count.
    /// </remarks>
    /// <param name="hotelId">The hotel's identifier.</param>
    /// <param name="period">The validated stay; its check-out day is exclusive.</param>
    /// <param name="cancellationToken">A token that cancels the database query.</param>
    /// <returns>
    /// One entry per room type of the hotel, zero counts included, ordered by room type identifier. The list is empty
    /// when no hotel has <paramref name="hotelId"/>; the caller decides what an unknown hotel means.
    /// </returns>
    public async Task<IReadOnlyList<RoomTypeAvailability>> CountAvailableByRoomTypeAsync(
        int hotelId,
        StayPeriod period,
        CancellationToken cancellationToken)
    {
        IQueryable<Booking> blocking = db.Bookings.Where(StayRules.ConfirmedOverlap(period));

        return await db.RoomTypes
            .AsNoTracking()
            .Where(t => t.HotelId == hotelId)
            .OrderBy(t => t.Id)
            .Select(t => new RoomTypeAvailability(t.Id, t.Rooms.Count(r => !blocking.Any(b => b.RoomId == r.Id))))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Finds the lowest-numbered room of a room type that is free for the whole stay.
    /// </summary>
    /// <remarks>
    /// <c>BookingService</c> calls this inside its READ COMMITTED transaction, after it has locked the room type's row
    /// <c>FOR UPDATE</c>. Each statement then reads a fresh snapshot, so this query sees every booking committed by
    /// earlier holders of the lock, and concurrent requests are given distinct rooms. The query itself takes no lock.
    /// </remarks>
    /// <param name="roomTypeId">The room type's identifier.</param>
    /// <param name="period">The validated stay; its check-out day is exclusive.</param>
    /// <param name="cancellationToken">A token that cancels the database query.</param>
    /// <returns>
    /// The free room with the lowest room number, not tracked by the context and without its navigations loaded; or
    /// <see langword="null"/> when every room of the type is taken or the room type does not exist.
    /// </returns>
    public async Task<Room?> FindFreeRoomAsync(int roomTypeId, StayPeriod period, CancellationToken cancellationToken)
    {
        IQueryable<Booking> blocking = db.Bookings.Where(StayRules.ConfirmedOverlap(period));

        // Room numbers are ordered as text. Every seeded number has three digits (101 to 410), and digit strings of
        // equal length sort in numeric order under any collation, so this picks the numerically lowest free room.
        return await db.Rooms
            .AsNoTracking()
            .Where(r => r.RoomTypeId == roomTypeId && !blocking.Any(b => b.RoomId == r.Id))
            .OrderBy(r => r.RoomNumber)
            .ThenBy(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}