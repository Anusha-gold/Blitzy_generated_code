using Microsoft.AspNetCore.Identity;

using SmartHotel.Data.Entities;

namespace SmartHotel.Api.Modules.Auth;

/// <summary>
/// Registers the Auth module: guest registration, login and bearer-token issuance.
/// </summary>
/// <remarks>
/// The module registers only its own services. The host registers what they consume: <see cref="TimeProvider"/>,
/// the validated <c>JwtOptions</c>, <c>SmartHotelDbContext</c>, JwtBearer validation and authorization.
/// </remarks>
public static class AuthModule
{
    // OWASP's current PBKDF2-HMAC-SHA512 work factor. Identity's own default is 100,000.
    private const int PasswordHashIterations = 220_000;

    /// <summary>
    /// Adds the guest password hasher, <see cref="AuthService"/>, <see cref="TokenService"/> and the hosted service
    /// that creates the login dummy hash at start.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <see cref="IPasswordHasher{TUser}"/> of <see cref="Guest"/>, as ASP.NET Core Identity's
    /// <see cref="PasswordHasher{TUser}"/>, singleton. It keeps the default
    /// <see cref="PasswordHasherCompatibilityMode.IdentityV3"/> format: PBKDF2-HMAC-SHA512 with a 128-bit salt and a
    /// 256-bit subkey, here at 220,000 iterations. A stored hash made with fewer iterations verifies as
    /// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/>, and <see cref="AuthService"/> replaces it at the
    /// guest's next login. The hasher is stateless and thread-safe, and one shared instance lets
    /// <see cref="AuthService"/> build its once-per-process dummy hash with the same settings.
    /// </description>
    /// </item>
    /// <item>
    /// <description><see cref="AuthService"/>, scoped, beside the request's <c>SmartHotelDbContext</c>.</description>
    /// </item>
    /// <item>
    /// <description>
    /// <see cref="TokenService"/>, singleton. It depends only on singletons, <c>IOptions&lt;JwtOptions&gt;</c> and
    /// <see cref="TimeProvider"/>, and builds its signing credentials once.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// A hosted service that creates <see cref="AuthService"/>'s dummy hash with that hasher when the host starts. The
    /// host starts its hosted services one after another, and the web server's own comes after every service the
    /// application registers, so the hash exists before the server accepts requests. Every login that no account can
    /// match then performs one PBKDF2 verification and no hashing, the first one included, as a wrong password does.
    /// That keeps the hashing work comparable, not the timing constant.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// Only the hasher is taken from ASP.NET Core Identity: no Identity stores, managers or cookie schemes are
    /// registered.
    /// </para>
    /// </remarks>
    /// <param name="services">The API host's service collection.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAuthModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<PasswordHasherOptions>(options => options.IterationCount = PasswordHashIterations);
        services.AddSingleton<IPasswordHasher<Guest>, PasswordHasher<Guest>>();
        services.AddScoped<AuthService>();
        services.AddSingleton<TokenService>();
        services.AddHostedService<DummyHashWarmup>();

        return services;
    }

    /// <summary>
    /// Creates <see cref="AuthService"/>'s dummy hash while the host starts, so no login has to.
    /// </summary>
    /// <param name="passwordHasher">The host's singleton hasher, the one every <see cref="AuthService"/> uses.</param>
    private sealed class DummyHashWarmup(IPasswordHasher<Guest> passwordHasher) : IHostedService
    {
        /// <summary>
        /// Creates the dummy hash unless the process already holds one. The work is one PBKDF2 hash and completes
        /// before the host starts the next service.
        /// </summary>
        /// <param name="cancellationToken">Signals that the start was abandoned; the hash is then not created.</param>
        /// <returns>A completed task.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _ = AuthService.EnsureDummyHash(passwordHasher);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Does nothing: the hash lives as long as the process.
        /// </summary>
        /// <param name="cancellationToken">Unused, because stopping has no work.</param>
        /// <returns>A completed task.</returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}