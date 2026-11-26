namespace SmartHotel.Data.Entities;

/// <summary>
/// A registered guest, the only user type in version 1. Email and full name are the only
/// personal data stored about a guest.
/// </summary>
public sealed class Guest
{
    /// <summary>
    /// Database-generated identity. Exposed to clients as <c>guestId</c> and carried as the
    /// token's <c>sub</c> claim.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Email address, stored already normalized by the API: trimmed, lower-case and printable
    /// ASCII. Unique across all guests; at most 254 characters.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// V3-format PBKDF2 hash produced by ASP.NET Core Identity's <c>PasswordHasher</c>.
    /// The API never returns it.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// Display name, trimmed, at most 200 characters.
    /// </summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// Registration instant in UTC. The value must have <see cref="DateTimeKind.Utc"/>,
    /// because Npgsql rejects any other kind for <c>timestamp with time zone</c>.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}