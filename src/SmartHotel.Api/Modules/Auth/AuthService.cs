using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using SmartHotel.Data;
using SmartHotel.Data.Entities;

namespace SmartHotel.Api.Modules.Auth;

/// <summary>
/// Registers guests and checks their credentials at login, issuing a bearer token when they match.
/// </summary>
/// <remarks>
/// <para>
/// Every email passes through <see cref="NormalizeEmail"/> before it is stored or looked up, and the lookup is plain
/// equality on the stored column: the database never folds case.
/// </para>
/// <para>
/// Passwords are hashed with the injected <see cref="IPasswordHasher{TUser}"/>, which <c>AuthModule</c> configures as
/// PBKDF2-HMAC-SHA512 with a 128-bit salt, a 256-bit subkey and 220,000 iterations, stored as the V3-format string.
/// The hash is never returned.
/// </para>
/// <para>
/// Registered scoped, beside the request's <see cref="SmartHotelDbContext"/>. The only values it logs are guest ids:
/// never an email address, password, hash or token.
/// </para>
/// </remarks>
/// <param name="db">The request's database session.</param>
/// <param name="passwordHasher">The singleton hasher carrying the configured iteration count.</param>
/// <param name="tokenService">Issues the bearer token for a successful login.</param>
/// <param name="timeProvider">The clock that stamps <see cref="Guest.CreatedAt"/>.</param>
/// <param name="logger">Receives the registration and failed-login events.</param>
public sealed partial class AuthService(
    SmartHotelDbContext db,
    IPasswordHasher<Guest> passwordHasher,
    TokenService tokenService,
    TimeProvider timeProvider,
    ILogger<AuthService> logger)
{
    // The unique index on "Guests"."Email", named in GuestConfiguration. PostgreSQL reports it as the
    // ConstraintName of the 23505 that a concurrent duplicate registration raises.
    private const string EmailUniqueIndex = "UX_Guests_Email";

    // One hash of a random secret per process, verified against when no account can match, so that every rejected
    // login costs one PBKDF2 verification. Every host in the process configures the same iteration count, so
    // sharing it across instances keeps the cost equal to a real account's. The lock makes concurrent first use
    // compute the hash once rather than merely publish one of several computed results.
    private static string? s_dummyHash;
    private static object? s_dummyHashLock;

    /// <summary>
    /// Folds an email address into its stored form: trimmed, then lower-cased with the invariant culture.
    /// </summary>
    /// <remarks>
    /// This is the single email fold. Registration stores its result, login looks guests up by it, and the
    /// database compares it with plain equality, so the unique index <c>UX_Guests_Email</c> enforces case-insensitive
    /// uniqueness. Registration accepts only printable ASCII, on which <see cref="string.ToLowerInvariant"/> maps
    /// exactly <c>A-Z</c> to <c>a-z</c> under every globalization mode, invariant mode included. The
    /// check constraint <c>CK_Guests_Email_Normalized</c> refuses any stored value that is not already in this form.
    /// </remarks>
    /// <param name="email">The address as entered.</param>
    /// <returns>The normalized address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="email"/> is <see langword="null"/>.</exception>
    public static string NormalizeEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        return email.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Creates a guest from a registration request that has passed model validation.
    /// </summary>
    /// <remarks>
    /// The email is stored normalized, the full name trimmed, and the password hashed exactly as entered. A taken
    /// email is found by a pre-check, or, when a concurrent registration of the same address wins the race, by the
    /// unique-violation PostgreSQL reports on <c>UX_Guests_Email</c>; both give
    /// <see cref="RegisterResult.EmailTaken"/>. Every other database error, any other unique violation included,
    /// propagates.
    /// </remarks>
    /// <param name="request">The validated request; none of its members is <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <returns>
    /// <see cref="RegisterResult.Created"/> with the new guest's id, or <see cref="RegisterResult.EmailTaken"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A member of <paramref name="request"/> is <see langword="null"/>.</exception>
    public async Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Email is null || request.Password is null || request.FullName is null)
        {
            throw new ArgumentException("A registration request must pass model validation first.", nameof(request));
        }

        string email = NormalizeEmail(request.Email);
        if (await db.Guests.AnyAsync(g => g.Email == email, cancellationToken))
        {
            return RegisterResult.EmailTaken.Instance;
        }

        Guest guest = new()
        {
            Email = email,
            FullName = request.FullName.Trim(),
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        };
        guest.PasswordHash = passwordHasher.HashPassword(guest, request.Password);

        db.Guests.Add(guest);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: EmailUniqueIndex,
        })
        {
            // The rejected insert stays tracked as Added; detaching it keeps a later save in this scope from
            // retrying it.
            db.Entry(guest).State = EntityState.Detached;
            return RegisterResult.EmailTaken.Instance;
        }

        LogGuestRegistered(logger, guest.Id);
        return new RegisterResult.Created(guest.Id);
    }

    /// <summary>
    /// Checks a guest's credentials and, when they match, issues a bearer token valid for 60 minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The response does not reveal whether an account exists. A <see langword="null"/> or blank email or password,
    /// an email that no stored address can match because it holds a character outside printable ASCII, an unknown
    /// email and a wrong password all return <see langword="null"/>, which the controller maps to one fixed 401.
    /// When no account can match, the password (or an empty string in place of <see langword="null"/>)
    /// is still verified against a dummy hash made with the same settings, so every rejection performs one PBKDF2
    /// verification. That makes the hashing work comparable, not the timing constant: the database lookup can
    /// still take different times.
    /// </para>
    /// <para>
    /// A stored hash made with an older format or fewer iterations is replaced with a current one after a
    /// successful verification.
    /// </para>
    /// </remarks>
    /// <param name="request">The bound request; its members may be <see langword="null"/> or blank.</param>
    /// <param name="cancellationToken">Cancels the database work.</param>
    /// <returns>
    /// The token and its expiry, or <see langword="null"/> when the credentials are invalid.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return RejectWithoutAccount(request.Password);
        }

        string email = NormalizeEmail(request.Email);

        // Stored addresses are printable ASCII only: registration accepts nothing else and CK_Guests_Email_Normalized
        // enforces it. A value with any other character can never match an account, and some (U+0000) cannot even
        // be sent as PostgreSQL text, so it takes the no-account path without a query.
        if (email.AsSpan().ContainsAnyExceptInRange('!', '~'))
        {
            return RejectWithoutAccount(request.Password);
        }

        Guest? guest = await db.Guests.FirstOrDefaultAsync(g => g.Email == email, cancellationToken);
        if (guest is null)
        {
            return RejectWithoutAccount(request.Password);
        }

        switch (passwordHasher.VerifyHashedPassword(guest, guest.PasswordHash, request.Password))
        {
            case PasswordVerificationResult.Success:
                break;

            case PasswordVerificationResult.SuccessRehashNeeded:
                guest.PasswordHash = passwordHasher.HashPassword(guest, request.Password);
                await db.SaveChangesAsync(cancellationToken);
                break;

            default:
                // Failed, and any result the hasher may add later: anything but an explicit success is a rejection.
                LogLoginFailed(logger);
                return null;
        }

        (string token, DateTime expiresAtUtc) = tokenService.Issue(guest);
        return new LoginResponse(token, expiresAtUtc);
    }

    private LoginResponse? RejectWithoutAccount(string? password)
    {
        // The result is discarded: only the work matters. The hasher throws on a null password.
        _ = passwordHasher.VerifyHashedPassword(new Guest(), GetDummyHash(), password ?? string.Empty);
        LogLoginFailed(logger);
        return null;
    }

    private string GetDummyHash()
    {
        return LazyInitializer.EnsureInitialized(
            ref s_dummyHash,
            ref s_dummyHashLock,
            () => passwordHasher.HashPassword(new Guest(), Guid.NewGuid().ToString("N")));
    }

    [LoggerMessage(
        EventId = 1001,
        EventName = "GuestRegistered",
        Level = LogLevel.Information,
        Message = "Guest registered. GuestId={GuestId}")]
    private static partial void LogGuestRegistered(ILogger logger, int guestId);

    // Carries no identifying data: no email, guest id or reason, so the log cannot tell accounts apart either.
    [LoggerMessage(
        EventId = 1002,
        EventName = "LoginFailed",
        Level = LogLevel.Warning,
        Message = "Failed login.")]
    private static partial void LogLoginFailed(ILogger logger);
}