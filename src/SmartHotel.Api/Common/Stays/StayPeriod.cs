using System.Globalization;

namespace SmartHotel.Api.Common.Stays;

/// <summary>
/// A hotel stay over the half-open date range [<paramref name="CheckIn"/>, <paramref name="CheckOut"/>):
/// the guest occupies the room on every night from the check-in day up to, but not including, the
/// check-out day.
/// </summary>
/// <remarks>
/// The positional constructor does not validate, so a period read back from a stored booking can be
/// built directly to read <see cref="Nights"/>. Request input enters only through
/// <see cref="TryCreate"/>, which enforces the date syntax, a stay of 1 to
/// <see cref="StayRules.MaxNights"/> nights and a check-in that is not in the past.
/// </remarks>
/// <param name="CheckIn">The check-in day, the first occupied night.</param>
/// <param name="CheckOut">
/// The check-out day, the exclusive end of the stay. It is not occupied, so another stay can start on it.
/// </param>
public readonly record struct StayPeriod(DateOnly CheckIn, DateOnly CheckOut)
{
    private const string CheckInKey = "checkIn";
    private const string CheckOutKey = "checkOut";

    // No message names its own field: the error key identifies the date, and a client shows its own label for that
    // field beside the message. Both dates therefore share the syntax messages.
    private const string DateRequiredMessage = "A date is required.";
    private const string DateFormatMessage = "The date must be in the format " + IsoDate.Format + ".";
    private const string CheckOutNotAfterCheckInMessage = "The date must be after the check-in date.";
    private const string CheckInInPastMessage = "The date cannot be in the past.";

    // StayRules.MaxNights is the single home of the 30-night maximum, shared with PriceCalculator;
    // the message is built from it rather than from a local copy, so the rule and its text cannot drift.
    private static readonly string MaxNightsMessage =
        string.Create(CultureInfo.InvariantCulture, $"A stay can be at most {StayRules.MaxNights} nights.");

    /// <summary>
    /// Gets the number of nights in the stay: the days from <see cref="CheckIn"/> to
    /// <see cref="CheckOut"/>, with the check-out day not counted.
    /// </summary>
    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;

    /// <summary>
    /// Validates a requested stay given as raw request text and, when every rule holds, creates it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Errors are keyed by the request field names <c>checkIn</c> and <c>checkOut</c>, the same keys
    /// model validation uses, so the dictionary can go straight into a 400 validation problem.
    /// </para>
    /// <para>
    /// Syntax is checked first, for both fields: each must be present and exactly
    /// <c>yyyy-MM-dd</c> (<see cref="IsoDate.TryParse"/>). If either fails, the relational rules are
    /// not evaluated, because they need both dates. Otherwise every relational rule is evaluated and
    /// every failure is reported: <c>checkOut</c> must be after <c>checkIn</c>; <c>checkIn</c> must not
    /// be earlier than <paramref name="today"/>; and the stay can be at most
    /// <see cref="StayRules.MaxNights"/> nights.
    /// </para>
    /// <para>
    /// Every message is a fixed English sentence and never contains any part of the input. None names its own
    /// field, because the key already identifies it.
    /// </para>
    /// </remarks>
    /// <param name="checkIn">
    /// The requested check-in date as the client sent it; <see langword="null"/> when absent.
    /// </param>
    /// <param name="checkOut">
    /// The requested check-out date as the client sent it; <see langword="null"/> when absent.
    /// </param>
    /// <param name="today">
    /// The caller's current UTC calendar date. A check-in earlier than this day is in the past; a
    /// check-in on this day is valid.
    /// </param>
    /// <param name="period">
    /// The validated stay when the method returns <see langword="true"/>; otherwise
    /// <see langword="default"/>.
    /// </param>
    /// <param name="errors">
    /// The validation messages per field key, compared ordinally. Empty, never <see langword="null"/>,
    /// when the method returns <see langword="true"/>; otherwise it holds at least one entry.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when both dates are valid and the stay satisfies every rule; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool TryCreate(
        string? checkIn,
        string? checkOut,
        DateOnly today,
        out StayPeriod period,
        out Dictionary<string, string[]> errors)
    {
        period = default;
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        bool checkInParsed = TryParseField(checkIn, CheckInKey, errors, out DateOnly parsedIn);
        bool checkOutParsed = TryParseField(checkOut, CheckOutKey, errors, out DateOnly parsedOut);
        if (!checkInParsed || !checkOutParsed)
        {
            return false;
        }

        StayPeriod candidate = new(parsedIn, parsedOut);
        if (candidate.Nights < 1)
        {
            AddError(errors, CheckOutKey, CheckOutNotAfterCheckInMessage);
        }

        if (parsedIn < today)
        {
            AddError(errors, CheckInKey, CheckInInPastMessage);
        }

        if (candidate.Nights > StayRules.MaxNights)
        {
            AddError(errors, CheckOutKey, MaxNightsMessage);
        }

        if (errors.Count > 0)
        {
            return false;
        }

        period = candidate;
        return true;
    }

    private static bool TryParseField(
        string? value,
        string key,
        Dictionary<string, string[]> errors,
        out DateOnly date)
    {
        if (string.IsNullOrEmpty(value))
        {
            date = default;
            AddError(errors, key, DateRequiredMessage);
            return false;
        }

        if (!IsoDate.TryParse(value, out date))
        {
            AddError(errors, key, DateFormatMessage);
            return false;
        }

        return true;
    }

    private static void AddError(Dictionary<string, string[]> errors, string key, string message)
    {
        errors[key] = errors.TryGetValue(key, out string[]? existing) ? [.. existing, message] : [message];
    }
}