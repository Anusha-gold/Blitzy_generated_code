using System.Linq.Expressions;

using SmartHotel.Data.Entities;

namespace SmartHotel.Api.Common.Stays;

/// <summary>
/// The rules every stay obeys: the longest stay that can be searched or booked, the overlap rule that decides
/// whether an existing booking blocks a requested stay, and the check-in rule that decides whether a booked stay's
/// check-in date has passed, after which the booking can no longer be cancelled.
/// </summary>
/// <remarks>
/// A stay is the half-open date range [checkIn, checkOut): the guest occupies the room on every night from the
/// check-in day up to, but not including, the check-out day. The check-out day is free, so a stay from the 10th to
/// the 12th and a stay from the 12th to the 14th do not overlap and can share a room.
/// </remarks>
public static class StayRules
{
    /// <summary>
    /// The longest stay, in nights. A stay that can be searched or booked has 1 to 30 nights.
    /// </summary>
    /// <remarks>
    /// This is the only declaration of the maximum. Stay validation (<see cref="StayPeriod.TryCreate"/>) and the
    /// price calculation (<c>PriceCalculator.Total</c>) both read it, so they cannot disagree.
    /// </remarks>
    public const int MaxNights = 30;

    /// <summary>
    /// Determines whether an existing stay and a requested stay share at least one night.
    /// </summary>
    /// <remarks>
    /// Both stays are half-open ranges [checkIn, checkOut), and two such ranges overlap exactly when each starts
    /// before the other ends. The check-out day is free: when one stay's check-out day is the other's check-in day,
    /// the stays do not overlap. Only dates are compared; whether a booking counts at all is decided by its status,
    /// which <see cref="ConfirmedOverlap"/> adds.
    /// </remarks>
    /// <param name="existingCheckIn">The existing stay's check-in day, its first occupied night.</param>
    /// <param name="existingCheckOut">The existing stay's check-out day, the exclusive end of that stay.</param>
    /// <param name="requestedCheckIn">The requested stay's check-in day, its first occupied night.</param>
    /// <param name="requestedCheckOut">The requested stay's check-out day, the exclusive end of that stay.</param>
    /// <returns>
    /// <see langword="true"/> when the two stays share at least one night; otherwise <see langword="false"/>.
    /// </returns>
    public static bool Overlaps(
        DateOnly existingCheckIn,
        DateOnly existingCheckOut,
        DateOnly requestedCheckIn,
        DateOnly requestedCheckOut)
    {
        return existingCheckIn < requestedCheckOut && requestedCheckIn < existingCheckOut;
    }

    /// <summary>
    /// Builds the predicate that selects the bookings blocking a requested stay: the
    /// <see cref="BookingStatus.Confirmed"/> bookings whose dates overlap <paramref name="period"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only confirmed bookings block a stay. For a <see cref="BookingStatus.Cancelled"/> booking the predicate is
    /// <see langword="false"/>, whatever its dates.
    /// </para>
    /// <para>
    /// The date test is the one <see cref="Overlaps"/> applies, with the booking as the existing stay and
    /// <paramref name="period"/> as the requested one. Both are half-open ranges [checkIn, checkOut), and the
    /// check-out day is free, so a booking that ends on the requested check-in day, or starts on the requested
    /// check-out day, does not block it.
    /// </para>
    /// <para>
    /// The expression is EF Core-translatable: it holds only member accesses, comparisons and two captured
    /// <see cref="DateOnly"/> values, which become query parameters. <c>AvailabilityQueries</c> composes it as
    /// <c>db.Bookings.Where(StayRules.ConfirmedOverlap(period))</c> and correlates that by <c>RoomId</c> into a
    /// <c>NOT EXISTS</c> subquery, so the SQL and this rule share one predicate. The parameters carry the same finite
    /// dates as <paramref name="period"/>, never <c>infinity</c>, so a requested check-out of 9999-12-31 stays
    /// half-open against a booking that starts that day.
    /// </para>
    /// <para>
    /// The database enforces the same rule through the exclusion constraint <c>EX_Bookings_Room_NoOverlap</c>,
    /// created by the <c>InitialCreate</c> migration:
    /// </para>
    /// <code>
    /// EXCLUDE USING gist ("RoomId" WITH =, daterange("CheckIn", "CheckOut", '[)') WITH &amp;&amp;)
    ///     WHERE ("Status" = 'Confirmed')
    /// </code>
    /// </remarks>
    /// <param name="period">The requested stay; its <see cref="StayPeriod.CheckOut"/> is exclusive.</param>
    /// <returns>
    /// An expression that is <see langword="true"/> for a booking that is confirmed and overlaps
    /// <paramref name="period"/>; otherwise <see langword="false"/>.
    /// </returns>
    public static Expression<Func<Booking, bool>> ConfirmedOverlap(StayPeriod period)
    {
        // Plain DateOnly locals are captured, so EF Core parameterizes the two dates instead of member accesses
        // on a captured struct.
        DateOnly checkIn = period.CheckIn;
        DateOnly checkOut = period.CheckOut;

        // The comparison restates Overlaps rather than calling it: EF Core cannot translate a call to an
        // application method into SQL, so the predicate must remain a plain expression tree.
        return b => b.Status == BookingStatus.Confirmed && b.CheckIn < checkOut && checkIn < b.CheckOut;
    }

    /// <summary>
    /// Determines whether a booked stay's check-in date has passed, after which the booking can no longer be cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Today" is the current UTC calendar date, whatever the hotel's city. A check-in date has passed only when it is
    /// earlier than today, so a stay that starts today can be cancelled until the end of that day (UTC), and from the
    /// first instant of the next UTC day it cannot. <see cref="StayPeriod.TryCreate"/> applies the same boundary to a
    /// new stay, whose check-in may be today but not earlier.
    /// </para>
    /// <para>
    /// The caller reads the clock and passes the date in, so the rule itself reads no clock and gives the same answer
    /// for the same two dates.
    /// </para>
    /// </remarks>
    /// <param name="checkIn">The booked stay's check-in day, its first occupied night.</param>
    /// <param name="today">The current UTC calendar date.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="checkIn"/> is earlier than <paramref name="today"/>; otherwise
    /// <see langword="false"/>, including when the stay starts today.
    /// </returns>
    public static bool HasCheckInPassed(DateOnly checkIn, DateOnly today)
    {
        return checkIn < today;
    }
}