using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Npgsql;

using SmartHotel.Api.Infrastructure;
using SmartHotel.Api.IntegrationTests.Infrastructure;

namespace SmartHotel.Api.IntegrationTests.Auth;

// Every token is forged by ForgeToken from one accepted baseline, changing exactly the element its case names, and
// the positive control proves that baseline is accepted. Each 401 is therefore caused by its single broken element:
// the lifetime, ended an hour ago or within IdentityModel's default clock skew; the missing expiration; the signing
// key; the issuer; the audience; the signature requirement; an algorithm other than HS256 under the API's own key; the
// guest id in sub, unusable or naming no registered guest; or the token's format. Tokens and keys are never written to
// output.
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
    private const string UnknownGuest = "unknown-guest";

    private const string ListEndpoint = "list";
    private const string CreateWithUnbindableBodyEndpoint = "create-unbindable-body";
    private const string CancelEndpoint = "cancel";

    private const string SubClaim = "sub";

    private const string MalformedToken = "not-a-jwt";
    private const string OtherParty = "SmartHotel.Other";

    // A well-formed guest id that no guest registered by these tests can have.
    private const int UnregisteredGuestId = int.MaxValue;

    private const string BookableHotel = "SmartHotel Seattle Waterfront";
    private const string BookableRoomType = "Family";

    private const string BearerScheme = "Bearer";
    private const string ReadinessPath = "/api/health";

    // A command that reads the guests table, and authentication's lookup of the token's guest by primary key. The
    // bookings table's own Guests column is never read through FROM or JOIN, so it matches neither.
    private const string GuestsTablePattern = @"\b(?:FROM|JOIN) ""Guests""";
    private const string GuestLookupPattern =
        @"(?s)\bFROM ""Guests"" AS (?<guest>\w+)\b.*\bWHERE \k<guest>\.""Id"" = @\w+";

    // Revokes read access to the guests table from the role the API connects as, its owner, so every later read of it
    // fails with insufficient_privilege, an error that is not transient and so is never retried.
    private const string RevokeGuestReadsSql = @"REVOKE SELECT ON ""Guests"" FROM CURRENT_USER";

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
    [InlineData(UnknownGuest)]
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

    // The token is the accepted baseline without its sub claim, or with a sub that names no registered guest, so it
    // passes the signature, issuer, audience and lifetime checks. Without the guest checks during authentication, the
    // POST would fail binding with a 400 and the list and the cancellation would reach their actions. A bare 401 on
    // each shows the token is rejected before MVC binds anything.
    [Theory]
    [InlineData(MissingSub, ListEndpoint)]
    [InlineData(MissingSub, CreateWithUnbindableBodyEndpoint)]
    [InlineData(MissingSub, CancelEndpoint)]
    [InlineData(UnknownGuest, ListEndpoint)]
    [InlineData(UnknownGuest, CreateWithUnbindableBodyEndpoint)]
    [InlineData(UnknownGuest, CancelEndpoint)]
    public async Task BookingsEndpoint_TokenWithoutRegisteredGuest_Returns401BeforeBinding(
        string brokenElement,
        string endpoint)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        ApiTestClient.GuestSession guest = await client.RegisterGuestAsync(ct);
        string token = ForgeToken(guest.GuestId, brokenElement);

        using HttpResponseMessage response = await SendBookingsRequestAsync(client, endpoint, token, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.Unauthorized, cancellationToken: ct);
        ProblemAssert.AssertBareBearerChallenge(response);
    }

    // A validly signed token whose sub names no registered guest, as one signed before the database was reset does,
    // sends a body that books once a registered guest sends it. Rejected during authentication, the request never
    // reaches the insert that the missing guest row would fail, so it gets the bare 401 and stores no booking.
    [Fact]
    public async Task CreateBooking_UnknownGuestWithValidBody_Returns401AndStoresNoBooking()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ApiTestClient client = factory.CreateApiClient();
        ApiTestClient.GuestSession guest = await client.RegisterGuestAsync(ct);
        SmartHotelApiFactory.SeededRoomType roomType =
            await factory.GetRoomTypeAsync(BookableHotel, BookableRoomType, ct);
        DateOnly today = ApiTestClient.TodayUtc;
        JsonObject body = ApiTestClient.BookingBody(
            roomType.HotelId,
            roomType.Id,
            today.AddDays(30),
            today.AddDays(32),
            guests: 2);

        using HttpResponseMessage response = await client.CreateBookingAsync(
            body,
            ForgeToken(guest.GuestId, UnknownGuest),
            ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.Unauthorized, cancellationToken: ct);
        ProblemAssert.AssertBareBearerChallenge(response);
        int stored = await factory.ExecuteDbAsync(
            (db, cancellationToken) => db.Bookings
                .AsNoTracking()
                .CountAsync(booking => booking.GuestId == UnregisteredGuestId, cancellationToken),
            ct);
        Assert.Equal(0, stored);

        // The same body under the registered guest's token is booked, so only the unknown guest caused the 401.
        ApiTestClient.Booking booked = await client.CreateBookingOkAsync(body, ForgeToken(guest.GuestId, Baseline), ct);
        Assert.Equal(roomType.Id, booked.RoomTypeId);
    }

    // Hotel search, hotel detail and the readiness probe require no authorization, so a valid token sent to them,
    // whether or not it names a registered guest, costs no guest lookup: search runs its one query, detail its hotel
    // lookup and per-type count, the probe none, and nothing reads the guests table. The bookings list requires
    // authorization, and its first command is that lookup, which shows the capture records it when it runs. The host is
    // a variant with a database of its own whose capture receives EF Core's executed commands, and nothing else uses
    // the database between a mark and the response read after it.
    [Theory]
    [InlineData(Baseline, HttpStatusCode.OK)]
    [InlineData(UnknownGuest, HttpStatusCode.Unauthorized)]
    public async Task PublicEndpoints_ValidToken_RunNoGuestLookup(string brokenElement, HttpStatusCode bookingsStatus)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.LogCapture capture = new();
        await using SmartHotelApiFactory variant = factory.CreateLogCaptureVariant(
            capture,
            SmartHotelApiFactory.LogCapture.DataLayerRules,
            additionalOverrides: null);

        // A hand-built factory is not a fixture, so it is started here.
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();
        ApiTestClient.GuestSession guest = await client.RegisterGuestAsync(ct);
        int hotelId = await variant.GetHotelIdAsync(BookableHotel, ct);
        string token = ForgeToken(variant, guest.GuestId, brokenElement);
        DateOnly today = ApiTestClient.TodayUtc;

        int mark = capture.Count;
        using (HttpResponseMessage search = await GetWithBearerAsync(client, SearchPath(today), token, ct))
        {
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            List<ApiTestClient.HotelSummary> hotels =
                await client.ReadAsync<List<ApiTestClient.HotelSummary>>(search, ct);
            Assert.Contains(hotels, hotel => hotel.Id == hotelId);
        }

        string searchCommand = Assert.Single(ExecutedCommands(capture.Since(mark)));
        Assert.DoesNotMatch(GuestsTablePattern, searchCommand);

        mark = capture.Count;
        using (HttpResponseMessage detail = await GetWithBearerAsync(client, DetailPath(hotelId, today), token, ct))
        {
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            ApiTestClient.HotelDetail hotel = await client.ReadAsync<ApiTestClient.HotelDetail>(detail, ct);
            Assert.Equal(hotelId, hotel.Id);
        }

        string[] detailCommands = ExecutedCommands(capture.Since(mark));
        Assert.Equal(2, detailCommands.Length);
        Assert.All(detailCommands, static command => Assert.DoesNotMatch(GuestsTablePattern, command));

        mark = capture.Count;
        using (HttpResponseMessage health = await GetWithBearerAsync(client, ReadinessPath, token, ct))
        {
            await AssertHealthyAsync(health, ct);
        }

        Assert.Empty(ExecutedCommands(capture.Since(mark)));

        mark = capture.Count;
        using (HttpResponseMessage bookings = await client.ListBookingsAsync(token, ct))
        {
            Assert.Equal(bookingsStatus, bookings.StatusCode);
        }

        string[] bookingsCommands = ExecutedCommands(capture.Since(mark));
        Assert.NotEmpty(bookingsCommands);
        Assert.Matches(GuestLookupPattern, bookingsCommands[0]);
        Assert.All(bookingsCommands.Skip(1), static command => Assert.DoesNotMatch(GuestsTablePattern, command));
    }

    // Once the guest has registered, the guests table becomes unreadable to the API, so the guest lookup is the one
    // command that fails. Search and the readiness probe require no authorization, so the same token sent to them
    // still gets their 200. The bookings list requires it, so the failed lookup ends that request as the sanitized 500,
    // and the one record at Warning or above is the sanitized event, which names the route and the SQLSTATE. The host
    // is a variant with a database of its own.
    [Fact]
    public async Task GuestLookupFails_PublicEndpointsReturn200AndBookingsReturnSanitized500()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.LogCapture capture = new();
        await using SmartHotelApiFactory variant = factory.CreateLogCaptureVariant(
            capture,
            SmartHotelApiFactory.LogCapture.DataLayerRules,
            additionalOverrides: null);

        // A hand-built factory is not a fixture, so it is started here.
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();
        ApiTestClient.GuestSession guest = await client.RegisterGuestAsync(ct);
        DateOnly today = ApiTestClient.TodayUtc;

        await using (NpgsqlConnection connection = await variant.OpenDatabaseConnectionAsync(ct))
        {
            await using NpgsqlCommand revoke = new(RevokeGuestReadsSql, connection);
            await revoke.ExecuteNonQueryAsync(ct);
        }

        int mark = capture.Count;
        using (HttpResponseMessage search = await GetWithBearerAsync(client, SearchPath(today), guest.Token, ct))
        {
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        }

        using (HttpResponseMessage health = await GetWithBearerAsync(client, ReadinessPath, guest.Token, ct))
        {
            await AssertHealthyAsync(health, ct);
        }

        SmartHotelApiFactory.LogCapture.AssertNone(
            capture.Since(mark),
            static record => record.Level >= LogLevel.Warning,
            "record at Warning or above for the public requests");

        mark = capture.Count;
        using HttpResponseMessage response = await client.ListBookingsAsync(guest.Token, ct);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.InternalServerError, cancellationToken: ct);

        // Read only after ProblemAssert has decoded the buffered body. Each check is a boolean with a fixed message, so
        // a leak is never written to the test output.
        string body = await response.Content.ReadAsStringAsync(ct);
        Assert.False(
            body.Contains("permission denied", StringComparison.OrdinalIgnoreCase),
            "Expected the 500 problem body not to contain the database's error message.");
        Assert.False(
            body.Contains(nameof(PostgresException), StringComparison.Ordinal),
            "Expected the 500 problem body not to contain the exception type name.");
        Assert.False(
            body.Contains("   at ", StringComparison.Ordinal),
            "Expected the 500 problem body not to contain a stack frame.");

        SmartHotelApiFactory.CapturedLog error = Assert.Single(
            capture.Since(mark),
            static record => record.Level >= LogLevel.Warning);
        Assert.Equal(typeof(ApiExceptionHandler).FullName, error.Category);
        Assert.Contains("Route=api/bookings ", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "SqlState=" + PostgresErrorCodes.InsufficientPrivilege,
            error.Message,
            StringComparison.Ordinal);
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

    // Forges a token for the class's own host.
    private string ForgeToken(int guestId, string brokenElement)
    {
        return ForgeToken(factory, guestId, brokenElement);
    }

    // Builds the accepted baseline, then breaks only brokenElement. The baseline mirrors what the API issues and
    // validates: HS256 with the host's key, its issuer and audience, and a sub claim naming a registered guest,
    // without which JwtBearer rejects the token. Its nbf sits a minute in the past because the API allows no clock
    // skew. The cases that expire the token within IdentityModel's default clock skew, omit its expiration or sign it
    // with HS384 or HS512 keep the host's key, so only the zero clock skew, the required expiration time or the
    // HS256-only algorithm list rejects them.
    private static string ForgeToken(SmartHotelApiFactory host, int guestId, string brokenElement)
    {
        // Not a JWT at all, so no descriptor is built and nothing else about it can be at fault.
        if (string.Equals(brokenElement, Malformed, StringComparison.Ordinal))
        {
            return MalformedToken;
        }

        DateTime now = TimeProvider.System.GetUtcNow().UtcDateTime;
        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = host.JwtIssuer,
            Audience = host.JwtAudience,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(10),
            SigningCredentials = Hs256(host.SigningKey),
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
                Assert.NotEqual(host.JwtIssuer, descriptor.Issuer);
                break;
            case WrongAudience:
                descriptor.Audience = OtherParty;
                Assert.NotEqual(host.JwtAudience, descriptor.Audience);
                break;
            case AudienceTrailingSlash:
                // The configured audience with a trailing slash: the audience must match exactly.
                descriptor.Audience = host.JwtAudience + "/";
                break;
            case AlgNone:
                // With no signing credentials the handler emits an unsigned JWT: header alg "none" and an empty
                // signature segment.
                descriptor.SigningCredentials = null;
                break;
            case AlgHs384:
                // The host's 64-byte key meets HS384's 48-byte minimum, so the signature verifies under it.
                descriptor.SigningCredentials = Hmac(host.SigningKey, SecurityAlgorithms.HmacSha384);
                break;
            case AlgHs512:
                // The host's 64-byte key meets HS512's 64-byte minimum, so the signature verifies under it.
                descriptor.SigningCredentials = Hmac(host.SigningKey, SecurityAlgorithms.HmacSha512);
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
            case UnknownGuest:
                // A valid guest id that no registered guest has, so only the guest lookup rejects it.
                Assert.NotEqual(UnregisteredGuestId, guestId);
                descriptor.Claims[SubClaim] = UnregisteredGuestId.ToString(CultureInfo.InvariantCulture);
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

    // Sends a GET with the bearer token, which the client's hotel helpers never attach, as the web app does for a
    // signed-in guest. Disposing the request leaves the response, which the caller disposes.
    private static async Task<HttpResponseMessage> GetWithBearerAsync(
        ApiTestClient client,
        string path,
        string token,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, token);
        return await client.Http.SendAsync(request, cancellationToken);
    }

    // The valid baseline search: Seattle, from today+30 to today+32, for two guests.
    private static string SearchPath(DateOnly today)
    {
        return "/api/hotels?city=Seattle&" + StayQuery(today);
    }

    // The hotel's detail for the same stay and party.
    private static string DetailPath(int hotelId, DateOnly today)
    {
        return string.Create(CultureInfo.InvariantCulture, $"/api/hotels/{hotelId}?{StayQuery(today)}");
    }

    private static string StayQuery(DateOnly today)
    {
        return $"checkIn={ApiTestClient.Iso(today.AddDays(30))}&checkOut={ApiTestClient.Iso(today.AddDays(32))}"
            + "&guests=2";
    }

    // The readiness probe's one success: 200 with the text/plain body Healthy.
    private static async Task AssertHealthyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(cancellationToken));
    }

    // The SQL of each command EF Core executed, in order: the executed-command record's message ends with the command
    // text, and sensitive-data logging is off, so parameters show only as placeholders.
    private static string[] ExecutedCommands(IReadOnlyList<SmartHotelApiFactory.CapturedLog> records)
    {
        return
        [
            .. records
                .Where(static record => record.Category == DbLoggerCategory.Database.Command.Name
                    && record.EventId == RelationalEventId.CommandExecuted.Id)
                .Select(static record => record.Message),
        ];
    }
}