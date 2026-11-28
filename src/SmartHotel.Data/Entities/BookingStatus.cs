namespace SmartHotel.Data.Entities;

/// <summary>
/// The lifecycle state of a booking. It is stored as its member name in a <c>varchar(16)</c>
/// column and serialized to JSON as that name.
/// </summary>
public enum BookingStatus
{
    /// <summary>
    /// The booking holds its room. Only confirmed bookings block availability under the half-open
    /// overlap rule, where the check-out day is not occupied, and only they are covered by the
    /// <c>EX_Bookings_Room_NoOverlap</c> exclusion constraint. A booking is confirmed on creation,
    /// because payment is simulated.
    /// </summary>
    Confirmed,

    /// <summary>
    /// The guest cancelled the booking, so its room is free again for those dates.
    /// This state is terminal.
    /// </summary>
    Cancelled
}