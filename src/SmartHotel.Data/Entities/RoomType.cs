namespace SmartHotel.Data.Entities;

/// <summary>
/// A bookable category of rooms within one hotel, such as Standard or Suite, with its capacity and nightly price.
/// </summary>
/// <remarks>
/// <para>
/// <c>(Id, HotelId)</c> is an alternate key and the principal of every room's composite foreign key, which keeps
/// each room's hotel equal to its room type's hotel.
/// </para>
/// <para>
/// Booking creation locks this row <c>FOR UPDATE</c>, so concurrent requests for the same room type allocate rooms
/// one at a time while requests for other room types do not wait.
/// </para>
/// </remarks>
public sealed class RoomType
{
    /// <summary>
    /// Gets or sets the database-generated identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the owning hotel. It is part of the alternate key the rooms reference, so it
    /// must never change after insert.
    /// </summary>
    public int HotelId { get; set; }

    /// <summary>
    /// Gets or sets the owning hotel.
    /// </summary>
    public Hotel Hotel { get; set; } = null!;

    /// <summary>
    /// Gets or sets the display name, at most 100 characters. The seed uses Standard, Deluxe, Family and Suite.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the largest party one room of this type can host, at least 1.
    /// </summary>
    public int MaxGuests { get; set; }

    /// <summary>
    /// Gets or sets the price per night in USD, with two decimal places, from 0 to at most 99,999.99.
    /// </summary>
    public decimal NightlyPrice { get; set; }

    /// <summary>
    /// Gets the physical rooms of this type. The seed gives every room type ten.
    /// </summary>
    public ICollection<Room> Rooms { get; } = new List<Room>();
}