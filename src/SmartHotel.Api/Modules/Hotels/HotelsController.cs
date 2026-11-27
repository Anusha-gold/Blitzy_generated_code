using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

using SmartHotel.Api.Common.Http;
using SmartHotel.Api.Common.Stays;

namespace SmartHotel.Api.Modules.Hotels;

/// <summary>
/// The anonymous catalogue endpoints under <c>api/hotels</c>: the hotel search and the hotel detail.
/// </summary>
/// <remarks>
/// <para>
/// Each action binds its query parameters one by one, so every 400 <c>errors</c> key is the request's own camelCase
/// parameter name. The <c>city</c> and <c>guests</c> rules run in model binding and validation, which
/// <c>[ApiController]</c> turns into a 400 before the action runs. <c>guests</c> binds through its own binder, because
/// MVC's binder for <see cref="Nullable{T}"/> turns a supplied blank value into <see langword="null"/>, which the
/// action could not tell from an omitted one. The dates bind as raw strings, because MVC's <see cref="DateOnly"/>
/// binder also accepts loose forms such as <c>2026-1-5</c> and <c>12/07/2026</c>. Only
/// <see cref="StayPeriod.TryCreate"/> parses them, as exactly <c>yyyy-MM-dd</c>, and applies the stay rules.
/// </para>
/// <para>
/// Only <c>GET</c> is mapped, so any other method on these routes, a plain <c>OPTIONS</c> included, gets routing's
/// 405 with an <c>Allow</c> header. A CORS preflight, an <c>OPTIONS</c> request that carries <c>Origin</c> and
/// <c>Access-Control-Request-Method</c>, never reaches that 405: the CORS middleware answers it with a bodyless 204,
/// whatever the origin or the requested method, and adds <c>Access-Control-Allow-*</c> headers only for the allowed
/// origin. Every error body is <c>application/problem+json</c>, no problem text written here echoes a query value,
/// and nothing is logged.
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
    private const string GuestsIntegerMessage = "guests must be an integer of at least 1.";

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
    /// missing, blank or longer than 100 characters, or <c>guests</c> when it is supplied but is blank, is not an
    /// integer or is below 1.
    /// </description></item>
    /// <item><description>
    /// 400 <see cref="ValidationProblemDetails"/> keyed <c>checkIn</c> or <c>checkOut</c> when a date is missing or
    /// is not an exact <c>yyyy-MM-dd</c> calendar date, <c>checkOut</c> is not after <c>checkIn</c>, <c>checkIn</c>
    /// is before today (UTC), or the stay is longer than <see cref="StayRules.MaxNights"/> nights.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="city">
    /// The city to search: required, not blank, at most 100 characters. Surrounding white space is ignored. The limit
    /// counts Unicode scalar values, the characters PostgreSQL counts for <c>varchar(100)</c>, so a character outside
    /// the Basic Multilingual Plane counts once.
    /// </param>
    /// <param name="checkIn">The check-in date, exactly <c>yyyy-MM-dd</c>, today (UTC) or later.</param>
    /// <param name="checkOut">
    /// The check-out date, exactly <c>yyyy-MM-dd</c>. It is the exclusive end of the stay and is not occupied.
    /// </param>
    /// <param name="guests">
    /// The party size, an integer of at least 1, or <see langword="null"/> when the parameter is omitted, which
    /// means 1. A supplied blank value is rejected, not defaulted.
    /// </param>
    /// <param name="cancellationToken">Cancels the database query when the request is aborted.</param>
    /// <returns>The 200 result with the qualifying hotels, or the 400 validation problem.</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<HotelSummaryResponse>>> Search(
        [FromQuery]
        [Required(ErrorMessage = "city is required.")]
        [MaxCharacters(100, ErrorMessage = "city must be at most 100 characters.")]
        string? city,
        [FromQuery] string? checkIn,
        [FromQuery] string? checkOut,
        [FromQueryGuests]
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

        // [Required] has already rejected a missing or blank city. Only the surrounding white space is removed here:
        // the availability query folds the city with the database's lower(), the function the lower("City") index is
        // built on, so the requested and the stored city are folded by the same rules.
        string normalizedCity = city!.Trim();
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
    /// <param name="guests">
    /// The party size, an integer of at least 1, or <see langword="null"/> when the parameter is omitted, which
    /// means 1. A supplied blank value is rejected, not defaulted.
    /// </param>
    /// <param name="cancellationToken">Cancels the database queries when the request is aborted.</param>
    /// <returns>The 200 result with the hotel detail, the 400 validation problem, or the 404 problem.</returns>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<HotelDetailResponse>> GetById(
        int id,
        [FromQuery] string? checkIn,
        [FromQuery] string? checkOut,
        [FromQueryGuests]
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

    /// <summary>
    /// Binds an optional <c>guests</c> query parameter through <see cref="OptionalGuestsBinder"/>, reading the query
    /// string only.
    /// </summary>
    /// <remarks>
    /// A <see cref="ModelBinderAttribute"/> that names a binder type takes the greedy
    /// <see cref="BindingSource.Custom"/> source, which reads every value provider. Setting
    /// <see cref="BindingSource.Query"/> keeps the query string the only source, as <c>[FromQuery]</c> does, so the
    /// value provider the binder receives is the query string's.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Parameter)]
    private sealed class FromQueryGuestsAttribute : ModelBinderAttribute
    {
        public FromQueryGuestsAttribute()
            : base(typeof(OptionalGuestsBinder))
        {
            BindingSource = BindingSource.Query;
        }
    }

    /// <summary>
    /// Binds an optional party size, telling an omitted parameter from a supplied value.
    /// </summary>
    /// <remarks>
    /// An omitted parameter binds nothing, so the parameter stays <see langword="null"/> and the action applies
    /// the default. A supplied value binds only when, with its surrounding Unicode white space ignored, it is an
    /// optional <c>+</c> or <c>-</c> followed by one or more ASCII digits <c>0</c>–<c>9</c> and nothing else, and
    /// it fits an <see cref="int"/>. Any other value, a blank or white-space-only one included, adds the fixed
    /// <see cref="GuestsIntegerMessage"/> under the parameter's name, and <c>[ApiController]</c> answers 400 before
    /// the action runs. That also rejects forms the framework's parsers accept: the hexadecimal <c>0x2</c>, which the
    /// type converter behind MVC's binder reads, and digits followed by NUL characters, such as <c>2%00</c>, which
    /// <c>int.TryParse</c> reads as <c>2</c>. Every integer that fits binds, 0 and negative ones included, so
    /// <see cref="RangeAttribute"/> still rejects one below 1. As with MVC's own binder, only the first value counts
    /// when the parameter is repeated.
    /// </remarks>
    private sealed class OptionalGuestsBinder : IModelBinder
    {
        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            ArgumentNullException.ThrowIfNull(bindingContext);

            // MVC falls back to the empty model name when the query string has no such key, and the query string
            // provider returns None for that name.
            ValueProviderResult result = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            if (result == ValueProviderResult.None)
            {
                return Task.CompletedTask;
            }

            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, result);

            // Trim removes the surrounding Unicode white space, as the type converter behind MVC's binder does. The
            // syntax is checked before parsing because int.TryParse also ignores trailing NUL characters; once it
            // holds, the parse fails only on an overflow. An empty result fails the check. The message never echoes
            // the value.
            string value = (result.FirstValue ?? string.Empty).Trim();
            if (!IsSignedAsciiInteger(value)
                || !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int guests))
            {
                bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, GuestsIntegerMessage);
                return Task.CompletedTask;
            }

            bindingContext.Result = ModelBindingResult.Success(guests);
            return Task.CompletedTask;
        }

        // True when the value is an optional leading '+' or '-' followed by one or more ASCII digits '0' to '9', and
        // nothing else.
        private static bool IsSignedAsciiInteger(ReadOnlySpan<char> value)
        {
            if (!value.IsEmpty && value[0] is '+' or '-')
            {
                value = value[1..];
            }

            return !value.IsEmpty && !value.ContainsAnyExceptInRange('0', '9');
        }
    }

    /// <summary>
    /// Validates that a string holds at most a given number of Unicode scalar values, the characters PostgreSQL
    /// counts for <c>varchar(n)</c>. <see langword="null"/> is valid, because <see cref="RequiredAttribute"/>
    /// reports it.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxLengthAttribute"/> counts UTF-16 code units, so it counts a character outside the Basic
    /// Multilingual Plane twice and rejects text the column can store. The value is measured as supplied, before the
    /// action trims it.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Parameter)]
    private sealed class MaxCharactersAttribute : ValidationAttribute
    {
        private readonly int _maximum;

        public MaxCharactersAttribute(int maximum)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximum);
            _maximum = maximum;
        }

        public override bool IsValid(object? value)
        {
            return value switch
            {
                null => true,
                string text => IsWithinMaximum(text),
                _ => throw new InvalidOperationException(
                    $"{nameof(MaxCharactersAttribute)} validates only strings, not {value.GetType().Name}."),
            };
        }

        // Each scalar value takes one or two UTF-16 code units, so only a string of _maximum + 1 to 2 * _maximum code
        // units needs counting, and the count stops as soon as it passes the limit. An unpaired surrogate counts as
        // one character.
        private bool IsWithinMaximum(string text)
        {
            if (text.Length <= _maximum)
            {
                return true;
            }

            if (text.Length > 2L * _maximum)
            {
                return false;
            }

            int scalarValues = 0;
            foreach (Rune rune in text.EnumerateRunes())
            {
                if (++scalarValues > _maximum)
                {
                    return false;
                }
            }

            return true;
        }
    }
}