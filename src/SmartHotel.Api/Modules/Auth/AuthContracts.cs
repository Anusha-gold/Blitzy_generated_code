using System.ComponentModel.DataAnnotations;

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
/// Required. After trimming: printable ASCII only (U+0021 to U+007E), a valid address with exactly one
/// <c>@</c> that is neither first nor last, and at most 254 characters (the RFC 5321 path limit).
/// </param>
/// <param name="Password">
/// Required, 8 to 128 characters, and not trimmed. A whitespace-only value is rejected as missing.
/// </param>
/// <param name="FullName">Required. After trimming: not blank and at most 200 characters.</param>
public sealed record RegisterRequest(
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
    private const int MaxFullNameLength = 200;

    // The camelCase JSON names. They key both the attribute errors (through ModelBinder) and the Validate
    // errors, whose member names MVC uses verbatim.
    private const string EmailMember = "email";
    private const string PasswordMember = "password";
    private const string FullNameMember = "fullName";

    private const string EmailMessage = "email must be a valid ASCII email address of at most 254 characters.";
    private const string FullNameMessage = "fullName must not be blank and must be at most 200 characters.";

    private static readonly EmailAddressAttribute EmailFormat = new();

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
        }
    }

    private static bool IsAcceptableEmail(string trimmed)
    {
        // Internationalized addresses are outside v1: only printable ASCII is accepted, which keeps the
        // single lower-case fold in AuthService.NormalizeEmail exact under every globalization mode.
        return trimmed.Length <= MaxEmailLength
            && !trimmed.AsSpan().ContainsAnyExceptInRange('!', '~')
            && EmailFormat.IsValid(trimmed);
    }

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
/// Outcome of a registration attempt. The private constructor limits the cases to the nested records.
/// </summary>
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