using System.Collections.Frozen;
using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Logs every unhandled exception as one sanitized Error event and leaves the response to the exception
/// handler middleware, which writes a generic 500 problem through <see cref="IProblemDetailsService"/>. A
/// <see cref="BadHttpRequestException"/> that carries a client error, which the server raises while the request is
/// read, is answered here with the generic problem for that status instead, and is not logged.
/// </summary>
/// <remarks>
/// <para>
/// The event carries only the trace identifier, the request method, the matched route template (or
/// "unmatched"), the type names of the exception and of its inner exceptions, their stack frames reduced to
/// type and method names, two truncation flags and, for the first <see cref="PostgresException"/> among all
/// the collected exceptions, its <see cref="PostgresException.SqlState"/> and
/// <see cref="PostgresException.ConstraintName"/>. The exceptions are collected breadth-first over
/// <see cref="Exception.InnerException"/> and every <see cref="AggregateException.InnerExceptions"/> item,
/// each distinct exception once. At most 65,536 exception references are examined: the exception itself, each
/// inner exception and each aggregate item, repeats included. That budget bounds the work, and TypesTruncated
/// is <see langword="true"/> only when it leaves a reference unexamined. The type list holds one entry for each
/// of the first 256 collected exceptions, in breadth-first order, followed once, in first-seen order, by each
/// type of the further collected exceptions that those entries do not already name, so every type reached is
/// named. At most 200 frames are listed in total across the first 256 collected exceptions, and
/// FramesTruncated is <see langword="true"/> when that cap leaves a frame out or when further exceptions were
/// collected, whose frames are not described. Exception messages, <see cref="Exception.Data"/>, the formatted
/// stack trace, the path, route and query values, headers, the body, claims and connection details are never
/// logged, because any of them can carry personal data or secrets.
/// </para>
/// <para>
/// Some exceptions never reach this handler or leave the request pipeline without a problem response: the
/// middleware passes no exception to its handlers once the response has started, it rethrows the original
/// exception when writing the problem fails, and the server runs the response-starting and
/// response-completed callbacks itself. Kestrel reports each of these exceptions on its own, and
/// <see cref="ServerErrorLogBridge"/> translates every such report into the same sanitized event. In
/// Development the framework's exception page answers an exception that reaches it before the response
/// starts, whether the middleware rethrew it or it was thrown outside the middleware;
/// <see cref="DeveloperPageProblemFilter"/> writes the generic problem in its place, so the page never
/// renders the exception's message, its stack trace or the request headers.
/// </para>
/// <para>
/// Each exception is logged once per request: a report of an exception already logged for the request, or
/// reachable from one through inner exceptions, writes nothing. The framework's own records of these
/// exceptions, from the exception handler middleware, the Development exception page and Kestrel's general
/// category, carry the raw exception. <c>appsettings.json</c> filters them out, and
/// <see cref="FrameworkLogRedaction"/> withholds them from every provider whatever log levels the configuration
/// sets. A rule of the bridge's own, which the redaction exempts, lets it alone receive Kestrel's records, and it
/// forwards neither their exception nor their message, so this event is the only one written.
/// </para>
/// </remarks>
/// <param name="logger">The logger that receives the sanitized event.</param>
/// <param name="problemDetailsService">The service that writes the generic problem for a client error.</param>
public sealed partial class ApiExceptionHandler(
    ILogger<ApiExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    private const int MaxInspections = 65_536;
    private const int MaxListedExceptions = 256;
    private const int MaxStackFrames = 200;
    private const string UnmatchedRoute = "unmatched";
    private const string UnknownMethod = "unknown";

    // The Items entry under this key lists the exceptions already logged for the request.
    private static readonly object ReportedExceptionsKey = new();

    /// <summary>
    /// Answers a <see cref="BadHttpRequestException"/> whose status is a client error, from 400 to 499, with the
    /// generic problem for that status. Writes the sanitized Error event for every other exception and declines to
    /// handle it, so the middleware produces the 500 problem body. Neither body holds exception data.
    /// </summary>
    /// <param name="httpContext">The request whose processing threw.</param>
    /// <param name="exception">The unhandled exception.</param>
    /// <param name="cancellationToken">
    /// Not observed: the problem writer observes the request's own abort token.
    /// </param>
    /// <returns>
    /// <see langword="true"/> once the client-error problem has been written; otherwise <see langword="false"/>, so
    /// the middleware writes the 500 problem.
    /// </returns>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        // The server rejects a request it cannot read any further, such as a body over its size limit, by throwing
        // from the body read with the client error it stands for, and MVC's JSON input formatter lets that exception
        // through. The middleware has already set 500 by now and, when every handler declines, writes the problem with
        // status 500, so declining would report the client's error as a server fault. The problem is written here
        // instead, from the status alone: the exception and its message, which names the server's limit, are never
        // passed on. A client error is not a server fault, so no Error event is written for it.
        if (exception is BadHttpRequestException { StatusCode: >= 400 and < 500 } rejection)
        {
            httpContext.Response.StatusCode = rejection.StatusCode;
            await problemDetailsService.WriteAsync(new ProblemDetailsContext { HttpContext = httpContext });
            return true;
        }

        // When writing the problem fails, the middleware rethrows this same exception and the server reports it;
        // the record kept for the request stops that report from logging it a second time.
        LogUnlessReported(logger, httpContext, exception, fallbackTraceId: null);

        return false;
    }

    /// <summary>
    /// Writes the sanitized Error event for an exception raised outside the request pipeline, where neither the
    /// request method nor the route is known, such as while the server's own rejection of a request is completed.
    /// </summary>
    /// <param name="logger">The logger that receives the event.</param>
    /// <param name="exception">The exception to describe by its types and frames.</param>
    /// <param name="traceIdentifier">The server's identifier for the request the exception concerns.</param>
    internal static void LogOutsideRequest(ILogger logger, Exception exception, string traceIdentifier)
    {
        LogUnlessReported(logger, httpContext: null, exception, traceIdentifier);
    }

    // Writes the sanitized event unless this exception, or one it is reachable from, is already logged for the
    // request. A report without a request is still written, identified by the server's request id.
    private static void LogUnlessReported(
        ILogger logger,
        HttpContext? httpContext,
        Exception exception,
        string? fallbackTraceId)
    {
        if (httpContext is null)
        {
            // The shared resolver decides which identifier is published, and a stand-in context carries the
            // server's request id as its fallback. Neither the method nor the route is known.
            var standIn = new DefaultHttpContext();
            if (fallbackTraceId is not null)
            {
                standIn.TraceIdentifier = fallbackTraceId;
            }

            WriteEvent(
                logger,
                ProblemDetailsSetup.ResolveTraceId(standIn),
                UnknownMethod,
                UnmatchedRoute,
                exception);
            return;
        }

        var reported = GetReportedExceptions(httpContext);
        if (IsReported(reported, exception))
        {
            return;
        }

        reported.Add(exception);
        LogSanitized(logger, httpContext, exception);
    }

    private static List<Exception> GetReportedExceptions(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(ReportedExceptionsKey, out var item) && item is List<Exception> reported)
        {
            return reported;
        }

        var created = new List<Exception>();
        httpContext.Items[ReportedExceptionsKey] = created;
        return created;
    }

    // An exception is covered when it is a logged one or reachable from one through InnerException and
    // AggregateException items, because the logged event already names its type and frames. The visited set
    // stops cycles and repeats. The search spends the same budget of MaxInspections references as the chain
    // collection, so its work is bounded however wide or deep the logged graphs are; an exception the budget
    // leaves unexamined counts as not yet logged and gets its own event.
    private static bool IsReported(List<Exception> reported, Exception exception)
    {
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        var remaining = MaxInspections;

        foreach (var logged in reported)
        {
            if (!Inspect(logged))
            {
                break;
            }
        }

        while (pending.TryPop(out var current))
        {
            if (ReferenceEquals(current, exception))
            {
                return true;
            }

            if (current.InnerException is { } inner)
            {
                Inspect(inner);
            }

            if (current is AggregateException aggregate)
            {
                foreach (var item in aggregate.InnerExceptions)
                {
                    if (!Inspect(item))
                    {
                        break;
                    }
                }
            }
        }

        return false;

        bool Inspect(Exception candidate)
        {
            if (remaining == 0)
            {
                return false;
            }

            remaining--;
            if (visited.Add(candidate))
            {
                pending.Push(candidate);
            }

            return true;
        }
    }

    private static void LogSanitized(ILogger logger, HttpContext httpContext, Exception exception)
    {
        // The middleware clears the request's endpoint and route values before it calls handlers and keeps the
        // matched endpoint on the feature. An exception that left the pipeline without reaching the handlers
        // still has its endpoint on the request. The raw route template holds no request values.
        var endpoint = (httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? httpContext.GetEndpoint())
            as RouteEndpoint;
        var routeTemplate = endpoint?.RoutePattern.RawText ?? UnmatchedRoute;

        WriteEvent(
            logger,
            ProblemDetailsSetup.ResolveTraceId(httpContext),
            httpContext.Request.Method,
            routeTemplate,
            exception);
    }

    private static void WriteEvent(
        ILogger logger,
        string traceId,
        string method,
        string routeTemplate,
        Exception exception)
    {
        var description = DescribeSanitized(exception);

        // The exception is never passed as the logger's exception argument, because every provider would then
        // print its message and formatted stack trace.
        LogUnhandledException(
            logger,
            traceId,
            method,
            routeTemplate,
            description.ExceptionTypes,
            description.ExceptionTypesTruncated,
            description.SqlState,
            description.ConstraintName,
            description.StackFrames,
            description.StackFramesTruncated);
    }

    /// <summary>
    /// Describes <paramref name="exception"/> as the sanitized Error event does: the type names of the collected
    /// exceptions, the <see cref="PostgresException.SqlState"/> and <see cref="PostgresException.ConstraintName"/>
    /// of the first <see cref="PostgresException"/> among them, and their stack frames reduced to type and method
    /// names, each list with its truncation flag, under the bounds the class remarks set.
    /// </summary>
    /// <remarks>
    /// No message, <see cref="Exception.Data"/>, formatted stack trace or file information is read, so the
    /// description can be logged wherever an exception must be reported without its text, such as a failed startup.
    /// </remarks>
    /// <param name="exception">The exception to describe.</param>
    /// <returns>The sanitized description, in the order the Error event lists its fields.</returns>
    internal static (
        string[] ExceptionTypes,
        bool ExceptionTypesTruncated,
        string? SqlState,
        string? ConstraintName,
        string[] StackFrames,
        bool StackFramesTruncated) DescribeSanitized(Exception exception)
    {
        var (chain, typesTruncated) = CollectExceptionChain(exception);
        var exceptionTypes = DescribeExceptionTypes(chain);
        var postgresException = chain.OfType<PostgresException>().FirstOrDefault();
        var (stackFrames, framesTruncated) = DescribeStackFrames(chain);

        // Only SqlState and ConstraintName are read from a PostgresException: its message, detail, hint and query
        // text can contain key values.
        return (
            exceptionTypes,
            typesTruncated,
            postgresException?.SqlState,
            postgresException?.ConstraintName,
            stackFrames,
            framesTruncated);
    }

    // Breadth-first over InnerException and every AggregateException item. Each reference examined spends one
    // unit of a budget of MaxInspections: the root, every InnerException and every aggregate item, repeats
    // included. The reference-equality set collects each exception once and stops cycles and repeats, such as an
    // AggregateException's InnerException, which is also its first item, so the chain holds every distinct
    // exception reached within the budget. Once the budget is spent, an aggregate's items are no longer
    // enumerated, and exceptions already queued are still collected without examining their children, so the
    // work is bounded however wide, deep or repetitive the graph is. Truncated is true only when at least one
    // reference was left unexamined.
    private static (List<Exception> Chain, bool Truncated) CollectExceptionChain(Exception root)
    {
        var chain = new List<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<Exception>();
        var remaining = MaxInspections;
        var truncated = false;
        Inspect(root);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            chain.Add(current);

            if (current.InnerException is { } inner)
            {
                Inspect(inner);
            }

            if (current is AggregateException aggregate)
            {
                foreach (var item in aggregate.InnerExceptions)
                {
                    if (!Inspect(item))
                    {
                        break;
                    }
                }
            }
        }

        return (chain, truncated);

        bool Inspect(Exception candidate)
        {
            if (remaining == 0)
            {
                truncated = true;
                return false;
            }

            remaining--;
            if (visited.Add(candidate))
            {
                pending.Enqueue(candidate);
            }

            return true;
        }
    }

    // One entry per collected exception for the first MaxListedExceptions, in chain order, so each entry's
    // position matches the prefix of that exception's frames. Each type of the further collected exceptions
    // that those entries do not already name follows once, in first-seen order, so every collected type is
    // named while the list beyond the entries grows only with the number of distinct types.
    private static string[] DescribeExceptionTypes(List<Exception> chain)
    {
        var listed = Math.Min(chain.Count, MaxListedExceptions);
        var types = new List<string>(listed);
        var named = new HashSet<Type>();

        for (var index = 0; index < listed; index++)
        {
            var type = chain[index].GetType();
            named.Add(type);
            types.Add(type.FullName ?? type.Name);
        }

        for (var index = listed; index < chain.Count; index++)
        {
            var type = chain[index].GetType();
            if (named.Add(type))
            {
                types.Add(type.FullName ?? type.Name);
            }
        }

        return [.. types];
    }

    // Each frame becomes "<position in the chain>:<declaring type>.<method>". Only the first MaxListedExceptions
    // collected exceptions are described, so every position matches an entry of the type list. The frames come
    // from the trace the exception captured, not its formatted StackTrace string, and file information is not
    // requested, so no message text, file path or line number is read. At most MaxStackFrames frames are listed
    // in total, and Truncated is true when at least one further frame with a method was left out, or when
    // exceptions beyond the described ones were collected, whose frames are not described.
    private static (string[] Frames, bool Truncated) DescribeStackFrames(List<Exception> chain)
    {
        var frames = new List<string>();
        var described = Math.Min(chain.Count, MaxListedExceptions);

        for (var index = 0; index < described; index++)
        {
            foreach (var frame in new StackTrace(chain[index], fNeedFileInfo: false).GetFrames())
            {
                var method = frame.GetMethod();
                if (method is null)
                {
                    continue;
                }

                if (frames.Count == MaxStackFrames)
                {
                    return (frames.ToArray(), true);
                }

                frames.Add($"{index}:{method.DeclaringType?.FullName ?? "?"}.{method.Name}");
            }
        }

        return (frames.ToArray(), chain.Count > described);
    }

    [LoggerMessage(
        EventId = 5000,
        EventName = "UnhandledException",
        Level = LogLevel.Error,
        Message = "Unhandled exception. TraceId={TraceId} Method={Method} Route={RouteTemplate} "
            + "Types={ExceptionTypes} TypesTruncated={ExceptionTypesTruncated} SqlState={SqlState} "
            + "Constraint={ConstraintName} Frames={StackFrames} FramesTruncated={StackFramesTruncated}")]
    private static partial void LogUnhandledException(
        ILogger logger,
        string traceId,
        string method,
        string routeTemplate,
        string[] exceptionTypes,
        bool exceptionTypesTruncated,
        string? sqlState,
        string? constraintName,
        string[] stackFrames,
        bool stackFramesTruncated);

    /// <summary>
    /// Answers, in Development, an exception that reaches the framework's developer exception page with the
    /// generic problem instead of the page, which would publish the exception's message, its stack trace and
    /// the request headers.
    /// </summary>
    /// <remarks>
    /// The page receives an exception the exception handler middleware rethrows because handling it failed
    /// before the response started, and one thrown by any middleware that runs ahead of the exception handler.
    /// By then the page has cleared the response and set its status, so the problem written here carries only the
    /// shared defaults. The rest of the filter chain is never invoked, because it ends in the page itself.
    /// </remarks>
    /// <param name="logger">The logger that receives the sanitized event.</param>
    /// <param name="problemDetailsService">The service that writes the generic problem.</param>
    internal sealed class DeveloperPageProblemFilter(
        ILogger<ApiExceptionHandler> logger,
        IProblemDetailsService problemDetailsService) : IDeveloperPageExceptionFilter
    {
        /// <summary>
        /// Writes the sanitized event for the exception unless the request has already logged it, then writes
        /// the generic problem. A failure to write propagates: the page then rethrows the original exception to
        /// the server, whose report of it writes nothing more, because the exception is already logged.
        /// </summary>
        /// <param name="errorContext">The request and the exception the page received.</param>
        /// <param name="next">The rest of the filter chain, which is never invoked.</param>
        /// <returns>A task that completes once the problem has been written.</returns>
        public Task HandleExceptionAsync(ErrorContext errorContext, Func<ErrorContext, Task> next)
        {
            ArgumentNullException.ThrowIfNull(errorContext);

            LogUnlessReported(logger, errorContext.HttpContext, errorContext.Exception, fallbackTraceId: null);

            return problemDetailsService
                .WriteAsync(new ProblemDetailsContext { HttpContext = errorContext.HttpContext })
                .AsTask();
        }
    }

    /// <summary>
    /// A log provider that turns each unhandled exception Kestrel reports into the sanitized event, so an
    /// exception that never reaches <see cref="TryHandleAsync"/>, or leaves the pipeline after it, is still
    /// diagnosed.
    /// </summary>
    /// <remarks>
    /// Kestrel logs an exception that escapes the request pipeline, a failed response-starting or
    /// response-completed callback and a response that breaks its declared length under its general category,
    /// one record per exception, while the request is still in progress. This provider reads only those
    /// records and forwards neither their exception nor their formatted message. It ignores every other
    /// category, the handler's own included, so the event it writes never comes back to it.
    /// </remarks>
    /// <param name="services">
    /// The root service provider. The handler's logger is resolved from it on first use, because the logger
    /// factory that creates that logger also constructs this provider.
    /// </param>
    /// <param name="httpContextAccessor">The accessor that yields the request a record belongs to.</param>
    internal sealed class ServerErrorLogBridge(IServiceProvider services, IHttpContextAccessor httpContextAccessor)
        : ILoggerProvider
    {
        /// <summary>
        /// The category of Kestrel's general records, under which it reports application errors.
        /// </summary>
        internal const string KestrelCategory = "Microsoft.AspNetCore.Server.Kestrel";

        private const int ApplicationErrorEventId = 13;
        private const string ApplicationErrorEventName = "ApplicationError";
        private const string TraceIdentifierKey = "TraceIdentifier";

        private ILogger? _logger;
        private volatile bool _disposed;

        /// <summary>
        /// Returns the translating logger for Kestrel's general category and a logger that is never enabled for
        /// any other category.
        /// </summary>
        /// <param name="categoryName">The category the logger factory asks for.</param>
        /// <returns>The logger for <paramref name="categoryName"/>.</returns>
        public ILogger CreateLogger(string categoryName)
        {
            return string.Equals(categoryName, KestrelCategory, StringComparison.Ordinal)
                ? new ApplicationErrorTranslator(this)
                : NullLogger.Instance;
        }

        /// <summary>
        /// Stops translating, because the services the event is written to are being disposed.
        /// </summary>
        public void Dispose()
        {
            _disposed = true;
        }

        private void Translate<TState>(TState state, Exception exception)
        {
            var logger = ResolveLogger();
            if (logger is null)
            {
                return;
            }

            LogUnlessReported(logger, httpContextAccessor.HttpContext, exception, ReadTraceIdentifier(state));
        }

        // Resolving the logger in the constructor would make the logger factory depend on itself. Once the host
        // has disposed its services, there is nothing left to write the event to.
        private ILogger? ResolveLogger()
        {
            if (_disposed)
            {
                return null;
            }

            try
            {
                return _logger ??= services.GetRequiredService<ILogger<ApiExceptionHandler>>();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        // The record's state lists its template values, and the server's request id is one of them.
        private static string? ReadTraceIdentifier<TState>(TState state)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    if (string.Equals(key, TraceIdentifierKey, StringComparison.Ordinal))
                    {
                        return value as string;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// The logger behind Kestrel's general category. It acts on application errors only.
        /// </summary>
        /// <param name="bridge">The provider that translates the records.</param>
        private sealed class ApplicationErrorTranslator(ServerErrorLogBridge bridge) : ILogger
        {
            /// <summary>
            /// Opens no scope: scope values never reach the sanitized event.
            /// </summary>
            /// <typeparam name="TState">The type of the scope state.</typeparam>
            /// <param name="state">The scope state, which is not read.</param>
            /// <returns>Always <see langword="null"/>.</returns>
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            /// <summary>
            /// Reports whether <paramref name="logLevel"/> is the level of Kestrel's application errors.
            /// </summary>
            /// <param name="logLevel">The level of the record.</param>
            /// <returns><see langword="true"/> for <see cref="LogLevel.Error"/> only.</returns>
            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel == LogLevel.Error;
            }

            /// <summary>
            /// Writes the sanitized event for an application error that carries its exception, and ignores every
            /// other record. Neither the exception nor the formatted message is forwarded.
            /// </summary>
            /// <typeparam name="TState">The type of the record's state.</typeparam>
            /// <param name="logLevel">The level of the record.</param>
            /// <param name="eventId">The record's event, which identifies an application error.</param>
            /// <param name="state">The record's template values, from which only the request id is read.</param>
            /// <param name="exception">The exception the server reports.</param>
            /// <param name="formatter">The message formatter, which is never invoked.</param>
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (exception is null
                    || !IsEnabled(logLevel)
                    || eventId.Id != ApplicationErrorEventId
                    || !string.Equals(eventId.Name, ApplicationErrorEventName, StringComparison.Ordinal))
                {
                    return;
                }

                bridge.Translate(state, exception);
            }
        }
    }

    /// <summary>
    /// Withholds, from every log provider, the framework log records that carry request values or exception text,
    /// whatever log levels the configuration sets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Log levels are configuration, and any configuration source, an environment variable such as
    /// <c>Logging__LogLevel__Default=Trace</c> included, can lower them. At the levels such a change admits, several
    /// framework categories write the request line with its query string, the request path and route values, the
    /// <c>Host</c>, <c>Origin</c>, <c>Accept</c> and <c>Content-Type</c> header values, action arguments, or the
    /// messages of JSON, token-validation, server and host exceptions. Some of them pass even the default levels,
    /// such as the generic host's Error record of a hosted service that failed to start, which carries the exception
    /// of a server that could not bind its configured address. A level threshold therefore cannot keep them out of
    /// the logs; this boundary does, in code, at the logger factory, before any provider receives a record.
    /// </para>
    /// <para>
    /// <see cref="ProblemDetailsSetup.AddApiProblemDetails"/> registers the redaction, once however often it is
    /// called, as a post-configuration of the host's <see cref="LoggerFilterOptions"/>. Nothing is read from
    /// configuration or resolved at registration. The redaction runs as the last step of building those options,
    /// after every configuration source and every registration has added its rules, and again whenever the
    /// configuration reloads. Each rule keeps its provider, category, level and filter, and additionally refuses every
    /// redacted level of every redacted category. A first rule that names neither a provider nor a category, at the
    /// configured minimum level, applies the same refusal to the loggers no other rule selects. Configuration
    /// therefore still decides every other category and level, and can still quiet a redacted category further, but
    /// can never admit a redacted level.
    /// </para>
    /// <para>
    /// Scopes are not captured. Hosting opens a scope holding the request path for every request, and a provider
    /// configured to include scopes would add that path to every record the request writes, the sanitized exception
    /// record included.
    /// </para>
    /// <para>
    /// <see cref="ServerErrorLogBridge"/> is exempt. It receives Kestrel's general records only to replace each report
    /// of an unhandled exception with the sanitized event, and it forwards neither their exception nor their message.
    /// </para>
    /// </remarks>
    internal sealed class FrameworkLogRedaction : IPostConfigureOptions<LoggerFilterOptions>
    {
        // The provider that translates Kestrel's unhandled-exception reports into the sanitized event.
        private static readonly string BridgeProviderName = typeof(ServerErrorLogBridge).FullName!;

        // Every level of each category that carries a record with request values or exception text in ASP.NET Core
        // 8.0.34 and its generic host. The categories are the exact names the framework creates its loggers under.
        // Levels not listed carry only fixed text, type, endpoint and route-template names, or identifiers the server
        // generates, and stay under the configuration's control.
        private static readonly FrozenDictionary<string, int> RedactedLevels = new Dictionary<string, int>
        {
            // Request starting, request finished and request unhandled: method, host, path and query string.
            // Critical stays open, although its records, the web host's startup failures, carry their exception:
            // hosting starts the request activity, whose W3C id is every problem's traceId, only while this category
            // is enabled at Critical, so keeping exception text out of the logs yields here to that correlation. The
            // records are written before any request is served.
            ["Microsoft.AspNetCore.Hosting.Diagnostics"] = Levels(LogLevel.Information),

            // A request host that matches no allowed host, or that matches one: the Host header value.
            ["Microsoft.AspNetCore.HostFiltering.HostFilteringMiddleware"] = Levels(
                LogLevel.Trace,
                LogLevel.Information),

            // The HTTPS redirect target: host, path and query string.
            ["Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware"] = Levels(LogLevel.Debug),

            // Kestrel's general category: unhandled application and connection exceptions at Warning, Error and
            // Critical, and binding failures with their exception at Trace and Information. The heartbeat and
            // request-body notices at those levels are withheld with them. Its Debug records name only connection and
            // request identifiers.
            ["Microsoft.AspNetCore.Server.Kestrel"] = Levels(
                LogLevel.Trace,
                LogLevel.Information,
                LogLevel.Warning,
                LogLevel.Error,
                LogLevel.Critical),

            // Malformed requests: the bad-request message, which quotes request-line and header fragments, and the
            // exception that ended request processing.
            ["Microsoft.AspNetCore.Server.Kestrel.BadRequests"] = Levels(LogLevel.Debug),

            // HTTP/2 and HTTP/3 connection, stream, HPACK and QPACK failures, each with its exception.
            ["Microsoft.AspNetCore.Server.Kestrel.Http2"] = Levels(
                LogLevel.Debug,
                LogLevel.Information,
                LogLevel.Critical),
            ["Microsoft.AspNetCore.Server.Kestrel.Http3"] = Levels(LogLevel.Debug, LogLevel.Information),

            // Socket communication errors with their exception, and the abort reasons sent with FIN and RST.
            ["Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets"] = Levels(LogLevel.Debug),

            // TLS handshake and certificate-store failures, each with its exception.
            ["Microsoft.AspNetCore.Server.Kestrel.Https.Internal.HttpsConnectionMiddleware"] = Levels(LogLevel.Debug),

            // The raw unhandled exception, and an exception thrown while handling it or rendering the page.
            ["Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware"] = Levels(LogLevel.Error),
            ["Microsoft.AspNetCore.Diagnostics.DeveloperExceptionPageMiddleware"] = Levels(LogLevel.Error),

            // The request's Origin header, and a rejected origin, method or request header.
            ["Microsoft.AspNetCore.Cors.Infrastructure.CorsService"] = Levels(LogLevel.Debug, LogLevel.Information),

            // A failure to apply the CORS response headers, with its exception.
            ["Microsoft.AspNetCore.Cors.Infrastructure.CorsMiddleware"] = Levels(LogLevel.Warning),

            // Candidate matching: the request path and a route value a constraint rejected.
            ["Microsoft.AspNetCore.Routing.Matching.DfaMatcher"] = Levels(LogLevel.Debug),

            // Parsing a request path against a named route, which the readiness preflight check does for every
            // preflight: the request path. The parser writes at no other level.
            ["Microsoft.AspNetCore.Routing.DefaultLinkParser"] = Levels(LogLevel.Debug),

            // A rejected token's validation exception, the failure message copied from it, and an unexpected
            // failure's exception. The handler rethrows an unexpected failure, so the sanitized event still records
            // it.
            ["Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler"] = Levels(
                LogLevel.Information,
                LogLevel.Error),

            // The JSON reader's exception message, which quotes the property path as the request body spelled it.
            ["Microsoft.AspNetCore.Mvc.Formatters.SystemTextJsonInputFormatter"] = Levels(LogLevel.Debug),

            // The request's Content-Type header value.
            ["Microsoft.AspNetCore.Mvc.ModelBinding.Binders.BodyModelBinder"] = Levels(LogLevel.Debug),

            // The request's Accept header values.
            ["Microsoft.AspNetCore.Mvc.Infrastructure.DefaultOutputFormatterSelector"] = Levels(LogLevel.Debug),

            // The bound action arguments: query values and request bodies.
            ["Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker"] = Levels(LogLevel.Trace),

            // The generic host's own records, each failure with its exception: a hosted service that failed to start,
            // such as the server failing to bind its configured address, at Error; a faulted background service at
            // Error and the host it stops at Critical; and an exception while stopping at Debug, beside the fixed
            // starting, started, stopping and stopped notices. The host writes nothing at Trace, Information or
            // Warning, which stay under the configuration's control.
            ["Microsoft.Extensions.Hosting.Internal.Host"] = Levels(
                LogLevel.Debug,
                LogLevel.Error,
                LogLevel.Critical),

            // An exception thrown by a started, stopping or stopped callback of the application lifetime, with the
            // loader messages of a type-load failure copied into the record's text.
            ["Microsoft.Extensions.Hosting.Internal.ApplicationLifetime"] = Levels(LogLevel.Critical),
        }.ToFrozenDictionary(StringComparer.Ordinal);

        private static readonly Func<string?, string?, LogLevel, bool> RedactionOnly =
            static (provider, category, level) => !IsRedacted(provider, category, level);

        /// <summary>
        /// Stops scope capture, adds the redaction to every rule's filter, and puts a rule in front that selects only
        /// the loggers no other rule matches, at the configured minimum level and with the redaction.
        /// </summary>
        /// <param name="name">The options name; the logger factory reads the default, unnamed options.</param>
        /// <param name="options">The options after every configuration step.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public void PostConfigure(string? name, LoggerFilterOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            options.CaptureScopes = false;

            IList<LoggerFilterRule> rules = options.Rules;
            for (int index = 0; index < rules.Count; index++)
            {
                LoggerFilterRule rule = rules[index];
                rules[index] = new LoggerFilterRule(
                    rule.ProviderName,
                    rule.CategoryName,
                    rule.LogLevel,
                    Redact(rule.Filter));
            }

            // A rule with neither a provider nor a category ranks below every other rule that applies to a logger,
            // and the first of equally ranked rules loses to the later ones, so this one is selected only where the
            // logger factory would otherwise fall back to the minimum level, which carries no filter.
            rules.Insert(0, new LoggerFilterRule(null, null, options.MinLevel, RedactionOnly));
        }

        private static int Levels(params LogLevel[] levels)
        {
            int mask = 0;
            foreach (LogLevel level in levels)
            {
                mask |= 1 << (int)level;
            }

            return mask;
        }

        private static bool IsRedacted(string? provider, string? category, LogLevel level)
        {
            if (category is null
                || level is < LogLevel.Trace or > LogLevel.Critical
                || string.Equals(provider, BridgeProviderName, StringComparison.Ordinal))
            {
                return false;
            }

            return RedactedLevels.TryGetValue(category, out int mask) && (mask & (1 << (int)level)) != 0;
        }

        // Keeps the rule's own filter, so configuration still decides what the redaction does not withhold.
        private static Func<string?, string?, LogLevel, bool> Redact(Func<string?, string?, LogLevel, bool>? filter)
        {
            if (filter is null)
            {
                return RedactionOnly;
            }

            return (provider, category, level) =>
                !IsRedacted(provider, category, level) && filter(provider, category, level);
        }
    }
}