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
/// Failure messages quote the response body, which is a problem document and carries no secrets. Request headers,
/// which may carry a bearer token, are never quoted.
/// </remarks>
public static class ProblemAssert
{
    private const string ProblemMediaType = "application/problem+json";
    private const string BearerScheme = "Bearer";
    private const string StatusMember = "status";
    private const string ErrorsMember = "errors";

    private static readonly string[] RequiredStringMembers = ["type", "title", "detail", "traceId"];

    // traceId is left out: it identifies the request, so it differs between otherwise identical problems.
    private static readonly string[] ComparedStringMembers = ["type", "title", "detail"];

    /// <summary>
    /// Asserts that <paramref name="response"/> is a problem response with the <paramref name="expected"/> status
    /// and returns its body.
    /// </summary>
    /// <param name="response">The response to check. It is read but not disposed; the caller owns it.</param>
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

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.True(
            response.StatusCode == expected,
            $"Expected status {(int)expected} but got {(int)response.StatusCode}. Body: {body}");

        // The media type alone is compared, because a charset parameter may follow it.
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.True(
            string.Equals(mediaType, ProblemMediaType, StringComparison.Ordinal),
            $"Expected Content-Type {ProblemMediaType} but got {mediaType ?? "no Content-Type"}. Body: {body}");

        JsonElement root = ParseObject(body);

        foreach (string member in RequiredStringMembers)
        {
            RequireNonEmptyString(root, member, body);
        }

        int status = RequireStatus(root, body);
        Assert.True(
            status == (int)expected,
            $"Expected the status member to be {(int)expected} but it is {status}. Body: {body}");

        if (expected == HttpStatusCode.BadRequest)
        {
            AssertErrors(root, errorKey, body);
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

        string firstBody = a.GetRawText();
        string secondBody = b.GetRawText();

        foreach (string member in ComparedStringMembers)
        {
            string first = RequireNonEmptyString(a, member, firstBody);
            string second = RequireNonEmptyString(b, member, secondBody);
            Assert.True(
                string.Equals(first, second, StringComparison.Ordinal),
                $"Expected both problems to have the same {member} but got \"{first}\" and \"{second}\".");
        }

        int firstStatus = RequireStatus(a, firstBody);
        int secondStatus = RequireStatus(b, secondBody);
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
        string schemes = string.Join(
            ", ",
            response.Headers.WwwAuthenticate.Select(static challenge => challenge.Scheme));
        Assert.True(
            hasBearer,
            $"Expected a WWW-Authenticate challenge for the {BearerScheme} scheme on the {(int)response.StatusCode} "
            + $"response but found [{schemes}].");
    }

    private static JsonElement ParseObject(string body)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw FailException.ForFailure(
                $"Expected the body to be a JSON problem document but it does not parse: {exception.Message} "
                + $"Body: {body}");
        }

        Assert.True(
            root.ValueKind == JsonValueKind.Object,
            $"Expected the problem body to be a JSON object but it is {root.ValueKind}. Body: {body}");

        return root;
    }

    // TryGetProperty matches names exactly, so a PascalCase member does not satisfy the camelCase contract.
    private static string RequireNonEmptyString(JsonElement problem, string member, string body)
    {
        bool isString = problem.TryGetProperty(member, out JsonElement value)
            && value.ValueKind == JsonValueKind.String;
        Assert.True(isString, $"Expected the problem to have a string {member} member. Body: {body}");

        string? text = value.GetString();
        Assert.False(
            string.IsNullOrWhiteSpace(text),
            $"Expected the problem's {member} member to be non-empty. Body: {body}");

        return text;
    }

    private static int RequireStatus(JsonElement problem, string body)
    {
        bool isNumber = problem.TryGetProperty(StatusMember, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number;
        Assert.True(isNumber, $"Expected the problem to have a numeric {StatusMember} member. Body: {body}");
        Assert.True(
            value.TryGetInt32(out int status),
            $"Expected the problem's {StatusMember} member to be an integer. Body: {body}");

        return status;
    }

    private static void AssertErrors(JsonElement problem, string? errorKey, string body)
    {
        bool isObject = problem.TryGetProperty(ErrorsMember, out JsonElement errors)
            && errors.ValueKind == JsonValueKind.Object;
        Assert.True(isObject, $"Expected the 400 problem to have an {ErrorsMember} object. Body: {body}");

        // A null key marks a body-level case (empty, malformed or not an object), where errors existing is enough.
        if (errorKey is null || errors.TryGetProperty(errorKey, out _))
        {
            return;
        }

        string presentKeys = string.Join(", ", errors.EnumerateObject().Select(static entry => entry.Name));
        Assert.Fail(
            $"Expected {ErrorsMember} to contain the key {errorKey} (case-sensitive) but found [{presentKeys}]. "
            + $"Body: {body}");
    }
}