using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;

using Npgsql;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Logs every unhandled exception as one sanitized Error event and leaves the response to the exception
/// handler middleware, which writes a generic 500 problem through <see cref="IProblemDetailsService"/>.
/// </summary>
/// <remarks>
/// The event carries only the trace identifier, the request method, the matched route template, the type
/// names of the exception and of every inner exception, their stack frames reduced to type and method names
/// and, for a <see cref="PostgresException"/>, its <see cref="PostgresException.SqlState"/> and
/// <see cref="PostgresException.ConstraintName"/>. Exception messages, <see cref="Exception.Data"/>, the
/// formatted stack trace, the path, route and query values, headers, the body, claims and connection details
/// are never logged, because any of them can carry personal data or secrets.
/// </remarks>
public sealed partial class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    private const int MaxExceptions = 32;
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

        var chain = CollectExceptionChain(exception);
        var postgresException = chain.OfType<PostgresException>().FirstOrDefault();

        // The exception is never passed as the logger's exception argument, because every provider would then
        // print its message and formatted stack trace. Only SqlState and ConstraintName are read from a
        // PostgresException: its message, detail, hint and query text can contain key values.
        LogUnhandledException(
            logger,
            ProblemDetailsSetup.ResolveTraceId(httpContext),
            httpContext.Request.Method,
            routeTemplate,
            chain.Select(static ex => ex.GetType().FullName ?? ex.GetType().Name).ToArray(),
            postgresException?.SqlState,
            postgresException?.ConstraintName,
            DescribeStackFrames(chain));

        return ValueTask.FromResult(false);
    }

    // Breadth-first over InnerException and every AggregateException item. The reference-equality set stops
    // cycles and repeats, such as an AggregateException's InnerException, which is also its first item, and the
    // cap bounds the work for pathological chains.
    private static List<Exception> CollectExceptionChain(Exception root)
    {
        var chain = new List<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance) { root };
        var pending = new Queue<Exception>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            chain.Add(current);

            if (current.InnerException is { } inner)
            {
                Enqueue(inner);
            }

            if (current is AggregateException aggregate)
            {
                foreach (var item in aggregate.InnerExceptions)
                {
                    Enqueue(item);
                }
            }
        }

        return chain;

        void Enqueue(Exception candidate)
        {
            if (visited.Count < MaxExceptions && visited.Add(candidate))
            {
                pending.Enqueue(candidate);
            }
        }
    }

    // Each frame becomes "<position in the chain>:<declaring type>.<method>". The frames come from the trace the
    // exception captured, not its formatted StackTrace string, and file information is not requested, so no
    // message text, file path or line number is read.
    private static string[] DescribeStackFrames(List<Exception> chain)
    {
        var frames = new List<string>();

        for (var index = 0; index < chain.Count; index++)
        {
            foreach (var frame in new StackTrace(chain[index], fNeedFileInfo: false).GetFrames())
            {
                if (frames.Count == MaxStackFrames)
                {
                    return frames.ToArray();
                }

                var method = frame.GetMethod();
                if (method is null)
                {
                    continue;
                }

                frames.Add($"{index}:{method.DeclaringType?.FullName ?? "?"}.{method.Name}");
            }
        }

        return frames.ToArray();
    }

    [LoggerMessage(
        EventId = 5000,
        EventName = "UnhandledException",
        Level = LogLevel.Error,
        Message = "Unhandled exception. TraceId={TraceId} Method={Method} Route={RouteTemplate} "
            + "Types={ExceptionTypes} SqlState={SqlState} Constraint={ConstraintName} Frames={StackFrames}")]
    private static partial void LogUnhandledException(
        ILogger logger,
        string traceId,
        string method,
        string routeTemplate,
        string[] exceptionTypes,
        string? sqlState,
        string? constraintName,
        string[] stackFrames);
}