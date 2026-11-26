using System.Diagnostics;

using Microsoft.AspNetCore.WebUtilities;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Registers the API's ProblemDetails services and owns the single defaults routine that completes every
/// error body, whether MVC's ProblemDetails factory builds it or <see cref="ApiProblemDetailsWriter"/> writes
/// it for status-code pages and the exception handler.
/// </summary>
public static class ProblemDetailsSetup
{
    private const string Rfc9110SectionBase = "https://tools.ietf.org/html/rfc9110#section-";
    private const string AboutBlankType = "about:blank";
    private const string FallbackTitle = "Error";
    private const string TraceIdKey = "traceId";
    private const string ErrorsKey = "errors";

    /// <summary>
    /// Adds ProblemDetails support with <see cref="ApplyDefaults"/> as its customization step and registers
    /// <see cref="ApiProblemDetailsWriter"/> ahead of every framework writer.
    /// </summary>
    /// <param name="services">The API host's service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ProblemDetailsService uses the first writer whose CanWrite returns true, and both AddControllers and
        // AddProblemDetails register a writer of their own, so this one goes to the front wherever the host
        // calls this method relative to AddControllers.
        services.Insert(0, ServiceDescriptor.Singleton<IProblemDetailsWriter, ApiProblemDetailsWriter>());

        // MVC's ProblemDetails factory invokes this routine on every problem it builds, and
        // ApiProblemDetailsWriter invokes it explicitly, so both error paths share it.
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ApplyDefaults);

        return services;
    }

    /// <summary>
    /// Completes a problem with the API's defaults: its status, an RFC 9110 <c>type</c>, the reason phrase as
    /// <c>title</c>, a fixed <c>detail</c> sentence, the <c>traceId</c> and, on a 400 that carries no validation
    /// dictionary, an empty <c>errors</c> object. A <c>type</c>, <c>title</c> or <c>detail</c> the problem
    /// already holds is kept.
    /// </summary>
    /// <param name="context">The problem being produced and the request it answers.</param>
    public static void ApplyDefaults(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = context.ProblemDetails;
        var httpContext = context.HttpContext;

        // A status the MVC factory has set wins: on that path the response status is still 200.
        int status = problem.Status ??= httpContext.Response.StatusCode;

        if (string.IsNullOrEmpty(problem.Type))
        {
            problem.Type = TypeFor(status);
        }

        if (string.IsNullOrEmpty(problem.Title))
        {
            var reasonPhrase = ReasonPhrases.GetReasonPhrase(status);
            problem.Title = string.IsNullOrEmpty(reasonPhrase) ? FallbackTitle : reasonPhrase;
        }

        // The detail depends on the status alone. context.Exception and context.AdditionalMetadata are never
        // read, so no exception or request data can reach a response body.
        if (string.IsNullOrEmpty(problem.Detail))
        {
            problem.Detail = DetailFor(status);
        }

        problem.Extensions[TraceIdKey] = ResolveTraceId(httpContext);

        // Every 400 carries errors. MVC's ValidationProblemDetails derives from HttpValidationProblemDetails
        // and already has them.
        if (status == StatusCodes.Status400BadRequest
            && problem is not HttpValidationProblemDetails
            && !problem.Extensions.ContainsKey(ErrorsKey))
        {
            problem.Extensions[ErrorsKey] = new Dictionary<string, string[]>();
        }
    }

    /// <summary>
    /// Returns the identifier that correlates a response with its log events: the current activity's id when
    /// one exists, otherwise the request's trace identifier.
    /// </summary>
    /// <param name="httpContext">The request being answered.</param>
    /// <returns>The value written as <c>traceId</c>.</returns>
    public static string ResolveTraceId(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return Activity.Current?.Id ?? httpContext.TraceIdentifier;
    }

    // Every client and server error status that RFC 9110 defines links to its own section, which agrees with
    // MVC's ClientErrorMapping wherever that maps the status. Any other error status links to its class.
    private static string TypeFor(int status)
    {
        return status switch
        {
            400 => Rfc9110SectionBase + "15.5.1",
            401 => Rfc9110SectionBase + "15.5.2",
            402 => Rfc9110SectionBase + "15.5.3",
            403 => Rfc9110SectionBase + "15.5.4",
            404 => Rfc9110SectionBase + "15.5.5",
            405 => Rfc9110SectionBase + "15.5.6",
            406 => Rfc9110SectionBase + "15.5.7",
            407 => Rfc9110SectionBase + "15.5.8",
            408 => Rfc9110SectionBase + "15.5.9",
            409 => Rfc9110SectionBase + "15.5.10",
            410 => Rfc9110SectionBase + "15.5.11",
            411 => Rfc9110SectionBase + "15.5.12",
            412 => Rfc9110SectionBase + "15.5.13",
            413 => Rfc9110SectionBase + "15.5.14",
            414 => Rfc9110SectionBase + "15.5.15",
            415 => Rfc9110SectionBase + "15.5.16",
            416 => Rfc9110SectionBase + "15.5.17",
            417 => Rfc9110SectionBase + "15.5.18",
            421 => Rfc9110SectionBase + "15.5.20",
            422 => Rfc9110SectionBase + "15.5.21",
            426 => Rfc9110SectionBase + "15.5.22",
            500 => Rfc9110SectionBase + "15.6.1",
            501 => Rfc9110SectionBase + "15.6.2",
            502 => Rfc9110SectionBase + "15.6.3",
            503 => Rfc9110SectionBase + "15.6.4",
            504 => Rfc9110SectionBase + "15.6.5",
            505 => Rfc9110SectionBase + "15.6.6",
            >= 400 and < 500 => Rfc9110SectionBase + "15.5",
            >= 500 and < 600 => Rfc9110SectionBase + "15.6",
            _ => AboutBlankType,
        };
    }

    private static string DetailFor(int status)
    {
        return status switch
        {
            400 => "The request contains invalid input. See the errors member for details.",
            401 => "A valid bearer token is required.",
            403 => "You do not have permission to perform this action.",
            404 => "The requested resource was not found.",
            405 => "The request method is not supported for this resource.",
            409 => "The request conflicts with the current state of the resource.",
            415 => "The request content type is not supported. Send application/json.",
            500 => "An unexpected error occurred.",
            _ => "The request could not be processed.",
        };
    }
}