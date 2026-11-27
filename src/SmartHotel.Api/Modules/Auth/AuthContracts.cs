using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;

namespace SmartHotel.Api.Modules.Auth;

// Request records declare every member as string? so that, with nullable reference types on, MVC adds no
// implicit [Required] of its own; the explicit attributes below are the only required checks. Validation
// attributes sit on the positional parameters because MVC throws when they target a record's properties.
//
// Every bound is a parameter attribute. MVC runs a model-level IValidatableObject.Validate only after every attribute
// check on the object has passed, so a bound kept there would be reported only once the other members were valid. As
// attributes, all the checks run together, and one 400 reports every invalid member.
//
// [ModelBinder(Name)] sets each parameter's key in the 400 errors object. For positional record parameters MVC
// keys attribute errors by the CLR parameter name (Email) and ignores SystemTextJsonValidationMetadataProvider,
// while the error contract keys them by the camelCase JSON name (email). The name does not affect body binding.

/// <summary>
/// Body of <c>POST /api/auth/register</c>. Each member carries its required check and one bounds check. After the
/// JSON object binds, every member's checks run independently, so a 400 reports each invalid member, with at most
/// one validation error per member. The email and full-name bounds apply to the trimmed values, the password bounds
/// to the value as sent. The password and full-name lengths count Unicode scalar values, not UTF-16 code units.
/// Error messages are fixed sentences that never echo the input.
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
/// Required, 8 to 128 characters, free of the null character (U+0000), and not trimmed. A whitespace-only value is
/// rejected as missing. Both length bounds count Unicode scalar values, so a character outside the Basic Multilingual
/// Plane counts once. The null character is refused because the hash's HMAC pads a short key with zero bytes, so a
/// password followed by null characters would match the password's own hash.
/// </param>
/// <param name="FullName">
/// Required. After trimming: not blank, at most 200 characters, and free of the null character (U+0000), which
/// PostgreSQL text cannot store. The limit counts Unicode scalar values, the characters PostgreSQL counts for
/// <c>varchar(200)</c>, so a character outside the Basic Multilingual Plane counts once.
/// </param>
public sealed partial record RegisterRequest(
    [ModelBinder(Name = RegisterRequest.EmailMember)]
    [Required(ErrorMessage = "A value is required.")]
    [RegisterRequest.EmailBounds]
    string? Email,
    [ModelBinder(Name = RegisterRequest.PasswordMember)]
    [Required(ErrorMessage = "A value is required.")]
    [RegisterRequest.PasswordBounds]
    string? Password,
    [ModelBinder(Name = RegisterRequest.FullNameMember)]
    [Required(ErrorMessage = "A value is required.")]
    [RegisterRequest.FullNameBounds]
    string? FullName)
{
    private const int MaxEmailLength = 254;
    private const int MaxLocalPartLength = 64;
    private const int MinPasswordLength = 8;
    private const int MaxPasswordLength = 128;
    private const int MaxFullNameLength = 200;

    // The IPv6-address-literal tag, compared without regard to case.
    private const string IPv6Tag = "IPv6:";

    // The camelCase JSON names, which key the attribute errors through ModelBinder.
    private const string EmailMember = "email";
    private const string PasswordMember = "password";
    private const string FullNameMember = "fullName";

    // No message names its own field: the error key identifies the member, and a client shows its own label for
    // that field beside the message.
    private const string EmailMessage =
        "The value must be a valid email address of at most 254 characters, using only ASCII characters.";
    private const string PasswordLengthMessage = "The value must be 8 to 128 characters long.";
    private const string PasswordNulMessage = "The value contains a character that is not allowed.";
    private const string FullNameMessage = "The value must not be blank and must be at most 200 characters.";
    private const string FullNameNulMessage = "The value contains a character that is not allowed.";

    // Whether value holds at most MaxFullNameLength Unicode scalar values. Each scalar value takes one or two UTF-16
    // code units, so only a value of MaxFullNameLength + 1 to 2 * MaxFullNameLength code units needs counting, and
    // the count stops as soon as it passes the limit.
    private static bool IsWithinFullNameLength(string value)
    {
        if (value.Length <= MaxFullNameLength)
        {
            return true;
        }

        if (value.Length > 2 * MaxFullNameLength)
        {
            return false;
        }

        int scalarValues = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (++scalarValues > MaxFullNameLength)
            {
                return false;
            }
        }

        return true;
    }

    // Internal rather than private: the compiler rejects a private nested attribute's constructor on the record's own
    // positional parameters (CS0122), and a base class is at least as accessible as the attributes derived from it.
    /// <summary>
    /// Checks one member's bounds, in the same validation pass as every other member's attributes.
    /// <see langword="null"/> and a whitespace-only value are valid, because <see cref="RequiredAttribute"/> reports
    /// them. Any other value fails with the fixed message of the first bound it breaks, so the member gets at most one
    /// error.
    /// </summary>
    internal abstract class MemberBoundsAttribute : ValidationAttribute
    {
        /// <inheritdoc/>
        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        {
            string? message = value switch
            {
                null => null,
                string text => string.IsNullOrWhiteSpace(text) ? null : FindViolation(text),
                _ => throw new InvalidOperationException(
                    $"{GetType().Name} validates only strings, not {value.GetType().Name}."),
            };

            // No member name: MVC files a result without one under the member's own key, the ModelBinder name.
            return message is null ? ValidationResult.Success : new ValidationResult(message);
        }

        /// <summary>Checks a value against the member's bounds, in order.</summary>
        /// <param name="value">The value as sent; neither <see langword="null"/> nor whitespace only.</param>
        /// <returns>
        /// The fixed message of the first bound <paramref name="value"/> breaks, or <see langword="null"/> when it
        /// meets them all.
        /// </returns>
        protected abstract string? FindViolation(string value);
    }

    /// <summary>
    /// The email bounds, applied to the trimmed value: printable ASCII only, at most <see cref="MaxEmailLength"/>
    /// characters, and an RFC 5321 mailbox.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class EmailBoundsAttribute : MemberBoundsAttribute
    {
        /// <inheritdoc/>
        protected override string? FindViolation(string value)
        {
            return IsAcceptableEmail(value.Trim()) ? null : EmailMessage;
        }
    }

    /// <summary>
    /// The password bounds, applied to the value as sent: <see cref="MinPasswordLength"/> to
    /// <see cref="MaxPasswordLength"/> Unicode scalar values, then no null character (U+0000).
    /// </summary>
    /// <remarks>
    /// <see cref="StringLengthAttribute"/> counts UTF-16 code units, so it counts a character outside the Basic
    /// Multilingual Plane twice. The value is never trimmed: the bounds apply to the exact value that
    /// <c>AuthService</c> hashes. That hash is PBKDF2 over HMAC-SHA512, which pads a key shorter than its 128-byte
    /// block with zero bytes, so a password and the same password followed by null characters, while both fit the
    /// block, derive the same hash. No stored password contains the character, and login refuses every password that
    /// does.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class PasswordBoundsAttribute : MemberBoundsAttribute
    {
        /// <inheritdoc/>
        protected override string? FindViolation(string value)
        {
            if (!IsWithinPasswordLength(value))
            {
                return PasswordLengthMessage;
            }

            return value.Contains('\0') ? PasswordNulMessage : null;
        }

        // Whether value holds MinPasswordLength to MaxPasswordLength Unicode scalar values. Each scalar value takes one
        // or two UTF-16 code units, so fewer than MinPasswordLength code units is always too short and more than
        // 2 * MaxPasswordLength always too long, while 2 * MinPasswordLength to MaxPasswordLength code units is always
        // within the bounds. Only the remaining lengths need counting, and the count stops as soon as it passes the
        // limit.
        private static bool IsWithinPasswordLength(string value)
        {
            if (value.Length < MinPasswordLength || value.Length > 2 * MaxPasswordLength)
            {
                return false;
            }

            if (value.Length >= 2 * MinPasswordLength && value.Length <= MaxPasswordLength)
            {
                return true;
            }

            int scalarValues = 0;
            foreach (Rune rune in value.EnumerateRunes())
            {
                if (++scalarValues > MaxPasswordLength)
                {
                    return false;
                }
            }

            return scalarValues >= MinPasswordLength;
        }
    }

    /// <summary>
    /// The full-name bounds, applied to the trimmed value: at most <see cref="MaxFullNameLength"/> Unicode scalar
    /// values, then no null character (U+0000). The value is not blank, so neither is its trimmed form.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class FullNameBoundsAttribute : MemberBoundsAttribute
    {
        /// <inheritdoc/>
        protected override string? FindViolation(string value)
        {
            string trimmed = value.Trim();

            // Length counts UTF-16 code units, so it would count a character outside the Basic Multilingual Plane
            // twice; the column's varchar(200) limit counts characters, which are Unicode scalar values.
            if (!IsWithinFullNameLength(trimmed))
            {
                return FullNameMessage;
            }

            // JSON can carry U+0000, but PostgreSQL text cannot store it, so the insert would fail.
            return trimmed.Contains('\0') ? FullNameNulMessage : null;
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