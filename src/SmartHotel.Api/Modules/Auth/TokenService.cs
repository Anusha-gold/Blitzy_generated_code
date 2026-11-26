using System.Globalization;
using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using SmartHotel.Api.Infrastructure;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.Modules.Auth;

/// <summary>
/// Issues the HS256 bearer tokens that <c>POST /api/auth/login</c> returns, each valid for exactly 60 minutes.
/// </summary>
/// <remarks>
/// Registered as a singleton by <c>AuthModule</c>. The class holds no per-request state, and the token
/// handler and signing credentials it shares across requests are thread-safe. It logs nothing, so the token,
/// the signing key and the claims never reach a log.
/// </remarks>
public sealed class TokenService
{
    // The 60-minute lifetime: exp is always exactly this far after iat.
    private const int LifetimeSeconds = 3600;

    // Without this flag the handler fills a missing iat, nbf or exp from its own clock, bypassing the injected
    // TimeProvider; the descriptor always sets all three explicitly.
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly string _issuer;
    private readonly string _audience;
    private readonly SigningCredentials _signingCredentials;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the service and builds its HS256 signing credentials once.
    /// </summary>
    /// <param name="options">
    /// The validated JWT settings. <c>Program.cs</c> fills <see cref="JwtOptions.SigningKey"/> only from the
    /// environment and validates it as at least 32 UTF-8 bytes before any request resolves this service.
    /// </param>
    /// <param name="timeProvider">The clock that supplies the token times.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No signing key is configured.</exception>
    public TokenService(IOptions<JwtOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        JwtOptions settings = options.Value;
        string signingKey = settings.SigningKey
            ?? throw new InvalidOperationException("Jwt__SigningKey must be set before tokens can be issued.");

        _issuer = settings.Issuer;
        _audience = settings.Audience;
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Issues a signed bearer token for <paramref name="guest"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One base instant, the current UTC time truncated to whole seconds, sets every time in the token:
    /// <c>iat</c> and <c>nbf</c> equal the base, and <c>exp</c> is the base plus exactly 3,600 seconds.
    /// </para>
    /// <para>
    /// The token is signed with HS256 and carries <c>iss</c> and <c>aud</c> from <see cref="JwtOptions"/>, plus
    /// exactly four claims: <c>sub</c> (the guest id in invariant-culture digits), <c>email</c> (the stored,
    /// normalized address), <c>name</c> (the full name) and <c>jti</c> (a random token id). It carries no role
    /// claim, because guests are the only user type.
    /// </para>
    /// </remarks>
    /// <param name="guest">The authenticated guest.</param>
    /// <returns>
    /// <c>Token</c>, the compact JWS; and <c>ExpiresAtUtc</c>, the token's <c>exp</c> instant as a
    /// <see cref="DateTimeKind.Utc"/> value with no fractional seconds, so it serializes as
    /// <c>yyyy-MM-ddTHH:mm:ssZ</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="guest"/> is <see langword="null"/>.</exception>
    public (string Token, DateTime ExpiresAtUtc) Issue(Guest guest)
    {
        ArgumentNullException.ThrowIfNull(guest);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTime issuedUtc = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()).UtcDateTime;
        DateTime expiresUtc = issuedUtc.AddSeconds(LifetimeSeconds);

        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = _issuer,
            Audience = _audience,
            IssuedAt = issuedUtc,
            NotBefore = issuedUtc,
            Expires = expiresUtc,
            SigningCredentials = _signingCredentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = guest.Id.ToString(CultureInfo.InvariantCulture),
                [JwtRegisteredClaimNames.Email] = guest.Email,
                [JwtRegisteredClaimNames.Name] = guest.FullName,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            },
        };

        return (_handler.CreateToken(descriptor), expiresUtc);
    }
}