using System.Text;

using Microsoft.Extensions.Options;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Settings for issuing and validating the API's HS256 bearer tokens, bound from the <c>Jwt</c>
/// configuration section.
/// </summary>
/// <remarks>
/// This is a class rather than a record because a record's synthesized <c>ToString</c> would print the
/// signing key.
/// </remarks>
public sealed class JwtOptions
{
    /// <summary>
    /// The configuration section the options are bound from.
    /// </summary>
    public const string SectionName = "Jwt";

    /// <summary>
    /// The shortest signing key accepted, in UTF-8 bytes: RFC 7518 requires an HS256 key of at least
    /// 256 bits.
    /// </summary>
    public const int MinimumSigningKeyBytes = 32;

    /// <summary>
    /// Gets or sets the token issuer (<c>iss</c>). <c>appsettings.json</c> supplies <c>SmartHotel.Api</c>.
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the token audience (<c>aud</c>). <c>appsettings.json</c> supplies <c>SmartHotel.Web</c>.
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the HS256 signing key text.
    /// </summary>
    /// <remarks>
    /// The key is filled only from <see cref="ISecretSource.JwtSigningKey"/>, by <c>Program.cs</c>'s
    /// <c>PostConfigure</c> step, which overwrites any configured value. A key placed in a JSON file is
    /// therefore ignored, and validation fails when the environment supplies none.
    /// </remarks>
    public string? SigningKey { get; set; }
}

/// <summary>
/// Validates <see cref="JwtOptions"/> when the options are first resolved, so a missing issuer, audience
/// or signing key stops the API before any database work.
/// </summary>
public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    /// <summary>
    /// Checks every setting and reports all failures together. Each message names the setting and never
    /// contains the signing key, its length or any part of it.
    /// </summary>
    /// <param name="name">The options name. Every name is validated alike.</param>
    /// <param name="options">The bound and post-configured options.</param>
    /// <returns>Success, or a failure listing every invalid setting.</returns>
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add("Jwt__Issuer (Jwt:Issuer) must be set.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add("Jwt__Audience (Jwt:Audience) must be set.");
        }

        if (options.SigningKey is null
            || Encoding.UTF8.GetByteCount(options.SigningKey) < JwtOptions.MinimumSigningKeyBytes)
        {
            failures.Add("Jwt__SigningKey must be at least 32 bytes in UTF-8.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Settings for the API's single cross-origin policy, bound from the <c>Cors</c> configuration section.
/// </summary>
public sealed class ApiCorsOptions
{
    /// <summary>
    /// The configuration section the options are bound from.
    /// </summary>
    public const string SectionName = "Cors";

    /// <summary>
    /// The name of the one CORS policy, which <c>Program.cs</c> registers and applies.
    /// </summary>
    public const string PolicyName = "web";

    /// <summary>
    /// Gets or sets the only origin allowed to call the API: the web app's serialized origin,
    /// <c>scheme://host[:port]</c> with no trailing <c>/</c>.
    /// </summary>
    /// <remarks>
    /// Set from the environment variable <c>Cors__AllowedOrigin</c>, which Compose derives from the web
    /// port (for example <c>http://localhost:4200</c>), or from in-memory configuration in the
    /// integration tests.
    /// </remarks>
    public string? AllowedOrigin { get; set; }
}

/// <summary>
/// Validates <see cref="ApiCorsOptions"/>: the allowed origin must be a serialized origin, and outside
/// Development it must be an <c>https://</c> origin.
/// </summary>
/// <remarks>
/// Browsers send the <c>Origin</c> header in serialized form: a lower-case scheme and host, no default
/// port and no path. A configured value in any other form, such as one with a trailing <c>/</c>, would
/// never match a request and would silently block the web app, so it is rejected at startup instead.
/// </remarks>
/// <param name="environment">The host environment, which decides whether plain <c>http</c> is allowed.</param>
public sealed class ApiCorsOptionsValidator(IHostEnvironment environment) : IValidateOptions<ApiCorsOptions>
{
    /// <summary>
    /// Checks the allowed origin and reports the first rule it breaks.
    /// </summary>
    /// <param name="name">The options name. Every name is validated alike.</param>
    /// <param name="options">The bound options.</param>
    /// <returns>Success, or a failure naming the first broken rule.</returns>
    public ValidateOptionsResult Validate(string? name, ApiCorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? value = options.AllowedOrigin;
        if (string.IsNullOrWhiteSpace(value))
        {
            return ValidateOptionsResult.Fail("Cors__AllowedOrigin must be set to the web app's origin.");
        }

        // The origin itself is not secret, but a malformed value can carry credentials (user:password@host),
        // so no message echoes the raw value. Only the last shape check shows the parsed origin, and by then
        // user information has been ruled out.
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return ValidateOptionsResult.Fail("Cors__AllowedOrigin must be an absolute URI.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return ValidateOptionsResult.Fail("Cors__AllowedOrigin must use the http or https scheme.");
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return ValidateOptionsResult.Fail(
                "Cors__AllowedOrigin must not contain user information, a query or a fragment.");
        }

        if (uri.AbsolutePath != "/")
        {
            return ValidateOptionsResult.Fail("Cors__AllowedOrigin must not contain a path.");
        }

        // Uri normalizes the authority, so a value that differs from it has a trailing '/', upper-case
        // letters, an explicit default port or surrounding whitespace.
        string serializedOrigin = uri.GetLeftPart(UriPartial.Authority);
        if (!string.Equals(value, serializedOrigin, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                $"Cors__AllowedOrigin must be a serialized origin, written exactly as {serializedOrigin}.");
        }

        if (!environment.IsDevelopment() && uri.Scheme != Uri.UriSchemeHttps)
        {
            return ValidateOptionsResult.Fail("Cors__AllowedOrigin must be an https:// origin outside Development.");
        }

        return ValidateOptionsResult.Success;
    }
}