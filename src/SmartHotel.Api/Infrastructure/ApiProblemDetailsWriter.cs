using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Options;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Writes the ProblemDetails bodies that no MVC object result produces, namely the bodyless 401, 403,
/// 404 and 405 responses handed on by status-code pages, the 500 response from the exception
/// handler and the client errors that handler answers, as <c>application/problem+json</c> whatever the
/// request's <c>Accept</c> header admits. Through <see cref="ServerRejections"/>, the same body also answers
/// the requests the server rejects before the application pipeline runs.
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

    /// <summary>
    /// The connection middleware that gives a request the server rejects before the application pipeline runs, such
    /// as one with an over-long request line or oversized headers, the API's problem response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kestrel answers such a request itself, with a status line and the header fields <c>Content-Length: 0</c>,
    /// <c>Connection: close</c>, <c>Date</c> and <c>Server</c>, written in one flush, and it offers no hook for the
    /// body. Before it writes that response, it reports the rejection as the diagnostic event
    /// <c>Microsoft.AspNetCore.Server.Kestrel.BadRequest</c> on the host's <see cref="DiagnosticListener"/>, whose
    /// payload is the rejected request's features: the method the server parsed, if it parsed the request line, and
    /// the server's trace identifier for the request. The middleware subscribes to that one event and wraps the
    /// connection's transport. Each report arms the connection's writer, which holds back what the server writes next
    /// until the server flushes it. A recognized rejection is then replaced by the same status line, a
    /// <c>Content-Type</c> and a <c>Content-Length</c> for the problem body, the other original header fields in their
    /// order, and the body that <see cref="IProblemDetailsService"/> writes for that status and trace identifier,
    /// exactly as for any other error. The body is left out when the rejected request is a <c>HEAD</c> request.
    /// Anything else is sent exactly as written.
    /// </para>
    /// <para>
    /// A recognized rejection has an HTTP/1.x status line with a status from 400 to 599, exactly one
    /// <c>Content-Length</c> field, equal to 0, no <c>Content-Type</c> or <c>Transfer-Encoding</c> field, and nothing
    /// after the blank line that ends its header fields. Only HTTP/1.x is inspected: the accepted transport when the
    /// endpoint speaks HTTP/1.1 and no TLS layer runs over it, and the transport a TLS layer decrypts unless the client
    /// negotiated HTTP/2. When the server rejects the request line itself, it has parsed no method, and the request is
    /// a <c>HEAD</c> request when the most recent read of the inspected transport begins, after any CR and LF bytes,
    /// with <c>HEAD</c> and a space: the server reads a request line from its start until the line is complete or over
    /// its limit. The bytes received are read only for that test; they are never logged or written to a response.
    /// Nothing of this runs in the application's request pipeline.
    /// </para>
    /// </remarks>
    internal static class ServerRejections
    {
        private const string BadRequestEventName = "Microsoft.AspNetCore.Server.Kestrel.BadRequest";

        // The host's listener, which every endpoint of the host shares, mapped to the subscription made on it.
        private static readonly ConditionalWeakTable<DiagnosticListener, IDisposable> Subscriptions = new();
        private static readonly object SubscriptionGate = new();

        /// <summary>
        /// Builds the connection middleware of one endpoint, before the endpoint accepts a connection: subscribes, once
        /// per host, to the server's report of each request it rejects, and returns the middleware, which runs the rest
        /// of the endpoint's connection pipeline over a wrapper of each accepted connection whose transport rewrites
        /// those rejections.
        /// </summary>
        /// <param name="next">The rest of the endpoint's connection pipeline.</param>
        /// <param name="listenOptions">
        /// The endpoint: its protocols decide whether the accepted transport is inspected, and its application services
        /// hold the host's listener and write the problem.
        /// </param>
        /// <returns>The endpoint's connection middleware.</returns>
        public static ConnectionDelegate Create(ConnectionDelegate next, ListenOptions listenOptions)
        {
            ArgumentNullException.ThrowIfNull(next);
            ArgumentNullException.ThrowIfNull(listenOptions);

            IServiceProvider services = listenOptions.ApplicationServices;
            ILogger logger = services.GetRequiredService<ILogger<ApiExceptionHandler>>();
            RejectionResponder responder = new(services, services.GetRequiredService<IProblemDetailsService>(), logger);
            Subscribe(services.GetRequiredService<DiagnosticListener>(), logger);

            return connection => OnConnectionAsync(connection, next, listenOptions, responder);
        }

        // Gives the accepted connection its shared state and runs the rest of the pipeline over the wrapper.
        private static Task OnConnectionAsync(
            ConnectionContext connection,
            ConnectionDelegate next,
            ListenOptions listenOptions,
            RejectionResponder responder)
        {
            // Request features fall back to the connection's, so the server's report, whose payload is the rejected
            // request's features, finds the state here.
            ConnectionState state = new();
            connection.Features.Set(state);

            // Read per connection, not when the endpoint defaults are applied: an endpoint's own Protocols setting in
            // configuration is applied after the defaults and decides.
            bool speaksHttp1 = listenOptions.Protocols.HasFlag(HttpProtocols.Http1);
            return next(new RejectionAwareConnection(connection, state, responder, speaksHttp1));
        }

        // Subscribes once per listener: a second subscription would receive every report twice, and an endpoint that
        // Kestrel binds again when its configuration reloads finds the first one in place. The predicate enables this
        // one event alone, so every other event the host checks for, its request activity included, stays disabled for
        // this subscriber. The host disposes its listener when it is disposed, which completes the subscription.
        private static void Subscribe(DiagnosticListener listener, ILogger logger)
        {
            lock (SubscriptionGate)
            {
                if (!Subscriptions.TryGetValue(listener, out _))
                {
                    Subscriptions.Add(
                        listener,
                        listener.Subscribe(new RejectionObserver(listener, logger), IsBadRequestEvent));
                }
            }
        }

        // The subscription's predicate, which the host's components ask through the listener before writing an event.
        private static bool IsBadRequestEvent(string name)
        {
            return name == BadRequestEventName;
        }

        // Drops a listener whose subscription has ended, so that a listener is never mapped to a finished subscription.
        private static void Forget(DiagnosticListener listener)
        {
            lock (SubscriptionGate)
            {
                Subscriptions.Remove(listener);
            }
        }

        /// <summary>
        /// Receives the server's report of each request it rejects, before the server writes the response, and arms
        /// the rejected request's connection with the request's method and trace identifier.
        /// </summary>
        /// <remarks>
        /// The report runs inside the server's rejection, which must go on whatever happens here, so nothing escapes:
        /// a failure is logged as the sanitized event, and the rejection is then sent as the server writes it. Only the
        /// connection state, the method and the trace identifier are read from the payload.
        /// </remarks>
        /// <param name="listener">The host's listener this observer is subscribed to.</param>
        /// <param name="logger">The logger for a failure to read a report.</param>
        private sealed class RejectionObserver(DiagnosticListener listener, ILogger logger)
            : IObserver<KeyValuePair<string, object?>>
        {
            /// <summary>
            /// Forgets the subscription once the host has disposed its listener.
            /// </summary>
            public void OnCompleted()
            {
                Forget(listener);
            }

            /// <summary>
            /// Forgets the subscription, which a failing listener ends this way.
            /// </summary>
            /// <param name="error">The listener's failure, which is not inspected.</param>
            public void OnError(Exception error)
            {
                Forget(listener);
            }

            /// <summary>
            /// Arms the connection of the request a rejection report concerns. A report whose payload carries no
            /// connection state, because it is not one of the server's request feature collections or because its
            /// connection is not wrapped, such as an HTTP/3 one, arms nothing.
            /// </summary>
            /// <param name="value">The event's name and its payload.</param>
            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Key != BadRequestEventName || value.Value is not IFeatureCollection features)
                {
                    return;
                }

                string? traceIdentifier = null;
                try
                {
                    if (features.Get<ConnectionState>() is not ConnectionState state)
                    {
                        return;
                    }

                    traceIdentifier = features.Get<IHttpRequestIdentifierFeature>()?.TraceIdentifier;
                    if (traceIdentifier is not null)
                    {
                        // The method is empty when the server rejected the request line before it parsed one.
                        state.OnRejected(features.Get<IHttpRequestFeature>()?.Method, traceIdentifier);
                    }
                }
                catch (Exception exception)
                {
                    // Without the server's identifier, the event gets one of its own, in the same format.
                    ApiExceptionHandler.LogOutsideRequest(
                        logger,
                        exception,
                        traceIdentifier ?? new HttpRequestIdentifierFeature().TraceIdentifier);
                }
            }
        }
    }

    /// <summary>
    /// What one connection's transport wrappers and the server's rejection reports share: whether the most recent read
    /// of the inspected transport began with a <c>HEAD</c> request line, and the rejection the server has reported but
    /// whose response the writer has not yet taken.
    /// </summary>
    private sealed class ConnectionState
    {
        private int _lastReadBeginsWithHead;
        private PendingRejection? _pendingRejection;

        /// <summary>
        /// Records whether <paramref name="buffer"/> begins with <c>HEAD</c> and a space after any CR and LF bytes,
        /// which the server skips before a request line.
        /// </summary>
        /// <param name="buffer">The unconsumed bytes a read of the inspected transport returned.</param>
        public void ObserveRead(in ReadOnlySequence<byte> buffer)
        {
            SequenceReader<byte> reader = new(buffer);
            reader.AdvancePastAny((byte)'\r', (byte)'\n');
            Volatile.Write(ref _lastReadBeginsWithHead, reader.IsNext("HEAD "u8) ? 1 : 0);
        }

        /// <summary>
        /// Records the server's report that it rejects a request, made before it writes the response.
        /// </summary>
        /// <param name="method">
        /// The method the server parsed for the rejected request, or empty when it rejected the request line itself.
        /// </param>
        /// <param name="traceIdentifier">The server's identifier for the rejected request.</param>
        public void OnRejected(string? method, string traceIdentifier)
        {
            // Without a parsed method, the rejected request line begins the most recent read: the server consumes
            // nothing of a request line, beyond the CR and LF bytes before it, until it has parsed the whole line.
            // Method tokens are case-sensitive, as the server parses them, so only HEAD itself omits the body.
            bool isHead = string.IsNullOrEmpty(method)
                ? Volatile.Read(ref _lastReadBeginsWithHead) == 1
                : string.Equals(method, HttpMethods.Head, StringComparison.Ordinal);
            Volatile.Write(ref _pendingRejection, new PendingRejection(isHead, traceIdentifier));
        }

        /// <summary>
        /// Takes the reported rejection whose response the server writes next, so that each report is taken once.
        /// </summary>
        /// <returns>The reported rejection, or <see langword="null"/> when none is waiting.</returns>
        public PendingRejection? TakeRejection()
        {
            return Volatile.Read(ref _pendingRejection) is null
                ? null
                : Interlocked.Exchange(ref _pendingRejection, null);
        }
    }

    /// <summary>
    /// A rejection the server has reported on a connection and answers next.
    /// </summary>
    /// <param name="IsHead">Whether the rejected request is a <c>HEAD</c> request, whose response has no body.</param>
    /// <param name="TraceIdentifier">The server's identifier for the rejected request.</param>
    private sealed record PendingRejection(bool IsHead, string TraceIdentifier);

    /// <summary>
    /// The accepted connection as the rest of the pipeline sees it: every member is the accepted connection's, except
    /// that its transport, and any transport a TLS layer assigns, is wrapped. The server keeps owning and disposing
    /// the accepted connection.
    /// </summary>
    private sealed class RejectionAwareConnection : ConnectionContext
    {
        private readonly ConnectionContext _inner;
        private readonly ConnectionState _state;
        private readonly RejectionResponder _responder;
        private readonly bool _speaksHttp1;
        private IDuplexPipe _transport;

        /// <summary>
        /// Wraps <paramref name="inner"/> and its accepted transport.
        /// </summary>
        /// <param name="inner">The accepted connection.</param>
        /// <param name="state">The connection's shared state.</param>
        /// <param name="responder">The producer of the problem responses.</param>
        /// <param name="speaksHttp1">Whether the endpoint accepts HTTP/1.1.</param>
        public RejectionAwareConnection(
            ConnectionContext inner,
            ConnectionState state,
            RejectionResponder responder,
            bool speaksHttp1)
        {
            _inner = inner;
            _state = state;
            _responder = responder;
            _speaksHttp1 = speaksHttp1;
            _transport = new RejectionAwareTransport(inner.Transport, state, responder, CarriesPlaintextHttp1);
        }

        /// <summary>
        /// The wrapped transport. A TLS layer assigns its decrypted transport, which is wrapped in turn, and restores
        /// the wrapper it was given when it ends, which is kept as it is.
        /// </summary>
        public override IDuplexPipe Transport
        {
            get => _transport;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                _transport = value is RejectionAwareTransport
                    ? value
                    : new RejectionAwareTransport(value, _state, _responder, CarriesDecryptedHttp1);
            }
        }

        /// <inheritdoc />
        public override string ConnectionId
        {
            get => _inner.ConnectionId;
            set => _inner.ConnectionId = value;
        }

        /// <inheritdoc />
        public override IFeatureCollection Features => _inner.Features;

        /// <inheritdoc />
        public override IDictionary<object, object?> Items
        {
            get => _inner.Items;
            set => _inner.Items = value;
        }

        /// <inheritdoc />
        public override CancellationToken ConnectionClosed
        {
            get => _inner.ConnectionClosed;
            set => _inner.ConnectionClosed = value;
        }

        /// <inheritdoc />
        public override EndPoint? LocalEndPoint
        {
            get => _inner.LocalEndPoint;
            set => _inner.LocalEndPoint = value;
        }

        /// <inheritdoc />
        public override EndPoint? RemoteEndPoint
        {
            get => _inner.RemoteEndPoint;
            set => _inner.RemoteEndPoint = value;
        }

        /// <inheritdoc />
        public override void Abort()
        {
            _inner.Abort();
        }

        /// <inheritdoc />
        public override void Abort(ConnectionAbortedException abortReason)
        {
            _inner.Abort(abortReason);
        }

        // The TLS middleware sets its connection feature before its handshake reads anything, so at the accepted
        // transport's first read or write the feature tells whether that transport carries TLS records instead.
        private bool CarriesPlaintextHttp1()
        {
            return _speaksHttp1 && _inner.Features.Get<ITlsConnectionFeature>() is null;
        }

        // The application protocol is settled when the handshake completes, before the TLS layer hands over its
        // decrypted transport. An HTTP/2 rejection is a HEADERS frame, which this rewriting does not apply to.
        private bool CarriesDecryptedHttp1()
        {
            ITlsApplicationProtocolFeature? negotiated = _inner.Features.Get<ITlsApplicationProtocolFeature>();
            return _speaksHttp1 && (negotiated is null || !negotiated.ApplicationProtocol.Span.SequenceEqual("h2"u8));
        }
    }

    /// <summary>
    /// One layer of a connection's transport, with a reader that records how each read begins and a writer that
    /// rewrites the server's rejections.
    /// </summary>
    private sealed class RejectionAwareTransport : IDuplexPipe
    {
        /// <summary>
        /// Wraps both directions of <paramref name="inner"/>.
        /// </summary>
        /// <param name="inner">The layer's own transport.</param>
        /// <param name="state">The connection's shared state.</param>
        /// <param name="responder">The producer of the problem responses.</param>
        /// <param name="carriesHttp1">
        /// Decides, at the layer's first read or write, whether the layer carries HTTP/1.x and is inspected at all.
        /// </param>
        public RejectionAwareTransport(
            IDuplexPipe inner,
            ConnectionState state,
            RejectionResponder responder,
            Func<bool> carriesHttp1)
        {
            Lazy<bool> inspected = new(carriesHttp1);
            Input = new RequestStartObserver(inner.Input, state, inspected);
            Output = new RejectionRewritingWriter(inner.Output, state, responder, inspected);
        }

        /// <inheritdoc />
        public PipeReader Input { get; }

        /// <inheritdoc />
        public PipeWriter Output { get; }
    }

    /// <summary>
    /// Passes every read through unchanged and, on a layer that carries HTTP/1.x, shows the connection state the bytes
    /// each read returns.
    /// </summary>
    /// <param name="inner">The layer's reader.</param>
    /// <param name="state">The connection's shared state.</param>
    /// <param name="inspected">Whether the layer carries HTTP/1.x.</param>
    private sealed class RequestStartObserver(PipeReader inner, ConnectionState state, Lazy<bool> inspected)
        : PipeReader
    {
        /// <inheritdoc />
        public override void AdvanceTo(SequencePosition consumed)
        {
            inner.AdvanceTo(consumed);
        }

        /// <inheritdoc />
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            inner.AdvanceTo(consumed, examined);
        }

        /// <inheritdoc />
        public override void CancelPendingRead()
        {
            inner.CancelPendingRead();
        }

        /// <inheritdoc />
        public override void Complete(Exception? exception = null)
        {
            inner.Complete(exception);
        }

        /// <inheritdoc />
        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            return inner.CompleteAsync(exception);
        }

        /// <inheritdoc />
        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadResult result = await inner.ReadAsync(cancellationToken);
            Observe(result);
            return result;
        }

        /// <inheritdoc />
        public override bool TryRead(out ReadResult result)
        {
            bool read = inner.TryRead(out result);
            if (read)
            {
                Observe(result);
            }

            return read;
        }

        private void Observe(in ReadResult result)
        {
            if (inspected.Value)
            {
                state.ObserveRead(result.Buffer);
            }
        }
    }

    /// <summary>
    /// Passes every write through unchanged until the server reports a rejection on the connection. On a layer that
    /// carries HTTP/1.x, the first write after the report starts holding back what the server writes, up to 8 KB, and
    /// the next flush sends either the rewritten rejection or the held-back bytes exactly as written, after which
    /// writes pass through again.
    /// </summary>
    /// <remarks>
    /// The server reports a rejection before it writes the response and closes the connection after it, so each report
    /// arms the writer for one response. A request the server rejects while the application reads its body, such as
    /// one over the body size limit, is answered by the application's own problem, which carries a
    /// <c>Content-Type</c> and so passes unchanged. Memory handed out by <see cref="GetMemory"/> or
    /// <see cref="GetSpan"/> belongs to the destination the next <see cref="Advance"/> writes to.
    /// </remarks>
    /// <param name="inner">The layer's writer.</param>
    /// <param name="state">The connection's shared state.</param>
    /// <param name="responder">The producer of the problem responses.</param>
    /// <param name="inspected">Whether the layer carries HTTP/1.x.</param>
    private sealed class RejectionRewritingWriter(
        PipeWriter inner,
        ConnectionState state,
        RejectionResponder responder,
        Lazy<bool> inspected) : PipeWriter
    {
        private const int MaxHeldBytes = 8 * 1024;
        private const int InitialHeldCapacity = 256;

        // Both are set while a write is held back, and cleared together.
        private ArrayBufferWriter<byte>? _held;
        private PendingRejection? _rejection;

        /// <inheritdoc />
        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;

        /// <summary>
        /// The unflushed bytes of the layer's writer and the bytes held back.
        /// </summary>
        public override long UnflushedBytes => inner.UnflushedBytes + (_held?.WrittenCount ?? 0);

        /// <inheritdoc />
        public override void Advance(int bytes)
        {
            if (_held is null)
            {
                inner.Advance(bytes);
                return;
            }

            _held.Advance(bytes);
            if (_held.WrittenCount > MaxHeldBytes)
            {
                Release();
            }
        }

        /// <inheritdoc />
        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            return Hold(sizeHint) ? _held!.GetMemory(sizeHint) : inner.GetMemory(sizeHint);
        }

        /// <inheritdoc />
        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            return Hold(sizeHint) ? _held!.GetSpan(sizeHint) : inner.GetSpan(sizeHint);
        }

        /// <inheritdoc />
        public override void CancelPendingFlush()
        {
            inner.CancelPendingFlush();
        }

        /// <summary>
        /// Sends the held-back bytes exactly as written, then completes the layer's writer.
        /// </summary>
        /// <param name="exception">The error that ends the writing, if any.</param>
        public override void Complete(Exception? exception = null)
        {
            Release();
            inner.Complete(exception);
        }

        /// <summary>
        /// Sends the held-back bytes exactly as written, then completes the layer's writer.
        /// </summary>
        /// <param name="exception">The error that ends the writing, if any.</param>
        /// <returns>A task that completes once the layer's writer is complete.</returns>
        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            Release();
            return inner.CompleteAsync(exception);
        }

        /// <summary>
        /// Flushes the layer's writer, first replacing held-back bytes that form a rejection with the problem
        /// response, or else sending them as written.
        /// </summary>
        /// <param name="cancellationToken">Cancels the flush of the layer's writer.</param>
        /// <returns>The result of the layer's flush.</returns>
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            // A flush with nothing held back yet ends nothing: the server has not written a response.
            return _held is not { WrittenCount: > 0 }
                ? inner.FlushAsync(cancellationToken)
                : RewriteAndFlushAsync(cancellationToken);
        }

        /// <summary>
        /// Writes and flushes <paramref name="source"/>, held back and rewritten like any other write.
        /// </summary>
        /// <param name="source">The bytes to write.</param>
        /// <param name="cancellationToken">Cancels the flush of the layer's writer.</param>
        /// <returns>The result of the layer's flush.</returns>
        public override ValueTask<FlushResult> WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default)
        {
            if (!Hold(source.Length))
            {
                return inner.WriteAsync(source, cancellationToken);
            }

            _held!.Write(source.Span);
            return RewriteAndFlushAsync(cancellationToken);
        }

        // Starts holding back writes when the layer carries HTTP/1.x and takes a rejection the server has reported on
        // the connection, and reports whether the next write is held back. A layer that is not inspected never takes
        // a report, which leaves it to the layer that carries the server's writes. A write that would take the
        // held-back bytes past the cap cannot belong to a rejection, so everything held passes on unchanged.
        private bool Hold(int sizeHint)
        {
            if (_held is null)
            {
                if (!inspected.Value || state.TakeRejection() is not PendingRejection rejection)
                {
                    return false;
                }

                _held = new ArrayBufferWriter<byte>(InitialHeldCapacity);
                _rejection = rejection;
            }

            if (_held.WrittenCount + Math.Max(sizeHint, 0) > MaxHeldBytes)
            {
                Release();
                return false;
            }

            return true;
        }

        // Sends the held-back bytes exactly as written; later writes pass through until the next report.
        private void Release()
        {
            ArrayBufferWriter<byte>? held = _held;
            _held = null;
            _rejection = null;
            if (held is { WrittenCount: > 0 })
            {
                inner.Write(held.WrittenSpan);
            }
        }

        private async ValueTask<FlushResult> RewriteAndFlushAsync(CancellationToken cancellationToken)
        {
            ArrayBufferWriter<byte> held = _held!;
            PendingRejection rejection = _rejection!;
            _held = null;
            _rejection = null;

            ReadOnlyMemory<byte>? rewritten = await responder.TryRewriteAsync(held.WrittenMemory, rejection);
            inner.Write((rewritten ?? held.WrittenMemory).Span);
            return await inner.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Recognizes the server's rejections and composes their problem responses.
    /// </summary>
    /// <param name="services">The application services the stand-in request carries.</param>
    /// <param name="problemDetailsService">The service that writes the problem body.</param>
    /// <param name="logger">The logger for a failure to produce the problem.</param>
    private sealed class RejectionResponder(
        IServiceProvider services,
        IProblemDetailsService problemDetailsService,
        ILogger logger)
    {
        private const int StatusLinePrefixLength = 9;
        private const int StatusCodeLength = 3;

        private static ReadOnlySpan<byte> LineEnd => "\r\n"u8;

        private static ReadOnlySpan<byte> HeadEnd => "\r\n\r\n"u8;

        /// <summary>
        /// Returns the problem response that replaces <paramref name="written"/> when it is a rejection.
        /// </summary>
        /// <remarks>
        /// When producing the problem fails, the failure is logged as the sanitized event and the rejection is sent
        /// as the server wrote it, so the client still receives its status.
        /// </remarks>
        /// <param name="written">
        /// Everything the server wrote, up to its next flush, after it reported the rejection.
        /// </param>
        /// <param name="rejection">
        /// The reported rejection: whether it answers a <c>HEAD</c> request, and the server's trace identifier.
        /// </param>
        /// <returns>
        /// The replacement response, or <see langword="null"/> when <paramref name="written"/> is to be sent unchanged.
        /// </returns>
        public async ValueTask<ReadOnlyMemory<byte>?> TryRewriteAsync(
            ReadOnlyMemory<byte> written,
            PendingRejection rejection)
        {
            List<Range> keptHeaders = [];
            if (!TryParseRejection(written.Span, keptHeaders, out int status, out int statusLineLength))
            {
                return null;
            }

            string traceIdentifier = rejection.TraceIdentifier;
            try
            {
                Problem? problem = await WriteProblemAsync(status, traceIdentifier);
                return problem is null
                    ? null
                    : Compose(written.Span, statusLineLength, keptHeaders, problem, rejection.IsHead);
            }
            catch (Exception exception)
            {
                ApiExceptionHandler.LogOutsideRequest(logger, exception, traceIdentifier);
                return null;
            }
        }

        // The body is exactly what the API writes for any other error with this status: a stand-in request carries the
        // status and the server's trace identifier through IProblemDetailsService, and its body is collected in memory.
        private async ValueTask<Problem?> WriteProblemAsync(int status, string traceIdentifier)
        {
            using MemoryStream body = new();
            DefaultHttpContext standIn = new()
            {
                RequestServices = services,
                TraceIdentifier = traceIdentifier,
            };
            standIn.Response.StatusCode = status;
            standIn.Response.Body = body;

            await problemDetailsService.WriteAsync(new ProblemDetailsContext { HttpContext = standIn });

            // Completing the response flushes what the writer buffered into the stream. The stream's bytes are taken
            // with ToArray, which also works on a stream the completion has closed.
            await standIn.Response.CompleteAsync();
            byte[] bytes = body.ToArray();

            // A header value outside printable ASCII could break the header block, so such a body is not sent.
            string? contentType = standIn.Response.ContentType;
            if (bytes.Length == 0 || string.IsNullOrEmpty(contentType) || !IsPrintableAscii(contentType))
            {
                return null;
            }

            return new Problem(contentType, bytes);
        }

        private static bool IsPrintableAscii(string value)
        {
            foreach (char character in value)
            {
                if (character is < ' ' or > '~')
                {
                    return false;
                }
            }

            return true;
        }

        // The whole of what was written must be one response head with nothing after its blank line: the status line,
        // then header fields, one of them Content-Length: 0. The ranges of the fields to keep are collected in order.
        private static bool TryParseRejection(
            ReadOnlySpan<byte> written,
            List<Range> keptHeaders,
            out int status,
            out int statusLineLength)
        {
            status = 0;
            statusLineLength = written.IndexOf(LineEnd);
            int headLength = written.IndexOf(HeadEnd);
            if (headLength < 0
                || headLength + HeadEnd.Length != written.Length
                || statusLineLength >= headLength
                || !TryReadErrorStatus(written[..statusLineLength], out status))
            {
                return false;
            }

            int contentLengthFields = 0;
            int position = statusLineLength + LineEnd.Length;
            while (position < headLength + LineEnd.Length)
            {
                int lineLength = written[position..].IndexOf(LineEnd);
                ReadOnlySpan<byte> line = written.Slice(position, lineLength);
                int colon = line.IndexOf((byte)':');
                if (colon <= 0)
                {
                    return false;
                }

                ReadOnlySpan<byte> name = line[..colon];
                ReadOnlySpan<byte> value = line[(colon + 1)..];
                value = value[Ascii.Trim(value)];

                if (Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
                {
                    if (!value.SequenceEqual("0"u8))
                    {
                        return false;
                    }

                    contentLengthFields++;
                }
                else if (Ascii.EqualsIgnoreCase(name, "Content-Type"u8)
                    || Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8))
                {
                    return false;
                }
                else
                {
                    keptHeaders.Add(new Range(position, position + lineLength));
                }

                position += lineLength + LineEnd.Length;
            }

            return contentLengthFields == 1;
        }

        // "HTTP/1.1 " or "HTTP/1.0 ", three digits for a status from 400 to 599, then the end of the line or a space
        // and the reason phrase.
        private static bool TryReadErrorStatus(ReadOnlySpan<byte> statusLine, out int status)
        {
            status = 0;
            int statusEnd = StatusLinePrefixLength + StatusCodeLength;
            if (statusLine.Length < statusEnd
                || !(statusLine.StartsWith("HTTP/1.1 "u8) || statusLine.StartsWith("HTTP/1.0 "u8))
                || (statusLine.Length > statusEnd && statusLine[statusEnd] != (byte)' '))
            {
                return false;
            }

            foreach (byte digit in statusLine.Slice(StatusLinePrefixLength, StatusCodeLength))
            {
                if (digit is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }

                status = (status * 10) + (digit - '0');
            }

            return status is >= 400 and <= 599;
        }

        // The status line as written, then Content-Type and Content-Length for the problem, the kept fields in their
        // order, the blank line and, unless the rejected request is a HEAD request, the body.
        private static ReadOnlyMemory<byte> Compose(
            ReadOnlySpan<byte> written,
            int statusLineLength,
            List<Range> keptHeaders,
            Problem problem,
            bool omitBody)
        {
            ArrayBufferWriter<byte> response = new(written.Length + problem.ContentType.Length + problem.Body.Length + 64);
            response.Write(written[..statusLineLength]);
            response.Write(LineEnd);
            response.Write("Content-Type: "u8);
            WriteAscii(response, problem.ContentType);
            response.Write(LineEnd);
            response.Write("Content-Length: "u8);
            WriteAscii(response, problem.Body.Length.ToString(CultureInfo.InvariantCulture));
            response.Write(LineEnd);

            foreach (Range header in keptHeaders)
            {
                response.Write(written[header]);
                response.Write(LineEnd);
            }

            response.Write(LineEnd);
            if (!omitBody)
            {
                response.Write(problem.Body);
            }

            return response.WrittenMemory;
        }

        private static void WriteAscii(ArrayBufferWriter<byte> response, string text)
        {
            int written = Encoding.ASCII.GetBytes(text, response.GetSpan(text.Length));
            response.Advance(written);
        }

        /// <summary>
        /// A problem body and its media type.
        /// </summary>
        /// <param name="ContentType">The <c>Content-Type</c> the problem writer set.</param>
        /// <param name="Body">The serialized problem.</param>
        private sealed record Problem(string ContentType, byte[] Body);
    }
}