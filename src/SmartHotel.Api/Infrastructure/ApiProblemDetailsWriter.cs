using System.Text.Json;

using Microsoft.Extensions.Options;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Writes the ProblemDetails bodies that no MVC object result produces, namely the bodyless 401, 403,
/// 404 and 405 responses handed on by status-code pages and the 500 response from the exception
/// handler, as <c>application/problem+json</c> whatever the request's <c>Accept</c> header admits.
/// </summary>
/// <remarks>
/// ASP.NET Core's default writer declines a request whose <c>Accept</c> header excludes JSON, which
/// leaves that response with a plain-text status page. This writer is registered ahead of the framework
/// writers and, because it replaces their behaviour instead of extending it, applies
/// <see cref="ProblemDetailsOptions.CustomizeProblemDetails"/> itself, so both error paths share one
/// defaults routine.
/// </remarks>
public sealed class ApiProblemDetailsWriter : IProblemDetailsWriter
{
    private const string ProblemJsonContentType = "application/problem+json";

    private readonly ProblemDetailsOptions _problemDetailsOptions;
    private readonly JsonSerializerOptions _serializerOptions;

    /// <summary>
    /// Initializes the writer with the application's ProblemDetails customization and the HTTP JSON
    /// serializer settings.
    /// </summary>
    /// <param name="problemDetailsOptions">The options holding the shared ProblemDetails defaults routine.</param>
    /// <param name="jsonOptions">The HTTP JSON options whose serializer settings shape the body.</param>
    public ApiProblemDetailsWriter(
        IOptions<ProblemDetailsOptions> problemDetailsOptions,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions)
    {
        _problemDetailsOptions = problemDetailsOptions.Value;
        _serializerOptions = jsonOptions.Value.SerializerOptions;
    }

    /// <summary>
    /// Accepts every request, so the <c>Accept</c> header never suppresses the problem body.
    /// </summary>
    /// <param name="context">The problem to be written.</param>
    /// <returns>Always <see langword="true"/>.</returns>
    public bool CanWrite(ProblemDetailsContext context) => true;

    /// <summary>
    /// Completes the problem with the response status and the shared defaults, then writes it as
    /// <c>application/problem+json</c>.
    /// </summary>
    /// <param name="context">The problem to be written and the request it answers.</param>
    /// <returns>A task that completes once the body has been written.</returns>
    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        var response = context.HttpContext.Response;
        var problem = context.ProblemDetails;

        // The response status is authoritative and is never changed here.
        problem.Status ??= response.StatusCode;

        // A custom writer inherits none of the default writer's customization step.
        _problemDetailsOptions.CustomizeProblemDetails?.Invoke(context);

        // context.Exception is never read, so this writer adds no exception data of its own. The body holds
        // only what the caller put in the problem plus the shared defaults; callers must supply safe content.
        // Only Content-Type and the body are written: WWW-Authenticate from the bearer challenge and
        // Allow from a 405 must survive. The runtime type keeps derived members such as
        // HttpValidationProblemDetails.Errors in the output.
        return new ValueTask(response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            _serializerOptions,
            contentType: ProblemJsonContentType,
            cancellationToken: context.HttpContext.RequestAborted));
    }
}