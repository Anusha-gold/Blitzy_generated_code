using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Registers the API's ProblemDetails services and owns the single defaults routine that completes every
/// error body, whether MVC's ProblemDetails factory builds it or <see cref="ApiProblemDetailsWriter"/> writes
/// it for status-code pages and the exception handler. It also supplies MVC's model-binding messages and its
/// invalid-model-state response, so a binding 400 carries only fixed messages under contract keys.
/// </summary>
public static class ProblemDetailsSetup
{
    private const string Rfc9110SectionBase = "https://tools.ietf.org/html/rfc9110#section-";
    private const string AboutBlankType = "about:blank";
    private const string FallbackTitle = "Error";
    private const string TraceIdKey = "traceId";
    private const string ErrorsKey = "errors";

    // The media types MVC's own invalid-model-state response offers. No XML formatter is registered, so content
    // negotiation always settles on the JSON formatter's application/problem+json.
    private const string ProblemJsonMediaType = "application/problem+json";
    private const string ProblemXmlMediaType = "application/problem+xml";

    // The JSON path of the request body itself, and the prefix of a path to one of its members.
    private const string JsonRootPath = "$";
    private const string JsonMemberPathPrefix = "$.";

    // Binding messages are fixed sentences. None quotes an attempted value, a JSON fragment, a parser message or a
    // .NET type or member name, because the error key already says which input failed.
    private const string InvalidValueMessage = "The value is not valid.";
    private const string MissingValueMessage = "A value is required.";
    private const string NotANumberMessage = "The value must be a number.";
    private const string InvalidJsonMemberMessage = "The JSON value is not valid.";
    private const string InvalidBodyMessage = "The request body must be a valid JSON object.";

    /// <summary>
    /// Adds ProblemDetails support with <see cref="ApplyDefaults"/> as its customization step, registers
    /// <see cref="ApiProblemDetailsWriter"/> ahead of every framework writer, wires the sanitized diagnostics
    /// for unhandled exceptions that the exception handler cannot answer, keeps the Development exception
    /// page from rendering exception data, and gives the requests Kestrel rejects before the pipeline runs the
    /// same problem body.
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
    /// <para>
    /// Every Kestrel endpoint defaults to HTTP/1.1 and runs <see cref="ApiProblemDetailsWriter.ServerRejections"/>
    /// as its first connection middleware, ahead of any TLS layer. The middleware learns of each rejection from the
    /// server's own diagnostic event, so nothing is added to the request pipeline, whose first middleware stays the
    /// exception handler. An endpoint's own <c>Protocols</c> setting in configuration still overrides the default.
    /// A host on another server, such as the in-process test server, never resolves the Kestrel options and builds
    /// no connection middleware.
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

        // Kestrel rejects some requests while it parses them, before any middleware runs, and answers with a bodyless
        // status of its own; the connection middleware gives that response the problem body. Over HTTP/2 the rejection
        // is a bare HEADERS frame that the middleware cannot extend with a body, so endpoints default to HTTP/1.1,
        // which TLS serves just as well. Kestrel applies the endpoint defaults when it binds each endpoint, before an
        // HTTPS endpoint adds its TLS layer. When Kestrel builds the endpoint's connection pipeline, before it accepts
        // a connection, the middleware resolves the problem writer from the endpoint's application services and
        // subscribes to the server's rejection event on the host's diagnostic listener. ConfigureEndpointDefaults
        // replaces any earlier defaults action; nothing else in the API sets one.
        services.AddOptions<KestrelServerOptions>().Configure(static kestrel =>
            kestrel.ConfigureEndpointDefaults(static listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
                listenOptions.Use(next => ApiProblemDetailsWriter.ServerRejections.Create(next, listenOptions));
            }));

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

    /// <summary>
    /// Replaces every model-binding message that takes an argument with a fixed sentence, so no binding error quotes
    /// the attempted value, such as a query string's text, or a member name, which for a property is its .NET name.
    /// A value that cannot be converted, or that must not be null, gets "The value is not valid."; a missing
    /// <c>[BindRequired]</c> value gets "A value is required."; and the number check gets "The value must be a
    /// number.". The messages that take no argument are fixed framework sentences already and are kept.
    /// </summary>
    /// <param name="messageProvider">MVC's provider, <see cref="MvcOptions.ModelBindingMessageProvider"/>.</param>
    public static void UseFixedBindingMessages(DefaultModelBindingMessageProvider messageProvider)
    {
        ArgumentNullException.ThrowIfNull(messageProvider);

        messageProvider.SetAttemptedValueIsInvalidAccessor(static (_, _) => InvalidValueMessage);
        messageProvider.SetNonPropertyAttemptedValueIsInvalidAccessor(static _ => InvalidValueMessage);
        messageProvider.SetUnknownValueIsInvalidAccessor(static _ => InvalidValueMessage);
        messageProvider.SetValueIsInvalidAccessor(static _ => InvalidValueMessage);
        messageProvider.SetValueMustNotBeNullAccessor(static _ => InvalidValueMessage);
        messageProvider.SetMissingBindRequiredValueAccessor(static _ => MissingValueMessage);
        messageProvider.SetValueMustBeANumberAccessor(static _ => NotANumberMessage);
    }

    /// <summary>
    /// Builds the 400 that MVC returns when an <c>[ApiController]</c> action's model state is invalid, for use as
    /// <see cref="ApiBehaviorOptions.InvalidModelStateResponseFactory"/>. The request's
    /// <see cref="ProblemDetailsFactory"/> creates the <see cref="ValidationProblemDetails"/>, so
    /// <see cref="ApplyDefaults"/> completes it as it does every MVC problem, and the result offers the same problem
    /// media types as MVC's default response. The <c>errors</c> object comes from a sanitized copy of the model state
    /// built from its keys and fixed messages; attempted values, raw values and exception text are never read.
    /// </summary>
    /// <remarks>
    /// <para>For an action with a body parameter, the keys are the body's JSON paths:</para>
    /// <list type="bullet">
    /// <item><description>
    /// The input formatter files an error under the JSON path where reading stopped. The path is kept only when it
    /// is <c>$</c> or names a member of the body type, <c>$.</c> followed by the member's serialized name, which is
    /// then written in the contract's letter case. Any other path, through an unknown member, an array element or
    /// a nested value, can quote member names the client invented and names nothing in the contract, so it becomes
    /// <c>$</c>. A member path's message is "The JSON value is not valid." and the root's is "The request body must
    /// be a valid JSON object.".
    /// </description></item>
    /// <item><description>
    /// The body binder files its own error, such as "A non-empty request body is required." for an empty body,
    /// under the empty key. It is filed under <c>$</c>, the body itself, with its message.
    /// </description></item>
    /// <item><description>
    /// The required error filed under the body parameter's own name is dropped. That name belongs to the action's
    /// signature, not to the JSON contract, and the body's own error already reports the failure. Should no other
    /// error remain, <c>$</c> reports the body with the root's message.
    /// </description></item>
    /// </list>
    /// <para>
    /// Every other key is kept with the messages that validation attributes and the binding message provider set,
    /// and an error that carries only an exception gets "The value is not valid.". Each key lists a message once.
    /// </para>
    /// </remarks>
    /// <param name="context">The action whose model state is invalid.</param>
    /// <returns>The 400 object result that wraps the validation problem.</returns>
    public static IActionResult CreateInvalidModelStateResponse(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpContext httpContext = context.HttpContext;
        ProblemDetailsFactory factory = httpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        ValidationProblemDetails problem = factory.CreateValidationProblemDetails(
            httpContext,
            SanitizeModelState(context));

        // As in MVC's default response, a 400 stays a BadRequestObjectResult.
        ObjectResult result = problem.Status == StatusCodes.Status400BadRequest
            ? new BadRequestObjectResult(problem)
            : new ObjectResult(problem) { StatusCode = problem.Status };
        result.ContentTypes.Add(ProblemJsonMediaType);
        result.ContentTypes.Add(ProblemXmlMediaType);

        return result;
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

    // A new model state that holds only error keys and fixed messages, as CreateInvalidModelStateResponse describes.
    // The source's own error limit already bounds the copy, so the copy sets none: a limit would add an error of its
    // own once reached.
    private static ModelStateDictionary SanitizeModelState(ActionContext context)
    {
        // Model state keys ignore letter case, so the copy merges keys the same way.
        Dictionary<string, List<string>> fields = new(StringComparer.OrdinalIgnoreCase);

        // MVC binds at most one parameter from the body.
        ParameterDescriptor? body = context.ActionDescriptor.Parameters.FirstOrDefault(
            static parameter => parameter.BindingInfo?.BindingSource == BindingSource.Body);
        string? bodyModelName = body is null ? null : body.BindingInfo?.BinderModelName ?? body.Name;
        string[]? memberNames = null;
        StringComparison memberComparison = StringComparison.Ordinal;
        bool droppedBodyModelError = false;

        foreach (KeyValuePair<string, ModelStateEntry> entry in context.ModelState)
        {
            ModelErrorCollection errors = entry.Value.Errors;
            if (errors.Count == 0)
            {
                continue;
            }

            string key = entry.Key;
            if (body is null)
            {
                AddModelErrorMessages(fields, key, errors, InvalidValueMessage);
            }
            else if (string.Equals(key, bodyModelName, StringComparison.OrdinalIgnoreCase))
            {
                droppedBodyModelError = true;
            }
            else if (key.Length == 0)
            {
                AddModelErrorMessages(fields, JsonRootPath, errors, InvalidBodyMessage);
            }
            else if (key.StartsWith(JsonRootPath, StringComparison.Ordinal))
            {
                if (memberNames is null)
                {
                    (memberNames, memberComparison) = ReadBodyMemberNames(context.HttpContext, body);
                }

                string path = ToContractPath(key, memberNames, memberComparison);
                AddMessage(fields, path, path == JsonRootPath ? InvalidBodyMessage : InvalidJsonMemberMessage);
            }
            else
            {
                AddModelErrorMessages(fields, key, errors, InvalidValueMessage);
            }
        }

        if (fields.Count == 0 && droppedBodyModelError)
        {
            AddMessage(fields, JsonRootPath, InvalidBodyMessage);
        }

        ModelStateDictionary sanitized = new(int.MaxValue);
        foreach (KeyValuePair<string, List<string>> field in fields)
        {
            foreach (string message in field.Value)
            {
                sanitized.AddModelError(field.Key, message);
            }
        }

        return sanitized;
    }

    // The serialized names of the body type's members under the JSON input formatter's serializer options, and the
    // comparison its serializer uses to match a payload's member names with them.
    private static (string[] Names, StringComparison Comparison) ReadBodyMemberNames(
        HttpContext httpContext,
        ParameterDescriptor body)
    {
        JsonSerializerOptions serializerOptions = httpContext.RequestServices
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value
            .JsonSerializerOptions;
        StringComparison comparison = serializerOptions.PropertyNameCaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        try
        {
            JsonTypeInfo typeInfo = serializerOptions.GetTypeInfo(body.ParameterType);
            return ([.. typeInfo.Properties.Select(static property => property.Name)], comparison);
        }
        catch (NotSupportedException)
        {
            // A type the serializer cannot describe has no member a path could name, so every path is the root.
            return ([], comparison);
        }
    }

    // $ itself, or $. followed by exactly one member name of the body type, written as the contract writes it. The
    // serializer matches names without regard to case under the Web defaults, so the payload's spelling must not
    // reach the key. Every other path becomes the root.
    private static string ToContractPath(string path, string[] memberNames, StringComparison comparison)
    {
        if (path.StartsWith(JsonMemberPathPrefix, StringComparison.Ordinal))
        {
            ReadOnlySpan<char> member = path.AsSpan(JsonMemberPathPrefix.Length);
            foreach (string name in memberNames)
            {
                if (member.Equals(name, comparison))
                {
                    return JsonMemberPathPrefix + name;
                }
            }
        }

        return JsonRootPath;
    }

    // Each error's own message, which on these keys is fixed text from a validation attribute or the binding message
    // provider; an error with no message carries only an exception, which is never read, and gets the fallback.
    private static void AddModelErrorMessages(
        Dictionary<string, List<string>> fields,
        string key,
        ModelErrorCollection errors,
        string fallbackMessage)
    {
        foreach (ModelError error in errors)
        {
            string message = string.IsNullOrWhiteSpace(error.ErrorMessage) ? fallbackMessage : error.ErrorMessage;
            AddMessage(fields, key, message);
        }
    }

    private static void AddMessage(Dictionary<string, List<string>> fields, string key, string message)
    {
        if (!fields.TryGetValue(key, out List<string>? messages))
        {
            messages = [];
            fields.Add(key, messages);
        }

        if (!messages.Contains(message, StringComparer.Ordinal))
        {
            messages.Add(message);
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
            408 => "The server did not receive the complete request in time.",
            409 => "The request conflicts with the current state of the resource.",
            413 => "The request body exceeds the size the server accepts.",
            414 => "The request target exceeds the length the server accepts.",
            415 => "The request content type is not supported. Send application/json.",
            431 => "The request header fields exceed the size the server accepts.",
            500 => "An unexpected error occurred.",
            505 => "The HTTP version of the request is not supported.",
            _ => "The request could not be processed.",
        };
    }
}