using SmartHotel.Api.Common.Stays;

namespace SmartHotel.Api.UnitTests.Stays;

public sealed class IsoDateTests
{
    [Theory]
    [InlineData("2026-12-07", 2026, 12, 7)]
    [InlineData("2028-02-29", 2028, 2, 29)]
    public void TryParse_ExactDate_ReturnsDate(string value, int year, int month, int day)
    {
        Assert.True(IsoDate.TryParse(value, out DateOnly date));
        Assert.Equal(new DateOnly(year, month, day), date);
    }

    [Theory]
    [InlineData("2026-1-5")]
    [InlineData("12/07/2026")]
    [InlineData("2026-12-07T00:00:00")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(" 2026-12-07")]
    [InlineData("2026-12-07 ")]
    // Exact width and ASCII digits, but only the first, then only the second, separator is wrong.
    [InlineData("2026/12-07")]
    [InlineData("2026-12/07")]
    // Exact width and hyphens, but 2026-12-07 written in non-ASCII (Arabic-Indic) digits.
    [InlineData("\u0662\u0660\u0662\u0666-\u0661\u0662-\u0660\u0667")]
    // Exact shape, but no such calendar day: February has 28 days in 2026 and 2027.
    [InlineData("2026-02-30")]
    [InlineData("2027-02-29")]
    public void TryParse_NonconformingValue_ReturnsFalse(string? value)
    {
        Assert.False(IsoDate.TryParse(value, out DateOnly date));
        Assert.Equal(default, date);
    }

    [Fact]
    public void Format_IsExactIsoDate()
    {
        Assert.Equal("yyyy-MM-dd", IsoDate.Format);
    }
}