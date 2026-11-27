using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using SmartHotel.Api.IntegrationTests.Infrastructure;

namespace SmartHotel.Api.IntegrationTests.Auth;

// Every token is forged by ForgeToken from one accepted baseline, changing exactly the element its case names, and
// the positive control proves that baseline is accepted. Each 401 is therefore caused by its single broken element:
// the lifetime, the signing key, the issuer, the audience, the signature requirement, or the token's format.
// Tokens and keys are never written to output.
public sealed class TokenValidationTests(SmartHotelApiFactory factory) : IClassFixture<SmartHotelApiFactory>
{
    private const string Baseline = "none";
    private const string Expired = "expired";
    private const string WrongKey = "wrong-key";
    private const string WrongIssuer = "wrong-issuer";
    private const string WrongAudience = "wrong-audience";
    private const string AlgNone = "alg-none";
    private const string Malformed = "malformed";

    private const string MalformedToken = "not-a-jwt";
    private const string OtherParty = "SmartHotel.Other";

    // 64 hex characters are 64 bytes in UTF-8, well over HS256's 256-bit minimum, so the foreign key is rejected for
    // not being the API's key rather than for being too short.
    private const int ForeignKeyLength = 64;

    // The descriptor's iat, nbf and exp are written exactly as set; without this flag the handler would fill missing
    // times from its own clock.
    private static readonly JsonWebTokenHandler TokenHandler = new() { SetDefaultTimesOnTokenCreation = false };

    [Theory]
    [InlineData(Expired)]
    [InlineData(WrongKey)]
    [InlineData(WrongIssuer)]
    [InlineData(WrongAudience)]
    [InlineData(AlgNone)]
    [InlineData(Malformed)]
    public async Task ProtectedEndpoint_RejectedToken_Returns401(string brokenElement)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        ApiTestClient.GuestSession guest = await client.RegisterGuestAsync(ct);
        string token = ForgeToken(guest.GuestId, brokenElement);

        using HttpResponseMessage response = await client.ListBookingsAsync(token, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.Unauthorized, cancellationToken: ct);

        // JwtBearer adds error parameters such as error="invalid_token"; only the scheme is part of the contract.
        ProblemAssert.AssertBearerChallenge(response);
    }

    [Fact]
    public async Task ProtectedEndpoint_CorrectlyForgedToken_Returns200()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        ApiTestClient.GuestSession guest = await client.RegisterGuestAsync(ct);
        string token = ForgeToken(guest.GuestId, Baseline);

        using HttpResponseMessage response = await client.ListBookingsAsync(token, ct);

        // A 200 also shows that the literal sub claim reaches the controller unmapped and names the registered guest.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<ApiTestClient.Booking> bookings = await client.ReadAsync<List<ApiTestClient.Booking>>(response, ct);
        Assert.Empty(bookings);
    }

    private static SigningCredentials Hs256(string key)
    {
        return new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);
    }

    // Builds the accepted baseline, then breaks only brokenElement. The baseline mirrors what the API issues and
    // validates: HS256 with the factory's key, its issuer and audience, and a sub claim naming a registered guest,
    // without which the controller could not resolve the caller. Its nbf sits a minute in the past because the API
    // allows no clock skew.
    private string ForgeToken(int guestId, string brokenElement)
    {
        // Not a JWT at all, so no descriptor is built and nothing else about it can be at fault.
        if (string.Equals(brokenElement, Malformed, StringComparison.Ordinal))
        {
            return MalformedToken;
        }

        DateTime now = TimeProvider.System.GetUtcNow().UtcDateTime;
        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = factory.JwtIssuer,
            Audience = factory.JwtAudience,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(10),
            SigningCredentials = Hs256(factory.SigningKey),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = guestId.ToString(CultureInfo.InvariantCulture),
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
        };

        switch (brokenElement)
        {
            case Baseline:
                break;
            case Expired:
                // Signed with the right key for the right issuer and audience, but its lifetime ended an hour ago.
                descriptor.IssuedAt = now.AddHours(-2);
                descriptor.NotBefore = now.AddHours(-2);
                descriptor.Expires = now.AddHours(-1);
                break;
            case WrongKey:
                descriptor.SigningCredentials = Hs256(
                    RandomNumberGenerator.GetHexString(ForeignKeyLength, lowercase: true));
                break;
            case WrongIssuer:
                descriptor.Issuer = OtherParty;
                Assert.NotEqual(factory.JwtIssuer, descriptor.Issuer);
                break;
            case WrongAudience:
                descriptor.Audience = OtherParty;
                Assert.NotEqual(factory.JwtAudience, descriptor.Audience);
                break;
            case AlgNone:
                // With no signing credentials the handler emits an unsigned JWT: header alg "none" and an empty
                // signature segment.
                descriptor.SigningCredentials = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(brokenElement), "The token element to break is unknown.");
        }

        return TokenHandler.CreateToken(descriptor);
    }
}