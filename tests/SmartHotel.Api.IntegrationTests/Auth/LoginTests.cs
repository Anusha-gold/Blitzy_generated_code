using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data.Entities;

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

    // ASP.NET Core Identity's V3 hash layout: a 0x01 marker, then three big-endian uint32 values (PRF, iteration
    // count, salt length), then the salt and the subkey. The expected values are the ones AuthModule configures.
    private const int V3HashLength = 1 + 4 + 4 + 4 + 16 + 32;
    private const byte V3FormatMarker = 0x01;
    private const uint HmacSha512Prf = 2;
    private const uint ConfiguredIterationCount = 220_000;
    private const uint SaltLengthBytes = 16;

    // A work factor below the configured one, as stored for a guest who registered before it was raised.
    private const int LowerIterationCount = 10_000;

    private readonly SmartHotelApiFactory _factory;

    public LoginTests(SmartHotelApiFactory factory)
    {
        _factory = factory;
    }

    // The four failures run on a host of their own whose password hasher records every verification and counts every
    // hash, because identical responses alone would not change if the dummy verification were removed. Each failure
    // must verify exactly once: a wrong password against the guest's stored hash, and every attempt no account can
    // match against one dummy hash. None may hash, because the dummy hash exists before the host accepts its first
    // request. Only counts and the hashes compared in memory are checked, never timings.
    [Fact]
    public async Task Login_WrongCredentials_Returns401WithIdenticalContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // The variant only needs the hasher replaced; no log record is asserted, so the capture gets no rules.
        SmartHotelApiFactory.LogCapture capture = new();
        await using SmartHotelApiFactory variant = _factory.CreateLogCaptureVariant(
            capture,
            captureRules: [],
            additionalOverrides: static services =>
            {
                // A real hasher with the host's own options, so every verification is the configured PBKDF2 work.
                services.RemoveAll<IPasswordHasher<Guest>>();
                services.AddSingleton<IPasswordHasher<Guest>>(static provider => new RecordingPasswordHasher(
                    new PasswordHasher<Guest>(provider.GetRequiredService<IOptions<PasswordHasherOptions>>())));
            });

        // A hand-built factory is not a fixture, so it is started here.
        await variant.InitializeAsync();
        RecordingPasswordHasher hasher = Assert.IsType<RecordingPasswordHasher>(
            variant.Services.GetRequiredService<IPasswordHasher<Guest>>());
        ApiTestClient client = variant.CreateApiClient();
        (int guestId, string email) = await RegisterGuestAsync(client, ct);
        string unknownEmail = $"unknown+{Guid.NewGuid():N}@example.test";

        // Registration has hashed its password by now, and the dummy hash has existed since the host started, so no
        // hash may follow this mark.
        int hashMark = hasher.HashCount;

        // Each attempt completes before the next starts, so the verifications recorded since a mark are its own.
        int mark = hasher.VerificationCount;
        JsonElement wrongPassword = await AssertUnauthorizedAsync(
            client.LoginAsync(new { email, password = WrongPassword }, ct),
            ct);
        PasswordVerification wrongPasswordCheck = hasher.SingleVerificationSince(mark, "wrong-password");

        mark = hasher.VerificationCount;
        JsonElement unknownAccount = await AssertUnauthorizedAsync(
            client.LoginAsync(new { email = unknownEmail, password = ValidPassword }, ct),
            ct);
        PasswordVerification unknownAccountCheck = hasher.SingleVerificationSince(mark, "unknown-email");

        mark = hasher.VerificationCount;
        JsonElement blankFields = await AssertUnauthorizedAsync(
            client.LoginAsync(new { email = string.Empty, password = string.Empty }, ct),
            ct);
        PasswordVerification blankFieldsCheck = hasher.SingleVerificationSince(mark, "blank-fields");

        // Sent as raw text, so the explicit JSON nulls on the wire do not depend on the serializer's null handling.
        mark = hasher.VerificationCount;
        using StringContent nullContent = new("{\"email\":null,\"password\":null}", Encoding.UTF8, JsonMediaType);
        JsonElement nullFields = await AssertUnauthorizedAsync(client.Http.PostAsync(LoginRoute, nullContent, ct), ct);
        PasswordVerification nullFieldsCheck = hasher.SingleVerificationSince(mark, "null-fields");

        // A failure that had to create the dummy hash would hash once on top of its verification. The message carries
        // the count only.
        int failureHashCount = hasher.HashCount - hashMark;
        Assert.True(
            failureHashCount == 0,
            $"Expected none of the four failed logins to hash a password but they hashed {failureHashCount} time(s).");

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

        // The hashes are compared in memory with fixed messages, so no hash or password reaches the test output.
        string storedHash = await ReadPasswordHashAsync(variant, guestId, ct);
        List<string> storedHashes = await variant.ExecuteDbAsync(
            static (db, token) => db.Guests.AsNoTracking().Select(static g => g.PasswordHash).ToListAsync(token),
            ct);

        Assert.True(
            wrongPasswordCheck.GuestId == guestId,
            "Expected the wrong-password login to verify the registered guest's password.");
        Assert.True(
            string.Equals(storedHash, wrongPasswordCheck.HashedPassword, StringComparison.Ordinal),
            "Expected the wrong-password login to verify against the guest's stored hash.");
        Assert.False(
            wrongPasswordCheck.ProvidedPasswordWasEmpty,
            "Expected the wrong-password login to verify the password it was sent.");

        // The dummy hash is a current hash of no account's password, so verifying against it costs what a real
        // account's verification costs.
        string dummyHash = unknownAccountCheck.HashedPassword;
        Assert.False(
            storedHashes.Contains(dummyHash, StringComparer.Ordinal),
            "Expected the unknown-email login to verify against a hash that no stored guest has.");
        AssertCurrentHash(dummyHash, "the dummy hash");
        Assert.False(
            unknownAccountCheck.ProvidedPasswordWasEmpty,
            "Expected the unknown-email login to verify the password it was sent.");

        // An empty password stays empty; null is replaced with an empty string because the hasher rejects null.
        (PasswordVerification Check, string Attempt)[] emptyPasswordChecks =
        [
            (blankFieldsCheck, "blank-fields"),
            (nullFieldsCheck, "null-fields"),
        ];
        foreach ((PasswordVerification check, string attempt) in emptyPasswordChecks)
        {
            Assert.True(
                string.Equals(dummyHash, check.HashedPassword, StringComparison.Ordinal),
                $"Expected the {attempt} login to verify against the same dummy hash as the unknown-email login.");
            Assert.True(
                check.ProvidedPasswordWasEmpty,
                $"Expected the {attempt} login to verify an empty password in place of the missing one.");
        }
    }

    // A stored hash with a lower work factor than the configured one, as kept for a guest who registered before it was
    // raised, is replaced by a current hash at the guest's next successful login. That hash then verifies as current,
    // so a second login succeeds and keeps it.
    [Theory]
    [InlineData("v3-fewer-iterations")]
    [InlineData("v2-format")]
    public async Task Login_LowerWorkFactorHash_IsRehashedOnLogin(string caseLabel)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();
        string email = $"rehash+{Guid.NewGuid():N}@example.test";

        // V2 is Identity's earlier format: PBKDF2-HMAC-SHA1 at 1,000 iterations.
        PasswordHasherOptions legacyOptions = caseLabel switch
        {
            "v3-fewer-iterations" => new PasswordHasherOptions { IterationCount = LowerIterationCount },
            "v2-format" => new PasswordHasherOptions
            {
                CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2,
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(caseLabel),
                caseLabel,
                "The case label is not recognized."),
        };

        // Inserted directly, because registration always stores a current hash. The email is already normalized.
        Guest guest = new()
        {
            Email = email,
            FullName = FullName,
            CreatedAt = TimeProvider.System.GetUtcNow().UtcDateTime,
        };
        PasswordHasher<Guest> legacyHasher = new(Options.Create(legacyOptions));
        guest.PasswordHash = legacyHasher.HashPassword(guest, ValidPassword);
        string seededHash = guest.PasswordHash;
        int guestId = await _factory.ExecuteDbAsync(
            async (db, token) =>
            {
                db.Guests.Add(guest);
                await db.SaveChangesAsync(token);
                return guest.Id;
            },
            ct);

        using (HttpResponseMessage first = await client.LoginAsync(new { email, password = ValidPassword }, ct))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // Compared in memory with fixed messages, so no hash reaches the test output.
        string upgradedHash = await ReadPasswordHashAsync(_factory, guestId, ct);
        Assert.False(
            string.Equals(seededHash, upgradedHash, StringComparison.Ordinal),
            "Expected the first login to replace the lower-work-factor hash.");
        AssertCurrentHash(upgradedHash, "the hash stored after the first login");

        using (HttpResponseMessage second = await client.LoginAsync(new { email, password = ValidPassword }, ct))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        string keptHash = await ReadPasswordHashAsync(_factory, guestId, ct);
        Assert.True(
            string.Equals(upgradedHash, keptHash, StringComparison.Ordinal),
            "Expected the second login to keep the upgraded hash, which is already current.");
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

    // The hash's HMAC-SHA512 pads a key shorter than its block with zero bytes, so the registered password followed by
    // null characters (U+0000) derives the very same hash. Each such value is still a wrong password: it gets the one
    // credential-failure 401, while the exact password still logs in.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Login_PasswordWithAppendedNullCharacters_Returns401(int nullCount)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();
        (_, string email) = await RegisterGuestAsync(client, ct);

        JsonElement wrongPassword = await AssertUnauthorizedAsync(
            client.LoginAsync(new { email, password = WrongPassword }, ct),
            ct);

        // Sent as raw text with JSON \u0000 escapes, so the null characters reach the server whatever the serializer
        // does with control characters. The email is a GUID-based address that needs no escaping.
        string nullEscapes = string.Concat(Enumerable.Repeat("\\u0000", nullCount));
        using StringContent content = new(
            $"{{\"email\":\"{email}\",\"password\":\"{ValidPassword}{nullEscapes}\"}}",
            Encoding.UTF8,
            JsonMediaType);
        JsonElement nullSuffixed = await AssertUnauthorizedAsync(client.Http.PostAsync(LoginRoute, content, ct), ct);
        ProblemAssert.AssertSameProblem(wrongPassword, nullSuffixed);

        using HttpResponseMessage exact = await client.LoginAsync(new { email, password = ValidPassword }, ct);
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
    }

    [Fact]
    public async Task Login_Valid_TokenTimesAreExact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();
        (int guestId, string email) = await RegisterGuestAsync(client, ct);

        using HttpResponseMessage response = await client.LoginAsync(new { email, password = ValidPassword }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The body is a live bearer credential, so no cache may store it.
        ApiTestClient.AssertNoStore(response, "POST api/auth/login");

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
    // errors member, because a bound body is never a validation failure, and with the bare Bearer challenge every 401
    // must carry (RFC 9110, section 15.5.2), the same one JwtBearer sends.
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
        ProblemAssert.AssertBareBearerChallenge(response);

        return problem;
    }

    private static async Task<string> ReadPasswordHashAsync(
        SmartHotelApiFactory factory,
        int guestId,
        CancellationToken cancellationToken)
    {
        return await factory.ExecuteDbAsync(
            (db, token) => db.Guests
                .AsNoTracking()
                .Where(g => g.Id == guestId)
                .Select(static g => g.PasswordHash)
                .SingleAsync(token),
            cancellationToken);
    }

    // Decodes a hash and asserts the current format: V3 with HMAC-SHA512, the configured iteration count and a 128-bit
    // salt. Failure messages name the subject and the layout values only, never the hash.
    private static void AssertCurrentHash(string hash, string subject)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(hash);
        }
        catch (FormatException)
        {
            throw FailException.ForFailure($"Expected {subject} to be Base64 text.");
        }

        Assert.True(
            bytes.Length == V3HashLength,
            $"Expected {subject} to be {V3HashLength} bytes long but it is {bytes.Length}.");
        Assert.True(bytes[0] == V3FormatMarker, $"Expected {subject} to start with the V3 format marker.");

        uint prf = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1));
        uint iterationCount = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5));
        uint saltLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9));
        Assert.True(
            prf == HmacSha512Prf,
            $"Expected {subject} to use PRF {HmacSha512Prf} (HMAC-SHA512) but it uses PRF {prf}.");
        Assert.True(
            iterationCount == ConfiguredIterationCount,
            $"Expected {subject} to use {ConfiguredIterationCount} iterations but it uses {iterationCount}.");
        Assert.True(
            saltLength == SaltLengthBytes,
            $"Expected {subject} to have a {SaltLengthBytes}-byte salt but its salt is {saltLength} bytes.");
    }

    // One VerifyHashedPassword call as the recording hasher received it. The hash is kept for in-memory comparison
    // only, and of the provided password only whether it was empty is kept.
    private sealed record PasswordVerification(int GuestId, string HashedPassword, bool ProvidedPasswordWasEmpty);

    // Delegates to a real hasher, records every verification and counts every hash, so a test can count the PBKDF2 work
    // a login performs without timing it. Safe for concurrent requests.
    private sealed class RecordingPasswordHasher(IPasswordHasher<Guest> inner) : IPasswordHasher<Guest>
    {
        private readonly ConcurrentQueue<PasswordVerification> _verifications = new();
        private int _hashCount;

        public int VerificationCount => _verifications.Count;

        public int HashCount => Volatile.Read(ref _hashCount);

        public string HashPassword(Guest user, string password)
        {
            // Counted before delegating, so a hash the inner hasher rejects is counted too.
            Interlocked.Increment(ref _hashCount);
            return inner.HashPassword(user, password);
        }

        public PasswordVerificationResult VerifyHashedPassword(
            Guest user,
            string hashedPassword,
            string providedPassword)
        {
            // Recorded before delegating, so a verification the inner hasher rejects is counted too.
            _verifications.Enqueue(new PasswordVerification(
                user.Id,
                hashedPassword,
                string.IsNullOrEmpty(providedPassword)));
            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }

        // Returns the one verification recorded after the first mark ones. The failure names the attempt and the
        // count only.
        public PasswordVerification SingleVerificationSince(int mark, string attempt)
        {
            PasswordVerification[] since = [.. _verifications.Skip(mark)];
            Assert.True(
                since.Length == 1,
                $"Expected the {attempt} login to verify a password exactly once but it verified {since.Length} "
                + "time(s).");

            return since[0];
        }
    }
}