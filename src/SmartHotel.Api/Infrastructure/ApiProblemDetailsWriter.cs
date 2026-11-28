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
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Writes the ProblemDetails bodies that no MVC object result produces, namely the bodyless 401, 403,
/// 404 and 405 responses handed on by status-code pages, the 500 response from the exception
/// handler and the client errors that handler answers, as <c>application/problem+json</c> whatever the
/// request's <c>Accept</c> header admits. Through <see cref="ServerRejections"/>, the connection middleware gives the
/// same problem envelope to each recognized HTTP/1.x rejection the server writes before the application pipeline
/// runs, a bodyless status line from 400 to 599, and leaves out the body when the rejected request is a <c>HEAD</c>
/// request; any other response the server writes passes through unchanged. Every endpoint serves HTTP/1.1 only: a
/// Kestrel protocols setting for any other protocol stops the API from starting, and after a configuration reload keeps
/// Kestrel from binding the endpoints it starts or rebinds.
/// </summary>
/// <remarks>
/// ASP.NET Core's default writer declines a request whose <c>Accept</c> header excludes JSON, which
/// leaves that response with a plain-text status page. This writer is registered ahead of the framework
/// writers and, because it replaces their behaviour instead of extending it, applies
/// <see cref="ProblemDetailsOptions.CustomizeProblemDetails"/> itself, so both error paths share one
/// defaults routine.
/// </remarks>
public sealed partial class ApiProblemDetailsWriter : IProblemDetailsWriter
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
    /// The connection middleware that gives the API's problem response to each recognized rejection the server writes
    /// before the application pipeline runs, such as that of an over-long request line or oversized headers, with the
    /// guard that keeps every endpoint to HTTP/1.1 alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kestrel answers such a request itself, with a status line and the header fields <c>Content-Length: 0</c>,
    /// <c>Connection: close</c> and <c>Date</c>, written in one flush, and it offers no hook for the body. Before it
    /// writes that response, it reports the rejection as the diagnostic event
    /// <c>Microsoft.AspNetCore.Server.Kestrel.BadRequest</c> on the host's <see cref="DiagnosticListener"/>, whose
    /// payload is the rejected request's features: the method the server parsed, if it parsed the request line, and
    /// the server's trace identifier for the request. The middleware subscribes to that one event and wraps the
    /// connection's transport. Each report arms the connection's writer, which holds back what the server writes next
    /// until the server flushes it. A recognized rejection is then replaced by the same status line, a
    /// <c>Content-Type</c> and a <c>Content-Length</c> for the problem body, the header fields every API response
    /// carries (<see cref="ProblemDetailsSetup.ResponseSecurityHeaders"/>), the other original header fields in their
    /// order, and the body that <see cref="IProblemDetailsService"/> writes for that status and trace identifier,
    /// exactly as for any other error. The body is left out when the rejected request is a <c>HEAD</c> request.
    /// Anything else is sent exactly as written.
    /// </para>
    /// <para>
    /// A recognized rejection has an HTTP/1.x status line with a status from 400 to 599, exactly one
    /// <c>Content-Length</c> field, equal to 0, no <c>Content-Type</c> or <c>Transfer-Encoding</c> field, and nothing
    /// after the blank line that ends its header fields. Every endpoint serves HTTP/1.1 alone, so the transport that
    /// carries the server's writes carries HTTP/1.x, and that transport is the one inspected: the accepted transport
    /// when no TLS layer runs over it, and otherwise the transport the TLS layer decrypts. When the server rejects the
    /// request line itself, it has parsed no method, and the request is a <c>HEAD</c> request when the most recent read
    /// of the inspected transport begins, after any CR and LF bytes, with <c>HEAD</c> and a space: the server reads a
    /// request line from its start until the line is complete or over its limit. The bytes received are read only for
    /// that test; they are never logged or written to a response. Nothing of this runs in the application's request
    /// pipeline.
    /// </para>
    /// <para>
    /// Over HTTP/2 and HTTP/3 the server answers a rejection with a header frame of its own, which carries no body and
    /// which this middleware cannot extend, so every endpoint serves HTTP/1.1 alone. Kestrel's configuration sets
    /// protocols in three places: the endpoint defaults, each endpoint, and each SNI entry of an endpoint, whose
    /// protocols a TLS connection for that host name negotiates in place of the endpoint's, out of sight of the
    /// endpoint's pipelines. A value with none of the HTTP/1.1, HTTP/2 and HTTP/3 flags gives an endpoint no pipeline
    /// at all, so neither builder below runs for it. <see cref="ProtocolsSettingsGuard"/> therefore holds every such
    /// setting to <c>Http1</c>, read as Kestrel reads it, and reports each other value as one Critical event, 5204,
    /// that names the setting: when Kestrel creates its options, after which the host refuses to start, so no endpoint
    /// is bound, and again on each reload of Kestrel's configuration. While that configuration holds such a setting,
    /// Kestrel binds no endpoint that a reload starts or rebinds, one whose SNI entries changed included: the server
    /// builds an endpoint's connection pipelines, and with them this middleware, when it binds the endpoint, and
    /// <see cref="Create"/> and <see cref="CreateMultiplexed"/> refuse to build them, which leaves that endpoint
    /// unbound while the host's other endpoints keep serving. Kestrel counts an endpoint it failed to bind on reload as
    /// configured, so a refused endpoint stays unbound until a later reload changes its own settings or the API
    /// restarts. An endpoint whose value builds no pipeline serves nothing. The event is the operator's report, because
    /// the server's own report of an endpoint it could not bind on reload is withheld from every log provider. The two
    /// builders also refuse an endpoint whose own protocols are anything but HTTP/1.1 alone, as protocols set outside
    /// configuration can be, with one Critical event, 5203, that names the endpoint's address and its protocols; an
    /// endpoint that serves HTTP/3 alone builds only its multiplexed pipeline, where <see cref="CreateMultiplexed"/>
    /// refuses it.
    /// </para>
    /// </remarks>
    internal static partial class ServerRejections
    {
        private const string BadRequestEventName = "Microsoft.AspNetCore.Server.Kestrel.BadRequest";
        private const string UnsupportedProtocolsMessage =
            "A Kestrel endpoint is configured to serve protocols other than HTTP/1.1 alone, which the API does not "
            + "serve. Configure that endpoint for Http1 alone.";
        private const string UnsupportedProtocolsSettingsMessage =
            "The Kestrel configuration holds a protocols setting other than Http1, which the API does not serve. Event "
            + "5204 names each such setting: set it to Http1, or remove it.";

        // The keys Kestrel's configuration reader takes protocols from, relative to the root of Kestrel's
        // configuration, and the section and delimiter that turn a key into its environment-variable name.
        private const string ProtocolsKey = "Protocols";
        private const string EndpointDefaultsKey = "EndpointDefaults";
        private const string EndpointsKey = "Endpoints";
        private const string SniKey = "Sni";
        private const string KestrelSectionName = "Kestrel";
        private const string VariableDelimiter = "__";

        // The host's listener, which every endpoint of the host shares, mapped to the subscription made on it.
        private static readonly ConditionalWeakTable<DiagnosticListener, IDisposable> Subscriptions = new();
        private static readonly object SubscriptionGate = new();

        /// <summary>
        /// Builds the connection middleware of one endpoint when the server binds it, before the endpoint accepts a
        /// connection: refuses the endpoint while Kestrel's configuration holds a protocols setting other than
        /// <c>Http1</c> or when the endpoint's own protocols are anything but HTTP/1.1 alone, subscribes, once per
        /// host, to the server's report of each request it rejects, and returns the middleware, which runs the rest of
        /// the endpoint's connection pipeline over a wrapper of each accepted connection whose transport rewrites those
        /// rejections.
        /// </summary>
        /// <param name="next">The rest of the endpoint's connection pipeline.</param>
        /// <param name="listenOptions">
        /// The endpoint: its protocols must be HTTP/1.1 alone, its server options hold Kestrel's configuration, and its
        /// application services hold the loggers, the host's listener and the problem writer.
        /// </param>
        /// <returns>The endpoint's connection middleware.</returns>
        /// <exception cref="InvalidOperationException">
        /// Kestrel's configuration holds a protocols setting other than <c>Http1</c>, which
        /// <see cref="ProtocolsSettingsGuard"/> has reported as event 5204, so nothing more is logged; or the
        /// endpoint's protocols are not HTTP/1.1 alone, and the refusal has been logged as event 5203.
        /// </exception>
        public static ConnectionDelegate Create(ConnectionDelegate next, ListenOptions listenOptions)
        {
            ArgumentNullException.ThrowIfNull(next);
            ArgumentNullException.ThrowIfNull(listenOptions);

            RefuseUnlessHttp1Only(listenOptions);

            IServiceProvider services = listenOptions.ApplicationServices;
            ILogger logger = services.GetRequiredService<ILogger<ApiExceptionHandler>>();
            RejectionResponder responder = new(services, services.GetRequiredService<IProblemDetailsService>(), logger);
            Subscribe(services.GetRequiredService<DiagnosticListener>(), logger);

            return connection => OnConnectionAsync(connection, next, responder);
        }

        /// <summary>
        /// Builds the multiplexed connection middleware of one endpoint when the server binds it. The server builds
        /// that pipeline only for an endpoint that serves HTTP/3, and for one that serves HTTP/3 alone it builds no
        /// other, so this is where such an endpoint is refused.
        /// </summary>
        /// <param name="next">The rest of the endpoint's multiplexed connection pipeline.</param>
        /// <param name="listenOptions">
        /// The endpoint: its protocols must be HTTP/1.1 alone, and its server options hold Kestrel's configuration.
        /// </param>
        /// <returns><paramref name="next"/> unchanged, because no multiplexed connection is ever inspected.</returns>
        /// <exception cref="InvalidOperationException">
        /// Kestrel's configuration holds a protocols setting other than <c>Http1</c>, which
        /// <see cref="ProtocolsSettingsGuard"/> has reported as event 5204, so nothing more is logged; or the
        /// endpoint's protocols are not HTTP/1.1 alone, and the refusal has been logged as event 5203.
        /// </exception>
        public static MultiplexedConnectionDelegate CreateMultiplexed(
            MultiplexedConnectionDelegate next,
            ListenOptions listenOptions)
        {
            ArgumentNullException.ThrowIfNull(next);
            ArgumentNullException.ThrowIfNull(listenOptions);

            RefuseUnlessHttp1Only(listenOptions);
            return next;
        }

        // Kestrel builds an endpoint's pipelines each time it binds the endpoint, at startup and whenever a reload
        // starts or rebinds it, so its configuration is read as it stands then. The guard has reported each setting
        // found there, when Kestrel created its options or on the reload that brought it, so nothing is logged for it
        // here. The server reads the endpoint's own protocols when it builds the pipelines, so they are final here too.
        // Their event names an IP endpoint by its address and port, and any other endpoint by its type alone, never by
        // a path or a name from configuration; both exceptions' messages are fixed.
        private static void RefuseUnlessHttp1Only(ListenOptions listenOptions)
        {
            if (listenOptions.KestrelServerOptions?.ConfigurationLoader?.Configuration is IConfiguration kestrel
                && FindUnsupportedProtocolsSettings(kestrel).Count > 0)
            {
                throw new InvalidOperationException(UnsupportedProtocolsSettingsMessage);
            }

            HttpProtocols protocols = listenOptions.Protocols;
            if (protocols == HttpProtocols.Http1)
            {
                return;
            }

            string endpoint = listenOptions.EndPoint is IPEndPoint address
                ? address.ToString()
                : listenOptions.EndPoint.GetType().Name;
            LogUnsupportedEndpointProtocols(
                listenOptions.ApplicationServices.GetRequiredService<ILogger<ApiProblemDetailsWriter>>(),
                endpoint,
                protocols);
            throw new InvalidOperationException(UnsupportedProtocolsMessage);
        }

        // Gives the accepted connection its shared state and runs the rest of the pipeline over the wrapper.
        private static Task OnConnectionAsync(
            ConnectionContext connection,
            ConnectionDelegate next,
            RejectionResponder responder)
        {
            // Request features fall back to the connection's, so the server's report, whose payload is the rejected
            // request's features, finds the state here.
            ConnectionState state = new();
            connection.Features.Set(state);
            return next(new RejectionAwareConnection(connection, state, responder));
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

        [LoggerMessage(
            EventId = 5203,
            EventName = "UnsupportedEndpointProtocols",
            Level = LogLevel.Critical,
            Message = "The Kestrel endpoint {Endpoint} is configured for the protocols {Protocols}, so it is refused "
                + "and does not listen: the API serves HTTP/1.1 only, because the server answers the requests it "
                + "rejects over HTTP/2 and HTTP/3 without a problem body. Configure that endpoint for Http1 alone.")]
        private static partial void LogUnsupportedEndpointProtocols(
            ILogger logger,
            string endpoint,
            HttpProtocols protocols);

        /// <summary>
        /// Finds every protocols setting in Kestrel's configuration that selects anything but HTTP/1.1 alone: that of
        /// the endpoint defaults, of each endpoint and of each SNI entry of an endpoint. Each value is parsed as
        /// Kestrel's configuration reader parses it, and one that does not parse is passed over, because Kestrel
        /// ignores it and the endpoint keeps the API's HTTP/1.1 default. Only those values and the key names of the
        /// endpoints and their SNI entries are read, never a certificate setting or any other value.
        /// </summary>
        /// <param name="kestrel">
        /// The configuration Kestrel's configuration loader reads, whose keys are relative to the <c>Kestrel</c>
        /// section.
        /// </param>
        /// <returns>
        /// Each such setting, by its environment-variable name, such as
        /// <c>Kestrel__Endpoints__&lt;name&gt;__Sni__&lt;host&gt;__Protocols</c>, with the protocols it selects, in the
        /// order Kestrel reads them; empty when every setting is <c>Http1</c>, absent or unparsed.
        /// </returns>
        internal static List<(string Setting, HttpProtocols Protocols)> FindUnsupportedProtocolsSettings(
            IConfiguration kestrel)
        {
            List<(string Setting, HttpProtocols Protocols)> unsupported = [];
            AddIfUnsupported(unsupported, kestrel.GetSection(EndpointDefaultsKey));
            foreach (IConfigurationSection endpoint in kestrel.GetSection(EndpointsKey).GetChildren())
            {
                AddIfUnsupported(unsupported, endpoint);
                foreach (IConfigurationSection sniEntry in endpoint.GetSection(SniKey).GetChildren())
                {
                    AddIfUnsupported(unsupported, sniEntry);
                }
            }

            return unsupported;
        }

        // Kestrel parses a section's protocols with Enum.TryParse<HttpProtocols>(value, ignoreCase: true), so a name, a
        // comma-separated list of names and a number all select protocols, and HttpProtocols.Http1 is the one value
        // kept. A number with none of the protocol flags is reported too: the endpoint it sets would serve nothing.
        private static void AddIfUnsupported(
            List<(string Setting, HttpProtocols Protocols)> unsupported,
            IConfigurationSection section)
        {
            if (Enum.TryParse<HttpProtocols>(section[ProtocolsKey], ignoreCase: true, out HttpProtocols protocols)
                && protocols != HttpProtocols.Http1)
            {
                string setting = ConfigurationPath.Combine(KestrelSectionName, section.Path, ProtocolsKey)
                    .Replace(ConfigurationPath.KeyDelimiter, VariableDelimiter, StringComparison.Ordinal);
                unsupported.Add((setting, protocols));
            }
        }

        [LoggerMessage(
            EventId = 5204,
            EventName = "UnsupportedProtocolsSetting",
            Level = LogLevel.Critical,
            Message = "The Kestrel setting {Setting} selects the protocols {Protocols}, which the API does not serve: "
                + "the API serves HTTP/1.1 only, because the server answers the requests it rejects over HTTP/2 and "
                + "HTTP/3 without a problem body. While the setting is in place the API does not start, and Kestrel "
                + "binds no endpoint that a configuration reload starts or rebinds; an endpoint refused this way stays "
                + "unbound until its own settings change again or the API restarts. Set it to Http1, or remove it.")]
        private static partial void LogUnsupportedProtocolsSetting(
            ILogger logger,
            string setting,
            HttpProtocols protocols);

        /// <summary>
        /// Holds every protocols setting in Kestrel's configuration to <c>Http1</c>. When Kestrel creates its options,
        /// the guard reports each other value as event 5204 and starts reporting such values again on each reload of
        /// that configuration; when the host starts, the guard refuses the start while such a value is in place, before
        /// any hosted service starts the server, so Kestrel binds no endpoint. A host on another server, such as the
        /// in-process test server, never creates the Kestrel options, so there the guard reads nothing and never
        /// refuses.
        /// </summary>
        /// <remarks>
        /// Kestrel creates its options when the host resolves the server, which the host does while it is being built,
        /// before the entry point can catch a failure and report it. The guard therefore never fails the options
        /// themselves: it refuses the host's start, which fails the entry point's run of the host, and the entry point
        /// reports that failure and exits with code 1.
        /// </remarks>
        /// <param name="logger">The logger for event 5204.</param>
        internal sealed class ProtocolsSettingsGuard(ILogger<ApiProblemDetailsWriter> logger)
            : IValidateOptions<KestrelServerOptions>, IHostedLifecycleService, IDisposable
        {
            private readonly object _gate = new();
            private IConfiguration? _kestrel;
            private IDisposable? _reloadReports;
            private bool _disposed;

            /// <summary>
            /// Reports each protocols setting other than <c>Http1</c> in the configuration Kestrel's configuration
            /// loader reads, as event 5204, and, on the first call that finds a configuration, starts reporting such
            /// settings again on each of its reloads. Options without a configuration loader read no protocols setting.
            /// </summary>
            /// <param name="name">The options name. Every name is read alike.</param>
            /// <param name="options">The configured Kestrel options.</param>
            /// <returns>
            /// <see cref="ValidateOptionsResult.Skip"/>, because the guard refuses the host's start rather than the
            /// options.
            /// </returns>
            public ValidateOptionsResult Validate(string? name, KestrelServerOptions options)
            {
                ArgumentNullException.ThrowIfNull(options);

                if (options.ConfigurationLoader?.Configuration is IConfiguration kestrel)
                {
                    // The reload reports start before this scan, so a reload that lands between the two is reported
                    // too.
                    Watch(kestrel);
                    Report(kestrel);
                }

                return ValidateOptionsResult.Skip;
            }

            /// <summary>
            /// Refuses the host's start while Kestrel's configuration holds a protocols setting other than
            /// <c>Http1</c>. The host calls this before it starts any hosted service, the server included, so no
            /// endpoint is bound. Each such setting was reported when Kestrel created its options or on the reload that
            /// brought it, so nothing is logged here.
            /// </summary>
            /// <param name="cancellationToken">Not observed: the check completes at once.</param>
            /// <returns>
            /// A completed task, or one faulted with an <see cref="InvalidOperationException"/> whose message is fixed.
            /// </returns>
            public Task StartingAsync(CancellationToken cancellationToken)
            {
                IConfiguration? kestrel;
                lock (_gate)
                {
                    kestrel = _kestrel;
                }

                return kestrel is not null && FindUnsupportedProtocolsSettings(kestrel).Count > 0
                    ? Task.FromException(new InvalidOperationException(UnsupportedProtocolsSettingsMessage))
                    : Task.CompletedTask;
            }

            /// <summary>
            /// Does nothing: the guard's check runs in <see cref="StartingAsync"/>.
            /// </summary>
            /// <param name="cancellationToken">Not observed.</param>
            /// <returns>A completed task.</returns>
            public Task StartAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            /// <summary>
            /// Does nothing: the guard's check runs in <see cref="StartingAsync"/>.
            /// </summary>
            /// <param name="cancellationToken">Not observed.</param>
            /// <returns>A completed task.</returns>
            public Task StartedAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            /// <summary>
            /// Does nothing: the reload reports end when the host disposes the guard.
            /// </summary>
            /// <param name="cancellationToken">Not observed.</param>
            /// <returns>A completed task.</returns>
            public Task StoppingAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            /// <summary>
            /// Does nothing: the reload reports end when the host disposes the guard.
            /// </summary>
            /// <param name="cancellationToken">Not observed.</param>
            /// <returns>A completed task.</returns>
            public Task StopAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            /// <summary>
            /// Does nothing: the reload reports end when the host disposes the guard.
            /// </summary>
            /// <param name="cancellationToken">Not observed.</param>
            /// <returns>A completed task.</returns>
            public Task StoppedAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            /// <summary>
            /// Ends the reload reports. A later call does nothing, and no report starts afterwards.
            /// </summary>
            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                    _reloadReports?.Dispose();
                    _reloadReports = null;
                }
            }

            // Keeps and watches the first configuration found, the one Kestrel's options read for the host's lifetime.
            private void Watch(IConfiguration kestrel)
            {
                lock (_gate)
                {
                    if (_disposed || _kestrel is not null)
                    {
                        return;
                    }

                    _kestrel = kestrel;
                    _reloadReports = ChangeToken.OnChange(kestrel.GetReloadToken, () => Report(kestrel));
                }
            }

            private void Report(IConfiguration kestrel)
            {
                foreach ((string setting, HttpProtocols protocols) in FindUnsupportedProtocolsSettings(kestrel))
                {
                    LogUnsupportedProtocolsSetting(logger, setting, protocols);
                }
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
            /// connection is not wrapped, arms nothing.
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
                        // The method is null or empty when no request method was parsed, as when the server rejected
                        // the request line itself.
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
        /// The method the server parsed for the rejected request; <see langword="null"/> or empty when no request
        /// method was parsed, as when the server rejected the request line itself.
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
        private IDuplexPipe _transport;

        /// <summary>
        /// Wraps <paramref name="inner"/> and its accepted transport.
        /// </summary>
        /// <param name="inner">The accepted connection.</param>
        /// <param name="state">The connection's shared state.</param>
        /// <param name="responder">The producer of the problem responses.</param>
        public RejectionAwareConnection(ConnectionContext inner, ConnectionState state, RejectionResponder responder)
        {
            _inner = inner;
            _state = state;
            _responder = responder;
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

        // The endpoint serves HTTP/1.1 alone, so the accepted transport carries HTTP/1.x unless a TLS layer runs over
        // it. The TLS middleware sets its connection feature before its handshake reads anything, so at the accepted
        // transport's first read or write the feature tells whether that transport carries TLS records instead.
        private bool CarriesPlaintextHttp1()
        {
            return _inner.Features.Get<ITlsConnectionFeature>() is null;
        }

        // The endpoint's own protocols are HTTP/1.1 alone, and the guard refuses every protocols setting in Kestrel's
        // configuration but Http1, an SNI entry's included, so the TLS layer offers only http/1.1 for negotiation and
        // the server speaks HTTP/1.x over the decrypted transport whatever the client asked for.
        private static bool CarriesDecryptedHttp1()
        {
            return true;
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

        // The status line as written, then Content-Type and Content-Length for the problem, the fields every API
        // response carries, the kept fields in their order, the blank line and, unless the rejected request is a HEAD
        // request, the body.
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

            // The request pipeline sets these fields on each of its responses, and the server answers this request
            // before any middleware runs, so they are written here, where the rejection is recomposed. The problem
            // writer stays out of it: it sets only Content-Type and writes the body, so the protocol fields of every
            // response it completes survive as they are.
            foreach (KeyValuePair<string, string> header in ProblemDetailsSetup.ResponseSecurityHeaders)
            {
                WriteAscii(response, header.Key);
                response.Write(": "u8);
                WriteAscii(response, header.Value);
                response.Write(LineEnd);
            }

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