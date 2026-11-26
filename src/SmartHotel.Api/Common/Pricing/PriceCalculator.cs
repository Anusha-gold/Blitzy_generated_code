using SmartHotel.Api.Common.Stays;

namespace SmartHotel.Api.Common.Pricing;

/// <summary>
/// The single server-side price rule: a stay costs its nightly price multiplied by its number of nights.
/// </summary>
/// <remarks>
/// <para>
/// Every amount is in USD. Requests never carry a price: the server computes each total from the nightly price
/// stored for the room type.
/// </para>
/// <para>
/// Both totals a guest meets come from this one function: <c>BookingService</c> stores its result as
/// <c>Bookings.TotalPrice</c>, and <c>HotelQueryService</c> returns it as the hotel detail's <c>stayTotal</c>.
/// The total shown before booking therefore always equals the total that is booked.
/// </para>
/// </remarks>
public static class PriceCalculator
{
    /// <summary>
    /// The highest nightly price, in USD: 99,999.99, the largest value the <c>numeric(7,2)</c> column
    /// <c>RoomTypes.NightlyPrice</c> can hold.
    /// </summary>
    public const decimal MaxNightlyPrice = 99_999.99m;

    /// <summary>
    /// Computes the total price of a stay: <paramref name="nightlyPrice"/> multiplied by <paramref name="nights"/>,
    /// rounded to 2 decimal places with <see cref="MidpointRounding.AwayFromZero"/>.
    /// </summary>
    /// <remarks>
    /// The calculation uses <see cref="decimal"/> throughout. For a price with at most two decimal places the
    /// product is exact and the rounding leaves it unchanged; for a price with more decimal places the rounding
    /// brings the total to whole cents.
    /// </remarks>
    /// <param name="nightlyPrice">
    /// The price of one night, in USD, from 0 to <see cref="MaxNightlyPrice"/> inclusive.
    /// </param>
    /// <param name="nights">
    /// The number of nights in the stay, from 1 to <see cref="StayRules.MaxNights"/> inclusive.
    /// </param>
    /// <returns>
    /// The total price of the stay, in USD, rounded to 2 decimal places. The largest possible total,
    /// <see cref="MaxNightlyPrice"/> for <see cref="StayRules.MaxNights"/> nights, is 2,999,999.70, which fits the
    /// <c>numeric(10,2)</c> column <c>Bookings.TotalPrice</c>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="nights"/> is less than 1 or greater than <see cref="StayRules.MaxNights"/>; or
    /// <paramref name="nightlyPrice"/> is less than 0 or greater than <see cref="MaxNightlyPrice"/>.
    /// </exception>
    public static decimal Total(decimal nightlyPrice, int nights)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nights, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nights, StayRules.MaxNights);
        ArgumentOutOfRangeException.ThrowIfLessThan(nightlyPrice, 0m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nightlyPrice, MaxNightlyPrice);

        return Math.Round(nightlyPrice * nights, 2, MidpointRounding.AwayFromZero);
    }
}