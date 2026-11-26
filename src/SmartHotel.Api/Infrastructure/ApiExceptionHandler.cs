using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Logs every unhandled exception as one sanitized Error event and leaves the response to the exception
/// handler middleware, which writes a generic 500 problem through <see cref="IProblemDetailsService"/>.
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
/// category, carry the raw exception and are filtered out by <c>appsettings.json</c>. A rule of the bridge's
/// own lets it alone receive Kestrel's records, and it forwards neither their exception nor their message, so
/// this event is the only one written.
/// </para>
/// </remarks>
/// <param name="logger">The logger that receives the sanitized event.</param>
public sealed partial class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    private const int MaxInspections = 65_536;
    private const int MaxListedExceptions = 256;
    private const int MaxStackFrames = 200;
    private const string UnmatchedRoute = "unmatched";
    private const string UnknownMethod = "unknown";

    // The Items entry under this key lists the exceptions already logged for the request.
    private static readonly object ReportedExceptionsKey = new();

    /// <summary>
    /// Writes the sanitized Error event for <paramref name="exception"/> and declines to handle it, so the
    /// middleware produces the 500 problem body, which holds no exception data.
    /// </summary>
    /// <param name="httpContext">The request whose processing threw.</param>
    /// <param name="exception">The unhandled exception.</param>
    /// <param name="cancellationToken">Not observed: the handler does no asynchronous work.</param>
    /// <returns>Always <see langword="false"/>, so the response is never written here.</returns>
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        // When writing the problem fails, the middleware rethrows this same exception and the server reports it;
        // the record kept for the request stops that report from logging it a second time.
        LogUnlessReported(logger, httpContext, exception, fallbackTraceId: null);

        return ValueTask.FromResult(false);
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
        var (chain, typesTruncated) = CollectExceptionChain(exception);
        var exceptionTypes = DescribeExceptionTypes(chain);
        var postgresException = chain.OfType<PostgresException>().FirstOrDefault();
        var (stackFrames, framesTruncated) = DescribeStackFrames(chain);

        // The exception is never passed as the logger's exception argument, because every provider would then
        // print its message and formatted stack trace. Only SqlState and ConstraintName are read from a
        // PostgresException: its message, detail, hint and query text can contain key values.
        LogUnhandledException(
            logger,
            traceId,
            method,
            routeTemplate,
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
    /// before the response started, and one thrown outside that middleware, such as by routing. By then the
    /// page has cleared the response and set its status, so the problem written here carries only the shared
    /// defaults. The rest of the filter chain is never invoked, because it ends in the page itself.
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
}