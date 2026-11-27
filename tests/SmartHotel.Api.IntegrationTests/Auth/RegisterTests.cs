using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Data.Entities;

using Xunit.Sdk;

namespace SmartHotel.Api.IntegrationTests.Auth;

public sealed class RegisterTests(SmartHotelApiFactory factory) : IClassFixture<SmartHotelApiFactory>
{
    private const string RegisterRoute = "api/auth/register";
    private const string JsonMediaType = "application/json";

    // Fixed, non-secret values for throwaway guests in this class's own test database.
    private const string ValidPassword = "Passw0rd!x";
    private const string ValidFullName = "Test Guest";

    // ASP.NET Core Identity's V3 hash layout: a 0x01 marker, then three big-endian uint32 values (PRF, iteration
    // count, salt length), then the salt and the subkey. The expected values are the ones AuthModule configures.
    private const int V3HashLength = 1 + 4 + 4 + 4 + 16 + 32;
    private const byte V3FormatMarker = 0x01;
    private const uint HmacSha512Prf = 2;
    private const uint ConfiguredIterationCount = 220_000;
    private const uint SaltLengthBytes = 16;

    [Theory]
    [InlineData("email-not-an-address", "email")]
    [InlineData("email-non-ascii", "email")]
    [InlineData("password-too-short", "password")]
    [InlineData("fullname-missing", "fullName")]
    [InlineData("empty-json-body", "$")]
    [InlineData("malformed-json", null)]
    public async Task Register_InvalidInput_Returns400(string caseLabel, string? expectedErrorKey)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();

        using HttpResponseMessage response = await SendInvalidRegistrationAsync(client, caseLabel, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.BadRequest, expectedErrorKey, ct);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        string email = UniqueEmail("dup");

        using HttpResponseMessage first = await client.RegisterAsync(
            new { email, password = ValidPassword, fullName = ValidFullName },
            ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Only the letter case differs, so the 409 shows that uniqueness holds on the normalized form.
        using HttpResponseMessage second = await client.RegisterAsync(
            new { email = email.ToUpperInvariant(), password = ValidPassword, fullName = ValidFullName },
            ct);

        await ProblemAssert.AssertProblemAsync(second, HttpStatusCode.Conflict, cancellationToken: ct);
    }

    [Fact]
    public async Task Register_Valid_Returns201WithoutHash()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        string rawEmail = $"  Mixed.Case+{Guid.NewGuid():N}@Example.TEST  ";
        string expectedEmail = rawEmail.Trim().ToLowerInvariant();

        using HttpResponseMessage response = await client.RegisterAsync(
            new { email = rawEmail, password = ValidPassword, fullName = ValidFullName },
            ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // A boolean with a fixed message rather than Assert.Null, which would print a regressed Location URI, and a
        // URI that identifies a guest could carry the email.
        Assert.True(
            response.Headers.Location is null,
            "Expected the registration response to have no Location header.");

        int guestId = await ReadGuestIdAsync(response, ct);

        Guest guest = await factory.ExecuteDbAsync(
            (db, token) => db.Guests.AsNoTracking().SingleAsync(g => g.Id == guestId, token),
            ct);

        // The stored strings are checked with fixed messages rather than Assert.Equal/NotEqual, so a failure never
        // prints the email, the full name, the password or the hash. Ordinal equality is as exact as Assert.Equal.
        Assert.True(
            string.Equals(expectedEmail, guest.Email, StringComparison.Ordinal),
            "Expected the stored email to be the submitted email trimmed and in lower case.");
        Assert.True(
            string.Equals(ValidFullName, guest.FullName, StringComparison.Ordinal),
            "Expected the stored full name to equal the submitted full name.");
        Assert.Equal(DateTimeKind.Utc, guest.CreatedAt.Kind);

        Assert.False(string.IsNullOrEmpty(guest.PasswordHash), "Expected a stored password hash.");
        Assert.False(
            string.Equals(guest.PasswordHash, ValidPassword, StringComparison.Ordinal),
            "Expected the stored password hash to differ from the password.");

        byte[] hash = Convert.FromBase64String(guest.PasswordHash);
        Assert.Equal(V3HashLength, hash.Length);
        Assert.Equal(V3FormatMarker, hash[0]);
        Assert.Equal(HmacSha512Prf, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(1)));
        Assert.Equal(ConfiguredIterationCount, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5)));
        Assert.Equal(SaltLengthBytes, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(9)));
    }

    private static string UniqueEmail(string prefix)
    {
        // Every test in the class shares one database and runs in any order, so each address must be new.
        return $"{prefix}+{Guid.NewGuid():N}@example.test";
    }

    // Each case breaks exactly one input and keeps the others valid: MVC runs RegisterRequest.Validate, which reports
    // the email errors, only after every attribute check has passed, so a second broken field would hide the key.
    private static Task<HttpResponseMessage> SendInvalidRegistrationAsync(
        ApiTestClient client,
        string caseLabel,
        CancellationToken cancellationToken)
    {
        return caseLabel switch
        {
            "email-not-an-address" => client.RegisterAsync(
                new { email = "not-an-email", password = ValidPassword, fullName = ValidFullName },
                cancellationToken),
            "email-non-ascii" => client.RegisterAsync(
                new
                {
                    email = $"g\u00e4st+{Guid.NewGuid():N}@example.test",
                    password = ValidPassword,
                    fullName = ValidFullName,
                },
                cancellationToken),
            "password-too-short" => client.RegisterAsync(
                new { email = UniqueEmail("short"), password = "Passw0r", fullName = ValidFullName },
                cancellationToken),
            "fullname-missing" => client.RegisterAsync(
                new { email = UniqueEmail("noname"), password = ValidPassword },
                cancellationToken),
            "empty-json-body" => PostRawJsonAsync(client, string.Empty, cancellationToken),
            "malformed-json" => PostRawJsonAsync(client, "{\"email\":", cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(caseLabel), caseLabel, "Unknown registration case."),
        };
    }

    private static async Task<HttpResponseMessage> PostRawJsonAsync(
        ApiTestClient client,
        string json,
        CancellationToken cancellationToken)
    {
        using StringContent content = new(json, Encoding.UTF8, JsonMediaType);
        return await client.Http.PostAsync(RegisterRoute, content, cancellationToken);
    }

    // Reads the 201 body as raw JSON rather than a typed record, so any member beyond guestId, such as a password
    // hash, fails the test. A regression could name a member after the guest's email or put it in a value, so no
    // member name, value or parser message from the response reaches a failure message; at most the member count does.
    private static async Task<int> ReadGuestIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        JsonElement root;
        await using (Stream body = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // The parser's message quotes characters of the offending input.
                throw FailException.ForFailure(
                    "Expected the registration response to be a JSON document but it could not be parsed.");
            }
        }

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        string[] memberNames = [.. root.EnumerateObject().Select(static member => member.Name)];
        Assert.True(
            memberNames.Length == 1 && string.Equals("guestId", memberNames[0], StringComparison.Ordinal),
            memberNames.Length == 1
                ? "Expected the registration response to have exactly one member, guestId, but its one member has "
                    + "a different name."
                : "Expected the registration response to have exactly one member, guestId, but it has "
                    + $"{memberNames.Length} members.");

        JsonElement guestIdValue = root.GetProperty("guestId");
        Assert.Equal(JsonValueKind.Number, guestIdValue.ValueKind);
        Assert.True(guestIdValue.TryGetInt32(out int guestId), "Expected guestId to be a 32-bit integer.");
        Assert.True(guestId > 0, "Expected guestId to be a positive integer.");

        return guestId;
    }
}