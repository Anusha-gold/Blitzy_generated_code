using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.IdentityModel.JsonWebTokens;

using SmartHotel.Api.IntegrationTests.Infrastructure;

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

        Assert.Equal(InvalidCredentialsTitle, wrongPassword.GetProperty("title").GetString());
        Assert.Equal(InvalidCredentialsDetail, wrongPassword.GetProperty("detail").GetString());
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
        Assert.Equal(JsonMediaType, response.Content.Headers.ContentType?.MediaType);

        JsonElement body;
        await using (Stream stream = await response.Content.ReadAsStreamAsync(ct))
        {
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            body = document.RootElement.Clone();
        }

        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        string[] expectedMembers = ["expiresAt", "token"];
        string[] members =
        [
            .. body.EnumerateObject().Select(static member => member.Name).Order(StringComparer.Ordinal),
        ];
        Assert.Equal(expectedMembers, members);

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
        JsonWebToken jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);

        Assert.True(jwt.TryGetPayloadValue("iat", out long issuedAt), "Expected an integer iat claim.");
        Assert.True(jwt.TryGetPayloadValue("nbf", out long notBefore), "Expected an integer nbf claim.");
        Assert.True(jwt.TryGetPayloadValue("exp", out long expires), "Expected an integer exp claim.");

        Assert.Equal(TokenLifetimeSeconds, expires - issuedAt);
        Assert.Equal(issuedAt, notBefore);

        // Comparing the whole text ordinally proves whole seconds, the Z suffix and that expiresAt is exp.
        string expectedExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expires)
            .UtcDateTime
            .ToString(ExpiresAtFormat, CultureInfo.InvariantCulture);
        Assert.Equal(expectedExpiresAt, expiresAt);

        long now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        Assert.InRange(issuedAt, now - ClockToleranceSeconds, now + ClockToleranceSeconds);

        Assert.Equal("HS256", jwt.Alg);
        Assert.Equal(_factory.JwtIssuer, jwt.Issuer);
        Assert.Contains(_factory.JwtAudience, jwt.Audiences);
        Assert.Equal(guestId.ToString(CultureInfo.InvariantCulture), jwt.Subject);

        Assert.True(jwt.TryGetPayloadValue("email", out string? emailClaim), "Expected an email claim.");
        Assert.Equal(email, emailClaim);
        Assert.True(jwt.TryGetPayloadValue("name", out string? nameClaim), "Expected a name claim.");
        Assert.Equal(FullName, nameClaim);
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