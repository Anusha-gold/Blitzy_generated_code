using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SmartHotel.Api.Common.Stays;

/// <summary>
/// Parses the API's single accepted date syntax, exactly <c>yyyy-MM-dd</c>.
/// </summary>
/// <remarks>
/// MVC's <see cref="DateOnly"/> binder also accepts loose forms such as <c>12/07/2026</c> and
/// <c>2026-1-5</c>, so every date input, in a query or a body, is bound as a string and accepted
/// only through <see cref="TryParse(string?, out DateOnly)"/>.
/// </remarks>
public static class IsoDate
{
    /// <summary>
    /// The exact date format the API accepts: a four-digit year, a two-digit month and a two-digit
    /// day, separated by hyphens.
    /// </summary>
    public const string Format = "yyyy-MM-dd";

    /// <summary>
    /// Parses <paramref name="value"/> when it is exactly <c>yyyy-MM-dd</c>, written with ASCII
    /// digits, and names a real calendar date. The input is never trimmed or otherwise normalized,
    /// so padding, signs, non-ASCII digits, unpadded parts, other field orders and time suffixes
    /// are all rejected.
    /// </summary>
    /// <param name="value">The raw date text. <see langword="null"/> is rejected.</param>
    /// <param name="date">
    /// The parsed date when the method returns <see langword="true"/>; otherwise
    /// <see langword="default"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="value"/> is an exact <c>yyyy-MM-dd</c> date that
    /// exists in the calendar; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryParse([NotNullWhen(true)] string? value, out DateOnly date)
    {
        date = default;
        if (value is null || value.Length != Format.Length)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool expected = i is 4 or 7 ? c == '-' : char.IsAsciiDigit(c);
            if (!expected)
            {
                return false;
            }
        }

        // Exactness rests on the shape check above, not on TryParseExact's own digit and whitespace
        // handling; from here it only decides whether the date exists (for example, not 2026-02-30).
        return DateOnly.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}