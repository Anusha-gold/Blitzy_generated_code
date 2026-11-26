using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;

using Npgsql;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Logs every unhandled exception as one sanitized Error event and leaves the response to the exception
/// handler middleware, which writes a generic 500 problem through <see cref="IProblemDetailsService"/>.
/// </summary>
/// <remarks>
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
/// </remarks>
public sealed partial class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    private const int MaxInspections = 65_536;
    private const int MaxListedExceptions = 256;
    private const int MaxStackFrames = 200;
    private const string UnmatchedRoute = "unmatched";

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

        // The middleware clears the request's endpoint and route values before it calls handlers and keeps the
        // matched endpoint on the feature. The raw route template holds no request values.
        var endpoint = httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint as RouteEndpoint;
        var routeTemplate = endpoint?.RoutePattern.RawText ?? UnmatchedRoute;

        var (chain, typesTruncated) = CollectExceptionChain(exception);
        var exceptionTypes = DescribeExceptionTypes(chain);
        var postgresException = chain.OfType<PostgresException>().FirstOrDefault();
        var (stackFrames, framesTruncated) = DescribeStackFrames(chain);

        // The exception is never passed as the logger's exception argument, because every provider would then
        // print its message and formatted stack trace. Only SqlState and ConstraintName are read from a
        // PostgresException: its message, detail, hint and query text can contain key values.
        LogUnhandledException(
            logger,
            ProblemDetailsSetup.ResolveTraceId(httpContext),
            httpContext.Request.Method,
            routeTemplate,
            exceptionTypes,
            typesTruncated,
            postgresException?.SqlState,
            postgresException?.ConstraintName,
            stackFrames,
            framesTruncated);

        return ValueTask.FromResult(false);
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
}