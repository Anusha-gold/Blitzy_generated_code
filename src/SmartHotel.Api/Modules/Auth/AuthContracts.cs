using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;

namespace SmartHotel.Api.Modules.Auth;

// Request records declare every member as string? so that, with nullable reference types on, MVC adds no
// implicit [Required] of its own; the explicit attributes below are the only required checks. Validation
// attributes sit on the positional parameters because MVC throws when they target a record's properties.
//
// [ModelBinder(Name)] sets each parameter's key in the 400 errors object. For positional record parameters MVC
// keys attribute errors by the CLR parameter name (Email) and ignores SystemTextJsonValidationMetadataProvider,
// while the error contract keys them by the camelCase JSON name (email). The name does not affect body binding.

/// <summary>
/// Body of <c>POST /api/auth/register</c>. The attributes run first; <see cref="Validate"/> then applies the
/// bounds that depend on the trimmed values. Error messages are fixed sentences that never echo the input.
/// </summary>
/// <param name="Email">
/// Required. After trimming: printable ASCII only (U+0021 to U+007E), at most 254 characters (the RFC 5321 path
/// limit), and an RFC 5321 mailbox: a local part of at most 64 characters, either dot-separated atoms or a quoted
/// string, then <c>@</c>, then either a domain name whose dot-separated labels are letters, digits and inner
/// hyphens of at most 63 characters each, or an RFC 5321 address literal: an IPv4 address such as
/// <c>user@[192.0.2.1]</c> or a tagged IPv6 address such as <c>user@[IPv6:2001:db8::1]</c>. The check is syntax
/// only; the domain is never resolved or contacted.
/// </param>
/// <param name="Password">
/// Required, 8 to 128 characters, and not trimmed. A whitespace-only value is rejected as missing.
/// </param>
/// <param name="FullName">
/// Required. After trimming: not blank, at most 200 characters, and free of the null character (U+0000), which
/// PostgreSQL text cannot store.
/// </param>
public sealed partial record RegisterRequest(
    [ModelBinder(Name = RegisterRequest.EmailMember)]
    [Required(ErrorMessage = "email is required.")]
    string? Email,
    [ModelBinder(Name = RegisterRequest.PasswordMember)]
    [Required(ErrorMessage = "password is required and must not be blank.")]
    [StringLength(128, MinimumLength = 8, ErrorMessage = "password must be 8 to 128 characters long.")]
    string? Password,
    [ModelBinder(Name = RegisterRequest.FullNameMember)]
    [Required(ErrorMessage = "fullName is required and must not be blank.")]
    string? FullName)
    : IValidatableObject
{
    private const int MaxEmailLength = 254;
    private const int MaxLocalPartLength = 64;
    private const int MaxFullNameLength = 200;

    // The IPv6-address-literal tag, compared without regard to case.
    private const string IPv6Tag = "IPv6:";

    // The camelCase JSON names. They key both the attribute errors (through ModelBinder) and the Validate
    // errors, whose member names MVC uses verbatim.
    private const string EmailMember = "email";
    private const string PasswordMember = "password";
    private const string FullNameMember = "fullName";

    private const string EmailMessage = "email must be a valid ASCII email address of at most 254 characters.";
    private const string FullNameMessage = "fullName must not be blank and must be at most 200 characters.";
    private const string FullNameNulMessage = "fullName must not contain the null character (U+0000).";

    /// <summary>
    /// Applies the email and full-name bounds to the trimmed values, yielding at most one error per field.
    /// A <see langword="null"/> member is skipped, because its <see cref="RequiredAttribute"/> reports it.
    /// </summary>
    /// <param name="validationContext">The context supplied by the validator; the checks do not use it.</param>
    /// <returns>The failed checks, keyed <c>email</c> and <c>fullName</c>.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Email is not null && !IsAcceptableEmail(Email.Trim()))
        {
            yield return new ValidationResult(EmailMessage, [EmailMember]);
        }

        if (FullName is not null)
        {
            string trimmed = FullName.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxFullNameLength)
            {
                yield return new ValidationResult(FullNameMessage, [FullNameMember]);
            }
            else if (trimmed.Contains('\0'))
            {
                // JSON can carry U+0000, but PostgreSQL text cannot store it, so the insert would fail.
                yield return new ValidationResult(FullNameNulMessage, [FullNameMember]);
            }
        }
    }

    private static bool IsAcceptableEmail(string trimmed)
    {
        // Internationalized addresses are outside v1: only printable ASCII is accepted, which keeps the
        // single lower-case fold in AuthService.NormalizeEmail exact under every globalization mode. The cheap
        // checks run first, so the patterns only ever see at most 254 printable ASCII characters. RFC 5321 section
        // 4.1.2 Mailbox = Local-part "@" ( Domain / address-literal ). The last @ separates the parts, because a
        // quoted local part may contain @ and neither form of the part after it can.
        if (trimmed.Length > MaxEmailLength || trimmed.AsSpan().ContainsAnyExceptInRange('!', '~'))
        {
            return false;
        }

        int at = trimmed.LastIndexOf('@');
        if (at < 1 || at > MaxLocalPartLength)
        {
            return false;
        }

        ReadOnlySpan<char> domain = trimmed.AsSpan(at + 1);
        return LocalPart().IsMatch(trimmed.AsSpan(0, at))
            && (DomainName().IsMatch(domain) || IsAddressLiteral(domain));
    }

    // RFC 5321 section 4.1.3 address-literal: "[" ( IPv4-address-literal / IPv6-address-literal /
    // General-address-literal ) "]". A General-address-literal (Standardized-tag ":" dcontent) needs a tag
    // registered with IANA, and IPv6 is the only registered tag, so every other tagged literal is rejected. The
    // check is syntax only: the address is never resolved or contacted.
    private static bool IsAddressLiteral(ReadOnlySpan<char> domain)
    {
        if (domain.Length < 2 || domain[0] != '[' || domain[^1] != ']')
        {
            return false;
        }

        ReadOnlySpan<char> address = domain[1..^1];
        if (address.StartsWith(IPv6Tag, StringComparison.OrdinalIgnoreCase))
        {
            // ABNF strings are case-insensitive, so the lower-case form that NormalizeEmail stores is the same tag.
            return IsIPv6Address(address[IPv6Tag.Length..]);
        }

        return IPv4Address().IsMatch(address);
    }

    // RFC 5321 section 4.1.3 IPv6-addr: eight hex groups (IPv6-full), or six followed by ":" and an IPv4 address
    // (IPv6v4-full). Either may instead use one "::", which stands for at least two zero groups, leaving at most six
    // explicit groups (IPv6-comp), or at most four before the IPv4 address (IPv6v4-comp). Zone ids ("%") and empty
    // groups other than the one "::" are not part of the grammar.
    private static bool IsIPv6Address(ReadOnlySpan<char> address)
    {
        int lastColon = address.LastIndexOf(':');
        if (lastColon < 0)
        {
            return false;
        }

        // Only an embedded IPv4 address contains dots, and it follows the last colon.
        ReadOnlySpan<char> tail = address[(lastColon + 1)..];
        if (!tail.Contains('.'))
        {
            return HasHexGroups(address, 8);
        }

        // When the last colon closes the "::", the "::" stays with the groups, as in ::192.0.2.1 or ffff::192.0.2.1;
        // otherwise it is the separator before the IPv4 address, as in ::ffff:192.0.2.1.
        ReadOnlySpan<char> groups = lastColon > 0 && address[lastColon - 1] == ':'
            ? address[..(lastColon + 1)]
            : address[..lastColon];
        return IPv4Address().IsMatch(tail) && HasHexGroups(groups, 6);
    }

    // Whether groups is exactly groupCount hex groups, or one "::" with at most groupCount - 2 explicit groups
    // around it. The pattern rejects a second "::", a lone leading or trailing colon and an over-long group.
    private static bool HasHexGroups(ReadOnlySpan<char> groups, int groupCount)
    {
        int compressed = groups.IndexOf("::", StringComparison.Ordinal);
        if (compressed < 0)
        {
            return IPv6Groups().IsMatch(groups) && groups.Count(':') == groupCount - 1;
        }

        ReadOnlySpan<char> before = groups[..compressed];
        ReadOnlySpan<char> after = groups[(compressed + 2)..];
        if ((!before.IsEmpty && !IPv6Groups().IsMatch(before)) || (!after.IsEmpty && !IPv6Groups().IsMatch(after)))
        {
            return false;
        }

        int explicitGroups = (before.IsEmpty ? 0 : before.Count(':') + 1) + (after.IsEmpty ? 0 : after.Count(':') + 1);
        return explicitGroups <= groupCount - 2;
    }

    // RFC 5321 section 4.1.2 Local-part: a Dot-string of atext atoms joined by single dots, or a Quoted-string of
    // qtextSMTP and quoted-pairSMTP (its space is already excluded by the repertoire check). \z rather than $ in
    // every pattern, so a trailing newline can never match.
    [GeneratedRegex(
        @"\A(?:[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*|""(?:[ !#-\[\]-~]|\\[ -~])*"")\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex LocalPart();

    // RFC 5321 section 4.1.2 Domain: dot-separated labels of letters, digits and inner hyphens, at most 63 characters
    // each (RFC 1035), with no trailing dot.
    [GeneratedRegex(
        @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex DomainName();

    // RFC 5321 section 4.1.3 IPv4-address-literal without brackets: four dot-separated Snum, each one to three
    // digits with a value of 0 to 255, leading zeros included.
    [GeneratedRegex(
        @"\A(?:25[0-5]|2[0-4][0-9]|[01]?[0-9]{1,2})(?:\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9]{1,2})){3}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex IPv4Address();

    // RFC 5321 section 4.1.3 IPv6-hex groups of one to four hex digits, joined by single colons.
    [GeneratedRegex(@"\A[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*\z", RegexOptions.CultureInvariant)]
    private static partial Regex IPv6Groups();

    /// <summary>Returns the type name with every member redacted.</summary>
    /// <returns>A description that carries no member value.</returns>
    public override string ToString()
    {
        // The synthesized record ToString would print the email, password and full name, and passwords and
        // email addresses are never logged.
        return "RegisterRequest { Email = [redacted], Password = [redacted], FullName = [redacted] }";
    }
}

/// <summary>
/// Body of a successful registration (201), serialized as exactly <c>{ "guestId": n }</c>. It never carries the
/// password hash or any other guest data.
/// </summary>
/// <param name="GuestId">The new guest's id.</param>
public sealed record RegisterResponse(int GuestId);

/// <summary>
/// Body of <c>POST /api/auth/login</c>. It deliberately carries no validation: any bound object, including
/// <see langword="null"/> or blank members, reaches the credential check and fails it with the single 401, so
/// the response never reveals which part was wrong. A JSON value of the wrong type is still a binding 400.
/// </summary>
/// <param name="Email">The email as entered; folded the same way as at registration before the lookup.</param>
/// <param name="Password">The password as entered; never trimmed.</param>
public sealed record LoginRequest(string? Email, string? Password)
{
    /// <summary>Returns the type name with every member redacted.</summary>
    /// <returns>A description that carries no member value.</returns>
    public override string ToString()
    {
        // The synthesized record ToString would print the email and password, which are never logged.
        return "LoginRequest { Email = [redacted], Password = [redacted] }";
    }
}

/// <summary>
/// Body of a successful login (200), serialized as
/// <c>{ "token": "&lt;jwt&gt;", "expiresAt": "2026-11-23T12:44:16Z" }</c>.
/// </summary>
/// <param name="Token">The signed HS256 bearer token, valid for 60 minutes.</param>
/// <param name="ExpiresAt">
/// The token's <c>exp</c> instant as a UTC <see cref="DateTime"/> with no fractional seconds, so it serializes
/// as <c>yyyy-MM-ddTHH:mm:ssZ</c>.
/// </param>
public sealed record LoginResponse(string Token, DateTime ExpiresAt)
{
    /// <summary>Returns the type name and expiry with the token redacted.</summary>
    /// <returns>A description that carries no token.</returns>
    public override string ToString()
    {
        // The synthesized record ToString would print the bearer token, which is never logged.
        return $"LoginResponse {{ Token = [redacted], ExpiresAt = {ExpiresAt:O} }}";
    }
}

/// <summary>
/// Outcome of a registration attempt. <see cref="AuthService.RegisterAsync(RegisterRequest, CancellationToken)"/>
/// produces only the two nested cases, <see cref="Created"/> and <see cref="EmailTaken"/>.
/// </summary>
/// <remarks>
/// The private constructor stops code from deriving through the parameterless constructor, but the protected copy
/// constructor that the record synthesizes still lets other derived records exist. Code that switches on a result
/// therefore keeps a default arm that rejects an unrecognized case.
/// </remarks>
public abstract record RegisterResult
{
    private RegisterResult()
    {
    }

    /// <summary>The guest was created.</summary>
    /// <param name="GuestId">The new guest's id.</param>
    public sealed record Created(int GuestId) : RegisterResult;

    /// <summary>
    /// The normalized email is already registered, found either by the pre-check or by the unique index
    /// <c>UX_Guests_Email</c> when a concurrent registration wins the race.
    /// </summary>
    public sealed record EmailTaken : RegisterResult
    {
        /// <summary>The shared instance; the case carries no data.</summary>
        public static EmailTaken Instance { get; } = new();
    }
}