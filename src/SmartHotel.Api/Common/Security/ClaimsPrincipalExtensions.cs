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
    /// Returns the guest id carried in the <c>sub</c> claim.
    /// </summary>
    /// <param name="principal">The authenticated user, typically <c>ControllerBase.User</c>.</param>
    /// <returns>The guest id, which is at least 1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The <c>sub</c> claim is missing, or is not a positive integer written in invariant-culture digits.
    /// </exception>
    /// <remarks>
    /// JwtBearer runs with <c>MapInboundClaims = false</c>, so the claim keeps its registered name <c>sub</c>
    /// and no other claim, such as <see cref="ClaimTypes.NameIdentifier"/>, is consulted. The exception message
    /// is a fixed sentence and never includes claim values.
    /// </remarks>
    public static int GetGuestId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var value = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 1
            ? id
            : throw new InvalidOperationException("The authenticated principal has no valid guest identifier.");
    }
}