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
    /// Adds the guest password hasher, <see cref="AuthService"/> and <see cref="TokenService"/>.
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

        return services;
    }
}