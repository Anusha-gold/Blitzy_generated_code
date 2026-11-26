namespace SmartHotel.Data.Entities;

/// <summary>
/// One physical, bookable room of a room type. Booking creation assigns the lowest-numbered room of the requested
/// type that is free for the stay.
/// </summary>
/// <remarks>
/// <para>
/// The hotel is constrained only through the composite foreign key <c>(RoomTypeId, HotelId)</c> to the room type's
/// alternate key <c>(Id, HotelId)</c>. There is deliberately no navigation to the hotel, because EF Core would infer
/// a second, unplanned foreign key from it.
/// </para>
/// <para>
/// There is deliberately no collection of bookings either: availability queries correlate bookings by
/// <c>RoomId</c> in a subquery instead of navigating from the room.
/// </para>
/// </remarks>
public sealed class Room
{
    /// <summary>
    /// Gets or sets the database-generated identifier, referenced by each booking's room.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the room type this room belongs to.
    /// </summary>
    public int RoomTypeId { get; set; }

    /// <summary>
    /// Gets or sets a copy of the room type's hotel identifier, kept equal to it by the composite foreign key. It
    /// exists so that the room number can be unique per hotel.
    /// </summary>
    public int HotelId { get; set; }

    /// <summary>
    /// Gets or sets the room type this room belongs to.
    /// </summary>
    public RoomType RoomType { get; set; } = null!;

    /// <summary>
    /// Gets or sets the room's label, unique within its hotel and at most 10 characters. The seed uses the room
    /// type's position as the floor, numbering rooms <c>101</c> to <c>110</c>, <c>201</c> to <c>210</c> and so on.
    /// </summary>
    public string RoomNumber { get; set; } = string.Empty;
}