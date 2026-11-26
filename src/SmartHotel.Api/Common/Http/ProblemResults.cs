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
/// Titles and details are fixed English text. A detail may name non-secret domain values, such as a room type
/// name and dates ("No Standard room is free from 2026-12-07 to 2026-12-09."). It never contains secrets,
/// password hashes, tokens, email addresses or exception text.
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
    /// <param name="errors">The validation messages per request field name. Each message is a fixed sentence.</param>
    /// <param name="detail">
    /// A fixed sentence describing the failure, or <see langword="null"/> to use the API's default 400 detail,
    /// which <c>ProblemDetailsSetup.ApplyDefaults</c> supplies.
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
    /// Creates a 401 Unauthorized problem.
    /// </summary>
    /// <param name="controller">The controller answering the request.</param>
    /// <param name="title">A fixed, short summary of the problem.</param>
    /// <param name="detail">A fixed sentence explaining the problem to the caller.</param>
    /// <returns>The 401 result for the action to return.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="controller"/>, <paramref name="title"/> or <paramref name="detail"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static ObjectResult UnauthorizedProblem(this ControllerBase controller, string title, string detail)
    {
        return Create(controller, StatusCodes.Status401Unauthorized, title, detail);
    }

    /// <summary>
    /// Creates a 403 Forbidden problem.
    /// </summary>
    /// <remarks>
    /// This is an object result, not <c>ControllerBase.Forbid()</c>: no authentication handler is invoked, and
    /// the body is this domain problem.
    /// </remarks>
    /// <param name="controller">The controller answering the request.</param>
    /// <param name="title">A fixed, short summary of the problem.</param>
    /// <param name="detail">A fixed sentence explaining the problem to the caller.</param>
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
    /// <param name="title">A fixed, short summary of the problem.</param>
    /// <param name="detail">A fixed sentence explaining the problem to the caller.</param>
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
    /// <param name="title">A fixed, short summary of the problem.</param>
    /// <param name="detail">A fixed sentence explaining the problem to the caller.</param>
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
}