using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;

using SmartHotel.Api.Common.Http;
using SmartHotel.Api.Common.Stays;

namespace SmartHotel.Api.Modules.Hotels;

/// <summary>
/// The anonymous catalogue endpoints under <c>api/hotels</c>: the hotel search and the hotel detail.
/// </summary>
/// <remarks>
/// <para>
/// Each action binds its query parameters one by one, so every 400 <c>errors</c> key is the request's own camelCase
/// parameter name. The <c>city</c> and <c>guests</c> rules are attributes that <c>[ApiController]</c> enforces before
/// the action runs. The dates bind as raw strings, because MVC's <see cref="DateOnly"/> binder also accepts loose
/// forms such as <c>2026-1-5</c> and <c>12/07/2026</c>. Only <see cref="StayPeriod.TryCreate"/> parses them, as
/// exactly <c>yyyy-MM-dd</c>, and applies the stay rules.
/// </para>
/// <para>
/// Only <c>GET</c> is mapped, so any other method on these routes gets routing's 405 with an <c>Allow</c> header.
/// Every error body is <c>application/problem+json</c>, no problem text written here echoes a query value, and
/// nothing is logged.
/// </para>
/// </remarks>
/// <param name="hotelQueryService">The request-scoped search and detail queries.</param>
/// <param name="timeProvider">
/// The API's clock, read inside each action to get the current UTC calendar date, the day before which a check-in is
/// in the past.
/// </param>
[ApiController]
[Route("api/hotels")]
public sealed class HotelsController(HotelQueryService hotelQueryService, TimeProvider timeProvider) : ControllerBase
{
    private const int DefaultGuests = 1;
    private const string GuestsRangeMessage = "guests must be at least 1.";

    /// <summary>
    /// Searches a city for hotels that can host the party for the whole stay: <c>GET /api/hotels</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// 200 with <c>[{ id, name, city, address, lowestNightlyPrice }]</c>, ordered by the lowest qualifying nightly
    /// price, then name. The city matches regardless of letter case. The list is <c>[]</c> when nothing qualifies,
    /// an unknown city included.
    /// </description></item>
    /// <item><description>
    /// 400 <see cref="ValidationProblemDetails"/>, from MVC before the action runs, keyed <c>city</c> when it is
    /// missing, blank or longer than 100 characters, or <c>guests</c> when it is not an integer or is below 1.
    /// </description></item>
    /// <item><description>
    /// 400 <see cref="ValidationProblemDetails"/> keyed <c>checkIn</c> or <c>checkOut</c> when a date is missing or
    /// is not an exact <c>yyyy-MM-dd</c> calendar date, <c>checkOut</c> is not after <c>checkIn</c>, <c>checkIn</c>
    /// is before today (UTC), or the stay is longer than <see cref="StayRules.MaxNights"/> nights.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="city">
    /// The city to search: required, not blank, at most 100 characters. Surrounding white space is ignored.
    /// </param>
    /// <param name="checkIn">The check-in date, exactly <c>yyyy-MM-dd</c>, today (UTC) or later.</param>
    /// <param name="checkOut">
    /// The check-out date, exactly <c>yyyy-MM-dd</c>. It is the exclusive end of the stay and is not occupied.
    /// </param>
    /// <param name="guests">The party size, at least 1; 1 when omitted.</param>
    /// <param name="cancellationToken">Cancels the database query when the request is aborted.</param>
    /// <returns>The 200 result with the qualifying hotels, or the 400 validation problem.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<HotelSummaryResponse>>> Search(
        [FromQuery]
        [Required(ErrorMessage = "city is required.")]
        [MaxLength(100, ErrorMessage = "city must be at most 100 characters.")]
        string? city,
        [FromQuery] string? checkIn,
        [FromQuery] string? checkOut,
        [FromQuery]
        [Range(1, int.MaxValue, ErrorMessage = GuestsRangeMessage)]
        int? guests,
        CancellationToken cancellationToken)
    {
        // The controller is created before model validation runs, so the clock is read here rather than in the
        // constructor: a request rejected by validation never consults it.
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (!StayPeriod.TryCreate(
            checkIn, checkOut, today, out StayPeriod period, out Dictionary<string, string[]> errors))
        {
            return this.BadRequestProblem(errors);
        }

        // [Required] has already rejected a missing or blank city. This fold is the one the lower("City") index
        // and the availability query's comparison expect.
        string normalizedCity = city!.Trim().ToLowerInvariant();
        IReadOnlyList<HotelSummaryResponse> hotels = await hotelQueryService.SearchAsync(
            new HotelSearchQuery(normalizedCity, period, guests ?? DefaultGuests),
            cancellationToken);

        // Ok is explicit: C# allows no user-defined conversion from an interface type to ActionResult<T>.
        return Ok(hotels);
    }

    /// <summary>
    /// Reads one hotel with every room type, its nightly price, its count of rooms free for the stay and the stay's
    /// total: <c>GET /api/hotels/{id}</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// 200 with <c>{ id, name, city, address, description, checkIn, checkOut, nights, guests, roomTypes }</c>, the
    /// room types ordered by nightly price. Each available-room count is physical inventory and ignores
    /// <paramref name="guests"/>, which is only echoed.
    /// </description></item>
    /// <item><description>
    /// 400 <see cref="ValidationProblemDetails"/> for the same date and <c>guests</c> rules as the search. They are
    /// checked before the hotel is looked up, so invalid input gives 400 even for an unknown id.
    /// </description></item>
    /// <item><description>
    /// 404 problem when no hotel has <paramref name="id"/>. A non-integer id matches no route, and routing answers
    /// it with the same status.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="id">The hotel's id, from the route.</param>
    /// <param name="checkIn">The check-in date, exactly <c>yyyy-MM-dd</c>, today (UTC) or later.</param>
    /// <param name="checkOut">
    /// The check-out date, exactly <c>yyyy-MM-dd</c>. It is the exclusive end of the stay and is not occupied.
    /// </param>
    /// <param name="guests">The party size, at least 1; 1 when omitted.</param>
    /// <param name="cancellationToken">Cancels the database queries when the request is aborted.</param>
    /// <returns>The 200 result with the hotel detail, the 400 validation problem, or the 404 problem.</returns>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<HotelDetailResponse>> GetById(
        int id,
        [FromQuery] string? checkIn,
        [FromQuery] string? checkOut,
        [FromQuery]
        [Range(1, int.MaxValue, ErrorMessage = GuestsRangeMessage)]
        int? guests,
        CancellationToken cancellationToken)
    {
        // Read after model validation for the same reason as in Search.
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (!StayPeriod.TryCreate(
            checkIn, checkOut, today, out StayPeriod period, out Dictionary<string, string[]> errors))
        {
            return this.BadRequestProblem(errors);
        }

        HotelDetailResponse? detail = await hotelQueryService.GetDetailAsync(
            id,
            new HotelDetailQuery(period, guests ?? DefaultGuests),
            cancellationToken);

        return detail is null
            ? this.NotFoundProblem("Hotel not found", "No hotel matches the requested id.")
            : Ok(detail);
    }
}