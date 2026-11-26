using System.Net;
using System.Text.Json;

using Xunit.Sdk;

namespace SmartHotel.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Assertions for the API's error contract: every error response is <c>application/problem+json</c> with
/// non-empty <c>type</c>, <c>title</c>, <c>detail</c> and <c>traceId</c> members, a <c>status</c> member equal to
/// the response code and, on a 400, an <c>errors</c> object.
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
    private const string ErrorsMember = "errors";
    private const string ProblemSubject = "the problem";
    private const string FirstProblemSubject = "the first problem";
    private const string SecondProblemSubject = "the second problem";

    private static readonly string[] RequiredStringMembers = ["type", "title", "detail", "traceId"];

    // traceId is left out: it identifies the request, so it differs between otherwise identical problems.
    private static readonly string[] ComparedStringMembers = ["type", "title", "detail"];

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
    /// For a 400, the exact, case-sensitive key that the <c>errors</c> object must contain, such as
    /// <c>checkIn</c>. <see langword="null"/> for a body-level 400 and for every other status.
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

        // The media type alone is compared, because a charset parameter may follow it.
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.True(
            string.Equals(mediaType, ProblemMediaType, StringComparison.Ordinal),
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
    /// Asserts that two problem documents carry the same <c>type</c>, <c>title</c>, <c>detail</c> and
    /// <c>status</c>. <c>traceId</c> is not compared, because it differs for every request.
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
            string first = RequireNonEmptyString(a, member, FirstProblemSubject);
            string second = RequireNonEmptyString(b, member, SecondProblemSubject);
            Assert.True(
                string.Equals(first, second, StringComparison.Ordinal),
                $"Expected both problems to have the same {member} but the values differ.");
        }

        int firstStatus = RequireStatus(a, FirstProblemSubject);
        int secondStatus = RequireStatus(b, SecondProblemSubject);
        Assert.True(
            firstStatus == secondStatus,
            $"Expected both problems to have the same status but got {firstStatus} and {secondStatus}.");
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

    // TryGetProperty matches names exactly, so a PascalCase member does not satisfy the camelCase contract.
    private static string RequireNonEmptyString(JsonElement problem, string member, string subject)
    {
        try
        {
            bool isPresent = problem.TryGetProperty(member, out JsonElement value);
            Assert.True(
                isPresent && value.ValueKind == JsonValueKind.String,
                $"Expected {subject} to have a string {member} member but it is {DescribeKind(isPresent, value)}.");

            string? text = value.GetString();
            Assert.False(
                string.IsNullOrWhiteSpace(text),
                $"Expected {subject}'s {member} member to be non-empty.");

            return text;
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
            bool isPresent = problem.TryGetProperty(StatusMember, out JsonElement value);
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
            bool isPresent = problem.TryGetProperty(ErrorsMember, out JsonElement errors);
            Assert.True(
                isPresent && errors.ValueKind == JsonValueKind.Object,
                $"Expected the 400 problem to have an {ErrorsMember} object but it is "
                + $"{DescribeKind(isPresent, errors)}.");

            // A null key marks a body-level case (empty, malformed or not an object), where errors existing is
            // enough.
            if (errorKey is null || errors.TryGetProperty(errorKey, out _))
            {
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