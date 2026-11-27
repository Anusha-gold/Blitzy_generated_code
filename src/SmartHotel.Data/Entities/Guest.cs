namespace SmartHotel.Data.Entities;

/// <summary>
/// A registered guest, the only user type in version 1. Email and full name are the only
/// personal data stored about a guest.
/// </summary>
public sealed class Guest
{
    /// <summary>
    /// Database name of the unique index on <see cref="Email"/>. PostgreSQL reports it as the
    /// <c>ConstraintName</c> of the unique violation (SQLSTATE <c>23505</c>) that a duplicate email
    /// raises, and the API maps exactly that violation to a duplicate registration.
    /// </summary>
    public const string EmailUniqueIndexName = "UX_Guests_Email";

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
    /// Registration instant in UTC. Callers must assign a finite instant with
    /// <see cref="DateTimeKind.Utc"/>, taken from the injected <see cref="TimeProvider"/>.
    /// Npgsql rejects a finite value of any other kind for <c>timestamp with time zone</c>,
    /// but stores <see cref="DateTime.MinValue"/>, which an unassigned property holds, as
    /// <c>-infinity</c> and <see cref="DateTime.MaxValue"/> as <c>infinity</c>, whatever
    /// their kind.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}