namespace SmartHotel.Data.Entities;

/// <summary>
/// A hotel in the catalogue. Hotels are inserted by <c>DbSeeder</c> and are never created through the API.
/// </summary>
/// <remarks>
/// There is deliberately no navigation to the hotel's rooms: a room's hotel column is constrained only through
/// its room type, and a direct navigation would add a second, unplanned foreign key to this table.
/// </remarks>
public sealed class Hotel
{
    /// <summary>
    /// Gets or sets the database-generated identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the hotel's display name, at most 200 characters.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the city the hotel is in, at most 100 characters. Searches match it case-insensitively.
    /// </summary>
    public string City { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the street address, at most 300 characters. Seeded addresses are fictional.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description shown on the hotel detail page, at most 2,000 characters.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets the room types this hotel offers. The seed gives every hotel four.
    /// </summary>
    public ICollection<RoomType> RoomTypes { get; } = new List<RoomType>();
}