using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using SmartHotel.Api.IntegrationTests.Infrastructure;

namespace SmartHotel.Api.IntegrationTests.Auth;

// Every token is forged by ForgeToken from one accepted baseline, changing exactly the element its case names, and
// the positive control proves that baseline is accepted. Each 401 is therefore caused by its single broken element:
// the lifetime, ended an hour ago or within IdentityModel's default clock skew; the missing expiration; the signing
// key; the issuer; the audience; the signature requirement; an algorithm other than HS256 under the API's own key; the
// guest id in sub; or the token's format. Tokens and keys are never written to output.
public sealed class TokenValidationTests(SmartHotelApiFactory factory) : IClassFixture<SmartHotelApiFactory>
{
    private const string Baseline = "none";
    private const string Expired = "expired";
    private const string ExpiredInsideDefaultSkew = "expired-inside-default-skew";
    private const string NoExpiration = "no-expiration";
    private const string WrongKey = "wrong-key";
    private const string WrongIssuer = "wrong-issuer";
    private const string WrongAudience = "wrong-audience";
    private const string AudienceTrailingSlash = "audience-trailing-slash";
    private const string AlgNone = "alg-none";
    private const string AlgHs384 = "alg-hs384";
    private const string AlgHs512 = "alg-hs512";
    private const string Malformed = "malformed";
    private const string MissingSub = "missing-sub";
    private const string NonNumericSub = "non-numeric-sub";
    private const string ZeroSub = "zero-sub";
    private const string NegativeSub = "negative-sub";

    private const string ListEndpoint = "list";
    private const string CreateWithUnbindableBodyEndpoint = "create-unbindable-body";
    private const string CancelEndpoint = "cancel";

    private const string SubClaim = "sub";

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
    [InlineData(ExpiredInsideDefaultSkew)]
    [InlineData(NoExpiration)]
    [InlineData(WrongKey)]
    [InlineData(WrongIssuer)]
    [InlineData(WrongAudience)]
    [InlineData(AudienceTrailingSlash)]
    [InlineData(AlgNone)]
    [InlineData(AlgHs384)]
    [InlineData(AlgHs512)]
    [InlineData(Malformed)]
    [InlineData(MissingSub)]
    [InlineData(NonNumericSub)]
    [InlineData(ZeroSub)]
    [InlineData(NegativeSub)]
    public async Task ProtectedEndpoint_RejectedToken_Returns401(string brokenElement)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        ApiTestClient.GuestSession guest = await client.RegisterGuestAsync(ct);
        string token = ForgeToken(guest.GuestId, brokenElement);

        using HttpResponseMessage response = await client.ListBookingsAsync(token, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.Unauthorized, cancellationToken: ct);

        // The challenge names only the scheme: no error or error_description says which check the token failed.
        ProblemAssert.AssertBareBearerChallenge(response);
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

    // The token is the accepted baseline without its sub claim, so it passes the signature, issuer, audience and
    // lifetime checks. Without the guest-id check during authentication, the POST would fail binding with a 400 and
    // the list and the cancellation would reach their actions. A bare 401 on each shows the token is rejected before
    // MVC binds anything.
    [Theory]
    [InlineData(ListEndpoint)]
    [InlineData(CreateWithUnbindableBodyEndpoint)]
    [InlineData(CancelEndpoint)]
    public async Task BookingsEndpoint_TokenWithoutGuestId_Returns401BeforeBinding(string endpoint)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        ApiTestClient.GuestSession guest = await client.RegisterGuestAsync(ct);
        string token = ForgeToken(guest.GuestId, MissingSub);

        using HttpResponseMessage response = await SendBookingsRequestAsync(client, endpoint, token, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.Unauthorized, cancellationToken: ct);
        ProblemAssert.AssertBareBearerChallenge(response);
    }

    private static SigningCredentials Hs256(string key)
    {
        return Hmac(key, SecurityAlgorithms.HmacSha256);
    }

    private static SigningCredentials Hmac(string key, string algorithm)
    {
        return new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            algorithm);
    }

    // Builds the accepted baseline, then breaks only brokenElement. The baseline mirrors what the API issues and
    // validates: HS256 with the factory's key, its issuer and audience, and a sub claim naming a registered guest,
    // without which JwtBearer rejects the token. Its nbf sits a minute in the past because the API allows no clock
    // skew. The cases that expire the token within IdentityModel's default clock skew, omit its expiration or sign it
    // with HS384 or HS512 keep the factory's key, so only the zero clock skew, the required expiration time or the
    // HS256-only algorithm list rejects them.
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
                [SubClaim] = guestId.ToString(CultureInfo.InvariantCulture),
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
            case ExpiredInsideDefaultSkew:
                // Its hour-long lifetime ended half of IdentityModel's default clock skew ago: safely in the past, yet
                // still accepted by a validator that keeps the default skew.
                TimeSpan expiredFor = TokenValidationParameters.DefaultClockSkew / 2;
                Assert.True(expiredFor > TimeSpan.Zero, "Expected IdentityModel's default clock skew to be positive.");
                DateTime expiredAt = now - expiredFor;
                descriptor.IssuedAt = expiredAt.AddHours(-1);
                descriptor.NotBefore = expiredAt.AddHours(-1);
                descriptor.Expires = expiredAt;
                break;
            case NoExpiration:
                // With SetDefaultTimesOnTokenCreation off, the handler writes no exp claim at all.
                descriptor.Expires = null;
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
            case AudienceTrailingSlash:
                // The configured audience with a trailing slash: the audience must match exactly.
                descriptor.Audience = factory.JwtAudience + "/";
                break;
            case AlgNone:
                // With no signing credentials the handler emits an unsigned JWT: header alg "none" and an empty
                // signature segment.
                descriptor.SigningCredentials = null;
                break;
            case AlgHs384:
                // The factory's 64-byte key meets HS384's 48-byte minimum, so the signature verifies under it.
                descriptor.SigningCredentials = Hmac(factory.SigningKey, SecurityAlgorithms.HmacSha384);
                break;
            case AlgHs512:
                // The factory's 64-byte key meets HS512's 64-byte minimum, so the signature verifies under it.
                descriptor.SigningCredentials = Hmac(factory.SigningKey, SecurityAlgorithms.HmacSha512);
                break;
            case MissingSub:
                descriptor.Claims.Remove(SubClaim);
                break;
            case NonNumericSub:
                descriptor.Claims[SubClaim] = "abc";
                break;
            case ZeroSub:
                // Guest ids start at 1.
                descriptor.Claims[SubClaim] = "0";
                break;
            case NegativeSub:
                // A sign is not part of a guest id, so this fails the rule before its value is considered.
                descriptor.Claims[SubClaim] = "-5";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(brokenElement), "The token element to break is unknown.");
        }

        string token = TokenHandler.CreateToken(descriptor);

        // The header names the algorithm the case signed with, and exp is present exactly when the case sets an
        // expiry, so the algorithm and expiration cases break only what they name.
        JsonWebToken forged = new(token);
        Assert.Equal(descriptor.SigningCredentials?.Algorithm ?? SecurityAlgorithms.None, forged.Alg);
        Assert.Equal(descriptor.Expires.HasValue, forged.TryGetPayloadValue(JwtRegisteredClaimNames.Exp, out long _));

        return token;
    }

    // Sends one bookings request with the given token. The POST body gives hotelId a string where an integer is
    // required, so it fails binding with a 400 whenever it is bound.
    private static Task<HttpResponseMessage> SendBookingsRequestAsync(
        ApiTestClient client,
        string endpoint,
        string token,
        CancellationToken cancellationToken)
    {
        return endpoint switch
        {
            ListEndpoint => client.ListBookingsAsync(token, cancellationToken),
            CreateWithUnbindableBodyEndpoint => client.CreateBookingAsync(
                new JsonObject { ["hotelId"] = "x" },
                token,
                cancellationToken),
            CancelEndpoint => client.CancelBookingAsync(1, token, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), "The bookings endpoint is unknown."),
        };
    }
}