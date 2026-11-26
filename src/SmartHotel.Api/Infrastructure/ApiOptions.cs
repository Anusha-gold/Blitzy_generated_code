using System.Globalization;
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
    /// therefore ignored, and validation fails when the environment supplies none or a blank
    /// (whitespace-only) one.
    /// </remarks>
    public string? SigningKey { get; set; }
}

/// <summary>
/// Validates <see cref="JwtOptions"/> when the options are first resolved, so a missing issuer or audience,
/// or a missing, blank or short signing key, stops the API before any database work.
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

        // A blank key is rejected before its length is measured: whitespace can reach the minimum byte
        // count and would still be predictable key material. The key is measured as given, never trimmed.
        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            failures.Add("Jwt__SigningKey must be set.");
        }
        else if (Encoding.UTF8.GetByteCount(options.SigningKey) < JwtOptions.MinimumSigningKeyBytes)
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
/// Browsers send the <c>Origin</c> header in serialized form: a lower-case scheme, the host in ASCII
/// (internationalized labels in punycode, an IPv4 address in dotted decimal, an IPv6 address in brackets
/// in compressed hexadecimal), the port only when it is not the scheme's default, and no user information
/// or path. A configured value in any other form, such as one with a trailing <c>/</c>, would never match
/// a request and would silently block the web app, so it is rejected at startup instead.
/// <para>
/// An IPv6 address that <see cref="Uri"/> writes with a dotted IPv4 tail (an IPv4-mapped, IPv4-compatible,
/// IPv4-translated or ISATAP address) is rejected in every spelling. <c>CorsPolicyBuilder.WithOrigins</c>
/// rebuilds an IPv6 origin through <see cref="Uri"/>, so the policy would hold the dotted form, while a
/// browser writes every piece in hexadecimal: no configured value for such an address could ever match.
/// </para>
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
        // so no message echoes the raw value. Only the last shape check shows a value: the origin rebuilt
        // from the parsed scheme, host and port, which never contains user information.
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

        // Browsers send the host in ASCII and refuse a host the IDN rules reject, so such a value never
        // matches the Origin header.
        string? asciiHost = ReadAsciiHost(uri);
        if (asciiHost is null)
        {
            return ValidateOptionsResult.Fail(
                "Cors__AllowedOrigin must have a host that is a valid internationalized domain name.");
        }

        // Browsers read a host whose last label is a number as an IPv4 address: they rewrite it in dotted
        // decimal, or refuse the URL when it is not a valid address. Uri parses such a host as IPv4 only
        // when it is a valid address without a trailing '.', so any other host ending in a number never
        // matches the Origin header.
        if (uri.HostNameType != UriHostNameType.IPv4
            && uri.HostNameType != UriHostNameType.IPv6
            && EndsInNumber(asciiHost))
        {
            return ValidateOptionsResult.Fail(
                "Cors__AllowedOrigin must write a host that ends in a number as a dotted-decimal IPv4 address.");
        }

        // Uri writes an IPv4-mapped, IPv4-compatible, IPv4-translated or ISATAP address with a dotted IPv4
        // tail, and CorsPolicyBuilder.WithOrigins stores the origin in that form. Browsers write every piece
        // in hexadecimal, so the policy could never match such an address, however the value spells it.
        if (uri.HostNameType == UriHostNameType.IPv6 && uri.Host.Contains('.'))
        {
            return ValidateOptionsResult.Fail(
                "Cors__AllowedOrigin must not use an IPv6 address that embeds an IPv4 address; write the IPv4 address instead.");
        }

        // The serialized origin is rebuilt from the parsed scheme, host and port the way a browser writes the
        // Origin header. A value that differs from it has, for example, a trailing '/', upper-case letters,
        // Unicode host labels, an empty user-information delimiter ('@' with nothing before it), an IPv6
        // address in other than its shortest form or with a zone identifier, an explicit default port or
        // surrounding whitespace.
        string serializedOrigin = SerializeOrigin(uri, asciiHost);
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

    /// <summary>
    /// Reads the host of <paramref name="uri"/> in lower-case ASCII, as browsers send it: internationalized
    /// labels become punycode, and label separators such as the ideographic full stop become <c>.</c>.
    /// </summary>
    /// <param name="uri">An absolute <c>http</c> or <c>https</c> URI.</param>
    /// <returns>
    /// The ASCII host, or <see langword="null"/> when the IDN rules reject it because a label holds a
    /// disallowed character, such as an unassigned code point.
    /// </returns>
    private static string? ReadAsciiHost(Uri uri)
    {
        try
        {
            return uri.IdnHost;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reports whether <paramref name="host"/> ends in a number the way browsers judge it: its last label,
    /// ignoring one trailing <c>.</c>, is all decimal digits, or <c>0x</c> followed only by hexadecimal
    /// digits.
    /// </summary>
    /// <param name="host">An ASCII host name.</param>
    /// <returns><see langword="true"/> when browsers would parse the host as an IPv4 address.</returns>
    private static bool EndsInNumber(string host)
    {
        string[] labels = host.Split('.');
        int last = labels.Length - 1;
        if (labels[last].Length == 0 && last > 0)
        {
            last--;
        }

        string label = labels[last];
        if (label.Length > 0 && label.All(char.IsAsciiDigit))
        {
            return true;
        }

        return label.Length >= 2
            && label[0] == '0'
            && (label[1] == 'x' || label[1] == 'X')
            && label[2..].All(char.IsAsciiHexDigit);
    }

    /// <summary>
    /// Writes the origin of <paramref name="uri"/> as a browser serializes it in the <c>Origin</c> header:
    /// the lower-case scheme, <c>://</c>, the ASCII host, and <c>:port</c> only when the port is not the
    /// scheme's default. User information is never part of it.
    /// </summary>
    /// <param name="uri">
    /// An absolute <c>http</c> or <c>https</c> URI. An IPv6 host must not be one that <see cref="Uri"/> writes
    /// with a dotted IPv4 tail, which <see cref="Validate"/> rejects before calling this method.
    /// </param>
    /// <param name="asciiHost">The host as <see cref="ReadAsciiHost"/> returned it.</param>
    /// <returns>The serialized origin.</returns>
    private static string SerializeOrigin(Uri uri, string asciiHost)
    {
        string host = asciiHost;
        if (uri.HostNameType == UriHostNameType.IPv6)
        {
            // IdnHost drops the brackets and keeps the zone identifier. Uri.Host brackets the address, drops
            // the zone identifier and writes the pieces in lower-case hexadecimal without leading zeros, with
            // the first longest run of two or more zero pieces as "::": a browser's form for every IPv6
            // address without a dotted IPv4 tail.
            host = uri.Host;
        }

        return uri.IsDefaultPort
            ? uri.Scheme + "://" + host
            : uri.Scheme + "://" + host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
    }
}