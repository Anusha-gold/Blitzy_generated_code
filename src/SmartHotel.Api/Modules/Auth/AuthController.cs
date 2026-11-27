using Microsoft.AspNetCore.Mvc;

using SmartHotel.Api.Common.Http;

namespace SmartHotel.Api.Modules.Auth;

/// <summary>
/// The anonymous guest-account endpoints under <c>api/auth</c>: registration and login.
/// </summary>
/// <remarks>
/// <para>
/// Each action binds its body, calls one <see cref="AuthService"/> method and maps the outcome. Request rejections
/// that the framework decides happen before the action runs, and every error body is
/// <c>application/problem+json</c>.
/// </para>
/// <para>
/// No action carries <c>[Consumes]</c>: the JSON input formatter is the only one registered, so MVC itself answers
/// 415 for an absent or non-JSON <c>Content-Type</c>. There is no logout action, because logout only discards the
/// client's in-memory session and the token expires on its own within 60 minutes.
/// </para>
/// <para>
/// No problem field ever carries the email, password, token or exception text.
/// </para>
/// </remarks>
/// <param name="authService">The request-scoped registration and credential service.</param>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService authService) : ControllerBase
{
    /// <summary>
    /// Registers a guest: <c>POST /api/auth/register</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// 201 with exactly <c>{ "guestId": n }</c> and no <c>Location</c> header, because no endpoint reads a guest.
    /// </description></item>
    /// <item><description>
    /// 415, from MVC, when <c>Content-Type</c> is absent or is not JSON, a bodyless request included.
    /// </description></item>
    /// <item><description>
    /// 400 <see cref="ValidationProblemDetails"/>, from MVC, for an empty, malformed or non-object JSON body, a field
    /// of the wrong JSON type, or a failed field check, keyed <c>email</c>, <c>password</c> or <c>fullName</c>.
    /// </description></item>
    /// <item><description>
    /// 409 when the normalized email is already registered, found by the pre-check or by the unique index on a
    /// concurrent registration.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="request">The body, already validated by model binding.</param>
    /// <param name="cancellationToken">Cancels the database work when the request is aborted.</param>
    /// <returns>The 201 result or the 409 problem.</returns>
    /// <exception cref="InvalidOperationException">The service returned an unmapped outcome.</exception>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        RegisterResult result = await authService.RegisterAsync(request, cancellationToken);

        // The nested result types are qualified so that Created cannot resolve to ControllerBase.Created.
        return result switch
        {
            RegisterResult.Created created =>
                StatusCode(StatusCodes.Status201Created, new RegisterResponse(created.GuestId)),
            RegisterResult.EmailTaken =>
                this.ConflictProblem("Email already registered", "An account with this email already exists."),
            _ => throw new InvalidOperationException("The registration outcome is not recognized."),
        };
    }

    /// <summary>
    /// Logs a guest in and issues a bearer token valid for 60 minutes: <c>POST /api/auth/login</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// 200 with <c>{ "token": "&lt;jwt&gt;", "expiresAt": "yyyy-MM-ddTHH:mm:ssZ" }</c>.
    /// </description></item>
    /// <item><description>
    /// 415, from MVC, when <c>Content-Type</c> is absent or is not JSON, a bodyless request included.
    /// </description></item>
    /// <item><description>
    /// 400 <see cref="ValidationProblemDetails"/>, from MVC, for a JSON body that cannot bind: empty, malformed,
    /// not an object, or an <c>email</c> or <c>password</c> that is neither a string nor <see langword="null"/>.
    /// No credential check runs.
    /// </description></item>
    /// <item><description>
    /// 401 for every bound request whose credentials do not match: a wrong password, an unknown email, or a
    /// missing, <see langword="null"/> or blank field. The title and detail are one fixed pair and there is no
    /// <c>errors</c> member, so the responses differ only in <c>traceId</c> and never reveal whether an account
    /// exists. Each carries the bare challenge <c>WWW-Authenticate: Bearer</c>, as JwtBearer's own 401s do.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="request">The bound body; its members may be <see langword="null"/> or blank.</param>
    /// <param name="cancellationToken">Cancels the database work when the request is aborted.</param>
    /// <returns>The 200 result or the 401 problem.</returns>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        LoginResponse? response = await authService.LoginAsync(request, cancellationToken);
        if (response is null)
        {
            return this.UnauthorizedProblem("Invalid credentials", "Email or password is incorrect.");
        }

        return Ok(response);
    }
}