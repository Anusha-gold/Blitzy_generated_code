namespace SmartHotel.Data.Entities;

/// <summary>
/// One guest's reservation of one room for a half-open date range, from <see cref="CheckIn"/> inclusive to
/// <see cref="CheckOut"/> exclusive.
/// </summary>
/// <remarks>
/// <para>
/// At most one <see cref="BookingStatus.Confirmed"/> booking of a room covers any night. The database guarantees
/// it through the exclusion constraint <c>EX_Bookings_Room_NoOverlap</c>, whatever the application does.
/// </para>
/// <para>
/// A booking is confirmed on creation, because payment is simulated. No payment data is stored.
/// </para>
/// </remarks>
public sealed class Booking
{
    /// <summary>
    /// Gets or sets the database-generated identifier. It breaks ties between bookings with the same creation instant
    /// when a guest's bookings are listed newest first.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the guest who owns the booking. Only that guest may list or cancel it.
    /// </summary>
    public int GuestId { get; set; }

    /// <summary>
    /// Gets or sets the guest who owns the booking.
    /// </summary>
    public Guest Guest { get; set; } = null!;

    /// <summary>
    /// Gets or sets the identifier of the room the server assigned. Availability queries correlate bookings with
    /// rooms by this value.
    /// </summary>
    public int RoomId { get; set; }

    /// <summary>
    /// Gets or sets the room the server assigned.
    /// </summary>
    public Room Room { get; set; } = null!;

    /// <summary>
    /// Gets or sets the first night of the stay.
    /// </summary>
    public DateOnly CheckIn { get; set; }

    /// <summary>
    /// Gets or sets the departure date, which is exclusive: the room is free that night, so another stay may start
    /// on it. It is always later than <see cref="CheckIn"/>.
    /// </summary>
    public DateOnly CheckOut { get; set; }

    /// <summary>
    /// Gets or sets the number of guests: at least 1 and, as the API checks on creation, at most the room type's
    /// <see cref="RoomType.MaxGuests"/>.
    /// </summary>
    public int GuestCount { get; set; }

    /// <summary>
    /// Gets or sets the total in USD with two decimal places, computed on the server as the nightly price multiplied
    /// by the number of nights. It is never taken from a request.
    /// </summary>
    public decimal TotalPrice { get; set; }

    /// <summary>
    /// Gets or sets the lifecycle state. Only confirmed bookings block their room's availability.
    /// </summary>
    public BookingStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the creation instant in UTC, by which a guest's bookings are listed newest first. Callers must
    /// assign a finite instant with <see cref="DateTimeKind.Utc"/>, taken from the injected
    /// <see cref="TimeProvider"/>. Npgsql rejects a finite value of any other kind for
    /// <c>timestamp with time zone</c>, but stores <see cref="DateTime.MinValue"/>, which an unassigned property
    /// holds, as <c>-infinity</c> and <see cref="DateTime.MaxValue"/> as <c>infinity</c>, whatever their kind.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}