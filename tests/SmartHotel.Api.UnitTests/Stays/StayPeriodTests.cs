using System.Globalization;

using SmartHotel.Api.Common.Stays;

namespace SmartHotel.Api.UnitTests.Stays;

public sealed class StayPeriodTests
{
    // A fixed "today" keeps every row deterministic: the rule receives the date and never reads a clock.
    private static readonly DateOnly Today = new(2026, 12, 1);

    [Theory]
    [InlineData("2026-12-01", "2026-12-02", 1)] // Check-in today is not in the past.
    [InlineData("2026-12-05", "2026-12-06", 1)]
    [InlineData("2026-12-01", "2026-12-03", 2)]
    [InlineData("2026-12-01", "2026-12-31", 30)] // The 30-night maximum.
    [InlineData("2026-12-05", "2027-01-04", 30)] // The maximum across a year boundary.
    public void TryCreate_ValidStay_ReturnsPeriodWithNights(string checkIn, string checkOut, int expectedNights)
    {
        Assert.True(StayPeriod.TryCreate(checkIn, checkOut, Today, out var period, out var errors));
        Assert.Empty(errors);
        Assert.Equal(expectedNights, period.Nights);
        Assert.Equal(DateOnly.ParseExact(checkIn, "yyyy-MM-dd", CultureInfo.InvariantCulture), period.CheckIn);
        Assert.Equal(DateOnly.ParseExact(checkOut, "yyyy-MM-dd", CultureInfo.InvariantCulture), period.CheckOut);
    }

    // Each row breaks exactly one rule and names the field that rule reports against.
    [Theory]
    [InlineData("2026-12-05", "2026-12-05", "checkOut")] // 0 nights.
    [InlineData("2026-12-05", "2027-01-05", "checkOut")] // 31 nights.
    [InlineData("2026-12-05", "2026-12-04", "checkOut")] // Check-out before check-in.
    [InlineData("2026-11-30", "2026-12-02", "checkIn")] // Check-in yesterday, a 2-night stay.
    [InlineData(null, "2026-12-07", "checkIn")] // Check-in missing.
    [InlineData("2026-12-05", null, "checkOut")] // Check-out missing.
    [InlineData("", "2026-12-07", "checkIn")]
    [InlineData("2026-1-5", "2026-12-07", "checkIn")]
    [InlineData("2026-12-05", "12/07/2026", "checkOut")]
    public void TryCreate_InvalidStay_ReturnsErrorForField(string? checkIn, string? checkOut, string expectedKey)
    {
        Assert.False(StayPeriod.TryCreate(checkIn, checkOut, Today, out var period, out var errors));
        Assert.Equal(default(StayPeriod), period);
        var messages = Assert.Contains(expectedKey, errors);
        Assert.NotEmpty(messages);
    }
}