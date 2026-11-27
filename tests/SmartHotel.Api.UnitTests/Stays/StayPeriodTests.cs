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
    [InlineData("9999-12-30", "9999-12-31", 1)] // The last representable check-out day stays accepted.
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
    [InlineData("2026-12-5", "2026-12-07", "checkIn")] // Unpadded day; read loosely it is a valid 2-night stay.
    [InlineData("2026-12-05", "12/07/2026", "checkOut")]
    public void TryCreate_InvalidStay_ReturnsErrorForField(string? checkIn, string? checkOut, string expectedKey)
    {
        Assert.False(StayPeriod.TryCreate(checkIn, checkOut, Today, out var period, out var errors));
        Assert.Equal(default(StayPeriod), period);
        var messages = Assert.Contains(expectedKey, errors);
        Assert.NotEmpty(messages);
    }

    // Each row breaks several rules at once and lists every field that must report, and no other.
    [Theory]
    [InlineData("2026-12-5", "12/07/2026", "checkIn", "checkOut")] // Both malformed: the second field is still checked.
    [InlineData(null, null, "checkIn", "checkOut")] // Both missing.
    [InlineData("2026-11-30", "12/07/2026", "checkOut")] // Past check-in, malformed check-out: no relational rule runs.
    [InlineData("2026-11-30", "2026-11-29", "checkIn", "checkOut")] // Past check-in and check-out before check-in.
    [InlineData("2026-11-30", "2027-01-01", "checkIn", "checkOut")] // Past check-in and a 32-night stay.
    public void TryCreate_SeveralInvalidFields_ReturnsExactlyTheseErrorKeys(
        string? checkIn,
        string? checkOut,
        params string[] expectedKeys)
    {
        Assert.False(StayPeriod.TryCreate(checkIn, checkOut, Today, out var period, out var errors));
        Assert.Equal(default, period);
        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), errors.Keys.Order(StringComparer.Ordinal));
        Assert.All(errors.Values, Assert.NotEmpty);
    }

    // The next two facts check one stay against two supplied dates, neither of them Today, so the past
    // check must use the date it is given rather than a fixed cutoff.
    [Fact]
    public void TryCreate_CheckInOnSuppliedToday_Succeeds()
    {
        DateOnly checkInDay = new(2026, 12, 5);

        Assert.True(StayPeriod.TryCreate("2026-12-05", "2026-12-07", checkInDay, out var period, out var errors));
        Assert.Empty(errors);
        Assert.Equal(checkInDay, period.CheckIn);
        Assert.Equal(new DateOnly(2026, 12, 7), period.CheckOut);
        Assert.Equal(2, period.Nights);
    }

    [Fact]
    public void TryCreate_CheckInBeforeSuppliedToday_ReturnsOnlyCheckInError()
    {
        DateOnly dayAfterCheckIn = new(2026, 12, 6);

        Assert.False(StayPeriod.TryCreate("2026-12-05", "2026-12-07", dayAfterCheckIn, out var period, out var errors));
        Assert.Equal(default, period);
        var (key, messages) = Assert.Single(errors);
        Assert.Equal("checkIn", key);
        Assert.NotEmpty(messages);
    }
}