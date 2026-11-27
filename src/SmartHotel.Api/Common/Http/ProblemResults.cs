using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace SmartHotel.Api.Common.Http;

/// <summary>
/// Domain-error helpers for controllers: each turns an expected outcome into a 400, 401, 403, 404 or 409
/// problem response.
/// </summary>
/// <remarks>
/// <para>
/// Every helper delegates to <c>ControllerBase.Problem</c> or <c>ControllerBase.ValidationProblem</c>, which build
/// the body through MVC's <see cref="Microsoft.AspNetCore.Mvc.Infrastructure.ProblemDetailsFactory"/>: the same
/// factory and object-result path as MVC's automatic 400. The factory takes the RFC 9110 <c>type</c> link from
/// <c>ApiBehaviorOptions.ClientErrorMapping</c>, sets <c>traceId</c>, and runs the shared defaults routine
/// <c>ProblemDetailsSetup.ApplyDefaults</c>, installed as <c>ProblemDetailsOptions.CustomizeProblemDetails</c>,
/// which fills any missing <c>type</c>, <c>title</c> or <c>detail</c>. The API registers no XML formatter, so
/// content negotiation always settles on <c>application/problem+json</c>, whatever the request's <c>Accept</c>
/// header says.
/// </para>
/// <para>
/// Inside a controller, call them as <c>this.NotFoundProblem(...)</c>: C# requires an explicit receiver to call
/// an extension method on the current instance.
/// </para>
/// <para>
/// The helpers copy the titles, details and validation messages they are given into the response unchanged and
/// redact nothing, so callers must supply safe text: fixed English sentences, which may name non-secret domain
/// values such as a room type name and dates ("No Standard room is free from 2026-12-07 to 2026-12-09."). Callers
/// must never pass raw request values, credentials, password hashes, tokens, email addresses or exception text.
/// </para>
/// </remarks>
public static class ProblemResults
{
    /// <summary>
    /// Creates a 400 Bad Request validation problem whose <c>errors</c> member holds <paramref name="errors"/>.
    /// </summary>
    /// <remarks>
    /// The result is a <see cref="BadRequestObjectResult"/> wrapping a <see cref="ValidationProblemDetails"/>
    /// titled "One or more validation errors occurred.". Keys are copied verbatim, so they stay the camelCase
    /// request field names, such as <c>checkIn</c>, <c>checkOut</c> and <c>guests</c>, that clients read. The
    /// controller's own <c>ModelState</c> is neither read nor changed.
    /// </remarks>
    /// <param name="controller">The controller answering the request.</param>
    /// <param name="errors">
    /// The validation messages per request field name. Each message is copied unchanged, so it must be safe text, as
    /// the class remarks define it.
    /// </param>
    /// <param name="detail">
    /// A sentence describing the failure, or <see langword="null"/> to use the API's default 400 detail, which
    /// <c>ProblemDetailsSetup.ApplyDefaults</c> supplies. A supplied sentence is copied unchanged, so it must be safe
    /// text.
    /// </param>
    /// <returns>The 400 result for the action to return.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="controller"/> or <paramref name="errors"/> is <see langword="null"/>.
    /// </exception>
    public static ActionResult BadRequestProblem(
        this ControllerBase controller,
        IReadOnlyDictionary<string, string[]> errors,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(errors);

        var modelState = new ModelStateDictionary();
        foreach (var (key, messages) in errors)
        {
            foreach (var message in messages)
            {
                modelState.AddModelError(key, message);
            }
        }

        // Named arguments bind the parameterized overload. The parameterless ValidationProblem() would run
        // ApiBehaviorOptions.InvalidModelStateResponseFactory against the controller's own ModelState instead.
        return controller.ValidationProblem(detail: detail, modelStateDictionary: modelState);
    }

    /// <summary>
    /// Creates a 401 Unauthorized problem that carries the bare challenge <c>WWW-Authenticate: Bearer</c>.
    /// </summary>
    /// <remarks>
    /// RFC 9110, section 15.5.2, requires every 401 response to carry at least one challenge. The API authenticates
    /// only with JwtBearer, so the challenge is the one JwtBearer itself sends for a missing or rejected token: the
    /// <c>Bearer</c> scheme with no parameters, such as <c>realm</c>, <c>error</c> or <c>error_description</c>. The
    /// header is set as the result executes, just before the body is written, the way <see cref="CreatedResult"/>
    /// sets <c>Location</c>, and it replaces any challenge already on the response, so the response carries exactly
    /// one. The body is the same problem the other helpers build.
    /// </remarks>
    /// <param name="controller">The controller answering the request.</param>
    /// <param name="title">A short summary of the problem. It is copied unchanged, so it must be safe text.</param>
    /// <param name="detail">
    /// A sentence explaining the problem to the caller. It is copied unchanged, so it must be safe text.
    /// </param>
    /// <returns>The 401 result for the action to return.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="controller"/>, <paramref name="title"/> or <paramref name="detail"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static ObjectResult UnauthorizedProblem(this ControllerBase controller, string title, string detail)
    {
        return new BearerChallengeProblemResult(
            Create(controller, StatusCodes.Status401Unauthorized, title, detail));
    }

    /// <summary>
    /// Creates a 403 Forbidden problem.
    /// </summary>
    /// <remarks>
    /// This is an object result, not <c>ControllerBase.Forbid()</c>: no authentication handler is invoked, and
    /// the body is this domain problem.
    /// </remarks>
    /// <param name="controller">The controller answering the request.</param>
    /// <param name="title">A short summary of the problem. It is copied unchanged, so it must be safe text.</param>
    /// <param name="detail">
    /// A sentence explaining the problem to the caller. It is copied unchanged, so it must be safe text.
    /// </param>
    /// <returns>The 403 result for the action to return.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="controller"/>, <paramref name="title"/> or <paramref name="detail"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static ObjectResult ForbiddenProblem(this ControllerBase controller, string title, string detail)
    {
        return Create(controller, StatusCodes.Status403Forbidden, title, detail);
    }

    /// <summary>
    /// Creates a 404 Not Found problem.
    /// </summary>
    /// <param name="controller">The controller answering the request.</param>
    /// <param name="title">A short summary of the problem. It is copied unchanged, so it must be safe text.</param>
    /// <param name="detail">
    /// A sentence explaining the problem to the caller. It is copied unchanged, so it must be safe text.
    /// </param>
    /// <returns>The 404 result for the action to return.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="controller"/>, <paramref name="title"/> or <paramref name="detail"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static ObjectResult NotFoundProblem(this ControllerBase controller, string title, string detail)
    {
        return Create(controller, StatusCodes.Status404NotFound, title, detail);
    }

    /// <summary>
    /// Creates a 409 Conflict problem.
    /// </summary>
    /// <param name="controller">The controller answering the request.</param>
    /// <param name="title">A short summary of the problem. It is copied unchanged, so it must be safe text.</param>
    /// <param name="detail">
    /// A sentence explaining the problem to the caller. It is copied unchanged, so it must be safe text.
    /// </param>
    /// <returns>The 409 result for the action to return.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="controller"/>, <paramref name="title"/> or <paramref name="detail"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static ObjectResult ConflictProblem(this ControllerBase controller, string title, string detail)
    {
        return Create(controller, StatusCodes.Status409Conflict, title, detail);
    }

    // The parameter names match the public helpers', so the guards report the caller-visible names.
    private static ObjectResult Create(ControllerBase controller, int statusCode, string title, string detail)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(detail);

        // No type or instance is passed: ClientErrorMapping and the shared defaults supply type.
        return controller.Problem(detail: detail, statusCode: statusCode, title: title);
    }

    // A problem result that adds the bearer challenge when it executes, so the header travels with the result instead
    // of being written to the response when the helper runs.
    private sealed class BearerChallengeProblemResult : ObjectResult
    {
        // Takes over everything the problem result carries, so the body and its negotiation stay exactly as built.
        public BearerChallengeProblemResult(ObjectResult problem)
            : base(problem.Value)
        {
            StatusCode = problem.StatusCode;
            DeclaredType = problem.DeclaredType;
            ContentTypes = problem.ContentTypes;
            Formatters = problem.Formatters;
        }

        public override void OnFormatting(ActionContext context)
        {
            base.OnFormatting(context);

            // JwtBearer's default challenge is its scheme name. Assigned, not appended, so it is the only challenge.
            context.HttpContext.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;
        }
    }
}