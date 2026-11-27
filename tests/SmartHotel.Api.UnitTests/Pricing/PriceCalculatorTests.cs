using SmartHotel.Api.Common.Pricing;

namespace SmartHotel.Api.UnitTests.Pricing;

public sealed class PriceCalculatorTests
{
    /// <summary>
    /// Stays as (nightly price, nights, expected total) inside the price bounds (0 to 99,999.99) and the night bounds (1 to 30).
    /// </summary>
    public static TheoryData<decimal, int, decimal> ValidTotals => new()
    {
        { 149.99m, 2, 299.98m },
        { 129.00m, 1, 129.00m },
        { 129.00m, 30, 3_870.00m },
        { 99_999.99m, 30, 2_999_999.70m }, // the largest possible total, which fits numeric(10,2)
        { 0m, 1, 0m },
        { 99_999.99m, 1, 99_999.99m },
    };

    /// <summary>
    /// Products that end exactly on a half cent. Banker's rounding (ToEven) would give 10.00 and 0.00.
    /// </summary>
    public static TheoryData<decimal, int, decimal> MidpointTotals => new()
    {
        { 10.005m, 1, 10.01m },
        { 0.0025m, 2, 0.01m },
    };

    /// <summary>
    /// Products with a fraction of a cent below or above the half cent, which round to the nearest cent. Rounding up
    /// (ToPositiveInfinity) would give 10.01 for the first row, and truncation (ToZero) 10.00 for the second.
    /// </summary>
    public static TheoryData<decimal, int, decimal> NonMidpointTotals => new()
    {
        { 10.004m, 1, 10.00m },
        { 10.006m, 1, 10.01m },
    };

    /// <summary>
    /// Prices outside the 0 to 99,999.99 bound.
    /// </summary>
    public static TheoryData<decimal> OutOfRangePrices =>
    [
        -0.01m,
        100_000.00m,
        decimal.MaxValue, // multiplying it would overflow, so this row also proves the price is checked first
    ];

    [Theory]
    [MemberData(nameof(ValidTotals))]
    public void Total_ValidInput_ReturnsNightlyPriceTimesNights(decimal nightlyPrice, int nights, decimal expected)
    {
        Assert.Equal(expected, PriceCalculator.Total(nightlyPrice, nights));
    }

    [Theory]
    [MemberData(nameof(MidpointTotals))]
    public void Total_MidpointProduct_RoundsAwayFromZero(decimal nightlyPrice, int nights, decimal expected)
    {
        Assert.Equal(expected, PriceCalculator.Total(nightlyPrice, nights));
    }

    [Theory]
    [MemberData(nameof(NonMidpointTotals))]
    public void Total_NonMidpointProduct_RoundsToNearestCent(decimal nightlyPrice, int nights, decimal expected)
    {
        Assert.Equal(expected, PriceCalculator.Total(nightlyPrice, nights));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(-1)]
    public void Total_NightsOutOfRange_Throws(int nights)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PriceCalculator.Total(149.99m, nights));
        Assert.Equal("nights", ex.ParamName);
    }

    [Theory]
    [MemberData(nameof(OutOfRangePrices))]
    public void Total_PriceOutOfRange_Throws(decimal nightlyPrice)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PriceCalculator.Total(nightlyPrice, 2));
        Assert.Equal("nightlyPrice", ex.ParamName);
    }

    [Fact]
    public void Total_SignedZeroPrice_ReturnsZero()
    {
        // Built at run time: the literal -0m folds to positive zero, and a theory row could lose the sign bit.
        decimal signedZero = decimal.Negate(0m);
        Assert.True(decimal.IsNegative(signedZero));

        Assert.Equal(0m, PriceCalculator.Total(signedZero, 2));
    }

    [Fact]
    public void MaxNightlyPrice_Is99999Point99()
    {
        Assert.Equal(99_999.99m, PriceCalculator.MaxNightlyPrice);
    }
}