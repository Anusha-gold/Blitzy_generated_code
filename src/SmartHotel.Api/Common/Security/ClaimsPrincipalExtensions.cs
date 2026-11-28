using System.Globalization;
using System.Security.Claims;

using Microsoft.IdentityModel.JsonWebTokens;

namespace SmartHotel.Api.Common.Security;

/// <summary>
/// Reads the authenticated guest's identity from JWT claims, so modules never reference <c>Modules/Auth</c>.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Reads the guest id carried in the <c>sub</c> claim, without throwing when it is unusable.
    /// </summary>
    /// <param name="principal">The user to read, such as the principal JwtBearer has just validated.</param>
    /// <param name="guestId">
    /// When this method returns <see langword="true"/>, the guest id, which is at least 1; otherwise 0.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the first <c>sub</c> claim parses as an <see cref="int"/> of at least 1 under
    /// <see cref="NumberStyles.None"/> and the invariant culture; <see langword="false"/> when the claim is missing or
    /// does not.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// This is the single guest-id rule. JwtBearer applies it when a token is validated, so a token that fails it is
    /// rejected with a 401 before any action runs, and <see cref="GetGuestId"/> applies it again for the caller's id.
    /// JwtBearer runs with <c>MapInboundClaims = false</c>, so the claim keeps its registered name <c>sub</c> and no
    /// other claim, such as <see cref="ClaimTypes.NameIdentifier"/>, is consulted.
    /// </remarks>
    public static bool TryGetGuestId(this ClaimsPrincipal principal, out int guestId)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var value = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 1)
        {
            guestId = id;
            return true;
        }

        guestId = 0;
        return false;
    }

    /// <summary>
    /// Returns the guest id carried in the <c>sub</c> claim.
    /// </summary>
    /// <param name="principal">The authenticated user, typically <c>ControllerBase.User</c>.</param>
    /// <returns>The guest id, which is at least 1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The <c>sub</c> claim is missing, or the first one does not parse as an <see cref="int"/> of at least 1 under
    /// <see cref="NumberStyles.None"/> and the invariant culture.
    /// </exception>
    /// <remarks>
    /// The claim is read by the rule of <see cref="TryGetGuestId"/>. JwtBearer already rejects every token that fails
    /// that rule, so for a principal it authenticated this method does not throw. The exception message is a fixed
    /// sentence and never includes claim values.
    /// </remarks>
    public static int GetGuestId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.TryGetGuestId(out var guestId)
            ? guestId
            : throw new InvalidOperationException("The authenticated principal has no valid guest identifier.");
    }
}