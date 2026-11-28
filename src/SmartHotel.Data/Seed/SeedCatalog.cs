using System.Globalization;

namespace SmartHotel.Data.Seed;

/// <summary>
/// The catalogue that <c>DbSeeder</c> inserts on a clean database: 5 hotels in 3 cities, the same 4 room types in
/// every hotel and 10 rooms per room type, which makes 20 room types and 200 rooms in total.
/// </summary>
/// <remarks>
/// <para>
/// Every name, address and description is original, fictional text. Hotel names and cities are contracts: the
/// search matches cities case-insensitively by exact value, and the tests look hotels and room types up by these
/// names. Every string fits its column: names 200, cities 100, addresses 300, descriptions 2,000, room type names
/// 100 and room numbers 10 characters.
/// </para>
/// <para>
/// Prices are in USD. A room type's nightly price is its base price plus <see cref="HotelPriceStep"/> for each
/// hotel before it, so the seeded prices range from 129.00 to 369.00. Room numbers use the room type's position as
/// the floor, so every hotel has rooms 101 to 110, 201 to 210, 301 to 310 and 401 to 410.
/// </para>
/// </remarks>
public static class SeedCatalog
{
    /// <summary>
    /// The number of rooms seeded for every room type in every hotel.
    /// </summary>
    public const int RoomsPerType = 10;

    /// <summary>
    /// The amount in USD added to every base price for each hotel ahead of the current one in <see cref="Hotels"/>.
    /// </summary>
    public const decimal HotelPriceStep = 10.00m;

    // A room's number is its floor (the room type's position) times this value plus its index on that floor.
    private const int FloorNumberMultiplier = 100;

    /// <summary>
    /// Gets the seeded hotels in order, numbered from 1 to 5: two in Seattle, two in New York and one in Chicago.
    /// </summary>
    internal static IReadOnlyList<HotelEntry> Hotels { get; } =
    [
        new(
            1,
            "SmartHotel Seattle Downtown",
            "Seattle",
            "1450 Cedar Lantern Avenue, Seattle, WA 98101",
            "A glass tower a short walk from the downtown shopping streets, with a rooftop lounge that looks out "
            + "over Elliott Bay. Guests can start the day in the ground-floor coffee bar and end it in the heated "
            + "indoor pool."),
        new(
            2,
            "SmartHotel Seattle Waterfront",
            "Seattle",
            "88 Tidewater Quay, Seattle, WA 98121",
            "A converted pier warehouse where every corridor ends in a view of the ferries crossing the Sound. "
            + "Rooms open onto wide timber decks, and bicycles are lent at the front desk for rides along the shore."),
        new(
            3,
            "SmartHotel New York Midtown",
            "New York",
            "312 Meridian Arcade Street, New York, NY 10036",
            "A quiet retreat a few blocks from the theater district, with soundproofed rooms and a library lounge on "
            + "the twelfth floor. The fitness studio stays open around the clock for travelers on any schedule."),
        new(
            4,
            "SmartHotel New York Brooklyn",
            "New York",
            "57 Brickworks Lane, Brooklyn, NY 11201",
            "A former brick warehouse close to the bridge walkways and riverside parks. Its courtyard garden hosts a "
            + "morning bakery counter, and the upper floors look across the river to the Manhattan skyline."),
        new(
            5,
            "SmartHotel Chicago Loop",
            "Chicago",
            "640 Riverlight Plaza, Chicago, IL 60601",
            "A modern hotel beside the river in the heart of the Loop, steps from the elevated train lines and the "
            + "lakefront paths. The top-floor terrace overlooks the routes of the architecture boat tours."),
    ];

    /// <summary>
    /// Gets the four room types every hotel offers, in position order from 1 to 4.
    /// </summary>
    internal static IReadOnlyList<RoomTypeEntry> RoomTypes { get; } =
    [
        new(1, "Standard", 2, 129.00m),
        new(2, "Deluxe", 2, 169.00m),
        new(3, "Family", 4, 219.00m),
        new(4, "Suite", 4, 329.00m),
    ];

    /// <summary>
    /// Computes a room type's nightly price in a given hotel: the base price plus <see cref="HotelPriceStep"/> for
    /// each hotel numbered before it.
    /// </summary>
    /// <param name="hotelNumber">The hotel's <see cref="HotelEntry.Number"/>, from 1 to the number of hotels.</param>
    /// <param name="basePrice">The room type's <see cref="RoomTypeEntry.BasePrice"/> in USD, 0 or more.</param>
    /// <returns>The nightly price in USD, for example 149.00 for a Standard room in hotel 3.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="hotelNumber"/> is outside 1 to the number of hotels, or <paramref name="basePrice"/> is
    /// less than 0.
    /// </exception>
    public static decimal NightlyPrice(int hotelNumber, decimal basePrice)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hotelNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hotelNumber, Hotels.Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(basePrice, 0m);

        return basePrice + ((hotelNumber - 1) * HotelPriceStep);
    }

    /// <summary>
    /// Builds the number of a room: the room type's position is the floor and the index is the room on that floor.
    /// </summary>
    /// <param name="roomTypePosition">The room type's <see cref="RoomTypeEntry.Position"/>, from 1 to the number of
    /// room types.</param>
    /// <param name="index">The room's index within its room type, from 1 to <see cref="RoomsPerType"/>.</param>
    /// <returns>A three-digit room number such as <c>101</c> or <c>410</c>, unique within a hotel.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="roomTypePosition"/> is outside 1 to the number of room types, or <paramref name="index"/> is
    /// outside 1 to <see cref="RoomsPerType"/>.
    /// </exception>
    public static string RoomNumber(int roomTypePosition, int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(roomTypePosition, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(roomTypePosition, RoomTypes.Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, RoomsPerType);

        // Invariant formatting: the containers run in invariant globalization mode, and a room number must never
        // depend on the current culture.
        return ((roomTypePosition * FloorNumberMultiplier) + index).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One seeded hotel.
    /// </summary>
    /// <param name="Number">The hotel's 1-based position in <see cref="Hotels"/>, which sets its price step.</param>
    /// <param name="Name">The display name, at most 200 characters.</param>
    /// <param name="City">The city, at most 100 characters, matched case-insensitively by the search.</param>
    /// <param name="Address">A fictional street address, at most 300 characters.</param>
    /// <param name="Description">An original description of the fictional property, at most 2,000 characters.</param>
    internal sealed record HotelEntry(int Number, string Name, string City, string Address, string Description);

    /// <summary>
    /// One room type offered by every seeded hotel.
    /// </summary>
    /// <param name="Position">The 1-based position in <see cref="RoomTypes"/>, which is also the floor of its
    /// rooms.</param>
    /// <param name="Name">The display name, at most 100 characters.</param>
    /// <param name="MaxGuests">The largest party one room of this type hosts, at least 1.</param>
    /// <param name="BasePrice">The nightly price in USD in hotel 1, before <see cref="NightlyPrice"/> adds the
    /// hotel's step.</param>
    internal sealed record RoomTypeEntry(int Position, string Name, int MaxGuests, decimal BasePrice);
}