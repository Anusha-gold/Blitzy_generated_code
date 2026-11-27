using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.IdentityModel.JsonWebTokens;

using SmartHotel.Api.IntegrationTests.Infrastructure;

using Xunit.Sdk;

namespace SmartHotel.Api.IntegrationTests.Auth;

public sealed class LoginTests : IClassFixture<SmartHotelApiFactory>
{
    private const string LoginRoute = "api/auth/login";
    private const string JsonMediaType = "application/json";
    private const string ExpiresAtFormat = "yyyy-MM-ddTHH:mm:ssZ";
    private const int TokenLifetimeSeconds = 3600;

    // Only rules out a wrong clock source; the exact token times are checked against each other.
    private const int ClockToleranceSeconds = 120;

    // Fixed, non-secret values for throwaway guests in this class's own test database.
    private const string ValidPassword = "Passw0rd!x";
    private const string WrongPassword = "WrongPassw0rd!";
    private const string FullName = "Test Guest";

    // The single title and detail the API returns for every credential failure.
    private const string InvalidCredentialsTitle = "Invalid credentials";
    private const string InvalidCredentialsDetail = "Email or password is incorrect.";

    private readonly SmartHotelApiFactory _factory;

    public LoginTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_WrongCredentials_Returns401WithIdenticalContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();
        (_, string email) = await RegisterGuestAsync(client, ct);
        string unknownEmail = $"unknown+{Guid.NewGuid():N}@example.test";

        JsonElement wrongPassword = await AssertUnauthorizedAsync(
            client.LoginAsync(new { email, password = WrongPassword }, ct),
            ct);
        JsonElement unknownAccount = await AssertUnauthorizedAsync(
            client.LoginAsync(new { email = unknownEmail, password = ValidPassword }, ct),
            ct);
        JsonElement blankFields = await AssertUnauthorizedAsync(
            client.LoginAsync(new { email = string.Empty, password = string.Empty }, ct),
            ct);

        // Sent as raw text, so the explicit JSON nulls on the wire do not depend on the serializer's null handling.
        using StringContent nullContent = new("{\"email\":null,\"password\":null}", Encoding.UTF8, JsonMediaType);
        JsonElement nullFields = await AssertUnauthorizedAsync(client.Http.PostAsync(LoginRoute, nullContent, ct), ct);

        // Equality is transitive, so comparing the first problem with each of the others covers every pair. Only
        // traceId may differ, so no response reveals whether the account exists or which field was wrong.
        ProblemAssert.AssertSameProblem(wrongPassword, unknownAccount);
        ProblemAssert.AssertSameProblem(wrongPassword, blankFields);
        ProblemAssert.AssertSameProblem(wrongPassword, nullFields);

        // Compared in memory with fixed messages, so a regressed title or detail never reaches the test output.
        string? title = wrongPassword.GetProperty("title").GetString();
        string? detail = wrongPassword.GetProperty("detail").GetString();
        Assert.True(
            string.Equals(InvalidCredentialsTitle, title, StringComparison.Ordinal),
            "Expected the 401 problem's title to be the fixed credential-failure title.");
        Assert.True(
            string.Equals(InvalidCredentialsDetail, detail, StringComparison.Ordinal),
            "Expected the 401 problem's detail to be the fixed credential-failure detail.");
    }

    [Theory]
    [InlineData("empty-body", "$")]
    [InlineData("malformed-json", null)]
    [InlineData("json-array", null)]
    [InlineData("email-is-number", "$.email")]
    public async Task Login_UnbindableJsonBody_Returns400(string caseLabel, string? expectedErrorKey)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();

        using StringContent content = new(BodyFor(caseLabel), Encoding.UTF8, JsonMediaType);
        using HttpResponseMessage response = await client.Http.PostAsync(LoginRoute, content, ct);

        // Binding rejects the body before any credential check runs. The errors object is keyed by JSON path: an
        // empty body is the body itself, $, and a wrong-typed email is $.email. Malformed and non-object bodies are
        // body-level failures that name no field, so only the presence of the errors object is checked for them.
        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedErrorKey, ct);

        static string BodyFor(string label)
        {
            return label switch
            {
                "empty-body" => string.Empty,
                "malformed-json" => "{\"email\":",
                "json-array" => "[]",
                "email-is-number" => "{ \"email\": 5, \"password\": \"x\" }",
                _ => throw new ArgumentOutOfRangeException(nameof(label), label, "The case label is not recognized."),
            };
        }
    }

    [Theory]
    [InlineData("no-content-type-no-body")]
    [InlineData("text-plain")]
    public async Task Login_NonJsonContentType_Returns415(string caseLabel)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();

        // Disposing the request also disposes its content.
        using HttpRequestMessage request = new(HttpMethod.Post, LoginRoute) { Content = ContentFor(caseLabel) };
        using HttpResponseMessage response = await client.Http.SendAsync(request, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, cancellationToken: ct);

        static HttpContent? ContentFor(string label)
        {
            return label switch
            {
                "no-content-type-no-body" => null,
                "text-plain" => new StringContent(
                    "{\"email\":\"someone@example.test\",\"password\":\"Passw0rd!x\"}",
                    Encoding.UTF8,
                    "text/plain"),
                _ => throw new ArgumentOutOfRangeException(nameof(label), label, "The case label is not recognized."),
            };
        }
    }

    [Fact]
    public async Task Login_Valid_TokenTimesAreExact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();
        (int guestId, string email) = await RegisterGuestAsync(client, ct);

        using HttpResponseMessage response = await client.LoginAsync(new { email, password = ValidPassword }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Compared in memory with a fixed message, so the response's own media type never reaches the test output.
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.True(
            string.Equals(JsonMediaType, mediaType, StringComparison.Ordinal),
            mediaType is null
                ? $"Expected the login response's media type to be {JsonMediaType} but it has no valid Content-Type."
                : $"Expected the login response's media type to be {JsonMediaType} but it has a different one.");

        JsonElement body;
        await using (Stream stream = await response.Content.ReadAsStreamAsync(ct))
        {
            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                body = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // The parser's message quotes the offending input, and this body carries a live token.
                throw FailException.ForFailure(
                    "Expected the login response to be a JSON document but it could not be parsed.");
            }
        }

        Assert.Equal(JsonValueKind.Object, body.ValueKind);

        // The sorted names are compared in memory and only their count is reported, because a regression could name
        // a member after the guest's email or the token.
        string[] expectedMembers = ["expiresAt", "token"];
        string[] members =
        [
            .. body.EnumerateObject().Select(static member => member.Name).Order(StringComparer.Ordinal),
        ];
        Assert.True(
            members.SequenceEqual(expectedMembers, StringComparer.Ordinal),
            members.Length == expectedMembers.Length
                ? "Expected the login response to have exactly the expiresAt and token members but its "
                    + $"{members.Length} member names differ from them."
                : "Expected the login response to have exactly the expiresAt and token members but it has "
                    + $"{members.Length} member(s).");

        // The token is a live credential, so it is never passed to an assertion that would print it.
        JsonElement tokenElement = body.GetProperty("token");
        Assert.Equal(JsonValueKind.String, tokenElement.ValueKind);
        string? token = tokenElement.GetString();
        Assert.False(string.IsNullOrEmpty(token), "Expected the login response to carry a non-empty token.");

        // The raw text, not a deserialized DateTime, so the exact wire format is what is compared.
        JsonElement expiresAtElement = body.GetProperty("expiresAt");
        Assert.Equal(JsonValueKind.String, expiresAtElement.ValueKind);
        string? expiresAt = expiresAtElement.GetString();

        // Reads the token without validating it; the API's own validation is covered by the protected endpoints.
        JsonWebToken jwt;
        try
        {
            jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);
        }
        catch (ArgumentException)
        {
            // Every rejection of a malformed token, SecurityTokenMalformedException included, is an
            // ArgumentException whose message or inner exception can describe the token, so neither is passed on.
            throw FailException.ForFailure("Expected the login token to be a well-formed JWT.");
        }

        Assert.True(jwt.TryGetPayloadValue("iat", out long issuedAt), "Expected an integer iat claim.");
        Assert.True(jwt.TryGetPayloadValue("nbf", out long notBefore), "Expected an integer nbf claim.");
        Assert.True(jwt.TryGetPayloadValue("exp", out long expires), "Expected an integer exp claim.");

        Assert.Equal(TokenLifetimeSeconds, expires - issuedAt);
        Assert.Equal(issuedAt, notBefore);

        // Comparing the whole text ordinally proves whole seconds, the Z suffix and that expiresAt is exp.
        string expectedExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expires)
            .UtcDateTime
            .ToString(ExpiresAtFormat, CultureInfo.InvariantCulture);
        Assert.True(
            string.Equals(expectedExpiresAt, expiresAt, StringComparison.Ordinal),
            $"Expected expiresAt to equal the token's exp claim written as {ExpiresAtFormat}.");

        long now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        Assert.InRange(issuedAt, now - ClockToleranceSeconds, now + ClockToleranceSeconds);

        // Header and claim values are compared in memory with fixed messages, because a regression could put the
        // guest's email or name into any of them.
        Assert.True(
            string.Equals("HS256", jwt.Alg, StringComparison.Ordinal),
            "Expected the token's alg header to be HS256.");
        Assert.True(
            string.Equals(_factory.JwtIssuer, jwt.Issuer, StringComparison.Ordinal),
            "Expected the token's iss claim to be the configured issuer.");
        Assert.True(
            jwt.Audiences.Contains(_factory.JwtAudience, StringComparer.Ordinal),
            "Expected the token's aud claim to include the configured audience.");
        Assert.True(
            string.Equals(guestId.ToString(CultureInfo.InvariantCulture), jwt.Subject, StringComparison.Ordinal),
            "Expected the token's sub claim to be the registered guest id.");

        Assert.True(jwt.TryGetPayloadValue("email", out string? emailClaim), "Expected an email claim.");
        Assert.True(
            string.Equals(email, emailClaim, StringComparison.Ordinal),
            "Expected the email claim to equal the registered email.");
        Assert.True(jwt.TryGetPayloadValue("name", out string? nameClaim), "Expected a name claim.");
        Assert.True(
            string.Equals(FullName, nameClaim, StringComparison.Ordinal),
            "Expected the name claim to equal the registered full name.");
        Assert.False(string.IsNullOrEmpty(jwt.Id), "Expected a non-empty jti claim.");
    }

    // Registers a guest with a fresh, already normalized email: the class's tests share one database and run in any
    // order, so no two of them may register the same address.
    private static async Task<(int GuestId, string Email)> RegisterGuestAsync(
        ApiTestClient client,
        CancellationToken cancellationToken)
    {
        string email = $"login+{Guid.NewGuid():N}@example.test";

        using HttpResponseMessage response = await client.RegisterAsync(
            new { email, password = ValidPassword, fullName = FullName },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        ApiTestClient.RegisterResponse registration =
            await client.ReadAsync<ApiTestClient.RegisterResponse>(response, cancellationToken);

        return (registration.GuestId, email);
    }

    // Awaits one login attempt and asserts the credential-failure problem: a 401 under the problem contract with no
    // errors member, because a bound body is never a validation failure.
    private static async Task<JsonElement> AssertUnauthorizedAsync(
        Task<HttpResponseMessage> sending,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await sending;
        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.Unauthorized,
            cancellationToken: cancellationToken);
        Assert.False(problem.TryGetProperty("errors", out _), "Expected the 401 problem to have no errors member.");

        return problem;
    }
}