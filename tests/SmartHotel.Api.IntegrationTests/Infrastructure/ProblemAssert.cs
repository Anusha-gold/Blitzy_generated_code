using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.Json;

using Xunit.Sdk;

namespace SmartHotel.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Assertions for the API's error contract: every error response is <c>application/problem+json</c> with
/// non-empty <c>type</c>, <c>title</c>, <c>detail</c> and <c>traceId</c> members, a <c>status</c> member equal to
/// the response code and, on a 400, an <c>errors</c> object that maps every key to an array of message strings.
/// Each of these members must occur exactly once, so a later duplicate cannot mask an invalid value.
/// </summary>
/// <remarks>
/// Failure messages carry metadata only: status codes, JSON value kinds, the fixed member names, counts and fixed
/// reasons. They never quote the body, member values, keys taken from the response or supplied by the caller,
/// headers or exception text, because a response can carry a bearer token or an email address and test output is
/// logged.
/// </remarks>
public static class ProblemAssert
{
    private const string ProblemMediaType = "application/problem+json";
    private const string BearerScheme = "Bearer";
    private const string StatusMember = "status";
    private const string TraceIdMember = "traceId";
    private const string ErrorsMember = "errors";
    private const string ProblemSubject = "the problem";
    private const string FirstProblemSubject = "the first problem";
    private const string SecondProblemSubject = "the second problem";

    private static readonly string[] RequiredStringMembers = ["type", "title", "detail", "traceId"];

    // traceId is left out: it identifies the request, so it differs between otherwise identical problems.
    private static readonly string[] ComparedStringMembers = ["type", "title", "detail"];

    // The members the problem contract fixes, and the only member names a failure message may contain.
    private static readonly string[] ContractMembers = ["type", "title", "status", "detail", "traceId", "errors"];

    /// <summary>
    /// Asserts that <paramref name="response"/> is a problem response with the <paramref name="expected"/> status
    /// and returns its body.
    /// </summary>
    /// <param name="response">
    /// The response to check. Its body is read only after its status and media type match. It is not
    /// disposed; the caller owns it.
    /// </param>
    /// <param name="expected">The status code the response and its <c>status</c> member must carry.</param>
    /// <param name="errorKey">
    /// For a 400, the exact, case-sensitive key, such as <c>checkIn</c>, that the <c>errors</c> object must contain
    /// once, mapped to at least one message with none of them blank. <see langword="null"/> for a body-level 400,
    /// which names no field and so also accepts an empty <c>errors</c> object, and for every other status.
    /// </param>
    /// <param name="cancellationToken">Cancels reading the response body.</param>
    /// <returns>The problem document's root object, valid independently of any parsed document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="errorKey"/> is set while <paramref name="expected"/> is not 400.
    /// </exception>
    public static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string? errorKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        // Only a 400 carries errors, so a key for any other status would make the case pass without checking it.
        if (errorKey is not null && expected != HttpStatusCode.BadRequest)
        {
            throw new ArgumentException(
                $"An error key applies only to a 400 problem, not to {(int)expected}.",
                nameof(errorKey));
        }

        Assert.True(
            response.StatusCode == expected,
            $"Expected status {(int)expected} but got {(int)response.StatusCode}.");

        // The media type alone is compared, because a charset parameter may follow it. HTTP media types are
        // case-insensitive (RFC 9110, section 8.3.1), so letter case is ignored, but the whole type must match.
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.True(
            string.Equals(mediaType, ProblemMediaType, StringComparison.OrdinalIgnoreCase),
            mediaType is null
                ? $"Expected Content-Type {ProblemMediaType} but the response has no valid Content-Type."
                : $"Expected Content-Type {ProblemMediaType} but the response has a different media type.");

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception.InnerException is ArgumentException)
        {
            // HttpContent rejects an unknown charset this way, and the inner exception quotes the header value.
            throw FailException.ForFailure(
                "Expected the problem body to be readable as text but the Content-Type declares an unsupported "
                + "character set.");
        }

        JsonElement root = ParseObject(body);

        foreach (string member in RequiredStringMembers)
        {
            RequireNonEmptyString(root, member, ProblemSubject);
        }

        int status = RequireStatus(root, ProblemSubject);
        Assert.True(
            status == (int)expected,
            $"Expected the status member to be {(int)expected} but it is {status}.");

        if (expected == HttpStatusCode.BadRequest)
        {
            AssertErrors(root, errorKey);
        }

        return root;
    }

    /// <summary>
    /// Asserts that two problem documents are identical apart from their top-level <c>traceId</c>, which differs
    /// for every request. Each must be an object with non-empty string <c>type</c>, <c>title</c> and <c>detail</c>
    /// members and an integer <c>status</c>, hold each contract member at most once and repeat no member name within
    /// any of its objects. Every other member must then match, a nested <c>traceId</c> included: objects have the
    /// same member names, in any order, with equal values; arrays match item by item in order; strings match
    /// ordinally; numbers match by value; and <c>true</c>, <c>false</c> and <c>null</c> match their own kind.
    /// </summary>
    /// <param name="a">The first problem document, as returned by <see cref="AssertProblemAsync"/>.</param>
    /// <param name="b">The second problem document, as returned by <see cref="AssertProblemAsync"/>.</param>
    public static void AssertSameProblem(JsonElement a, JsonElement b)
    {
        Assert.True(
            a.ValueKind == JsonValueKind.Object,
            $"Expected the first problem to be a JSON object but it is {a.ValueKind}.");
        Assert.True(
            b.ValueKind == JsonValueKind.Object,
            $"Expected the second problem to be a JSON object but it is {b.ValueKind}.");

        foreach (string member in ComparedStringMembers)
        {
            RequireNonEmptyString(a, member, FirstProblemSubject);
            RequireNonEmptyString(b, member, SecondProblemSubject);
        }

        int firstStatus = RequireStatus(a, FirstProblemSubject);
        int secondStatus = RequireStatus(b, SecondProblemSubject);
        Assert.True(
            firstStatus == secondStatus,
            $"Expected both problems to have the same status but got {firstStatus} and {secondStatus}.");

        Dictionary<string, JsonElement> first = ReadComparedMembers(a, FirstProblemSubject);
        Dictionary<string, JsonElement> second = ReadComparedMembers(b, SecondProblemSubject);

        // traceId identifies the request, so it is the one member that differs between otherwise identical problems.
        first.Remove(TraceIdMember);
        second.Remove(TraceIdMember);

        foreach (string member in ContractMembers)
        {
            bool inFirst = first.Remove(member, out JsonElement firstValue);
            bool inSecond = second.Remove(member, out JsonElement secondValue);
            Assert.True(
                inFirst == inSecond,
                $"Expected both problems to be identical apart from {TraceIdMember} but {member} is present only in "
                + $"{(inFirst ? FirstProblemSubject : SecondProblemSubject)}.");
            Assert.True(
                !inFirst || JsonValuesEqual(firstValue, secondValue),
                $"Expected both problems to be identical apart from {TraceIdMember} but their {member} members "
                + "differ.");
        }

        // What remains lies outside the contract, so its names come from the response and only counts are reported.
        int onlyInFirst = first.Keys.Count(name => !second.ContainsKey(name));
        int onlyInSecond = second.Keys.Count(name => !first.ContainsKey(name));
        Assert.True(
            onlyInFirst == 0 && onlyInSecond == 0,
            $"Expected both problems to be identical apart from {TraceIdMember} but {onlyInFirst} member(s) outside "
            + $"the problem contract exist only in {FirstProblemSubject} and {onlyInSecond} only in "
            + $"{SecondProblemSubject}.");

        int differing = first.Count(entry => !JsonValuesEqual(entry.Value, second[entry.Key]));
        Assert.True(
            differing == 0,
            $"Expected both problems to be identical apart from {TraceIdMember} but {differing} member(s) outside "
            + "the problem contract differ between them.");
    }

    /// <summary>
    /// Asserts that <paramref name="response"/> carries a <c>WWW-Authenticate</c> challenge for the
    /// <c>Bearer</c> scheme, which shows that writing the problem body kept the authentication challenge.
    /// </summary>
    /// <param name="response">The 401 response to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    public static void AssertBearerChallenge(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        bool hasBearer = response.Headers.WwwAuthenticate.Any(
            static challenge => string.Equals(challenge.Scheme, BearerScheme, StringComparison.Ordinal));
        int challengeCount = response.Headers.WwwAuthenticate.Count;
        Assert.True(
            hasBearer,
            $"Expected a WWW-Authenticate challenge for the {BearerScheme} scheme on the {(int)response.StatusCode} "
            + $"response but found {challengeCount} challenge(s), none for that scheme.");
    }

    private static JsonElement ParseObject(string body)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw FailException.ForFailure(
                string.IsNullOrWhiteSpace(body)
                    ? "Expected the body to be a JSON problem document but it is empty or only whitespace."
                    : "Expected the body to be a JSON problem document but it is not valid JSON.");
        }

        Assert.True(
            root.ValueKind == JsonValueKind.Object,
            $"Expected the problem body to be a JSON object but it is {root.ValueKind}.");

        return root;
    }

    private static void RequireNonEmptyString(JsonElement problem, string member, string subject)
    {
        try
        {
            bool isPresent = TryGetSingleMember(problem, member, subject, out JsonElement value);
            Assert.True(
                isPresent && value.ValueKind == JsonValueKind.String,
                $"Expected {subject} to have a string {member} member but it is {DescribeKind(isPresent, value)}.");

            Assert.False(
                string.IsNullOrWhiteSpace(value.GetString()),
                $"Expected {subject}'s {member} member to be non-empty.");
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            throw Malformed(subject);
        }
    }

    private static int RequireStatus(JsonElement problem, string subject)
    {
        try
        {
            bool isPresent = TryGetSingleMember(problem, StatusMember, subject, out JsonElement value);
            Assert.True(
                isPresent && value.ValueKind == JsonValueKind.Number,
                $"Expected {subject} to have a numeric {StatusMember} member but it is "
                + $"{DescribeKind(isPresent, value)}.");
            Assert.True(
                value.TryGetInt32(out int status),
                $"Expected {subject}'s {StatusMember} member to be an integer.");

            return status;
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            throw Malformed(subject);
        }
    }

    private static void AssertErrors(JsonElement problem, string? errorKey)
    {
        try
        {
            bool isPresent = TryGetSingleMember(problem, ErrorsMember, ProblemSubject, out JsonElement errors);
            Assert.True(
                isPresent && errors.ValueKind == JsonValueKind.Object,
                $"Expected the 400 problem to have an {ErrorsMember} object but it is "
                + $"{DescribeKind(isPresent, errors)}.");

            // Every value must be an array of message strings, the field-message shape the web app shows the guest.
            int keyMatches = 0;
            JsonElement messages = default;
            foreach (JsonProperty entry in errors.EnumerateObject())
            {
                bool isCaseKey = errorKey is not null && entry.NameEquals(errorKey);
                string owner = isCaseKey ? "the case's key" : "one key";
                Assert.True(
                    entry.Value.ValueKind == JsonValueKind.Array,
                    $"Expected every {ErrorsMember} value to be an array of message strings but the value of {owner} "
                    + $"is {entry.Value.ValueKind}.");

                int nonStringCount = entry.Value.EnumerateArray()
                    .Count(static message => message.ValueKind != JsonValueKind.String);
                Assert.True(
                    nonStringCount == 0,
                    $"Expected every {ErrorsMember} message to be a string but the array of {owner} holds "
                    + $"{nonStringCount} other value(s).");

                if (isCaseKey)
                {
                    keyMatches++;
                    messages = entry.Value;
                }
            }

            // A null key marks a body-level case (empty, malformed or not an object), which names no field, so an
            // errors object of that shape is enough, an empty one included.
            if (errorKey is null)
            {
                return;
            }

            Assert.True(
                keyMatches <= 1,
                $"Expected {ErrorsMember} to contain the case's key (case-sensitive) once but it occurs {keyMatches} "
                + "times.");

            if (keyMatches == 1)
            {
                int messageCount = messages.GetArrayLength();
                int blankCount = messages.EnumerateArray()
                    .Count(static message => string.IsNullOrWhiteSpace(message.GetString()));
                Assert.True(
                    messageCount > 0,
                    $"Expected the case's key in {ErrorsMember} to map to at least one message but its array is "
                    + "empty.");
                Assert.True(
                    blankCount == 0,
                    $"Expected every message for the case's key in {ErrorsMember} to be non-blank but {blankCount} of "
                    + $"its {messageCount} are blank.");

                return;
            }

            // Neither the response's keys nor the case's key is printed, so only the key count and whether a
            // case-insensitive match exists are reported.
            int keyCount = 0;
            bool hasCaseVariant = false;
            foreach (JsonProperty entry in errors.EnumerateObject())
            {
                keyCount++;
                hasCaseVariant |= string.Equals(entry.Name, errorKey, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Fail(
                $"Expected {ErrorsMember} to contain the case's key (case-sensitive), but that key is missing from "
                + $"its {keyCount} key(s), "
                + (hasCaseVariant
                    ? "at least one of which differs from it only in letter case."
                    : "none of which differs from it only in letter case."));
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            throw Malformed(ProblemSubject);
        }
    }

    // Returns a compared problem's members by decoded name, once each contract member is known to occur at most once
    // and every string and member name in the document decodes, with no object repeating a name.
    private static Dictionary<string, JsonElement> ReadComparedMembers(JsonElement problem, string subject)
    {
        try
        {
            foreach (string member in ContractMembers)
            {
                TryGetSingleMember(problem, member, subject, out _);
            }

            RequireDecodableAndUnique(problem, subject);

            Dictionary<string, JsonElement> members = new(StringComparer.Ordinal);
            foreach (JsonProperty property in problem.EnumerateObject())
            {
                members.Add(property.Name, property.Value);
            }

            return members;
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            throw Malformed(subject);
        }
    }

    // Decoding every string and member name up front keeps the comparison from meeting malformed text. An object
    // that repeats a member name is rejected, because which of its values counts would be ambiguous.
    private static void RequireDecodableAndUnique(JsonElement element, string subject)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Assert.True(
                    names.Add(property.Name),
                    $"Expected every object in {subject} to have distinct member names but one repeats a name, so "
                    + "the comparison would be ambiguous.");
                RequireDecodableAndUnique(property.Value, subject);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RequireDecodableAndUnique(item, subject);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            _ = element.GetString();
        }
    }

    // JSON equality for documents that passed RequireDecodableAndUnique.
    private static bool JsonValuesEqual(JsonElement first, JsonElement second)
    {
        return first.ValueKind == second.ValueKind
            && first.ValueKind switch
            {
                JsonValueKind.Object => ObjectsEqual(first, second),
                JsonValueKind.Array => ArraysEqual(first, second),
                JsonValueKind.String => string.Equals(first.GetString(), second.GetString(), StringComparison.Ordinal),
                JsonValueKind.Number => string.Equals(
                    CanonicalNumber(first),
                    CanonicalNumber(second),
                    StringComparison.Ordinal),
                JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
                _ => false,
            };
    }

    // Member order carries no meaning in a JSON object, so names are matched ordinally, whatever their position.
    private static bool ObjectsEqual(JsonElement first, JsonElement second)
    {
        Dictionary<string, JsonElement> secondMembers = new(StringComparer.Ordinal);
        foreach (JsonProperty property in second.EnumerateObject())
        {
            secondMembers.Add(property.Name, property.Value);
        }

        int firstCount = 0;
        foreach (JsonProperty property in first.EnumerateObject())
        {
            firstCount++;
            if (!secondMembers.TryGetValue(property.Name, out JsonElement secondValue)
                || !JsonValuesEqual(property.Value, secondValue))
            {
                return false;
            }
        }

        return firstCount == secondMembers.Count;
    }

    private static bool ArraysEqual(JsonElement first, JsonElement second)
    {
        return first.GetArrayLength() == second.GetArrayLength()
            && first.EnumerateArray()
                .Zip(second.EnumerateArray())
                .All(static pair => JsonValuesEqual(pair.First, pair.Second));
    }

    // A JSON number's value is its sign with its significant digits at their scale, so 1, 1.0, 10e-1 and 0.1E1 are
    // one value and -0 is 0. Exact digits are compared because decimal and double round, and the exponent is a
    // BigInteger because JSON does not bound it. The raw text always follows the JSON number grammar.
    private static string CanonicalNumber(JsonElement number)
    {
        string text = number.GetRawText();
        bool isNegative = text.StartsWith('-');
        string magnitude = isNegative ? text[1..] : text;
        int exponentStart = magnitude.IndexOfAny(['e', 'E']);
        string mantissa = exponentStart < 0 ? magnitude : magnitude[..exponentStart];
        BigInteger exponent = exponentStart < 0
            ? BigInteger.Zero
            : BigInteger.Parse(
                magnitude[(exponentStart + 1)..],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture);

        int pointIndex = mantissa.IndexOf('.');
        string digits = mantissa;
        if (pointIndex >= 0)
        {
            digits = string.Concat(mantissa.AsSpan(0, pointIndex), mantissa.AsSpan(pointIndex + 1));
            exponent -= mantissa.Length - pointIndex - 1;
        }

        string significant = digits.TrimStart('0');
        if (significant.Length == 0)
        {
            return "0";
        }

        string trimmed = significant.TrimEnd('0');
        exponent += significant.Length - trimmed.Length;

        return (isNegative ? "-" : string.Empty) + trimmed + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    // JsonElement.TryGetProperty returns the last of several members that share a name, so a later duplicate could
    // hide an invalid first value. Every member is counted instead, by its decoded name compared ordinally, which
    // also keeps a PascalCase member from satisfying the camelCase contract. The caller handles undecodable names.
    private static bool TryGetSingleMember(JsonElement problem, string member, string subject, out JsonElement value)
    {
        int count = 0;
        value = default;
        foreach (JsonProperty property in problem.EnumerateObject())
        {
            if (property.NameEquals(member))
            {
                count++;
                value = property.Value;
            }
        }

        Assert.True(count <= 1, $"Expected {subject} to have exactly one {member} member but it has {count}.");

        return count == 1;
    }

    private static string DescribeKind(bool isPresent, JsonElement value)
    {
        return isPresent ? value.ValueKind.ToString() : "missing";
    }

    // System.Text.Json validates \u escapes only when it decodes a string or member name, and its error text quotes
    // the offending code unit, so the failure names the subject alone and never carries that exception.
    private static FailException Malformed(string subject)
    {
        return FailException.ForFailure(
            $"Expected {subject} to hold only decodable JSON text but a string or member name in it is malformed.");
    }
}