using Microsoft.EntityFrameworkCore;

using SmartHotel.Api.Common.Pricing;
using SmartHotel.Api.Modules.Availability;
using SmartHotel.Data;

namespace SmartHotel.Api.Modules.Hotels;

/// <summary>
/// Answers the two anonymous catalogue reads: the hotel search, <c>GET /api/hotels</c>, and the hotel detail with the
/// availability and stay price of every room type, <c>GET /api/hotels/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both reads receive a stay that the controller has already validated, so this service neither validates input nor
/// reads the clock. The free-room rule itself lives in <see cref="AvailabilityQueries"/>; this service maps its results
/// onto the Hotels contracts, so the Availability module's records never reach the Hotels JSON.
/// </para>
/// <para>
/// The search issues at most one SQL query, and none when the city cannot match any stored hotel, for example one
/// containing U+0000. The detail issues two: the hotel with its room types, then the per-type counts. Neither writes,
/// opens a transaction or tracks entities.
/// </para>
/// <para>
/// Registered scoped, beside the request's <see cref="SmartHotelDbContext"/> and <see cref="AvailabilityQueries"/>.
/// It logs nothing, so no query value ever reaches a log.
/// </para>
/// </remarks>
/// <param name="db">The request's database context, shared with <paramref name="availability"/>.</param>
/// <param name="availability">The shared free-room queries of the Availability module.</param>
public sealed class HotelQueryService(SmartHotelDbContext db, AvailabilityQueries availability)
{
    /// <summary>
    /// Finds the hotels in a city that can host the party for the whole stay, each with its lowest qualifying nightly
    /// price.
    /// </summary>
    /// <remarks>
    /// A hotel is listed only when at least one of its room types hosts at least <see cref="HotelSearchQuery.Guests"/>
    /// guests and has a room free for the stay, and only those room types contribute to the lowest price. The order
    /// of <see cref="AvailabilityQueries.SearchHotelsAsync"/> is kept: lowest nightly price, then name.
    /// </remarks>
    /// <param name="query">The validated search: the normalized city, the stay and the party size.</param>
    /// <param name="cancellationToken">A token that cancels the database query.</param>
    /// <returns>
    /// The qualifying hotels, ordered by lowest nightly price, then name. The list is empty when nothing qualifies,
    /// including when no hotel is in the city.
    /// </returns>
    public async Task<IReadOnlyList<HotelSummaryResponse>> SearchAsync(
        HotelSearchQuery query,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AvailabilityQueries.HotelAvailability> hotels = await availability.SearchHotelsAsync(
            query.NormalizedCity,
            query.Period,
            query.Guests,
            cancellationToken);

        return hotels
            .Select(h => new HotelSummaryResponse(h.HotelId, h.Name, h.City, h.Address, h.LowestNightlyPrice))
            .ToList();
    }

    /// <summary>
    /// Reads one hotel with every room type it offers, each with its count of rooms free for the whole stay and the
    /// price of that stay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The available-room counts are physical inventory and ignore <see cref="HotelDetailQuery.Guests"/>: one booking
    /// lowers its room type's count by exactly one and cancelling it restores the count. Room types too small for the
    /// party are still listed; the guest count is only echoed, and the web app disables booking them.
    /// </para>
    /// <para>
    /// Each stay total comes from <see cref="PriceCalculator.Total"/>, the function that also computes a booking's
    /// total price, so the figure shown equals the total booked.
    /// </para>
    /// </remarks>
    /// <param name="id">The hotel's identifier, from the route.</param>
    /// <param name="query">The validated stay and the effective guest count.</param>
    /// <param name="cancellationToken">A token that cancels the database queries.</param>
    /// <returns>
    /// The hotel detail with its room types ordered by nightly price, then identifier; or <see langword="null"/> when
    /// no hotel has <paramref name="id"/>, which the controller answers with 404.
    /// </returns>
    public async Task<HotelDetailResponse?> GetDetailAsync(
        int id,
        HotelDetailQuery query,
        CancellationToken cancellationToken)
    {
        var hotel = await db.Hotels
            .AsNoTracking()
            .Where(h => h.Id == id)
            .Select(h => new
            {
                h.Id,
                h.Name,
                h.City,
                h.Address,
                h.Description,
                RoomTypes = h.RoomTypes
                    .Select(t => new { t.Id, t.Name, t.MaxGuests, t.NightlyPrice })
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (hotel is null)
        {
            return null;
        }

        // Awaited only after the lookup, never concurrently with it: both queries run on the request's DbContext,
        // which does not support parallel operations.
        IReadOnlyList<AvailabilityQueries.RoomTypeAvailability> counts =
            await availability.CountAvailableByRoomTypeAsync(id, query.Period, cancellationToken);

        // The counts come from a separate statement, so a room type missing from them is reported with no free room.
        Dictionary<int, int> availableByRoomType = counts.ToDictionary(c => c.RoomTypeId, c => c.AvailableRooms);

        int nights = query.Period.Nights;
        List<RoomTypeAvailabilityResponse> roomTypes = hotel.RoomTypes
            .OrderBy(t => t.NightlyPrice)
            .ThenBy(t => t.Id)
            .Select(t => new RoomTypeAvailabilityResponse(
                t.Id,
                t.Name,
                t.MaxGuests,
                t.NightlyPrice,
                availableByRoomType.GetValueOrDefault(t.Id),
                PriceCalculator.Total(t.NightlyPrice, nights)))
            .ToList();

        return new HotelDetailResponse(
            hotel.Id,
            hotel.Name,
            hotel.City,
            hotel.Address,
            hotel.Description,
            query.Period.CheckIn,
            query.Period.CheckOut,
            nights,
            query.Guests,
            roomTypes);
    }
}