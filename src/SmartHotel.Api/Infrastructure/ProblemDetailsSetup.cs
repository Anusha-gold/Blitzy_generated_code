using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// Adds ProblemDetails support with <see cref="ApplyDefaults"/> as its customization step, registers
    /// <see cref="ApiProblemDetailsWriter"/> ahead of every framework writer, wires the sanitized diagnostics
    /// for unhandled exceptions that the exception handler cannot answer, and keeps the Development exception
    /// page from rendering exception data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The diagnostics wiring adds the HTTP context accessor and
    /// <see cref="ApiExceptionHandler.ServerErrorLogBridge"/> as a log provider, with a filter rule of its own
    /// that lets it receive the Error records of Kestrel's general category, which <c>appsettings.json</c>
    /// keeps away from every other provider.
    /// </para>
    /// <para>
    /// <see cref="ApiExceptionHandler.DeveloperPageProblemFilter"/> answers every exception that reaches the
    /// Development exception page with the generic problem in place of the page.
    /// </para>
    /// </remarks>
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

        // Kestrel reports exceptions that escape the pipeline or its response callbacks only to its general log
        // category, whose records carry the raw exception. The bridge alone receives them, through a rule for
        // its own provider type, and finds the request they belong to through the accessor.
        services.AddHttpContextAccessor();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, ApiExceptionHandler.ServerErrorLogBridge>());
        services.Configure<LoggerFilterOptions>(options =>
            options.AddFilter<ApiExceptionHandler.ServerErrorLogBridge>(
                ApiExceptionHandler.ServerErrorLogBridge.KestrelCategory,
                LogLevel.Error));

        // In Development the framework's exception page answers what the exception handler could not, and it
        // would publish the exception's message, stack trace and the request headers. The filter writes the
        // generic problem in its place.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IDeveloperPageExceptionFilter,
            ApiExceptionHandler.DeveloperPageProblemFilter>());

        return services;
    }

    /// <summary>
    /// Completes a problem with the API's defaults: its status, an RFC 9110 <c>type</c>, the reason phrase as
    /// <c>title</c>, a fixed <c>detail</c> sentence, the <c>traceId</c> and, on a 400, exactly one <c>errors</c>
    /// object: a field-message dictionary, which maps string keys to arrays of message strings. A <c>type</c>,
    /// <c>title</c> or <c>detail</c> that is missing or blank, holding only white space or U+FEFF, receives its
    /// default, and nonblank text the problem already holds is kept exactly, untrimmed. On both 400 branches the
    /// field messages are normalized into a new dictionary that keeps each string key verbatim, turns each
    /// collection of strings into an array of its non-null messages and drops every other entry. A plain 400
    /// takes the normalized copy of its <c>errors</c> extension, which is empty when the extension is missing or
    /// not a dictionary. A validation problem's <c>Errors</c> member is its <c>errors</c> object: it takes the
    /// normalized copy of its own messages or, when null, of the <c>errors</c> extension, and no <c>errors</c>
    /// extension is written beside it.
    /// </summary>
    /// <param name="context">The problem being produced and the request it answers.</param>
    public static void ApplyDefaults(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = context.ProblemDetails;
        var httpContext = context.HttpContext;

        // A status the MVC factory has set wins: on that path the response status is still 200.
        int status = problem.Status ??= httpContext.Response.StatusCode;

        // A blank type, title or detail counts as missing, because clients treat blank text as absent and MVC's
        // client-error mapping replaces only a null value. Nonblank text is kept exactly as it is, untrimmed.
        if (IsBlank(problem.Type))
        {
            problem.Type = TypeFor(status);
        }

        if (IsBlank(problem.Title))
        {
            var reasonPhrase = ReasonPhrases.GetReasonPhrase(status);
            problem.Title = string.IsNullOrEmpty(reasonPhrase) ? FallbackTitle : reasonPhrase;
        }

        // The default detail is a fixed sentence chosen by the status alone. context.Exception and
        // context.AdditionalMetadata are never read, so no exception or request data enters a default. A nonblank
        // detail the caller or MVC already set is kept as it is.
        if (IsBlank(problem.Detail))
        {
            problem.Detail = DetailFor(status);
        }

        problem.Extensions[TraceIdKey] = ResolveTraceId(httpContext);

        // Every 400 carries exactly one errors object, a field-message dictionary: string keys mapped to arrays of
        // message strings, with no null array and no null message. Both branches store a normalized copy, so the
        // valid entries of a source survive beside its invalid ones. A plain 400 takes the copy of its errors
        // extension, which is empty when the extension is missing or not a dictionary. On a validation problem,
        // MVC's ValidationProblemDetails included, the typed Errors member is that object: it takes the copy of
        // its own messages or, when the settable member is null, of the errors extension. The extension is then
        // removed, because beside the typed member it would serialize as a second errors member.
        if (status == StatusCodes.Status400BadRequest)
        {
            problem.Extensions.TryGetValue(ErrorsKey, out object? extension);
            if (problem is HttpValidationProblemDetails validation)
            {
                validation.Errors = NormalizeFieldMessages(validation.Errors ?? extension);
                problem.Extensions.Remove(ErrorsKey);
            }
            else
            {
                problem.Extensions[ErrorsKey] = NormalizeFieldMessages(extension);
            }
        }
    }

    /// <summary>
    /// Returns the identifier that correlates a response with its log events: the current activity's id when that
    /// activity uses the W3C format, otherwise the request's server-generated trace identifier.
    /// </summary>
    /// <param name="httpContext">The request being answered.</param>
    /// <returns>The value written as <c>traceId</c>.</returns>
    public static string ResolveTraceId(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // A W3C id is built only from validated hexadecimal trace and span ids. A hierarchical id embeds the text
        // of an incoming Request-Id or unparseable traceparent header, which must never reach a response or a log.
        return Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } activityId }
            ? activityId
            : httpContext.TraceIdentifier;
    }

    // Text is blank when it is null or holds only white space or U+FEFF. char.IsWhiteSpace does not count U+FEFF,
    // but JavaScript's String.prototype.trim strips it, so a browser client reads such text as empty.
    private static bool IsBlank([NotNullWhen(false)] string? text)
    {
        if (text is null)
        {
            return true;
        }

        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character) && character != '\uFEFF')
            {
                return false;
            }
        }

        return true;
    }

    // A field-message dictionary is what System.Text.Json writes as an object of message-string arrays. The copy
    // is always new and keys its entries ordinally, each string key verbatim. An entry whose value is a collection
    // of strings becomes a new array of its non-null messages in their original order, kept even when empty. An
    // entry with a null or non-string key, or a null value or one that is not a collection of strings, is dropped,
    // and the valid entries beside it are kept. A string is a character sequence and a list of key-value pairs is
    // written as an array, so neither qualifies, and any value that is not a dictionary yields an empty copy.
    private static Dictionary<string, string[]> NormalizeFieldMessages(object? value)
    {
        Dictionary<string, string[]> messages = new(StringComparer.Ordinal);
        IEnumerable<KeyValuePair<string, string[]>>? typedEntries = value switch
        {
            IDictionary<string, string[]> dictionary => dictionary,
            IReadOnlyDictionary<string, string[]> readOnlyDictionary => readOnlyDictionary,
            _ => null,
        };

        if (typedEntries is not null)
        {
            foreach (KeyValuePair<string, string[]> entry in typedEntries)
            {
                AddFieldMessages(messages, entry.Key, entry.Value);
            }
        }
        else if (value is IDictionary untypedEntries)
        {
            foreach (DictionaryEntry entry in untypedEntries)
            {
                AddFieldMessages(messages, entry.Key, entry.Value);
            }
        }

        return messages;
    }

    // A typed dictionary can still hold a null key, a null array or null messages at run time, so typed and
    // untyped entries pass the same checks.
    private static void AddFieldMessages(Dictionary<string, string[]> messages, object? key, object? value)
    {
        if (key is string field && value is IEnumerable<string> fieldMessages)
        {
            messages[field] = fieldMessages.Where(static message => message is not null).ToArray();
        }
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