using SmartHotel.Api.Common.Stays;

namespace SmartHotel.Api.Modules.Hotels;

// The response records are this module's own wire contract. HotelQueryService maps the Availability module's
// query results onto them, so another module's types never become part of the Hotels JSON.

/// <summary>
/// The validated input of a hotel search, <c>GET /api/hotels</c>. The controller builds it only after every query
/// parameter has passed its checks.
/// </summary>
/// <param name="NormalizedCity">
/// The requested city, trimmed and folded with <see cref="string.ToLowerInvariant"/>. A hotel matches when
/// <c>lower("City")</c> equals it, so the search ignores letter case.
/// </param>
/// <param name="Period">
/// The requested stay, already validated: 1 to 30 nights and a check-in that is not earlier than the current UTC
/// date. Its check-out day is the exclusive end of the stay and is not occupied.
/// </param>
/// <param name="Guests">
/// The party size, at least 1, and 1 when the query omits <c>guests</c>. Only room types whose maximum number of
/// guests is at least this many qualify a hotel.
/// </param>
public sealed record HotelSearchQuery(string NormalizedCity, StayPeriod Period, int Guests);

/// <summary>
/// The validated input of a hotel detail request, <c>GET /api/hotels/{id}</c>. The controller builds it only after
/// the dates and the guest count have passed their checks, before the hotel is looked up.
/// </summary>
/// <param name="Period">
/// The requested stay, already validated: 1 to 30 nights and a check-in that is not earlier than the current UTC
/// date. Its check-out day is the exclusive end of the stay and is not occupied.
/// </param>
/// <param name="Guests">
/// The party size, at least 1, and 1 when the query omits <c>guests</c>. It is echoed in the response and does
/// not filter the room types or their available-room counts.
/// </param>
public sealed record HotelDetailQuery(StayPeriod Period, int Guests);

/// <summary>
/// One element of the <c>GET /api/hotels</c> array, serialized as
/// <c>{ "id", "name", "city", "address", "lowestNightlyPrice" }</c>. A hotel is listed only when at least one of
/// its room types can host the party and has a free room for the whole stay.
/// </summary>
/// <remarks>
/// MVC's default (Web) JSON options write the members in camelCase, so the record needs no serialization
/// attributes.
/// </remarks>
/// <param name="Id">The hotel's id, the <c>{id}</c> of <c>GET /api/hotels/{id}</c>.</param>
/// <param name="Name">The hotel's display name.</param>
/// <param name="City">
/// The city as stored, such as <c>New York</c>, rather than the normalized search input.
/// </param>
/// <param name="Address">The hotel's street address.</param>
/// <param name="LowestNightlyPrice">
/// The lowest nightly price in USD, a JSON number with two decimals from the room types' <c>numeric(7,2)</c>
/// prices, among the hotel's room types that can host the party and have a free room for the stay. Results are
/// ordered by it, then by name.
/// </param>
public sealed record HotelSummaryResponse(
    int Id,
    string Name,
    string City,
    string Address,
    decimal LowestNightlyPrice);

/// <summary>
/// The body of <c>GET /api/hotels/{id}</c>, serialized as
/// <c>{ id, name, city, address, description, checkIn, checkOut, nights, guests, roomTypes }</c>. It echoes the
/// requested stay and lists every room type of the hotel with its availability and price for that stay.
/// </summary>
/// <remarks>
/// MVC's default (Web) JSON options write the members of this record and of
/// <see cref="RoomTypeAvailabilityResponse"/> in camelCase, and System.Text.Json writes <see cref="DateOnly"/> as
/// <c>yyyy-MM-dd</c>, so neither record needs serialization attributes.
/// </remarks>
/// <param name="Id">The hotel's id.</param>
/// <param name="Name">The hotel's display name.</param>
/// <param name="City">The city as stored, such as <c>New York</c>.</param>
/// <param name="Address">The hotel's street address.</param>
/// <param name="Description">The description shown on the hotel detail page.</param>
/// <param name="CheckIn">
/// The requested check-in day, the first night of the stay, serialized as <c>yyyy-MM-dd</c>.
/// </param>
/// <param name="CheckOut">
/// The requested check-out day, serialized as <c>yyyy-MM-dd</c>. It is the exclusive end of the stay and is not
/// occupied.
/// </param>
/// <param name="Nights">
/// The number of nights from <paramref name="CheckIn"/> to <paramref name="CheckOut"/>, 1 to 30.
/// </param>
/// <param name="Guests">
/// The effective guest count, 1 when the query omitted it. The web app disables booking a room type whose maximum
/// is below it.
/// </param>
/// <param name="RoomTypes">Every room type of the hotel, ordered by nightly price.</param>
public sealed record HotelDetailResponse(
    int Id,
    string Name,
    string City,
    string Address,
    string Description,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Nights,
    int Guests,
    IReadOnlyList<RoomTypeAvailabilityResponse> RoomTypes);

/// <summary>
/// One room type in <see cref="HotelDetailResponse.RoomTypes"/>, serialized as
/// <c>{ id, name, maxGuests, nightlyPrice, availableRooms, stayTotal }</c>.
/// </summary>
/// <param name="Id">The room type's id, the <c>roomTypeId</c> of <c>POST /api/bookings</c>.</param>
/// <param name="Name">The room type's display name, such as <c>Standard</c>.</param>
/// <param name="MaxGuests">The largest party one room of this type can host, at least 1.</param>
/// <param name="NightlyPrice">
/// The price per night in USD, a JSON number with two decimals, stored as <c>numeric(7,2)</c>.
/// </param>
/// <param name="AvailableRooms">
/// The number of rooms of this type with no <c>Confirmed</c> booking that overlaps the stay. It counts physical
/// inventory whatever the requested guest count. For a search of the booked dates, one booking lowers it by exactly one
/// and its cancellation restores it, provided nothing else changed this room type's bookings or rooms in between; for
/// other searched dates the change can be zero, because a stay can overlap two bookings of one room and cancelling one
/// leaves it taken. The web app disables "Book" when it is 0 or when the guest count exceeds
/// <paramref name="MaxGuests"/>.
/// </param>
/// <param name="StayTotal">
/// The price of the whole stay in USD, a JSON number with two decimals: <c>PriceCalculator.Total</c>, which rounds
/// to two places, applied to <paramref name="NightlyPrice"/> and the stay's nights. It is the same function that
/// computes a booking's <c>totalPrice</c>, so the figure shown equals the total recorded on the booking when the
/// room is booked.
/// </param>
public sealed record RoomTypeAvailabilityResponse(
    int Id,
    string Name,
    int MaxGuests,
    decimal NightlyPrice,
    int AvailableRooms,
    decimal StayTotal);