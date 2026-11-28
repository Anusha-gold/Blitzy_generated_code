using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using SmartHotel.Api.Infrastructure;
using SmartHotel.Api.IntegrationTests.Infrastructure;
using SmartHotel.Api.Modules.Auth;

namespace SmartHotel.Api.IntegrationTests.Http;

/// <summary>
/// The error contract for responses the framework produces rather than an action: routing's 405 and 404, requests
/// whose <c>Accept</c> header admits no JSON, an unhandled exception, the 400 for input that fails model binding,
/// and a plaintext request refused outside Development, unless it names a listed host and is redirected. Each must
/// still be an <c>application/problem+json</c> document that keeps its protocol headers and carries no exception data.
/// Every response, a success included, also carries the fields that keep a browser from sniffing its media type or
/// rendering it in a frame, and none names the server in a <c>Server</c> header. A binding 400 also keys its errors
/// only by contract names and JSON paths, and echoes no request text. The log records behind those responses are
/// held to the same rule: whatever log levels configuration sets, no record that reaches a log provider carries a
/// request value, a header value, a credential, an exception's text, or the name of the database or its server, while
/// every other record still follows the configured levels.
/// </summary>
/// <remarks>
/// <para>
/// The logging tests each host their own API with a <see cref="SmartHotelApiFactory.LogCapture"/> that keeps every
/// record it receives. The raised-verbosity hosts are configured the way any configuration source can raise them:
/// provider-less and provider-specific <c>Default</c> rules at <c>Trace</c>, longer category and wildcard rules at
/// <c>Trace</c>, scope capture on, and a code rule and minimum level at <c>Trace</c>. Failure messages name records by
/// level, category and event id only, never by their text.
/// </para>
/// <para>
/// The server's own rejections, of a malformed request before any middleware runs and of a body over its size limit,
/// are sent over TCP to the fixture's loopback listener, because the in-process server parses no request bytes.
/// </para>
/// <para>
/// Outside Development the transport is tested on the API run as a process of its own, with a throwaway certificate
/// and an environment set for that process alone, because Kestrel takes a certificate password from its process
/// environment only: an HTTPS listener serves the readiness probe with the certificate the environment's password
/// opens, while a password from the command line or a JSON file, or a listener that is plaintext only, stops startup
/// with exit code 1.
/// </para>
/// </remarks>
public sealed partial class ProblemDetailsContractTests : IClassFixture<SmartHotelApiFactory>
{
    private const string ProblemMediaType = "application/problem+json";
    private const string JsonMediaType = "application/json";
    private const string UnknownRoutePath = "/api/does-not-exist";
    private const string LoginRoute = "api/auth/login";
    private const string RegisterRoute = "api/auth/register";
    private const string ReadinessPath = "/api/health";
    private const string ForeignOrigin = "http://evil.example";
    private const string SecretMarker = "secret-marker";

    // Request text that a binding 400 must not echo. It needs no JSON escaping, so an echo would appear verbatim in
    // the response text, and its space makes System.Text.Json write a member path through it in bracket form,
    // $['name'].
    private const string BindingMarker = "zq7f3a9c binding-probe";

    private const string InvalidDateSearchScenario = "invalid-date-search";
    private const string BookingsWithoutTokenScenario = "bookings-without-token";
    private const string UnknownRouteScenario = "unknown-route";
    private const string WrongMethodScenario = "wrong-method";
    private const string AllowedOriginRequestScenario = "allowed-origin-request";

    private const string LoginScenario = "login";
    private const string BookingsWithTokenScenario = "bookings-with-token";
    private const string HealthScenario = "health";

    private const string HttpsOnlyDetail = "This API accepts HTTPS requests only.";
    private const string InvalidInputDetail =
        "Some of the information provided is missing or not valid. Correct it and try again.";
    private const string HttpsWebOrigin = "https://localhost:4200";

    // The setting that lists the hosts a plaintext request is redirected from outside Development, and the HTTPS port
    // that lets in-process redirection build its target.
    private const string RedirectHostsSetting = "HttpsRedirection:AllowedHosts";
    private const string DefaultHttpsPort = "443";

    // The fields every API response carries: no media-type sniffing and no rendering in a frame.
    private static readonly (string Name, string Value)[] SecurityHeaders =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("X-Frame-Options", "DENY"),
        ("Content-Security-Policy", "frame-ancestors 'none'"),
    ];

    // The API process tests' listeners, on a loopback port the operating system chooses, the variables Kestrel reads
    // its default certificate from, the console lines of the API's NoHttpsListener and HostRunFailed events and of the
    // hosting lifetime's address report, and the places outside the environment a certificate password can come from.
    private const string HttpsLoopbackUrl = "https://127.0.0.1:0";
    private const string HttpLoopbackUrl = "http://127.0.0.1:0";
    private const string CertificatePathVariable = "Kestrel__Certificates__Default__Path";
    private const string CertificatePasswordVariable = "Kestrel__Certificates__Default__Password";
    private const string NoHttpsListenerRecord = "SmartHotel.Api[5201]";
    private const string HostRunFailedRecord = "SmartHotel.Api[5202]";
    private const string ListeningRecord = "Now listening on:";
    private const string CommandLinePasswordSource = "command-line";
    private const string JsonFilePasswordSource = "json-file";

    private const string SearchGuestsMarkerScenario = "search-guests-marker";
    private const string LoginEmailNumberScenario = "login-email-number";
    private const string LoginUpperCaseEmailNumberScenario = "login-upper-case-email-number";
    private const string LoginUnknownMemberTruncatedScenario = "login-unknown-member-truncated";
    private const string RegisterFullNameNumberScenario = "register-full-name-number";
    private const string LoginEmptyBodyScenario = "login-empty-body";
    private const string BookingHotelIdStringScenario = "booking-hotel-id-string";

    private const string AuthFaultHeader = "X-Redaction-Test-Auth-Fault";
    private const string KestrelCategory = "Microsoft.AspNetCore.Server.Kestrel";
    private const string EndpointMiddlewareCategory = "Microsoft.AspNetCore.Routing.EndpointMiddleware";
    private const string JwtBearerCategory = "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler";
    private const string ActionInvokerCategory = "Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker";
    private const string HostCategory = "Microsoft.Extensions.Hosting.Internal.Host";
    private const string HostingDiagnosticsCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    // The configuration lets this category through only from Warning, so its unredacted Trace level shows that
    // configuration still quiets a category the redaction covers. The in-process test server never writes to it.
    private const string QuietedCategory = "Microsoft.AspNetCore.Server.Kestrel.Http2";
    private const LogLevel QuietedFloor = LogLevel.Warning;

    // ApiExceptionHandler's sanitized UnhandledException, and Kestrel's ApplicationError, which the bridge translates.
    private const int UnhandledExceptionEventId = 5000;
    private const int KestrelApplicationErrorEventId = 13;
    private const string KestrelApplicationErrorEventName = "ApplicationError";

    // AuthService's GuestRegistered and DatabaseInitializer's SeedInserted.
    private const int GuestRegisteredEventId = 1001;
    private const int SeedInsertedEventId = 5103;

    // Fixed, non-secret values for throwaway guests in a variant host's own test database.
    private const string ValidPassword = "Passw0rd!x";
    private const string ValidFullName = "Test Guest";

    // NpgsqlConnection.DataSource, the server EF Core's connection records would name, has the form tcp://host:port.
    private const string ServerAddressScheme = "tcp://";

    // EF Core's command records show each parameter as this placeholder while sensitive-data logging is off.
    private const string ParameterPlaceholder = "='?'";

    private const string AbsentDatabaseSuffix = "_absent";

    private static readonly EventId ProbeEvent = new(9_901, "RedactionProbe");
    private static readonly string ExceptionHandlerCategory = typeof(ApiExceptionHandler).FullName!;
    private static readonly string AuthServiceCategory = typeof(AuthService).FullName!;
    private static readonly string DatabaseInitializerCategory = typeof(DatabaseInitializer).FullName!;

    private static readonly LogLevel[] RecordLevels =
    [
        LogLevel.Trace,
        LogLevel.Debug,
        LogLevel.Information,
        LogLevel.Warning,
        LogLevel.Error,
        LogLevel.Critical,
    ];

    // The levels of each category that carry request values or exception text in ASP.NET Core 8.0.34 and its generic
    // host; every other level of these categories carries only fixed text, type and endpoint names, or
    // server-generated identifiers.
    private static readonly (string Category, LogLevel[] Levels)[] RedactedLevels =
    [
        (HostCategory, [LogLevel.Debug, LogLevel.Error, LogLevel.Critical]),
        ("Microsoft.Extensions.Hosting.Internal.ApplicationLifetime", [LogLevel.Critical]),
        (HostingDiagnosticsCategory, [LogLevel.Information, LogLevel.Critical]),
        ("Microsoft.AspNetCore.HostFiltering.HostFilteringMiddleware", [LogLevel.Trace, LogLevel.Information]),
        ("Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware", [LogLevel.Debug]),
        (KestrelCategory, [LogLevel.Trace, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical]),
        ("Microsoft.AspNetCore.Server.Kestrel.BadRequests", [LogLevel.Debug]),
        (QuietedCategory, [LogLevel.Debug, LogLevel.Information, LogLevel.Critical]),
        ("Microsoft.AspNetCore.Server.Kestrel.Http3", [LogLevel.Debug, LogLevel.Information]),
        ("Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets", [LogLevel.Debug]),
        ("Microsoft.AspNetCore.Server.Kestrel.Https.Internal.HttpsConnectionMiddleware", [LogLevel.Debug]),
        ("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", [LogLevel.Error]),
        ("Microsoft.AspNetCore.Diagnostics.DeveloperExceptionPageMiddleware", [LogLevel.Error]),
        ("Microsoft.AspNetCore.Cors.Infrastructure.CorsService", [LogLevel.Debug, LogLevel.Information]),
        ("Microsoft.AspNetCore.Cors.Infrastructure.CorsMiddleware", [LogLevel.Warning]),
        ("Microsoft.AspNetCore.Routing.Matching.DfaMatcher", [LogLevel.Debug]),
        ("Microsoft.AspNetCore.Routing.DefaultLinkParser", [LogLevel.Debug]),
        (JwtBearerCategory, [LogLevel.Information, LogLevel.Error]),
        ("Microsoft.AspNetCore.Mvc.Formatters.SystemTextJsonInputFormatter", [LogLevel.Debug]),
        ("Microsoft.AspNetCore.Mvc.ModelBinding.Binders.BodyModelBinder", [LogLevel.Debug]),
        ("Microsoft.AspNetCore.Mvc.Infrastructure.DefaultOutputFormatterSelector", [LogLevel.Debug]),
        (ActionInvokerCategory, [LogLevel.Trace]),
    ];

    // Categories whose records carry no request values; configuration alone decides their levels.
    private static readonly string[] UnredactedCategories =
    [
        "Microsoft.Hosting.Lifetime",
        "Microsoft.AspNetCore.Server.Kestrel.Connections",
        "Microsoft.AspNetCore.HttpsPolicy.HstsMiddleware",
        EndpointMiddlewareCategory,
        "Microsoft.AspNetCore.Authorization.DefaultAuthorizationService",
        "Microsoft.EntityFrameworkCore.Database.Command",
        "SmartHotel.Api.Modules.Auth.AuthService",
    ];

    private const string PlainOptionsScenario = "plain-options";
    private const string AllowedOriginPreflightScenario = "allowed-origin-preflight";
    private const string ForeignOriginPreflightScenario = "foreign-origin-preflight";

    // Each exceeds one of Kestrel's default limits, which the API leaves unchanged: an 8 KB request line, 32 KB of
    // headers and a body of 30,000,000 bytes.
    private const int OversizedRequestTargetLength = 9_000;
    private const int OversizedHeaderLength = 33_000;
    private const long MaxRequestBodySize = 30_000_000;

    private const string RequestTargetTooLongScenario = "request-target-too-long";
    private const string HeaderTooLargeScenario = "header-too-large";
    private const string MalformedHeaderLineScenario = "malformed-header-line";
    private const string MissingHostScenario = "missing-host";
    private const string UnsupportedVersionScenario = "unsupported-version";

    private const string DeclaredLengthScenario = "declared-length";
    private const string ChunkedBodyScenario = "chunked-body";

    private const string NoPriorRequestScenario = "no-prior-request";
    private const string HeadLikeBodyScenario = "head-like-body";
    private const string HeadHeaderTooLargeScenario = "head-header-too-large";
    private const string HeadRequestTargetTooLongScenario = "head-request-target-too-long";
    private const string HeadAfterBlankLinesScenario = "head-after-blank-lines";
    private const string HeadUnsupportedVersionScenario = "head-unsupported-version";
    private const string GetRequestTargetTooLongScenario = "get-request-target-too-long";

    // The request marker, then the text System.Text.Json's exception messages carry: .NET type names and the
    // position of the failure in the payload.
    private static readonly string[] BindingDisclosures =
        [BindingMarker, "SmartHotel.", "System.", "LineNumber", "BytePositionInLine", "Path:"];

    private readonly SmartHotelApiFactory _factory;
    private readonly TestPostgres _postgres;

    // A unique value per test, placed in every request value and exception the logging tests control.
    private readonly string _marker = "rdx" + Guid.NewGuid().ToString("N");

    public ProblemDetailsContractTests(SmartHotelApiFactory factory, TestPostgres postgres)
    {
        _factory = factory;
        _postgres = postgres;
    }

    // Only GET is mapped on api/hotels, so routing answers PUT with a bodyless 405 and an Allow header. The status-code
    // page turns it into a problem, and the writer must leave Allow in place.
    [Fact]
    public async Task WrongMethod_Returns405Problem()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();

        using HttpRequestMessage request = new(HttpMethod.Put, "/api/hotels");
        using HttpResponseMessage response = await client.Http.SendAsync(request, cancellationToken);

        await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.MethodNotAllowed,
            cancellationToken: cancellationToken);

        List<string> allowedMethods = ReadAllowedMethods(response);
        Assert.NotEmpty(allowedMethods);
        Assert.Contains("GET", allowedMethods, StringComparer.Ordinal);
    }

    // The readiness probe is mapped for GET only, so routing answers OPTIONS with a bodyless 405 and Allow: GET. A CORS
    // preflight for the probe, from the allowed origin or any other and on another path form routing sends to it, must
    // get that same problem, not the CORS middleware's 204, and no CORS grant.
    [Theory]
    [InlineData(ReadinessPath, PlainOptionsScenario)]
    [InlineData(ReadinessPath, AllowedOriginPreflightScenario)]
    [InlineData("/API/Health/", AllowedOriginPreflightScenario)]
    [InlineData(ReadinessPath, ForeignOriginPreflightScenario)]
    public async Task ReadinessProbe_OptionsRequest_Returns405Problem(string path, string scenario)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();

        string? origin = scenario switch
        {
            PlainOptionsScenario => null,
            AllowedOriginPreflightScenario => _factory.AllowedOrigin,
            ForeignOriginPreflightScenario => ForeignOrigin,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        using HttpRequestMessage request = new(HttpMethod.Options, path);
        if (origin is not null)
        {
            request.Headers.Add(CorsConstants.Origin, origin);
            request.Headers.Add(CorsConstants.AccessControlRequestMethod, HttpMethods.Get);
        }

        using HttpResponseMessage response = await client.Http.SendAsync(request, cancellationToken);

        await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.MethodNotAllowed,
            cancellationToken: cancellationToken);

        Assert.Contains("GET", ReadAllowedMethods(response), StringComparer.Ordinal);
        Assert.False(response.Headers.Contains(CorsConstants.AccessControlAllowOrigin));
    }

    // Keeping the readiness preflight out of CORS must not touch the controllers: a preflight for a booking from the
    // web app's origin is still granted by the single-origin policy, without credentials.
    [Fact]
    public async Task ControllerPreflight_FromAllowedOrigin_IsGranted()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();

        using HttpRequestMessage request = new(HttpMethod.Options, "/api/bookings");
        request.Headers.Add(CorsConstants.Origin, _factory.AllowedOrigin);
        request.Headers.Add(CorsConstants.AccessControlRequestMethod, HttpMethods.Post);
        request.Headers.Add(CorsConstants.AccessControlRequestHeaders, "authorization,content-type");
        using HttpResponseMessage response = await client.Http.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        string allowedOrigin = Assert.Single(ReadHeaderItems(response, CorsConstants.AccessControlAllowOrigin));
        Assert.Equal(_factory.AllowedOrigin, allowedOrigin);
        Assert.Contains(
            HttpMethods.Post,
            ReadHeaderItems(response, CorsConstants.AccessControlAllowMethods),
            StringComparer.Ordinal);

        List<string> allowedHeaders = ReadHeaderItems(response, CorsConstants.AccessControlAllowHeaders);
        Assert.Contains("Authorization", allowedHeaders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Content-Type", allowedHeaders, StringComparer.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains(CorsConstants.AccessControlAllowCredentials));
    }

    // A variant host whose matcher policy throws while routing selects the endpoint. Routing must run inside the
    // exception handler middleware, which the recording handler registered after the API's own proves by seeing the
    // exception; outside it, the Development exception page would answer and no exception handler would run.
    [Fact]
    public async Task RoutingException_ReachesExceptionHandler_Returns500WithoutDetails()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        RecordingExceptionHandler recorder = new();
        await using SmartHotelApiFactory variant = new(_postgres)
        {
            ServiceOverrides = services =>
            {
                services.AddSingleton<MatcherPolicy, ThrowingMatcherPolicy>();
                services.AddSingleton<IExceptionHandler>(recorder);
            },
        };

        // A hand-built factory is not a fixture, so it is started here.
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();

        using HttpRequestMessage request = new(HttpMethod.Get, ReadinessPath);
        request.Headers.Add(ThrowingMatcherPolicy.FaultHeader, "1");
        using HttpResponseMessage response = await client.Http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            cancellationToken: cancellationToken);

        // Each check is a boolean with a fixed message, because Assert.DoesNotContain quotes the body around a match,
        // and that would write the leaked exception text to the test output.
        Assert.False(
            body.Contains(SecretMarker, StringComparison.Ordinal),
            "Expected the 500 problem body not to contain the exception message.");
        Assert.False(
            body.Contains("InvalidOperationException", StringComparison.Ordinal),
            "Expected the 500 problem body not to contain the exception type name.");
        Assert.False(
            body.Contains("   at ", StringComparison.Ordinal),
            "Expected the 500 problem body not to contain a stack frame.");
        Assert.False(
            body.Contains(nameof(ThrowingMatcherPolicy), StringComparison.Ordinal),
            "Expected the 500 problem body not to contain the throwing matcher policy's type name.");

        Type handled = Assert.Single(recorder.ExceptionTypes);
        Assert.Equal(typeof(InvalidOperationException), handled);
    }

    // The exception handler is the first middleware of the API's pipeline. A startup filter's middleware runs ahead of
    // everything Program adds, so every startup filter the API registers must hand the pipeline configuration on
    // unchanged: its handling of the server's rejections lives in the connection pipeline instead, and its request
    // activity listener is a startup filter only for when the web host adds it.
    [Fact]
    public void Api_RegistersNoStartupFilterAheadOfExceptionHandler()
    {
        Action<IApplicationBuilder> next = static _ => { };
        IStartupFilter[] apiFilters =
        [
            .. _factory.Services.GetServices<IStartupFilter>()
                .Where(static filter => filter.GetType().Assembly == typeof(Program).Assembly),
        ];

        Assert.All(apiFilters, filter => Assert.Same(next, filter.Configure(next)));
    }

    [Fact]
    public async Task UnknownRoute_Returns404Problem()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();

        using HttpRequestMessage request = new(HttpMethod.Get, UnknownRoutePath);
        using HttpResponseMessage response = await client.Http.SendAsync(request, cancellationToken);

        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.NotFound, cancellationToken: cancellationToken);
    }

    // Every response of the pipeline, a success or a problem from MVC, routing, the bearer challenge or the status-code
    // pages, carries the fields that keep a browser from sniffing its media type or rendering it in a frame. They are
    // added beside the protocol fields each response needs: Allow on routing's 405, the bare bearer challenge on the
    // 401 and the CORS grant on a request from the web app's origin.
    [Theory]
    [InlineData(HealthScenario, HttpStatusCode.OK)]
    [InlineData(AllowedOriginRequestScenario, HttpStatusCode.OK)]
    [InlineData(InvalidDateSearchScenario, HttpStatusCode.BadRequest)]
    [InlineData(BookingsWithoutTokenScenario, HttpStatusCode.Unauthorized)]
    [InlineData(UnknownRouteScenario, HttpStatusCode.NotFound)]
    [InlineData(WrongMethodScenario, HttpStatusCode.MethodNotAllowed)]
    public async Task PipelineResponse_CarriesSecurityHeadersBesideProtocolHeaders(
        string scenario,
        HttpStatusCode expected)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();

        (HttpMethod method, string uri) = scenario switch
        {
            HealthScenario or AllowedOriginRequestScenario => (HttpMethod.Get, ReadinessPath),
            InvalidDateSearchScenario => (HttpMethod.Get, SameDayCheckOutSearchUri()),
            BookingsWithoutTokenScenario => (HttpMethod.Get, "/api/bookings"),
            UnknownRouteScenario => (HttpMethod.Get, UnknownRoutePath),
            WrongMethodScenario => (HttpMethod.Put, "/api/hotels"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        using HttpRequestMessage request = new(method, uri);
        if (scenario == AllowedOriginRequestScenario)
        {
            request.Headers.Add(CorsConstants.Origin, _factory.AllowedOrigin);
        }

        using HttpResponseMessage response = await client.Http.SendAsync(request, cancellationToken);

        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(expected, response.StatusCode);
        }
        else
        {
            await ProblemAssert.AssertProblemAsync(
                response,
                expected,
                scenario == InvalidDateSearchScenario ? "checkOut" : null,
                cancellationToken);
        }

        AssertSecurityHeaders(response);
        switch (scenario)
        {
            case AllowedOriginRequestScenario:
                Assert.Equal(
                    _factory.AllowedOrigin,
                    Assert.Single(ReadHeaderItems(response, CorsConstants.AccessControlAllowOrigin)));
                break;
            case BookingsWithoutTokenScenario:
                ProblemAssert.AssertBareBearerChallenge(response);
                break;
            case WrongMethodScenario:
                Assert.Contains("GET", ReadAllowedMethods(response), StringComparer.Ordinal);
                break;
        }
    }

    // The first row takes MVC's object-result path: with no XML formatter and ReturnHttpNotAcceptable off, negotiation
    // falls back to the JSON formatter. The other two take the status-code-page path, where the default problem
    // writer would decline an Accept header that admits no JSON and the API's writer must not.
    [Theory]
    [InlineData("application/xml", InvalidDateSearchScenario, HttpStatusCode.BadRequest, "checkOut")]
    [InlineData("application/xml", BookingsWithoutTokenScenario, HttpStatusCode.Unauthorized, null)]
    [InlineData("text/html", UnknownRouteScenario, HttpStatusCode.NotFound, null)]
    public async Task UnsupportedAccept_StillReturnsProblemJson(
        string accept,
        string scenario,
        HttpStatusCode expected,
        string? errorKey)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();

        string uri = scenario switch
        {
            InvalidDateSearchScenario => SameDayCheckOutSearchUri(),
            BookingsWithoutTokenScenario => "/api/bookings",
            UnknownRouteScenario => UnknownRoutePath,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        using HttpResponseMessage response = await client.Http.SendAsync(request, cancellationToken);

        await ProblemAssert.AssertProblemAsync(response, expected, errorKey, cancellationToken);

        // The exact spelling is checked as a boolean with a fixed message, because Assert.Equal would print the
        // response's media type.
        Assert.True(
            string.Equals(ProblemMediaType, response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal),
            $"Expected the media type to be exactly {ProblemMediaType}.");

        if (scenario == BookingsWithoutTokenScenario)
        {
            ProblemAssert.AssertBearerChallenge(response);
        }
    }

    // A variant host whose clock throws once armed. The search passes model validation and the controller then reads
    // the clock, so the request reaches an unhandled exception whose message is the marker. The recording handler,
    // registered after the API's own, sees the exception the middleware handles, which identifies the 500 as the
    // clock's and not an earlier failure's.
    [Fact]
    public async Task UnhandledException_Returns500WithoutDetails()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        ThrowingTimeProvider clock = new(SecretMarker);
        RecordingExceptionHandler recorder = new();
        await using SmartHotelApiFactory variant = new(_postgres)
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IExceptionHandler>(recorder);
            },
        };

        // A hand-built factory is not a fixture, so it is started here. Migration and seeding run while the clock still
        // delegates to the system clock.
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();

        // The dates come from one read of the real clock, so the query is a valid 2-night stay even across midnight
        // UTC, and only the controller's clock read fails.
        DateOnly today = ApiTestClient.TodayUtc;
        string checkIn = ApiTestClient.Iso(today.AddDays(30));
        string checkOut = ApiTestClient.Iso(today.AddDays(32));

        // The identical search succeeds while the clock still delegates, so the request itself is valid and only the
        // armed clock can fail it.
        using (HttpResponseMessage unarmed = await client.SearchAsync(
            "Seattle",
            checkIn,
            checkOut,
            "2",
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, unarmed.StatusCode);
        }

        clock.Arm();
        try
        {
            using HttpResponseMessage response = await client.SearchAsync(
                "Seattle",
                checkIn,
                checkOut,
                "2",
                cancellationToken);

            await ProblemAssert.AssertProblemAsync(
                response,
                HttpStatusCode.InternalServerError,
                cancellationToken: cancellationToken);

            // The exception handler resets the failed response before it writes the problem; the fields every
            // response carries are set as that problem starts, so they survive the reset.
            AssertSecurityHeaders(response);

            // Read only after ProblemAssert has decoded the buffered body. Reading first would let an unsupported
            // charset raise an exception whose message quotes the Content-Type value, where ProblemAssert reports a
            // fixed message instead.
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            // Each check is a boolean with a fixed message, because Assert.DoesNotContain quotes the body around a
            // match, and that would write the leaked exception text to the test output.
            Assert.False(
                body.Contains(SecretMarker, StringComparison.Ordinal),
                "Expected the 500 problem body not to contain the exception message.");
            Assert.False(
                body.Contains("InvalidOperationException", StringComparison.Ordinal),
                "Expected the 500 problem body not to contain the exception type name.");
            Assert.False(
                body.Contains("   at ", StringComparison.Ordinal),
                "Expected the 500 problem body not to contain a stack frame.");
            Assert.False(
                body.Contains(nameof(ThrowingTimeProvider), StringComparison.Ordinal),
                "Expected the 500 problem body not to contain the throwing clock's type name.");
            Assert.False(
                body.Contains("GetUtcNow", StringComparison.Ordinal),
                "Expected the 500 problem body not to contain the clock member name.");

            // The controller reads the clock once, and nothing else reads it while the request runs: the search
            // carries no token, so the bearer handler never consults it, and the exception path reads no time. The
            // handler runs before the response starts, so its record is complete once the response has arrived.
            // Counts and identity are checked as booleans with fixed messages, because a failing collection assertion
            // would print each exception, message included.
            IReadOnlyList<InvalidOperationException> thrown = clock.ThrownExceptions;
            IReadOnlyList<Exception> handled = recorder.Exceptions;
            Assert.True(
                thrown.Count == 1,
                $"Expected the armed clock to throw exactly once, but it threw {thrown.Count} times.");
            Assert.True(
                handled.Count == 1,
                $"Expected the exception handlers to see exactly one exception, but they saw {handled.Count}.");
            Assert.True(
                ReferenceEquals(thrown[0], handled[0]),
                "Expected the handled exception to be the one the armed clock threw.");
        }
        finally
        {
            // Disarmed before the variant is disposed, so the host's shutdown never meets a throwing clock.
            clock.Disarm();
        }
    }

    // Each request carries the marker where the framework would log it: the query string, the path, a route value, a
    // JSON property name, the Content-Type, Accept, Origin and CORS request headers, the credentials, bearer tokens,
    // and the messages of an unexpected authentication failure and an unhandled exception.
    [Fact]
    public async Task RaisedVerbosity_RequestsCarryingMarkedValues_LeaveNoValueOrExceptionTextInAnyRecord()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.LogCapture capture = new();
        ThrowingTimeProvider clock = new(_marker + " clock fault");
        await using SmartHotelApiFactory variant = CreateRaisedVerbosityVariant(capture, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);

            // An unexpected authentication failure: the bearer handler's first event throws for a request that asks
            // for it, so the handler logs its processing failure and rethrows.
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options => options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context => context.Request.Headers.ContainsKey(AuthFaultHeader)
                        ? throw new InvalidOperationException(_marker + " authentication fault")
                        : Task.CompletedTask,
                });
        });

        // A hand-built factory is not a fixture, so it is started here.
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();
        string checkIn = ApiTestClient.Iso(ApiTestClient.TodayUtc.AddDays(30));
        string checkOut = ApiTestClient.Iso(ApiTestClient.TodayUtc.AddDays(32));
        string password = _marker + "-password";

        await ExpectStatusAsync(
            HttpStatusCode.OK,
            client.SearchAsync(_marker, checkIn, checkOut, "2", ct),
            ct);
        await ExpectStatusAsync(
            HttpStatusCode.NotFound,
            client.Http.GetAsync($"/api/{_marker}?q={_marker}", ct),
            ct);
        await ExpectStatusAsync(
            HttpStatusCode.NotFound,
            client.Http.GetAsync($"/api/hotels/{_marker}", ct),
            ct);

        // Malformed JSON is a body-level failure that names no field, so only the errors object is required.
        await ExpectStatusAsync(
            HttpStatusCode.BadRequest,
            client.Http.PostAsync(
                "/api/auth/login",
                new StringContent($"{{\"{_marker}\": nul", Encoding.UTF8, "application/json"),
                ct),
            ct);
        await ExpectStatusAsync(
            HttpStatusCode.UnsupportedMediaType,
            client.Http.PostAsync(
                "/api/auth/login",
                new StringContent("{}", Encoding.UTF8, $"text/x-{_marker}"),
                ct),
            ct);

        // A zero-night stay, so the 400 problem is negotiated against an Accept header that admits no JSON, from an
        // origin the CORS policy rejects.
        using (HttpRequestMessage request = new(
            HttpMethod.Get,
            $"/api/hotels?city=Seattle&checkIn={checkIn}&checkOut={checkIn}&guests=2"))
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue($"application/x-{_marker}"));
            request.Headers.TryAddWithoutValidation("Origin", $"http://{_marker}.test");
            await ExpectStatusAsync(
                HttpStatusCode.BadRequest,
                client.Http.SendAsync(request, ct),
                ct,
                errorKey: "checkOut");
        }

        // A preflight from the allowed origin that asks for a header the policy does not allow.
        using (HttpRequestMessage preflight = new(HttpMethod.Options, "/api/hotels"))
        {
            preflight.Headers.TryAddWithoutValidation("Origin", variant.AllowedOrigin);
            preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
            preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", $"x-{_marker}");
            await ExpectStatusAsync(HttpStatusCode.NoContent, client.Http.SendAsync(preflight, ct), ct);
        }

        // A preflight from the allowed origin for a path that carries the marker: before the CORS policy answers it,
        // the readiness preflight check parses that path against the readiness route.
        using (HttpRequestMessage preflight = new(HttpMethod.Options, $"/api/{_marker}"))
        {
            preflight.Headers.TryAddWithoutValidation("Origin", variant.AllowedOrigin);
            preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
            await ExpectStatusAsync(HttpStatusCode.NoContent, client.Http.SendAsync(preflight, ct), ct);
        }

        string email = _marker + "@example.test";
        await ExpectStatusAsync(
            HttpStatusCode.Created,
            client.RegisterAsync(new { email, password, fullName = _marker }, ct),
            ct);

        // The wrong password is a credential failure the action reports, not an authentication challenge.
        await ExpectStatusAsync(
            HttpStatusCode.Unauthorized,
            client.LoginAsync(new { email, password = password + "-wrong" }, ct),
            ct);

        // A rejected token is challenged without error details, so neither challenge can echo the marked token.
        await ExpectStatusAsync(
            HttpStatusCode.Unauthorized,
            client.ListBookingsAsync(_marker, ct),
            ct,
            bareBearerChallenge: true);
        await ExpectStatusAsync(
            HttpStatusCode.Unauthorized,
            client.ListBookingsAsync($"{_marker}.{_marker}.{_marker}", ct),
            ct,
            bareBearerChallenge: true);

        using (HttpRequestMessage faulted = new(HttpMethod.Get, "/api/bookings"))
        {
            faulted.Headers.TryAddWithoutValidation(AuthFaultHeader, "1");
            using HttpResponseMessage response = await client.Http.SendAsync(faulted, ct);
            await AssertServerErrorWithW3CTraceIdAsync(response, ct);
        }

        // The dates are valid, so only the controller's clock read fails, with the marker as its message.
        clock.Arm();
        try
        {
            using HttpResponseMessage response = await client.SearchAsync("Seattle", checkIn, checkOut, "2", ct);
            await AssertServerErrorWithW3CTraceIdAsync(response, ct);
        }
        finally
        {
            // Disarmed before the host stops, so the shutdown never meets a throwing clock.
            clock.Disarm();
        }

        // Stopping the host completes every request, so each record a request writes once its response is sent, such
        // as the request-finished record, has arrived before the records are read.
        await variant.DisposeAsync();
        IReadOnlyList<SmartHotelApiFactory.CapturedLog> records = capture.Records;

        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            record => record.Text.Contains(_marker, StringComparison.OrdinalIgnoreCase),
            "record carrying the marker");
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            static record => record.HasException,
            "record carrying an exception");
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            static record => IdentityModelMessage().IsMatch(record.Text),
            "record carrying a token-validation message");
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            static record => record.Scopes.Contains("RequestPath", StringComparison.Ordinal),
            "record carrying the request path scope");

        // Both failures are recorded once, as the sanitized event.
        SmartHotelApiFactory.CapturedLog[] sanitized =
        [
            .. records.Where(static record =>
                record.Category == ExceptionHandlerCategory && record.EventId == UnhandledExceptionEventId),
        ];
        Assert.Equal(2, sanitized.Length);
        Assert.All(sanitized, static record => Assert.Equal(LogLevel.Error, record.Level));

        // Configuration still raises what carries no values: the endpoint records, the action invoker's Information
        // records beside its withheld Trace ones, and the bearer handler's Debug records beside its withheld
        // Information and Error ones.
        Assert.Contains(
            records,
            static record => record.Category == EndpointMiddlewareCategory && record.Level == LogLevel.Information);
        Assert.Contains(
            records,
            static record => record.Category == ActionInvokerCategory && record.Level == LogLevel.Information);
        Assert.Contains(
            records,
            static record => record.Category == JwtBearerCategory && record.Level == LogLevel.Debug);
    }

    // Covers every category of the inventory, Kestrel's included, which the in-process test server never uses: each
    // level is written once with the marker in its message and exception, and only the levels that are neither redacted
    // nor below the configured level arrive. Kestrel's unhandled-exception report arrives only as the sanitized event,
    // and no scope is captured.
    [Fact]
    public async Task RaisedVerbosity_RedactedLevelsReachNoProvider_WhileConfigurationDecidesTheRest()
    {
        SmartHotelApiFactory.LogCapture capture = new();
        await using SmartHotelApiFactory variant = CreateRaisedVerbosityVariant(capture, additionalOverrides: null);
        await variant.InitializeAsync();
        ILoggerFactory loggerFactory = variant.Services.GetRequiredService<ILoggerFactory>();

        List<string> mismatches = [];
        foreach ((string category, LogLevel[] redacted) in RedactedLevels)
        {
            LogLevel floor = category == QuietedCategory ? QuietedFloor : LogLevel.Trace;
            CheckLevels(category, level => level >= floor && !redacted.Contains(level));
        }

        foreach (string category in UnredactedCategories)
        {
            CheckLevels(category, static _ => true);
        }

        if (mismatches.Count > 0)
        {
            Assert.Fail("Unexpected delivery for: " + string.Join("; ", mismatches) + ".");
        }

        // Kestrel reports an unhandled exception as its ApplicationError record. Only the bridge receives it, and it
        // writes the sanitized event instead.
        int mark = capture.Count;
        ILogger kestrel = loggerFactory.CreateLogger(KestrelCategory);
        kestrel.Log(
            LogLevel.Error,
            new EventId(KestrelApplicationErrorEventId, KestrelApplicationErrorEventName),
            _marker,
            new InvalidOperationException(_marker),
            static (state, _) => state);

        IReadOnlyList<SmartHotelApiFactory.CapturedLog> reported = capture.Since(mark);
        Assert.DoesNotContain(reported, static record => record.Category == KestrelCategory);
        SmartHotelApiFactory.CapturedLog sanitized = Assert.Single(
            reported,
            static record => record.Category == ExceptionHandlerCategory);
        Assert.Equal(UnhandledExceptionEventId, sanitized.EventId);
        Assert.Equal(LogLevel.Error, sanitized.Level);
        Assert.False(sanitized.HasException, "The sanitized event must not carry the exception.");
        Assert.False(
            sanitized.Text.Contains(_marker, StringComparison.OrdinalIgnoreCase),
            "Expected the sanitized event's text not to carry the marker.");

        // A scope opened around a record never reaches the provider.
        mark = capture.Count;
        ILogger endpoint = loggerFactory.CreateLogger(EndpointMiddlewareCategory);
        using (endpoint.BeginScope(_marker))
        {
            endpoint.Log(LogLevel.Information, ProbeEvent, "scoped", exception: null, static (state, _) => state);
        }

        SmartHotelApiFactory.CapturedLog scoped = Assert.Single(
            capture.Since(mark),
            static record => record.Category == EndpointMiddlewareCategory && record.EventId == ProbeEvent.Id);
        Assert.False(
            scoped.Scopes.Contains(_marker, StringComparison.OrdinalIgnoreCase),
            "Expected the record's scopes not to carry the scope opened around it.");

        void CheckLevels(string category, Func<LogLevel, bool> delivered)
        {
            ILogger logger = loggerFactory.CreateLogger(category);
            foreach (LogLevel level in RecordLevels)
            {
                int before = capture.Count;
                logger.Log(
                    level,
                    ProbeEvent,
                    _marker,
                    new InvalidOperationException(_marker),
                    static (state, _) => state);
                bool arrived = capture.Since(before).Any(record =>
                    record.Category == category && record.Level == level && record.EventId == ProbeEvent.Id);
                if (arrived != delivered(level))
                {
                    mismatches.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{category} at {level} {(arrived ? "arrived" : "was withheld")}"));
                }
            }
        }
    }

    // A hosted service that fails to start stands for a server that cannot bind its configured address: the generic
    // host reports the failure, with its exception, at Error, which the default configuration admits. No record that
    // reaches a provider carries the exception or its text, and the failure still stops the host from starting.
    [Fact]
    public async Task HostedServiceFailsToStart_StartupFailsWithoutExceptionTextInAnyRecord()
    {
        SmartHotelApiFactory.LogCapture capture = new();
        FailingStartup startup = new(_marker + " startup fault");
        await using SmartHotelApiFactory variant = _factory.CreateLogCaptureVariant(
            capture,
            [],
            services => services.AddHostedService(_ => startup));

        // The host's start fails on the hosted service's exception, so the factory never serves a request. The
        // factory reports the failure by its exception types alone, so its message carries no marker either.
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await variant.InitializeAsync());
        Assert.True(startup.Started, "The failing hosted service was never started.");
        Assert.True(
            failure.Message.StartsWith("Starting the API host failed.", StringComparison.Ordinal),
            "Expected the factory's failure message to begin 'Starting the API host failed.'");
        Assert.False(
            failure.Message.Contains(_marker, StringComparison.Ordinal),
            "Expected the factory's failure message not to carry the hosted service's exception text.");

        // The seed record shows that the capture received the host's records at the configured levels.
        IReadOnlyList<SmartHotelApiFactory.CapturedLog> records = capture.Records;
        Assert.Contains(
            records,
            static record => record.Category == DatabaseInitializerCategory && record.EventId == SeedInsertedEventId);
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            static record => record.Category == HostCategory && record.Level >= LogLevel.Error,
            "host failure record");
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            record => record.Text.Contains(_marker, StringComparison.OrdinalIgnoreCase),
            "record carrying the marker");
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            static record => record.HasException,
            "record carrying an exception");
    }

    // A startup filter whose pipeline configuration throws stands for any failure while the web host builds the request
    // pipeline: the web host reports it, with its exception, under its diagnostics category at Critical. The capture
    // admits that category at every level, yet no such record reaches it, no record carries the exception or its text,
    // and the failure still stops the host from starting.
    [Fact]
    public async Task PipelineFailsToBuild_StartupFailsWithoutExceptionTextInAnyRecord()
    {
        SmartHotelApiFactory.LogCapture capture = new();
        FailingPipeline pipeline = new(_marker + " pipeline fault");
        await using SmartHotelApiFactory variant = _factory.CreateLogCaptureVariant(
            capture,
            [(HostingDiagnosticsCategory, LogLevel.Trace)],
            services => services.AddSingleton<IStartupFilter>(pipeline));

        // The host's start fails while it builds the pipeline, so the factory never serves a request. The factory
        // reports the failure by its exception types alone, so its message carries no marker either.
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await variant.InitializeAsync());
        Assert.True(pipeline.Configured, "The failing pipeline configuration was never invoked.");
        Assert.StartsWith("Starting the API host failed.", failure.Message, StringComparison.Ordinal);
        Assert.False(
            failure.Message.Contains(_marker, StringComparison.Ordinal),
            "Expected the factory's failure message not to carry the pipeline's exception text.");

        // The seed record shows that the capture received the host's records at the configured levels.
        IReadOnlyList<SmartHotelApiFactory.CapturedLog> records = capture.Records;
        Assert.Contains(
            records,
            static record => record.Category == DatabaseInitializerCategory && record.EventId == SeedInsertedEventId);
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            static record => record.Category == HostingDiagnosticsCategory && record.Level == LogLevel.Critical,
            "web host startup failure record");
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            record => record.Text.Contains(_marker, StringComparison.OrdinalIgnoreCase),
            "record carrying the marker");
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            static record => record.HasException,
            "record carrying an exception");
    }

    // Once the variant host has started, its secret source names a database that does not exist on the same server,
    // so the search's connection is refused while it is being opened (3D000). That refusal is not transient, so no
    // retry delays it, and it reaches the exception handler as an outage does once its retries are spent.
    [Fact]
    public async Task Search_DatabaseConnectionFails_Returns500WithOnlyTheSanitizedErrorRecord()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.LogCapture capture = new();
        DatabaseRedirect redirect = new();
        await using SmartHotelApiFactory variant = _factory.CreateLogCaptureVariant(
            capture,
            SmartHotelApiFactory.LogCapture.DataLayerRules,
            redirect.Install);
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();
        string absentDatabase = variant.DatabaseName + AbsentDatabaseSuffix;

        // The dates come from the real clock, so the query passes validation and the request reaches the database.
        string checkIn = ApiTestClient.Iso(ApiTestClient.TodayUtc.AddDays(30));
        string checkOut = ApiTestClient.Iso(ApiTestClient.TodayUtc.AddDays(32));

        redirect.Arm();
        int mark = capture.Count;
        using HttpResponseMessage response = await client.SearchAsync("Seattle", checkIn, checkOut, "2", ct);

        await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            cancellationToken: ct);

        IReadOnlyList<SmartHotelApiFactory.CapturedLog> records = capture.Since(mark);
        SmartHotelApiFactory.CapturedLog error = Assert.Single(
            records,
            static record => record.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal(ExceptionHandlerCategory, error.Category);
        Assert.Equal(UnhandledExceptionEventId, error.EventId);
        Assert.True(
            error.Message.Contains("SqlState=" + PostgresErrorCodes.InvalidCatalogName, StringComparison.Ordinal),
            $"Expected the sanitized event to report SqlState={PostgresErrorCodes.InvalidCatalogName}.");

        // No record, at any level, names the database or the server it was reached on.
        AssertNoRecordNamesTheDatabaseOrServer(records, absentDatabase);
    }

    // EF Core's categories reach the provider at every level, so the startup migration and seed, a search and a
    // registration, which together open, read, close and dispose connections, would leave any record that names the
    // database or the server whatever level the host's configuration admits. The connection category's own Debug
    // records and the command records with their placeholders arrive, so the capture reaches the levels such records
    // are written at.
    [Fact]
    public async Task StartupSearchAndRegister_AtEveryLevel_LeaveNoRecordNamingTheDatabaseOrServer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SmartHotelApiFactory.LogCapture capture = new();
        await using SmartHotelApiFactory variant = _factory.CreateLogCaptureVariant(
            capture,
            SmartHotelApiFactory.LogCapture.DataLayerRules,
            additionalOverrides: null);
        await variant.InitializeAsync();
        ApiTestClient client = variant.CreateApiClient();
        string checkIn = ApiTestClient.Iso(ApiTestClient.TodayUtc.AddDays(30));
        string checkOut = ApiTestClient.Iso(ApiTestClient.TodayUtc.AddDays(32));
        string email = $"trace+{Guid.NewGuid():N}@example.test";

        using HttpResponseMessage search = await client.SearchAsync("Seattle", checkIn, checkOut, "2", ct);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        using HttpResponseMessage register = await client.RegisterAsync(
            new { email, password = ValidPassword, fullName = ValidFullName },
            ct);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        // Every record from the host's start on, its migration and seed included.
        IReadOnlyList<SmartHotelApiFactory.CapturedLog> records = capture.Since(0);
        Assert.Contains(
            records,
            static record => record.Category == DbLoggerCategory.Migrations.Name
                && record.EventId == RelationalEventId.MigrationApplying.Id);
        Assert.Contains(
            records,
            static record => record.Category == DatabaseInitializerCategory && record.EventId == SeedInsertedEventId);
        Assert.Contains(
            records,
            static record => record.Category == DbLoggerCategory.Database.Connection.Name
                && record.Level == LogLevel.Debug);
        Assert.Contains(
            records,
            static record => record.Category == DbLoggerCategory.Database.Command.Name
                && record.EventId == RelationalEventId.CommandExecuted.Id
                && record.Message.Contains(ParameterPlaceholder, StringComparison.Ordinal));
        Assert.Single(
            records,
            static record => record.Category == AuthServiceCategory && record.EventId == GuestRegisteredEventId);

        AssertNoRecordNamesTheDatabaseOrServer(records, variant.DatabaseName);
    }

    // Kestrel rejects these requests while it parses them, before any middleware runs, and writes a bodyless status of
    // its own. Only a listening server parses raw bytes, so each request goes over a TCP connection of its own to the
    // fixture's loopback listener. The response must be the API's problem, framed by its Content-Length, and the
    // server still closes the connection after it, as it does after every rejection.
    [Theory]
    [InlineData(RequestTargetTooLongScenario, HttpStatusCode.RequestUriTooLong)]
    [InlineData(HeaderTooLargeScenario, HttpStatusCode.RequestHeaderFieldsTooLarge)]
    [InlineData(MalformedHeaderLineScenario, HttpStatusCode.BadRequest)]
    [InlineData(MissingHostScenario, HttpStatusCode.BadRequest)]
    [InlineData(UnsupportedVersionScenario, HttpStatusCode.HttpVersionNotSupported)]
    public async Task ServerRejectedRequest_ReturnsProblem(string scenario, HttpStatusCode expected)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Uri address = await _factory.StartLoopbackListenerAsync(cancellationToken);

        string request = scenario switch
        {
            RequestTargetTooLongScenario =>
                $"GET /{new string('a', OversizedRequestTargetLength - 1)} HTTP/1.1\r\nHost: localhost\r\n\r\n",
            HeaderTooLargeScenario => OversizedHeaderRequest("GET"),
            MalformedHeaderLineScenario => MalformedHeaderLineRequest(),
            MissingHostScenario => $"GET {ReadinessPath} HTTP/1.1\r\n\r\n",
            UnsupportedVersionScenario => $"GET {ReadinessPath} HTTP/1.2\r\nHost: localhost\r\n\r\n",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        using RawHttpConnection connection = await RawHttpConnection.OpenAsync(address, cancellationToken);
        await connection.SendAsync(request);
        RawResponse rejection = await connection.ReadResponseAsync(headRequest: false);

        using HttpResponseMessage response = rejection.ToResponseMessage();
        await ProblemAssert.AssertProblemAsync(response, expected, cancellationToken: cancellationToken);

        Assert.Equal("close", rejection.GetHeader("Connection"));
        Assert.Null(rejection.GetHeader("Server"));
        AssertSecurityHeaders(rejection);
        Assert.Equal(0, await connection.CountRemainingBytesAsync());
    }

    // A response to HEAD carries the header fields a GET would get and no body (RFC 9110, section 9.3.2), so the
    // problem's Content-Type and Content-Length arrive without the body: on a fresh connection, and after a request
    // whose body, framed by its Content-Length or by chunks, the server read on the same kept-alive connection. The
    // rejection is either of the header fields, after the request line is parsed, or of the request line itself: its
    // length, its HTTP version, or its length after the blank lines the server skips before a request line. A
    // body whose bytes begin like a HEAD request line never makes the next request count as one: a GET rejected after
    // it carries the problem, whose traceId is the server's identifier for the rejected request, the connection id
    // and the request's number on the connection. Kestrel closes the connection after every rejection.
    [Theory]
    [InlineData(NoPriorRequestScenario, HeadHeaderTooLargeScenario, HttpStatusCode.RequestHeaderFieldsTooLarge)]
    [InlineData(DeclaredLengthScenario, HeadHeaderTooLargeScenario, HttpStatusCode.RequestHeaderFieldsTooLarge)]
    [InlineData(ChunkedBodyScenario, HeadHeaderTooLargeScenario, HttpStatusCode.RequestHeaderFieldsTooLarge)]
    [InlineData(DeclaredLengthScenario, HeadRequestTargetTooLongScenario, HttpStatusCode.RequestUriTooLong)]
    [InlineData(DeclaredLengthScenario, HeadAfterBlankLinesScenario, HttpStatusCode.RequestUriTooLong)]
    [InlineData(ChunkedBodyScenario, HeadUnsupportedVersionScenario, HttpStatusCode.HttpVersionNotSupported)]
    [InlineData(HeadLikeBodyScenario, GetRequestTargetTooLongScenario, HttpStatusCode.RequestUriTooLong)]
    public async Task ServerRejectedRequest_OmitsProblemBodyOnlyForHead(
        string priorRequest,
        string rejectedRequest,
        HttpStatusCode expected)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Uri address = await _factory.StartLoopbackListenerAsync(cancellationToken);

        using RawHttpConnection connection = await RawHttpConnection.OpenAsync(address, cancellationToken);
        uint rejectedRequestNumber = 1;
        if (priorRequest != NoPriorRequestScenario)
        {
            await connection.SendAsync(BodyBearingRegisterRequest(priorRequest));
            RawResponse served = await connection.ReadResponseAsync(headRequest: false);

            using HttpResponseMessage servedResponse = served.ToResponseMessage();
            await ProblemAssert.AssertProblemAsync(
                servedResponse,
                HttpStatusCode.BadRequest,
                cancellationToken: cancellationToken);
            Assert.NotEqual("close", served.GetHeader("Connection"));
            rejectedRequestNumber++;
        }

        string request = rejectedRequest switch
        {
            HeadHeaderTooLargeScenario => OversizedHeaderRequest("HEAD"),
            HeadRequestTargetTooLongScenario => OversizedTargetRequest("HEAD"),
            HeadAfterBlankLinesScenario => "\r\n\r\n" + OversizedTargetRequest("HEAD"),
            HeadUnsupportedVersionScenario => $"HEAD {ReadinessPath} HTTP/1.2\r\nHost: localhost\r\n\r\n",
            GetRequestTargetTooLongScenario => OversizedTargetRequest("GET"),
            _ => throw new ArgumentOutOfRangeException(nameof(rejectedRequest)),
        };
        bool headRequest = rejectedRequest != GetRequestTargetTooLongScenario;

        await connection.SendAsync(request);
        RawResponse rejection = await connection.ReadResponseAsync(headRequest);

        using HttpResponseMessage response = rejection.ToResponseMessage();
        if (headRequest)
        {
            Assert.Equal((int)expected, rejection.Status);
            Assert.Equal(ProblemMediaType, response.Content.Headers.ContentType?.MediaType);
            Assert.True(rejection.ContentLength > 0, "Expected the Content-Length of the problem body.");
        }
        else
        {
            JsonElement problem = await ProblemAssert.AssertProblemAsync(
                response,
                expected,
                cancellationToken: cancellationToken);

            // Kestrel's connection ids are 13 characters of its base-32 alphabet, and the request number is eight
            // upper-case hex digits.
            string requestNumber = rejectedRequestNumber.ToString("X8", CultureInfo.InvariantCulture);
            string? traceId = problem.GetProperty("traceId").GetString();
            Match traceIdParts = KestrelTraceId().Match(traceId ?? string.Empty);
            Assert.True(
                traceIdParts.Success
                    && string.Equals(requestNumber, traceIdParts.Groups["request"].Value, StringComparison.Ordinal),
                $"Expected the problem's traceId to be a connection id, a colon and request number {requestNumber}.");
        }

        Assert.Equal("close", rejection.GetHeader("Connection"));
        Assert.Null(rejection.GetHeader("Server"));
        AssertSecurityHeaders(rejection);
        Assert.Equal(0, await connection.CountRemainingBytesAsync());
    }

    // The connection serves a request first, so the rejection follows a completed exchange on a kept-alive connection.
    // The served response must pass through unchanged, and the rejection must still become a problem.
    [Fact]
    public async Task ServerRejectionAfterServedRequest_ReturnsProblem()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Uri address = await _factory.StartLoopbackListenerAsync(cancellationToken);

        using RawHttpConnection connection = await RawHttpConnection.OpenAsync(address, cancellationToken);
        await connection.SendAsync($"GET {ReadinessPath} HTTP/1.1\r\nHost: localhost\r\n\r\n");
        RawResponse served = await connection.ReadResponseAsync(headRequest: false);

        using HttpResponseMessage servedResponse = served.ToResponseMessage();
        Assert.Equal(HttpStatusCode.OK, servedResponse.StatusCode);
        Assert.Equal("text/plain", servedResponse.Content.Headers.ContentType?.MediaType);
        string servedBody = await servedResponse.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(
            string.Equals("Healthy", servedBody, StringComparison.Ordinal),
            "Expected the readiness body to be exactly Healthy.");

        await connection.SendAsync(MalformedHeaderLineRequest());
        RawResponse rejection = await connection.ReadResponseAsync(headRequest: false);

        using HttpResponseMessage response = rejection.ToResponseMessage();
        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.BadRequest, cancellationToken: cancellationToken);
    }

    // An ordinary client's successful request through the listening server is answered exactly as without it, and
    // Kestrel names itself in no Server header.
    [Fact]
    public async Task ReadinessProbe_ThroughListeningServer_ReturnsHealthy()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Uri address = await _factory.StartLoopbackListenerAsync(cancellationToken);

        // The listener is on a loopback address, which the client must reach directly: a proxy the environment of the
        // host running the tests names would otherwise carry the request, since NO_PROXY's usual localhost entry does
        // not cover 127.0.0.1.
        using HttpClient http = new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = address };
        using HttpResponseMessage response = await http.GetAsync(ReadinessPath, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(
            string.Equals("Healthy", body, StringComparison.Ordinal),
            "Expected the readiness body to be exactly Healthy.");
        Assert.Empty(response.Headers.Server);
        AssertSecurityHeaders(response);
    }

    // A variant derived through WithWebHostBuilder builds its host through its parent factory's CreateHost. A Production
    // variant built and disposed before the listener starts must not become the listener's builder: a Production host
    // on plaintext Kestrel binds no HTTPS address, so the API would set the process exit code and stop it. A factory of
    // the test's own fixes that order whatever order xUnit runs the class's tests in.
    [Fact]
    public async Task LoopbackListener_StartedAfterProductionVariant_ServesRequests()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using SmartHotelApiFactory factory = new(_postgres);
        await factory.InitializeAsync();
        int exitCode = Environment.ExitCode;

        WebApplicationFactory<Program> production = await StartProductionVariantAsync(
            factory,
            new Dictionary<string, string?>(),
            cancellationToken);
        string variantEnvironment = production.Services.GetRequiredService<IHostEnvironment>().EnvironmentName;
        await production.DisposeAsync();
        Assert.Equal(Environments.Production, variantEnvironment);

        Uri address = await factory.StartLoopbackListenerAsync(cancellationToken);

        // Directly to the loopback listener, never through a proxy the environment of the host running the tests
        // names, whose NO_PROXY entry for localhost does not cover 127.0.0.1.
        using HttpClient http = new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = address };
        using HttpResponseMessage response = await http.GetAsync(ReadinessPath, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(
            string.Equals("Healthy", body, StringComparison.Ordinal),
            "Expected the readiness body to be exactly Healthy.");
        Assert.Equal(exitCode, Environment.ExitCode);
    }

    // Kestrel enforces its body-size limit while the API reads the body, by throwing a client error from the read: at
    // the first read for a declared length over the limit, and once a chunked body passes it. The response must carry
    // that 413 as a problem with the generic detail, never the exception's message, which names the limit.
    [Theory]
    [InlineData(DeclaredLengthScenario)]
    [InlineData(ChunkedBodyScenario)]
    public async Task OversizedRequestBody_Returns413ProblemWithoutDetails(string scenario)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Uri address = await _factory.StartLoopbackListenerAsync(cancellationToken);
        string oversizedLength = (MaxRequestBodySize + 1).ToString(CultureInfo.InvariantCulture);

        string framing = scenario switch
        {
            DeclaredLengthScenario => "Content-Length: " + oversizedLength,
            ChunkedBodyScenario => "Transfer-Encoding: chunked",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        using RawHttpConnection connection = await RawHttpConnection.OpenAsync(address, cancellationToken);
        await connection.SendAsync(
            $"POST /api/auth/register HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n{framing}\r\n\r\n");

        // A declared body is rejected before any of it is read, so only its start is sent.
        if (scenario == DeclaredLengthScenario)
        {
            await connection.SendAsync("{\"email\":");
        }
        else
        {
            await connection.SendChunkedJsonBodyAsync(MaxRequestBodySize + 1);
        }

        RawResponse rejection = await connection.ReadResponseAsync(headRequest: false);

        using HttpResponseMessage response = rejection.ToResponseMessage();
        await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.RequestEntityTooLarge,
            cancellationToken: cancellationToken);

        // Kestrel's message reads "Request body too large. The max request body size is 30000000 bytes."
        string body = Encoding.UTF8.GetString(rejection.Body);

        // Each check is a boolean with a fixed message, because Assert.DoesNotContain quotes the body around a match.
        Assert.False(
            body.Contains("too large", StringComparison.Ordinal),
            "Expected the 413 problem body not to contain the exception message.");
        Assert.False(
            body.Contains(MaxRequestBodySize.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "Expected the 413 problem body not to contain the request body size limit.");
        Assert.False(
            body.Contains("BadHttpRequestException", StringComparison.Ordinal),
            "Expected the 413 problem body not to contain the exception type name.");
        Assert.False(
            body.Contains("   at ", StringComparison.Ordinal),
            "Expected the 413 problem body not to contain a stack frame.");
    }

    // One header field larger than the 32 KB Kestrel accepts for all of them.
    private static string OversizedHeaderRequest(string method)
    {
        return $"{method} {ReadinessPath} HTTP/1.1\r\nHost: localhost\r\n"
            + $"X-Oversized: {new string('a', OversizedHeaderLength)}\r\n\r\n";
    }

    // A request line longer than the 8 KB Kestrel accepts.
    private static string OversizedTargetRequest(string method)
    {
        return $"{method} /{new string('a', OversizedRequestTargetLength - 1)} HTTP/1.1\r\nHost: localhost\r\n\r\n";
    }

    // A registration the API answers with a 400 after reading its whole body, which leaves the connection open: an
    // empty JSON object, which lacks every required field, framed by its Content-Length or as one chunk, or bytes that
    // begin like a HEAD request line, which are not JSON, framed by their Content-Length.
    private static string BodyBearingRegisterRequest(string scenario)
    {
        string body = scenario == HeadLikeBodyScenario ? $"HEAD {ReadinessPath} HTTP/1.1\r\n" : "{}";
        string length = body.Length.ToString(CultureInfo.InvariantCulture);
        string framedBody = scenario switch
        {
            DeclaredLengthScenario or HeadLikeBodyScenario => $"Content-Length: {length}\r\n\r\n{body}",
            ChunkedBodyScenario =>
                $"Transfer-Encoding: chunked\r\n\r\n{body.Length.ToString("X", CultureInfo.InvariantCulture)}\r\n"
                + $"{body}\r\n0\r\n\r\n",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        return $"POST /api/auth/register HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n{framedBody}";
    }

    // A header line without a colon, which Kestrel rejects as an invalid request header.
    private static string MalformedHeaderLineRequest()
    {
        return $"GET {ReadinessPath} HTTP/1.1\r\nHost: localhost\r\nNo colon in this line\r\n\r\n";
    }

    // The Production variant lists no redirect host and configures no HTTPS port, and the in-process server publishes
    // none, so outside Development a plaintext request is never redirected. It must be refused before CORS and
    // authentication: credentials, a bearer token and the readiness probe all get the same 403 problem, and the
    // bookings request never reaches the challenge.
    [Theory]
    [InlineData(LoginScenario)]
    [InlineData(BookingsWithTokenScenario)]
    [InlineData(HealthScenario)]
    public async Task PlaintextRequestOutsideDevelopment_Returns403Problem(string scenario)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebApplicationFactory<Program> production = await StartProductionVariantAsync(
            new Dictionary<string, string?>(),
            cancellationToken);
        ApiTestClient client = new(production.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));

        using HttpResponseMessage response = scenario switch
        {
            LoginScenario => await client.LoginAsync(
                new { email = "guest@example.com", password = "not-checked" },
                cancellationToken),
            BookingsWithTokenScenario => await client.ListBookingsAsync("not-a-token", cancellationToken),
            HealthScenario => await client.Http.GetAsync("/api/health", cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            cancellationToken: cancellationToken);
        Assert.True(
            string.Equals(HttpsOnlyDetail, problem.GetProperty("detail").GetString(), StringComparison.Ordinal),
            $"Expected the 403 detail to be exactly '{HttpsOnlyDetail}'.");
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Null(response.Headers.Location);
        AssertSecurityHeaders(response);
    }

    // HTTPS redirection builds its target from the request's Host header, which the client chooses. With an HTTPS port
    // configured, outside Development a plaintext request is redirected only when HttpsRedirection:AllowedHosts lists
    // its host: entries are trimmed, empty ones ignored, and the host is compared without regard to case or port. A
    // host the setting does not list, or any host when the setting is unset or blank, gets the 403 problem with no
    // Location, and the host appears nowhere in the response.
    [Theory]
    [InlineData("api.example.test", "api.example.test", "https://api.example.test/api/health")]
    [InlineData(" other.test ; ; API.Example.Test ", "api.example.test:8080", "https://api.example.test/api/health")]
    [InlineData("192.0.2.10", "192.0.2.10", "https://192.0.2.10/api/health")]
    [InlineData("[2001:db8::10]", "[2001:db8::10]:8080", "https://[2001:db8::10]/api/health")]
    [InlineData("[::ffff:192.0.2.10]", "[::ffff:192.0.2.10]:8080", "https://[::ffff:192.0.2.10]/api/health")]
    [InlineData("api.example.test", "evil.example", null)]
    [InlineData("api.example.test", "api.example.test.evil.example", null)]
    [InlineData(" ; ", "api.example.test", null)]
    [InlineData(null, "api.example.test", null)]
    public async Task PlaintextRequestOutsideDevelopment_IsRedirectedOnlyFromListedHost(
        string? allowedHosts,
        string host,
        string? expectedLocation)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebApplicationFactory<Program> production = await StartProductionVariantAsync(
            new Dictionary<string, string?>
            {
                [RedirectHostsSetting] = allowedHosts,
                ["HTTPS_PORT"] = DefaultHttpsPort,
            },
            cancellationToken);
        using HttpClient client = production.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpRequestMessage request = new(HttpMethod.Get, ReadinessPath);
        request.Headers.Host = host;
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        if (expectedLocation is not null)
        {
            Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
            Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
            AssertSecurityHeaders(response);
            return;
        }

        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            cancellationToken: cancellationToken);
        Assert.Equal(HttpsOnlyDetail, problem.GetProperty("detail").GetString());
        Assert.Null(response.Headers.Location);
        AssertSecurityHeaders(response);

        string hostName = new HostString(host).Host;
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.False(
            body.Contains(hostName, StringComparison.OrdinalIgnoreCase),
            "Expected the 403 problem body not to name the request's host.");
        Assert.False(
            response.Headers.Concat(response.Content.Headers).Any(header => header.Value.Any(
                value => value.Contains(hostName, StringComparison.OrdinalIgnoreCase))),
            "Expected no 403 response header to name the request's host.");
    }

    // An HttpsRedirection:AllowedHosts entry that is not a host alone, such as one with a wildcard, a scheme, a port or
    // a path, an IPv6 address without its brackets, or a bracketed IPv6 address with a prefix length, a zone or a port,
    // stops startup outside Development together with the other configuration failures. The failure names the setting
    // and never the value.
    [Theory]
    [InlineData("*")]
    [InlineData("*.example.test")]
    [InlineData("https://api.example.test")]
    [InlineData("api.example.test:443")]
    [InlineData("api.example.test/api")]
    [InlineData("2001:db8::10")]
    [InlineData("[2001:db8::10/64]")]
    [InlineData("[fe80::1%25eth0]")]
    [InlineData("[fe80::1%eth0]")]
    [InlineData("[2001:db8::10]:443")]
    [InlineData("api.example.test;bad host.example.test")]
    public async Task InvalidRedirectHostsOutsideDevelopment_StopsStartup(string allowedHosts)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => StartProductionVariantAsync(
                new Dictionary<string, string?> { [RedirectHostsSetting] = allowedHosts },
                cancellationToken));

        Assert.StartsWith("The API configuration is invalid.", failure.Message, StringComparison.Ordinal);
        Exception inner = Assert.Single(failure.InnerExceptions);
        Assert.IsType<InvalidOperationException>(inner);
        Assert.True(
            inner.Message.Contains(RedirectHostsSetting, StringComparison.Ordinal),
            "Expected the startup failure to name HttpsRedirection:AllowedHosts.");
        Assert.False(
            failure.Message.Contains(allowedHosts, StringComparison.Ordinal),
            "Expected the startup failure not to quote the setting's value.");
    }

    // Outside Development Kestrel serves HTTPS on the listener and with the certificate the environment names, and the
    // guard passes a request that arrives over TLS on to the pipeline. The API runs as a process of its own, because
    // Kestrel takes a certificate password from its process environment alone and the tests set no variable in theirs.
    // The client trusts the throwaway certificate and nothing else, so a served request shows that Kestrel bound TLS
    // with the PFX the environment's password opened.
    [Fact]
    public async Task HttpsRequestOutsideDevelopment_IsServed()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using ThrowawayCertificate certificate = ThrowawayCertificate.Create();
        Dictionary<string, string> environment = ProductionProcessEnvironment(HttpsLoopbackUrl);
        environment[CertificatePathVariable] = certificate.FilePath;
        environment[CertificatePasswordVariable] = certificate.Password;
        await using ApiProcess api = ApiProcess.Start(environment, [], AppContext.BaseDirectory);

        Uri? address = await api.WaitForHttpsListenerAsync(cancellationToken);
        Assert.True(
            address is not null,
            $"The API exited without listening on HTTPS; it wrote {api.DescribeRecords()}.");

        // The certificate is self-signed, so an untrusted chain is the one error accepted, and only for that
        // certificate: the address must still match a name it carries. The client connects to the loopback listener
        // directly, so TLS is negotiated with the API, never tunnelled through a proxy the environment of the host
        // running the tests names, whose NO_PROXY entry for localhost does not cover 127.0.0.1.
        bool presented = false;
        using HttpClient https = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, remote, _, errors) =>
                {
                    bool pinned = remote is not null && certificate.IsSameAs(remote);
                    presented |= pinned;
                    return pinned && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None;
                },
            },
        })
        {
            BaseAddress = address,
        };

        using HttpResponseMessage response = await https.GetAsync(ReadinessPath, cancellationToken);

        Assert.True(presented, "The API's TLS listener did not present the certificate the environment named.");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(
            string.Equals("Healthy", body, StringComparison.Ordinal),
            "Expected the readiness body to be exactly Healthy.");
        Assert.Empty(response.Headers.Server);
        AssertSecurityHeaders(response);
        AssertOutputCarriesNoSecret(api.Output, certificate.Password);
    }

    // Kestrel reads its listeners and certificates through the API's view of the Kestrel section, where a certificate
    // password comes from the environment alone. The in-memory settings stand in for appsettings.json and the command
    // line: the paths and the URL pass through, and neither password from them is answered. The passwords are a marker
    // unique to the run, and every check that involves a password is a boolean whose failure message holds no value,
    // so the outcome does not depend on the password variables of the host running the tests and none is ever printed.
    // The API process tests beside this one show the outcome on a real listener: a password from the environment opens
    // the certificate, and the same password from the command line or a JSON file stops startup.
    [Fact]
    public async Task KestrelConfiguration_TakesCertificatePasswordsOnlyFromEnvironment()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string certificatePath = "/run/tls/api.pfx";
        const string endpointUrl = "https://+:8443";
        string marker = "from-configuration-" + Guid.NewGuid().ToString("N");
        string[] certificateSections = ["Certificates:Default", "Endpoints:Https:Certificate"];
        await using WebApplicationFactory<Program> production = await StartProductionVariantAsync(
            new Dictionary<string, string?>
            {
                ["Kestrel:Certificates:Default:Path"] = certificatePath,
                ["Kestrel:Certificates:Default:Password"] = marker,
                ["Kestrel:Endpoints:Https:Url"] = endpointUrl,
                ["Kestrel:Endpoints:Https:Certificate:Path"] = certificatePath,
                ["Kestrel:Endpoints:Https:Certificate:Password"] = marker,
            },
            cancellationToken);

        KestrelConfigurationLoader? loader =
            production.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.ConfigurationLoader;
        Assert.NotNull(loader);
        IConfiguration kestrel = loader.Configuration;

        Assert.Equal(endpointUrl, kestrel["Endpoints:Https:Url"]);
        foreach (string section in certificateSections)
        {
            Assert.Equal(certificatePath, kestrel[ConfigurationPath.Combine(section, "Path")]);
            string? password = kestrel[ConfigurationPath.Combine(section, "Password")];
            Assert.False(
                string.Equals(marker, password, StringComparison.Ordinal),
                $"The certificate password under {section} came from configuration rather than the environment.");

            // Only key names reach a failure message: the environment may add a Password child, which must not carry
            // the configured value.
            IConfigurationSection[] children = [.. kestrel.GetSection(section).GetChildren()];
            Assert.Contains("Path", children.Select(static child => child.Key));
            Assert.False(
                children.Any(child => string.Equals(marker, child.Value, StringComparison.Ordinal)),
                $"A child of {section} carries the certificate password from configuration.");
        }
    }

    // A certificate password from anywhere but the environment never reaches Kestrel. The certificate and listener that
    // serve HTTPS above, with the password on the command line, or in an appsettings.Production.json beside a copy of
    // appsettings.json, leave Kestrel unable to open the PFX: the API reports HostRunFailed with the cryptographic
    // exception's type, exits with code 1 and never listens. Its output carries no password and no other secret.
    [Theory]
    [InlineData(CommandLinePasswordSource)]
    [InlineData(JsonFilePasswordSource)]
    public async Task CertificatePasswordOutsideEnvironment_StopsStartupWithoutListening(string source)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using ThrowawayCertificate certificate = ThrowawayCertificate.Create();
        Dictionary<string, string> environment = ProductionProcessEnvironment(HttpsLoopbackUrl);
        environment[CertificatePathVariable] = certificate.FilePath;

        string[] arguments = [];
        string contentRoot = AppContext.BaseDirectory;
        if (source == CommandLinePasswordSource)
        {
            arguments = ["--Kestrel:Certificates:Default:Password=" + certificate.Password];
        }
        else if (source == JsonFilePasswordSource)
        {
            contentRoot = CreateContentRootWithCertificatePassword(certificate.DirectoryPath, certificate.Password);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        await using ApiProcess api = ApiProcess.Start(environment, arguments, contentRoot);

        Uri? address = await api.WaitForHttpsListenerAsync(cancellationToken);
        Assert.True(address is null, $"The API listened on HTTPS with a certificate password from the {source}.");
        int exitCode = await api.WaitForExitAsync(cancellationToken);

        Assert.Equal(1, exitCode);
        string output = api.Output;
        Assert.True(
            output.Contains(HostRunFailedRecord, StringComparison.Ordinal),
            $"Expected the HostRunFailed event 5202, but the API wrote {api.DescribeRecords()}.");
        Assert.True(
            output.Contains(typeof(CryptographicException).FullName!, StringComparison.Ordinal),
            "HostRunFailed does not name the CryptographicException of a certificate Kestrel could not open.");
        Assert.False(
            output.Contains(ListeningRecord, StringComparison.Ordinal),
            "The API reported a listening address although its certificate could not be opened.");
        AssertOutputCarriesNoSecret(output, certificate.Password);
    }

    // Outside Development a host whose listeners are all plaintext could serve nothing, because the guard refuses every
    // request that does not arrive over HTTPS. The API therefore stops once Kestrel has bound them: it reports
    // NoHttpsListener, exits with code 1 and never reports an HTTPS address.
    [Fact]
    public async Task PlaintextOnlyListenerOutsideDevelopment_StopsStartup()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using ApiProcess api = ApiProcess.Start(
            ProductionProcessEnvironment(HttpLoopbackUrl),
            [],
            AppContext.BaseDirectory);

        int exitCode = await api.WaitForExitAsync(cancellationToken);

        Assert.Equal(1, exitCode);
        string output = api.Output;
        Assert.True(
            output.Contains(NoHttpsListenerRecord, StringComparison.Ordinal),
            $"Expected the NoHttpsListener event 5201, but the API wrote {api.DescribeRecords()}.");
        Assert.False(
            output.Contains(ListeningRecord + " https://", StringComparison.OrdinalIgnoreCase),
            "The API reported an HTTPS address although only a plaintext listener was configured.");
        AssertOutputCarriesNoSecret(output, certificatePassword: null);
    }

    // An API process inherits no variable that would configure the API or its host, whichever delimiter, prefix or
    // letter case its name carries, and keeps every variable the dotnet host and the runtime need. The environment is
    // a synthetic one with a fixed value, never that of the process running the tests.
    [Theory]
    [InlineData("Kestrel:Endpoints:Inherited:Url", false)]
    [InlineData("Kestrel__Endpoints__Inherited__Url", false)]
    [InlineData("DOTNET_Kestrel__Endpoints__Inherited__Url", false)]
    [InlineData("kestrel__certificates__default__password", false)]
    [InlineData("Logging:LogLevel:Default", false)]
    [InlineData("Logging__Console__FormatterName", false)]
    [InlineData("ConnectionStrings:SmartHotel", false)]
    [InlineData("ConnectionStrings__SmartHotel", false)]
    [InlineData("CUSTOMCONNSTR_SmartHotel", false)]
    [InlineData("Jwt:SigningKey", false)]
    [InlineData("Cors__AllowedOrigin", false)]
    [InlineData("hostBuilder:reloadConfigOnChange", false)]
    [InlineData("ASPNETCORE_ENVIRONMENT", false)]
    [InlineData("aspnetcore_forwardedheaders_enabled", false)]
    [InlineData("DOTNET_ENVIRONMENT", false)]
    [InlineData("DOTNET_URLS", false)]
    [InlineData("dotnet_urls", false)]
    [InlineData("URLS", false)]
    [InlineData("HTTPS_PORT", false)]
    [InlineData("AllowedHosts", false)]
    [InlineData("DOTNET_SHUTDOWNTIMEOUTSECONDS", false)]
    [InlineData("ASPNET_ENV", false)]
    [InlineData("DB_PASSWORD", false)]
    [InlineData("DB_HOST", false)]
    [InlineData("PATH", true)]
    [InlineData("HOME", true)]
    [InlineData("TMPDIR", true)]
    [InlineData("DOTNET_ROOT", true)]
    [InlineData("DOTNET_ROOT_X64", true)]
    [InlineData("DOTNET_ROOT(x86)", true)]
    [InlineData("DOTNET_HOST_PATH", true)]
    [InlineData("DOTNET_gcServer", true)]
    [InlineData("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", true)]
    [InlineData("LANG", true)]
    [InlineData("LC_ALL", true)]
    [InlineData("SystemRoot", true)]
    [InlineData("ProgramFiles(x86)", true)]
    [InlineData("SSL_CERT_DIR", true)]
    public void ApiProcessEnvironment_KeepsOnlyVariablesThatConfigureNothingOfTheApi(string name, bool kept)
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal) { [name] = "inherited" };

        ApiProcess.RemoveApiSettings(environment);

        Assert.Equal(kept, environment.ContainsKey(name));
    }

    // The environment of an API process outside Development that listens on the URL given: the class's database and
    // signing key, which the API takes from the environment alone, and the https web origin it requires there. The
    // database is the class's own, already migrated and seeded, so the process's start-up changes nothing in it.
    private Dictionary<string, string> ProductionProcessEnvironment(string url)
    {
        return new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_URLS"] = url,
            ["ConnectionStrings__SmartHotel"] = _factory.DatabaseConnectionString,
            ["Jwt__SigningKey"] = _factory.SigningKey,
            ["Cors__AllowedOrigin"] = HttpsWebOrigin,
        };
    }

    // A content root, in a new directory under the parent given, whose configuration files hold the certificate
    // password: the build's appsettings.json, and an appsettings.Production.json with the password under Kestrel's
    // default-certificate key.
    private static string CreateContentRootWithCertificatePassword(string parent, string password)
    {
        string contentRoot = Path.Combine(parent, "content");
        Directory.CreateDirectory(contentRoot);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(contentRoot, "appsettings.json"));

        JsonObject settings = new()
        {
            ["Kestrel"] = new JsonObject
            {
                ["Certificates"] = new JsonObject
                {
                    ["Default"] = new JsonObject { ["Password"] = password },
                },
            },
        };
        File.WriteAllText(Path.Combine(contentRoot, "appsettings.Production.json"), settings.ToJsonString());
        return contentRoot;
    }

    // An API process's output must carry none of the secrets its environment or its certificate settings held. Each
    // check is a boolean with a fixed message, so no secret reaches a failure message either.
    private void AssertOutputCarriesNoSecret(string output, string? certificatePassword)
    {
        string? databasePassword = new NpgsqlConnectionStringBuilder(_factory.DatabaseConnectionString).Password;
        Assert.False(
            output.Contains(_factory.DatabaseConnectionString, StringComparison.Ordinal),
            "The API's output carries its connection string.");
        Assert.False(
            !string.IsNullOrEmpty(databasePassword) && output.Contains(databasePassword, StringComparison.Ordinal),
            "The API's output carries the database password.");
        Assert.False(
            output.Contains(_factory.SigningKey, StringComparison.Ordinal),
            "The API's output carries the signing key.");
        Assert.False(
            certificatePassword is not null && output.Contains(certificatePassword, StringComparison.Ordinal),
            "The API's output carries the certificate password.");
    }

    // The class's host in the Production environment, on the same, already migrated and seeded database.
    private Task<WebApplicationFactory<Program>> StartProductionVariantAsync(
        Dictionary<string, string?> settings,
        CancellationToken cancellationToken)
    {
        return StartProductionVariantAsync(_factory, settings, cancellationToken);
    }

    // The source factory's host in the Production environment, derived through WithWebHostBuilder, on the source's
    // already migrated and seeded database. Outside Development the API accepts only an https web origin, so one is
    // added in memory after the factory's own, together with any further settings; no process environment variable is
    // set or read. HTTPS redirection takes its port from HTTPS_PORT or ANCM_HTTPS_PORT in the configuration, which the
    // environment of the host running the tests can supply, ASPNETCORE_HTTPS_PORT among others, before it asks the
    // server. Unless the settings name one, both are pinned empty in memory, which names no port, and the in-process
    // server publishes no addresses, so redirection never finds a port. The hosts redirection may serve, which the same
    // environment could list, are pinned unset unless the settings list them. Reading Server builds and starts the
    // host, and Task.Run keeps that synchronous start off xUnit's synchronization context.
    private static async Task<WebApplicationFactory<Program>> StartProductionVariantAsync(
        SmartHotelApiFactory source,
        Dictionary<string, string?> settings,
        CancellationToken cancellationToken)
    {
        settings[ConfigurationPath.Combine(ApiCorsOptions.SectionName, nameof(ApiCorsOptions.AllowedOrigin))] =
            HttpsWebOrigin;
        settings.TryAdd("HTTPS_PORT", "");
        settings.TryAdd("ANCM_HTTPS_PORT", "");
        settings.TryAdd(RedirectHostsSetting, null);
        WebApplicationFactory<Program> variant = source.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        });

        try
        {
            await Task.Run(() => _ = variant.Server, cancellationToken);
            return variant;
        }
        catch
        {
            await variant.DisposeAsync();
            throw;
        }
    }

    // Each case fails model binding for one reason. Its errors object must hold exactly the case's key: a query
    // parameter's name, or the body's JSON path in the contract's letter case, never the action's parameter name or
    // an empty key. The body must carry neither the marker sent in the request nor parser text.
    [Theory]
    [InlineData(SearchGuestsMarkerScenario, "guests")]
    [InlineData(LoginEmailNumberScenario, "$.email")]
    [InlineData(LoginUpperCaseEmailNumberScenario, "$.email")]
    [InlineData(LoginUnknownMemberTruncatedScenario, "$")]
    [InlineData(RegisterFullNameNumberScenario, "$.fullName")]
    [InlineData(LoginEmptyBodyScenario, "$")]
    [InlineData(BookingHotelIdStringScenario, "$.hotelId")]
    public async Task BindingFailure_Returns400KeyedByContractWithoutRequestText(
        string scenario,
        string expectedErrorKey)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ApiTestClient client = _factory.CreateApiClient();

        using HttpResponseMessage response = await SendBindingFailureAsync(client, scenario, cancellationToken);

        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            expectedErrorKey,
            cancellationToken);

        // Read only after ProblemAssert has decoded the buffered body, so an unsupported charset yields its fixed
        // message rather than an exception whose message quotes the Content-Type value.
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        // AssertProblemAsync has checked that errors is one object holding the case's key, so only the count of
        // keys, never a key from the response, can reach this failure message.
        int keyCount = problem.GetProperty("errors").EnumerateObject().Count();
        Assert.True(keyCount == 1, $"Expected the errors object to hold only the case's key but it holds {keyCount}.");

        // The default 400 detail is the fixed guest-facing sentence. It is compared as a boolean with a fixed message,
        // so a failure prints no response text.
        Assert.True(
            string.Equals(InvalidInputDetail, problem.GetProperty("detail").GetString(), StringComparison.Ordinal),
            $"Expected the 400 detail to be exactly '{InvalidInputDetail}'.");

        foreach (string disclosure in BindingDisclosures)
        {
            Assert.False(
                body.Contains(disclosure, StringComparison.Ordinal),
                $"Expected the 400 body to contain no '{disclosure}'.");
        }
    }

    // Only the input each case names is broken; the stay, the credentials and the token are otherwise valid.
    private static Task<HttpResponseMessage> SendBindingFailureAsync(
        ApiTestClient client,
        string scenario,
        CancellationToken cancellationToken)
    {
        DateOnly checkIn = ApiTestClient.TodayUtc.AddDays(30);
        DateOnly checkOut = checkIn.AddDays(2);

        return scenario switch
        {
            SearchGuestsMarkerScenario => client.SearchAsync(
                "Seattle",
                ApiTestClient.Iso(checkIn),
                ApiTestClient.Iso(checkOut),
                BindingMarker,
                cancellationToken),
            LoginEmailNumberScenario => PostRawJsonAsync(
                client,
                LoginRoute,
                $"{{\"email\":5,\"password\":\"{BindingMarker}\"}}",
                cancellationToken),
            LoginUpperCaseEmailNumberScenario => PostRawJsonAsync(
                client,
                LoginRoute,
                $"{{\"EMAIL\":5,\"password\":\"{BindingMarker}\"}}",
                cancellationToken),
            LoginUnknownMemberTruncatedScenario => PostRawJsonAsync(
                client,
                LoginRoute,
                $"{{\"{BindingMarker}\": [1,",
                cancellationToken),
            RegisterFullNameNumberScenario => PostRawJsonAsync(
                client,
                RegisterRoute,
                "{\"email\":\"a@b.test\",\"password\":\"Passw0rd!x\",\"fullName\":123}",
                cancellationToken),
            LoginEmptyBodyScenario => PostRawJsonAsync(client, LoginRoute, string.Empty, cancellationToken),
            BookingHotelIdStringScenario => CreateBookingWithStringHotelIdAsync(
                client,
                checkIn,
                checkOut,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "The scenario is not recognized."),
        };
    }

    private static async Task<HttpResponseMessage> PostRawJsonAsync(
        ApiTestClient client,
        string route,
        string json,
        CancellationToken cancellationToken)
    {
        using StringContent content = new(json, Encoding.UTF8, JsonMediaType);
        return await client.Http.PostAsync(route, content, cancellationToken);
    }

    // Authorization runs before model binding, so the request carries a guest's token to reach the input formatter.
    // Booking numbers are strict JSON numbers, so a string hotelId is a value of the wrong JSON type.
    private static async Task<HttpResponseMessage> CreateBookingWithStringHotelIdAsync(
        ApiTestClient client,
        DateOnly checkIn,
        DateOnly checkOut,
        CancellationToken cancellationToken)
    {
        ApiTestClient.GuestSession guest = await client.RegisterGuestAsync(cancellationToken);
        JsonObject booking = ApiTestClient.BookingBody(1, 1, checkIn, checkOut, 2);
        booking["hotelId"] = BindingMarker;

        return await client.CreateBookingAsync(booking, guest.Token, cancellationToken);
    }

    // checkOut equal to checkIn is a zero-night stay, which the stay rules report under checkOut.
    private static string SameDayCheckOutSearchUri()
    {
        string date = ApiTestClient.Iso(ApiTestClient.TodayUtc.AddDays(30));
        return $"/api/hotels?city=Seattle&checkIn={date}&checkOut={date}&guests=2";
    }

    // HttpClient files Allow among the content headers. The response headers are read as well, so the assertion does
    // not depend on which collection the handler put it in.
    private static List<string> ReadAllowedMethods(HttpResponseMessage response)
    {
        List<string> methods = [.. response.Content.Headers.Allow];
        if (methods.Count == 0 && response.Headers.TryGetValues("Allow", out IEnumerable<string>? values))
        {
            methods.AddRange(values.SelectMany(static value =>
                value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)));
        }

        return methods;
    }

    // The raised configuration, applied the way the host applies its own: through the logging configuration binder,
    // after appsettings.json, so its rules join the host's. The code rule and minimum level stand for a registration
    // that raises verbosity.
    private SmartHotelApiFactory CreateRaisedVerbosityVariant(
        SmartHotelApiFactory.LogCapture capture,
        Action<IServiceCollection>? additionalOverrides)
    {
        IConfiguration raised = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LogLevel:Default"] = "Trace",
                ["LogLevel:Microsoft"] = "Trace",
                ["LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["LogLevel:Microsoft.AspNetCore.*"] = "Trace",
                ["LogLevel:Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware"] = "Trace",
                ["LogLevel:Microsoft.AspNetCore.Diagnostics.DeveloperExceptionPageMiddleware"] = "Trace",
                ["LogLevel:" + KestrelCategory] = "Trace",
                [SmartHotelApiFactory.LogCapture.Alias + ":LogLevel:Default"] = "Trace",
                [SmartHotelApiFactory.LogCapture.Alias + ":LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics*"] =
                    "Trace",
                [SmartHotelApiFactory.LogCapture.Alias + ":LogLevel:" + JwtBearerCategory] = "Trace",
                [SmartHotelApiFactory.LogCapture.Alias + ":LogLevel:" + QuietedCategory] = QuietedFloor.ToString(),
                ["CaptureScopes"] = "true",
            })
            .Build();

        return _factory.CreateLogCaptureVariant(
            capture,
            [("Microsoft.AspNetCore", LogLevel.Trace)],
            services =>
            {
                services.AddLogging(logging => logging.AddConfiguration(raised));
                services.Configure<LoggerFilterOptions>(static options => options.MinLevel = LogLevel.Trace);

                additionalOverrides?.Invoke(services);
            });
    }

    // An error status is held to the problem contract through ProblemAssert, whose failure messages quote no body,
    // header or caller-supplied value; a success status is compared directly. errorKey is the field a 400 must key in
    // errors, null for a body-level 400, and bareBearerChallenge marks a 401 whose every challenge must be a bare
    // Bearer.
    private static async Task ExpectStatusAsync(
        HttpStatusCode expected,
        Task<HttpResponseMessage> sending,
        CancellationToken cancellationToken,
        string? errorKey = null,
        bool bareBearerChallenge = false)
    {
        // Awaited first, so the response is disposed even when the arguments are rejected.
        using HttpResponseMessage response = await sending;

        // A key or a challenge check the expected status cannot carry would let the case pass without checking it.
        if (errorKey is not null && expected != HttpStatusCode.BadRequest)
        {
            throw new ArgumentException(
                $"An error key applies only to a 400 problem, not to {(int)expected}.",
                nameof(errorKey));
        }

        if (bareBearerChallenge && expected != HttpStatusCode.Unauthorized)
        {
            throw new ArgumentException(
                $"A bearer challenge applies only to a 401 problem, not to {(int)expected}.",
                nameof(bareBearerChallenge));
        }

        if ((int)expected < 400)
        {
            Assert.Equal(expected, response.StatusCode);
            return;
        }

        await ProblemAssert.AssertProblemAsync(response, expected, errorKey, cancellationToken);

        if (bareBearerChallenge)
        {
            ProblemAssert.AssertBareBearerChallenge(response);
        }
    }

    // The callers' requests reach the pipeline and send no trace header, so the problem's traceId is the W3C id of the
    // request activity. The redaction withholds the hosting diagnostics category's Critical records, so hosting starts
    // that activity only for the API's listener on its activity source.
    private static async Task AssertServerErrorWithW3CTraceIdAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        JsonElement problem = await ProblemAssert.AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            cancellationToken: cancellationToken);
        string? traceId = problem.GetProperty("traceId").GetString();
        Assert.True(
            traceId is not null && W3CTraceId().IsMatch(traceId),
            "Expected the problem's traceId to be a W3C trace id.");
    }

    // A record names the database by its name and the server by the address form NpgsqlConnection.DataSource takes.
    private static void AssertNoRecordNamesTheDatabaseOrServer(
        IReadOnlyList<SmartHotelApiFactory.CapturedLog> records,
        string databaseName)
    {
        SmartHotelApiFactory.LogCapture.AssertNone(
            records,
            record => record.Message.Contains(databaseName, StringComparison.Ordinal)
                || record.Message.Contains(ServerAddressScheme, StringComparison.Ordinal),
            "record naming the database or the server");
    }

    // Every IdentityModel error message starts with its IDX code.
    [GeneratedRegex(@"IDX\d{5}", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityModelMessage();

    [GeneratedRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex W3CTraceId();

    // Kestrel's identifier for a request it rejects: its connection id, 13 characters of its base-32 alphabet, a colon,
    // and the request's number on the connection as eight upper-case hex digits.
    [GeneratedRegex("^[0-9A-V]{13}:(?<request>[0-9A-F]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex KestrelTraceId();

    // Reads a comma-separated response header as its trimmed items; the list is empty when the header is absent.
    private static List<string> ReadHeaderItems(HttpResponseMessage response, string name)
    {
        List<string> items = [];
        if (response.Headers.TryGetValues(name, out IEnumerable<string>? values))
        {
            items.AddRange(values.SelectMany(static value =>
                value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)));
        }

        return items;
    }

    // Each field every API response carries must appear exactly once, with its fixed value.
    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        foreach ((string name, string value) in SecurityHeaders)
        {
            Assert.True(
                response.Headers.TryGetValues(name, out IEnumerable<string>? values),
                $"Expected the {(int)response.StatusCode} response to carry {name}.");
            Assert.Equal(value, Assert.Single(values));
        }
    }

    // The same fields on a response read from the wire, where a repeated field fails GetHeader.
    private static void AssertSecurityHeaders(RawResponse response)
    {
        foreach ((string name, string value) in SecurityHeaders)
        {
            Assert.Equal(value, response.GetHeader(name));
        }
    }

    // Throws while routing selects the endpoint for a request that carries the fault header, and leaves every other
    // request's candidates untouched. It applies to every endpoint, so any routed path reaches it.
    private sealed class ThrowingMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
    {
        public const string FaultHeader = "X-Test-Routing-Fault";

        public override int Order => 0;

        public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
        {
            return true;
        }

        public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
        {
            if (httpContext.Request.Headers.ContainsKey(FaultHeader))
            {
                throw new InvalidOperationException(SecretMarker);
            }

            return Task.CompletedTask;
        }
    }

    // Records each exception the exception handler middleware offers it and declines every one, so the middleware still
    // writes the generic problem. Requests may run on any thread, so the record is a concurrent queue.
    private sealed class RecordingExceptionHandler : IExceptionHandler
    {
        private readonly ConcurrentQueue<Exception> _exceptions = new();

        public IReadOnlyList<Exception> Exceptions => [.. _exceptions];

        public IReadOnlyList<Type> ExceptionTypes => [.. _exceptions.Select(static exception => exception.GetType())];

        public ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            _exceptions.Enqueue(exception);
            return ValueTask.FromResult(false);
        }
    }

    // Delegates to the system clock until armed, then throws an exception with the given message on every read and
    // keeps each exception it throws, so a test can identify the exception that reached the handler. Arm and Disarm may
    // run on another thread than the request that reads the clock, so the flag is read and written with volatile
    // semantics, and the record is a concurrent queue.
    private sealed class ThrowingTimeProvider(string message) : TimeProvider
    {
        private readonly ConcurrentQueue<InvalidOperationException> _thrown = new();
        private int _armed;

        public IReadOnlyList<InvalidOperationException> ThrownExceptions => [.. _thrown];

        public void Arm()
        {
            Volatile.Write(ref _armed, 1);
        }

        public void Disarm()
        {
            Volatile.Write(ref _armed, 0);
        }

        // Inside a TimeProvider subclass the simple name System is the inherited static property, not the namespace,
        // so the system clock is always reached through the type name.
        public override DateTimeOffset GetUtcNow()
        {
            if (Volatile.Read(ref _armed) == 1)
            {
                InvalidOperationException fault = new(message);
                _thrown.Enqueue(fault);
                throw fault;
            }

            return TimeProvider.System.GetUtcNow();
        }
    }

    // A hosted service whose start fails with the given message, as the server's start does when it cannot bind.
    private sealed class FailingStartup(string message) : IHostedService
    {
        private int _started;

        public bool Started => Volatile.Read(ref _started) == 1;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Volatile.Write(ref _started, 1);
            throw new InvalidOperationException(message);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    // A startup filter whose pipeline configuration fails with the given message, as building the request pipeline does
    // when a middleware or its registration throws.
    private sealed class FailingPipeline(string message) : IStartupFilter
    {
        private int _configured;

        public bool Configured => Volatile.Read(ref _configured) == 1;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return _ =>
            {
                Volatile.Write(ref _configured, 1);
                throw new InvalidOperationException(message);
            };
        }
    }

    // Hands out the factory's own credentials until armed and afterwards a connection string that names a database
    // absent from the same server. The API resolves the string once per scope, so every request after Arm connects
    // to the absent database.
    private sealed class DatabaseRedirect
    {
        private int _armed;

        private bool IsArmed => Volatile.Read(ref _armed) == 1;

        public void Arm()
        {
            Volatile.Write(ref _armed, 1);
        }

        // Runs as a service override, after the factory has registered its own secret source as an instance.
        public void Install(IServiceCollection services)
        {
            ServiceDescriptor registered = services.Last(
                static descriptor => descriptor.ServiceType == typeof(ISecretSource));
            ISecretSource inner = registered.ImplementationInstance as ISecretSource
                ?? throw new InvalidOperationException("The factory's secret source is not a registered instance.");
            string connectionString = inner.DatabaseConnectionString
                ?? throw new InvalidOperationException("The factory's secret source has no connection string.");

            NpgsqlConnectionStringBuilder absent = new(connectionString);
            absent.Database += AbsentDatabaseSuffix;

            services.RemoveAll<ISecretSource>();
            services.AddSingleton<ISecretSource>(new RedirectingSecretSource(inner, absent.ConnectionString, this));
        }

        // A class rather than a record: a record's synthesized ToString would print the credentials.
        private sealed class RedirectingSecretSource(
            ISecretSource inner,
            string absentDatabaseConnectionString,
            DatabaseRedirect redirect) : ISecretSource
        {
            public string? DatabaseConnectionString =>
                redirect.IsArmed ? absentDatabaseConnectionString : inner.DatabaseConnectionString;

            public string? JwtSigningKey => inner.JwtSigningKey;
        }
    }

    // A client connection that sends requests byte for byte, so it can send what HttpClient refuses to build, and parses
    // each response itself. Every read and write observes the test's token and a timeout of its own.
    private sealed class RawHttpConnection : IDisposable
    {
        private const int SpaceChunkLength = 64 * 1024;

        private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(30);

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly CancellationTokenSource _timeout;

        // Bytes received but not yet parsed are _buffer[_start.._end].
        private byte[] _buffer = new byte[8 * 1024];
        private int _start;
        private int _end;

        private RawHttpConnection(TcpClient client, CancellationTokenSource timeout)
        {
            _client = client;
            _stream = client.GetStream();
            _timeout = timeout;
        }

        public static async Task<RawHttpConnection> OpenAsync(Uri address, CancellationToken cancellationToken)
        {
            CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ExchangeTimeout);
            TcpClient client = new();
            try
            {
                await client.ConnectAsync(address.Host, address.Port, timeout.Token);
                return new RawHttpConnection(client, timeout);
            }
            catch
            {
                client.Dispose();
                timeout.Dispose();
                throw;
            }
        }

        public async Task SendAsync(string text)
        {
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(text), _timeout.Token);
        }

        // Sends a chunked JSON body that is an opening brace followed by white space, which the JSON reader keeps
        // consuming, until the body is exactly `length` bytes long. The last chunk is never sent: the server must
        // reject the body once it passes its limit, and every byte is written before the server can read the last one,
        // so no write meets a connection the server has closed.
        public async Task SendChunkedJsonBodyAsync(long length)
        {
            await SendAsync("1\r\n{\r\n");

            byte[] fullChunk = SpaceChunk(SpaceChunkLength);
            for (long remaining = length - 1; remaining > 0; remaining -= SpaceChunkLength)
            {
                byte[] chunk = remaining >= SpaceChunkLength ? fullChunk : SpaceChunk((int)remaining);
                await _stream.WriteAsync(chunk, _timeout.Token);
            }
        }

        // Reads one response: its status line, its header fields up to the blank line and, unless it answers HEAD, the
        // body its Content-Length or chunked framing delimits.
        public async Task<RawResponse> ReadResponseAsync(bool headRequest)
        {
            string[] statusLine = (await ReadLineAsync()).Split(' ', 3);
            if (statusLine.Length < 2
                || !statusLine[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
                || !int.TryParse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture, out int status))
            {
                throw new InvalidDataException("The response does not start with an HTTP/1.x status line.");
            }

            List<KeyValuePair<string, string>> headers = [];
            for (string line = await ReadLineAsync(); line.Length > 0; line = await ReadLineAsync())
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    throw new InvalidDataException("The response has a header line without a field name.");
                }

                headers.Add(new KeyValuePair<string, string>(line[..colon], line[(colon + 1)..].Trim()));
            }

            RawResponse head = new(status, headers, []);
            if (headRequest)
            {
                return head;
            }

            byte[] body = head.IsChunked
                ? await ReadChunkedBodyAsync()
                : head.ContentLength is long length
                    ? await ReadExactAsync(checked((int)length))
                    : throw new InvalidDataException("The response declares neither a Content-Length nor chunks.");

            return new RawResponse(status, headers, body);
        }

        // Reads until the server closes the connection and counts the bytes that arrived after the parsed responses.
        public async Task<int> CountRemainingBytesAsync()
        {
            int count = _end - _start;
            _start = _end;

            byte[] scratch = new byte[4 * 1024];
            int read;
            while ((read = await _stream.ReadAsync(scratch, _timeout.Token)) > 0)
            {
                count += read;
            }

            return count;
        }

        public void Dispose()
        {
            _client.Dispose();
            _timeout.Dispose();
        }

        private static byte[] SpaceChunk(int size)
        {
            byte[] sizeLine = Encoding.ASCII.GetBytes(size.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
            byte[] chunk = new byte[sizeLine.Length + size + 2];
            sizeLine.CopyTo(chunk, 0);
            chunk.AsSpan(sizeLine.Length, size).Fill((byte)' ');
            "\r\n"u8.CopyTo(chunk.AsSpan(sizeLine.Length + size));
            return chunk;
        }

        private async Task<byte[]> ReadChunkedBodyAsync()
        {
            using MemoryStream body = new();
            while (true)
            {
                string sizeLine = await ReadLineAsync();
                int extension = sizeLine.IndexOf(';', StringComparison.Ordinal);
                string sizeText = (extension < 0 ? sizeLine : sizeLine[..extension]).Trim();
                if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int size))
                {
                    throw new InvalidDataException("The response has a malformed chunk size.");
                }

                if (size == 0)
                {
                    break;
                }

                byte[] data = await ReadExactAsync(size);
                body.Write(data, 0, data.Length);
                if ((await ReadLineAsync()).Length != 0)
                {
                    throw new InvalidDataException("A chunk of the response is not followed by a line end.");
                }
            }

            // Trailer fields, if any, end with a blank line like the header fields.
            string trailer;
            do
            {
                trailer = await ReadLineAsync();
            }
            while (trailer.Length > 0);

            return body.ToArray();
        }

        private async Task<string> ReadLineAsync()
        {
            while (true)
            {
                int length = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n"u8);
                if (length >= 0)
                {
                    string line = Encoding.Latin1.GetString(_buffer, _start, length);
                    _start += length + 2;
                    return line;
                }

                if (!await FillAsync())
                {
                    throw new InvalidDataException("The connection closed before the end of a response line.");
                }
            }
        }

        private async Task<byte[]> ReadExactAsync(int count)
        {
            byte[] bytes = new byte[count];
            int buffered = Math.Min(count, _end - _start);
            Buffer.BlockCopy(_buffer, _start, bytes, 0, buffered);
            _start += buffered;

            if (buffered < count)
            {
                await _stream.ReadExactlyAsync(bytes.AsMemory(buffered), _timeout.Token);
            }

            return bytes;
        }

        // Moves the unparsed bytes to the front, grows a full buffer, and appends what the next read returns. False
        // means the server closed the connection.
        private async Task<bool> FillAsync()
        {
            int unparsed = _end - _start;
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, unparsed);
            _start = 0;
            _end = unparsed;
            if (_end == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            int read = await _stream.ReadAsync(_buffer.AsMemory(_end), _timeout.Token);
            _end += read;
            return read > 0;
        }
    }

    // A response as the server sent it: the status code, the header fields in their order, and the body with its framing
    // removed.
    private sealed class RawResponse(int status, IReadOnlyList<KeyValuePair<string, string>> headers, byte[] body)
    {
        public int Status { get; } = status;

        public byte[] Body { get; } = body;

        public long? ContentLength => GetHeader("Content-Length") is string value
            ? long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)
            : null;

        public bool IsChunked => GetHeader("Transfer-Encoding") is string value
            && value.Contains("chunked", StringComparison.OrdinalIgnoreCase);

        // The value of a field the response may carry only once, or null when it is absent.
        public string? GetHeader(string name)
        {
            string[] values =
            [
                .. headers
                    .Where(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                    .Select(static header => header.Value),
            ];

            return values.Length switch
            {
                0 => null,
                1 => values[0],
                _ => throw new InvalidDataException($"The response repeats the {name} field."),
            };
        }

        // The response as HttpClient would surface it, so the problem assertions apply unchanged.
        public HttpResponseMessage ToResponseMessage()
        {
            ByteArrayContent content = new(Body);
            if (GetHeader("Content-Type") is string contentType)
            {
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            return new HttpResponseMessage((HttpStatusCode)Status) { Content = content };
        }
    }

    // The API as a process of its own, run by the dotnet host from the test output directory. This is test
    // infrastructure, not a way to run the product, which runs only under Compose: it exists because Kestrel takes a
    // certificate password from its process environment alone, and the tests set no variable in their own process.
    // The process's environment is that of the process running the tests, less every variable that would configure
    // the API or its host in any key form, plus the variables a test supplies. Standard output and error are read line
    // by line as they arrive, so a full pipe never stalls the API, and kept for checks only: they can quote settings,
    // so a failure message names event ids and exit codes, never the output. Disposing asks for the process and all
    // its descendants to be killed, then waits up to KillTimeout, 30 seconds, for the API process itself to exit, and
    // throws a TimeoutException when it has not. That exit ends the API's use of the class's database before the
    // fixture drops it. The wait observes the API process alone, not any process it started.
    private sealed partial class ApiProcess : IAsyncDisposable
    {
        private const string ApiAssemblyFileName = "SmartHotel.Api.dll";

        // How long the API may take to listen or to exit on its own, and to exit once it is killed.
        private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(30);

        // The prefixes the host's configuration strips from the variables it reads: every DOTNET_ and ASPNETCORE_
        // variable is host configuration under the rest of its name, as well as application configuration whole.
        private const string DotnetPrefix = "DOTNET_";
        private const string AspNetCorePrefix = "ASPNETCORE_";

        // The flat keys ASP.NET Core 8 reads from configuration to set up the API: the generic and web hosts' settings,
        // host filtering's, the forwarded headers switch and the HTTPS redirection port, and the legacy environment
        // name the web host reads from the environment.
        private static readonly string[] FlatSettingNames =
        [
            "ENVIRONMENT", "ASPNET_ENV", "APPLICATIONNAME", "CONTENTROOT", "WEBROOT", "STARTUPASSEMBLY", "URLS",
            "HTTP_PORTS", "HTTPS_PORTS", "HTTPS_PORT", "ANCM_HTTPS_PORT", "PREFERHOSTINGURLS", "ALLOWEDHOSTS",
            "FORWARDEDHEADERS_ENABLED", "HOSTINGSTARTUPASSEMBLIES", "HOSTINGSTARTUPEXCLUDEASSEMBLIES",
            "PREVENTHOSTINGSTARTUP", "CAPTURESTARTUPERRORS", "DETAILEDERRORS", "SUPPRESSSTATUSMESSAGES",
            "STATICWEBASSETS", "SHUTDOWNTIMEOUTSECONDS", "STARTUPTIMEOUTSECONDS", "SERVICESSTARTCONCURRENTLY",
            "SERVICESSTOPCONCURRENTLY",
        ];

        // The database settings the secret source reads from the environment by name, and the prefixes the environment
        // configuration provider maps into the ConnectionStrings section.
        private static readonly string[] FlatSettingPrefixes =
            ["DB_", "CUSTOMCONNSTR_", "MYSQLCONNSTR_", "SQLAZURECONNSTR_", "SQLCONNSTR_"];

        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly TaskCompletionSource<Uri> _httpsAddress =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ApiProcess(Process process)
        {
            _process = process;
            _process.OutputDataReceived += OnLineReceived;
            _process.ErrorDataReceived += OnLineReceived;
        }

        // Every line read so far, standard output and error interleaved. It must never reach a failure message.
        public string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        // Starts the API with the arguments given and the working directory, which is its content root.
        public static ApiProcess Start(
            IReadOnlyDictionary<string, string> environment,
            IEnumerable<string> arguments,
            string workingDirectory)
        {
            ProcessStartInfo startInfo = new(FindDotnetHost())
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };

            // One item per argument and no shell, so a path with spaces reaches the host intact.
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, ApiAssemblyFileName));
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            RemoveApiSettings(startInfo.Environment);
            foreach ((string name, string value) in environment)
            {
                startInfo.Environment[name] = value;
            }

            Process process = new() { StartInfo = startInfo };
            ApiProcess api = new(process);
            try
            {
                process.Start();
            }
            catch
            {
                process.Dispose();
                throw;
            }

            // The API reads nothing from standard input; closing it keeps the process off the test runner's.
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return api;
        }

        // The first HTTPS address the API reports listening on, or null once it has exited without reporting one. The
        // wait for the exit also waits for the end of the output, so an address reported just before an exit is seen.
        public async Task<Uri?> WaitForHttpsListenerAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(StartTimeout);
            Task exited = _process.WaitForExitAsync(deadline.Token);
            await Task.WhenAny(_httpsAddress.Task, exited);

            if (_httpsAddress.Task.IsCompletedSuccessfully)
            {
                // Ends the exit wait, which nothing awaits any longer.
                await deadline.CancelAsync();
                return await _httpsAddress.Task;
            }

            if (exited.IsCompletedSuccessfully)
            {
                return null;
            }

            // The runner's cancellation stays a cancellation; only the deadline becomes a failure.
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The API process neither listened on HTTPS nor exited within a minute.");
        }

        // The API's exit code, once it has exited on its own and its output has ended.
        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(StartTimeout);
            try
            {
                await _process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The API process did not exit within a minute.");
            }

            return _process.ExitCode;
        }

        // The event ids of the console records read so far, the only part of the output a failure message may name.
        public string DescribeRecords()
        {
            string[] eventIds =
            [
                .. ConsoleRecord().Matches(Output).Select(static record => record.Groups["id"].Value).Distinct(),
            ];

            return eventIds.Length == 0
                ? "no console record"
                : "console records with event ids " + string.Join(", ", eventIds);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (_process.HasExited)
                {
                    // The API process has exited on its own, so it needs no stopping.
                }

                // Deliberately not the test's token: the process must be gone even when the test was cancelled, so the
                // wait has a bound of its own.
                using CancellationTokenSource deadline = new(KillTimeout);
                try
                {
                    await _process.WaitForExitAsync(deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("The API process did not exit within 30 seconds of being killed.");
                }
            }
            finally
            {
                _process.Dispose();
            }
        }

        // Removes from an inherited environment every variable that would configure the API or its host, in any of the
        // forms the environment configuration provider reads, and keeps the rest, which the dotnet host and the runtime
        // need: PATH, HOME, TMPDIR, the DOTNET_ROOT variables, the locale, the runtime's switches and the system
        // variables, none of which on Windows holds a delimiter. On macOS __CF_USER_TEXT_ENCODING goes, which only
        // CoreFoundation's default text encoding reads, falling back without it.
        public static void RemoveApiSettings(IDictionary<string, string?> environment)
        {
            foreach (string name in environment.Keys.Where(IsApiSetting).ToList())
            {
                environment.Remove(name);
            }
        }

        // The environment configuration provider reads a name as a key whose sections a double underscore or a colon
        // delimits, and the host's providers also read it less a DOTNET_ or ASPNETCORE_ prefix. A name whose key has a
        // section therefore configures some part of the API, whatever the section and whichever delimiter or prefix it
        // carries, and so does every ASPNETCORE_ name; a flat name does when it is a host setting or a database
        // setting, with or without a DOTNET_ prefix. Prefixes and configuration keys match ignoring case, as Windows
        // environment names do, so names are compared ignoring case.
        private static bool IsApiSetting(string name)
        {
            string key = name.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
            if (key.Contains(ConfigurationPath.KeyDelimiter, StringComparison.Ordinal)
                || key.StartsWith(AspNetCorePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (key.StartsWith(DotnetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                key = key[DotnetPrefix.Length..];
            }

            return FlatSettingNames.Contains(key, StringComparer.OrdinalIgnoreCase)
                || FlatSettingPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        // The dotnet host that runs the tests: dotnet test names it in DOTNET_HOST_PATH, a test process the host
        // started directly is that host, and otherwise it is at the root of the installation whose runtime runs the
        // tests, three levels above that runtime's directory, shared/Microsoft.NETCore.App/<version>.
        private static string FindDotnetHost()
        {
            string hostFileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

            string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath))
            {
                return hostPath;
            }

            string? processPath = Environment.ProcessPath;
            if (processPath is not null
                && string.Equals(Path.GetFileName(processPath), hostFileName, StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }

            string runtimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
            DirectoryInfo? installation = new DirectoryInfo(runtimeDirectory).Parent?.Parent?.Parent;
            string? installedHost = installation is null ? null : Path.Combine(installation.FullName, hostFileName);
            if (installedHost is not null && File.Exists(installedHost))
            {
                return installedHost;
            }

            throw new InvalidOperationException("The dotnet host that runs the tests could not be found.");
        }

        // The simple console formatter's first line of a record, "<level>: <category>[<event id>]".
        [GeneratedRegex(
            @"^(?:trce|dbug|info|warn|fail|crit): [^\s\[]+\[(?<id>\d+)\]",
            RegexOptions.Multiline | RegexOptions.CultureInvariant)]
        private static partial Regex ConsoleRecord();

        // The hosting lifetime's report of an HTTPS address the server listens on.
        [GeneratedRegex(@"Now listening on: (?<address>https://\S+)", RegexOptions.CultureInvariant)]
        private static partial Regex HttpsListeningReport();

        private void OnLineReceived(object sender, DataReceivedEventArgs line)
        {
            // Null marks the end of the stream.
            if (line.Data is null)
            {
                return;
            }

            lock (_output)
            {
                _output.Append(line.Data).Append('\n');
            }

            Match report = HttpsListeningReport().Match(line.Data);
            if (report.Success
                && Uri.TryCreate(report.Groups["address"].Value, UriKind.Absolute, out Uri? address))
            {
                _httpsAddress.TrySetResult(address);
            }
        }
    }

    // A self-signed TLS server certificate for localhost and 127.0.0.1, valid for a day and made for one test. It is
    // written as a PKCS #12 file under a random password into a new directory of its own under the temporary path,
    // which on Unix only the current user can read. Disposing deletes the directory and everything in it.
    private sealed class ThrowawayCertificate : IDisposable
    {
        private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

        private readonly byte[] _rawData;

        private ThrowawayCertificate(string directoryPath, string filePath, string password, byte[] rawData)
        {
            DirectoryPath = directoryPath;
            FilePath = filePath;
            Password = password;
            _rawData = rawData;
        }

        public string DirectoryPath { get; }

        public string FilePath { get; }

        public string Password { get; }

        public static ThrowawayCertificate Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), "smarthotel-tls-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directoryPath);
            }
            else
            {
                Directory.CreateDirectory(
                    directoryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            try
            {
                using RSA key = RSA.Create(2048);
                CertificateRequest request = new(
                    "CN=localhost",
                    key,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                SubjectAlternativeNameBuilder names = new();
                names.AddDnsName("localhost");
                names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    [new Oid(ServerAuthenticationOid)],
                    critical: false));

                DateTimeOffset now = TimeProvider.System.GetUtcNow();
                using X509Certificate2 certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
                string password = RandomNumberGenerator.GetHexString(32, lowercase: true);
                string filePath = Path.Combine(directoryPath, "api.pfx");

                FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (FileStream file = new(filePath, options))
                {
                    file.Write(certificate.Export(X509ContentType.Pkcs12, password));
                }

                return new ThrowawayCertificate(directoryPath, filePath, password, certificate.RawData);
            }
            catch
            {
                Directory.Delete(directoryPath, recursive: true);
                throw;
            }
        }

        // Whether a presented certificate is this one, byte for byte.
        public bool IsSameAs(X509Certificate presented)
        {
            return presented.GetRawCertData().AsSpan().SequenceEqual(_rawData);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}