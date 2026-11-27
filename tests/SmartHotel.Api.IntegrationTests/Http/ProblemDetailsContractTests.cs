using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using SmartHotel.Api.Infrastructure;
using SmartHotel.Api.IntegrationTests.Infrastructure;

namespace SmartHotel.Api.IntegrationTests.Http;

/// <summary>
/// The error contract for responses the framework produces rather than an action: routing's 405 and 404, requests
/// whose <c>Accept</c> header admits no JSON, an unhandled exception, the 400 for input that fails model binding,
/// and a plaintext request refused outside Development. Each must still be an <c>application/problem+json</c>
/// document that keeps its protocol headers and carries no exception data. A binding 400 also keys its errors only
/// by contract names and JSON paths, and echoes no request text.
/// </summary>
/// <remarks>
/// The server's own rejections, of a malformed request before any middleware runs and of a body over its size limit,
/// are sent over TCP to the fixture's loopback listener, because the in-process server parses no request bytes.
/// </remarks>
public sealed class ProblemDetailsContractTests : IClassFixture<SmartHotelApiFactory>
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

    private const string LoginScenario = "login";
    private const string BookingsWithTokenScenario = "bookings-with-token";
    private const string HealthScenario = "health";

    private const string HttpsOnlyDetail = "This API accepts HTTPS requests only.";
    private const string HttpsWebOrigin = "https://localhost:4200";

    private const string SearchGuestsMarkerScenario = "search-guests-marker";
    private const string LoginEmailNumberScenario = "login-email-number";
    private const string LoginUpperCaseEmailNumberScenario = "login-upper-case-email-number";
    private const string LoginUnknownMemberTruncatedScenario = "login-unknown-member-truncated";
    private const string RegisterFullNameNumberScenario = "register-full-name-number";
    private const string LoginEmptyBodyScenario = "login-empty-body";
    private const string BookingHotelIdStringScenario = "booking-hotel-id-string";

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

        Assert.DoesNotContain(SecretMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(ThrowingMatcherPolicy), body, StringComparison.Ordinal);

        Type handled = Assert.Single(recorder.ExceptionTypes);
        Assert.Equal(typeof(InvalidOperationException), handled);
    }

    // The exception handler is the first middleware of the API's pipeline. A startup filter's middleware runs ahead of
    // everything Program adds, so the API must register none: its handling of the server's rejections lives in the
    // connection pipeline instead.
    [Fact]
    public void Api_RegistersNoStartupFilterAheadOfExceptionHandler()
    {
        IEnumerable<IStartupFilter> filters = _factory.Services.GetServices<IStartupFilter>();

        Assert.DoesNotContain(filters, static filter => filter.GetType().Assembly == typeof(Program).Assembly);
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
    // the clock, so the request reaches an unhandled exception whose message is the marker.
    [Fact]
    public async Task UnhandledException_Returns500WithoutDetails()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        ThrowingTimeProvider clock = new();
        await using SmartHotelApiFactory variant = new(_postgres)
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
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
        }
        finally
        {
            // Disarmed before the variant is disposed, so the host's shutdown never meets a throwing clock.
            clock.Disarm();
        }
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
            Assert.Matches($"^[0-9A-V]{{13}}:{requestNumber}$", problem.GetProperty("traceId").GetString());
        }

        Assert.Equal("close", rejection.GetHeader("Connection"));
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
        Assert.Equal("Healthy", await servedResponse.Content.ReadAsStringAsync(cancellationToken));

        await connection.SendAsync(MalformedHeaderLineRequest());
        RawResponse rejection = await connection.ReadResponseAsync(headRequest: false);

        using HttpResponseMessage response = rejection.ToResponseMessage();
        await ProblemAssert.AssertProblemAsync(response, HttpStatusCode.BadRequest, cancellationToken: cancellationToken);
    }

    // An ordinary client's successful request through the listening server is answered exactly as without it.
    [Fact]
    public async Task ReadinessProbe_ThroughListeningServer_ReturnsHealthy()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Uri address = await _factory.StartLoopbackListenerAsync(cancellationToken);

        using HttpClient http = new() { BaseAddress = address };
        using HttpResponseMessage response = await http.GetAsync(ReadinessPath, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(cancellationToken));
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
        Assert.DoesNotContain("too large", body, StringComparison.Ordinal);
        Assert.DoesNotContain(MaxRequestBodySize.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
        Assert.DoesNotContain("BadHttpRequestException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
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

    // The Production variant configures no HTTPS port and the in-process server publishes none, so outside Development
    // HTTPS redirection cannot redirect a plaintext request and passes it on. It must then be refused before CORS and
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
        Assert.Equal(HttpsOnlyDetail, problem.GetProperty("detail").GetString());
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Null(response.Headers.Location);
    }

    // The same Production host serves a request that arrives over HTTPS: the guard passes it on to the pipeline.
    [Fact]
    public async Task HttpsRequestOutsideDevelopment_IsServed()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebApplicationFactory<Program> production = await StartProductionVariantAsync(
            new Dictionary<string, string?>(),
            cancellationToken);
        HttpClient https = production.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });

        using HttpResponseMessage response = await https.GetAsync("/api/health", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(cancellationToken));
    }

    // Kestrel reads its listeners and certificates through the API's view of the Kestrel section, where a certificate
    // password comes from the environment alone. The in-memory settings stand in for appsettings.json and the command
    // line: the paths and the URL pass through, and neither password from them is answered. The passwords are a marker
    // unique to the run, and every check that involves a password is a boolean whose failure message holds no value,
    // so the outcome does not depend on the password variables of the host running the tests and none is ever printed.
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

    // The class's host in the Production environment, on the same, already migrated and seeded database. Outside
    // Development the API accepts only an https web origin, so one is added in memory after the factory's own, together
    // with any further settings; no process environment variable is set or read. HTTPS redirection takes its port from
    // HTTPS_PORT or ANCM_HTTPS_PORT in the configuration, which the environment of the host running the tests can
    // supply, ASPNETCORE_HTTPS_PORT among others, before it asks the server. Both are pinned empty in memory, which
    // names no port, and the in-process server publishes no addresses, so redirection never finds a port. Reading
    // Server builds and starts the host, and Task.Run keeps that synchronous start off xUnit's synchronization context.
    private async Task<WebApplicationFactory<Program>> StartProductionVariantAsync(
        Dictionary<string, string?> settings,
        CancellationToken cancellationToken)
    {
        settings[ConfigurationPath.Combine(ApiCorsOptions.SectionName, nameof(ApiCorsOptions.AllowedOrigin))] =
            HttpsWebOrigin;
        settings["HTTPS_PORT"] = "";
        settings["ANCM_HTTPS_PORT"] = "";
        WebApplicationFactory<Program> variant = _factory.WithWebHostBuilder(builder =>
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

    // Records the type of each exception the exception handler middleware offers it and declines every one, so the
    // middleware still writes the generic problem. Requests may run on any thread, so the record is a concurrent queue.
    private sealed class RecordingExceptionHandler : IExceptionHandler
    {
        private readonly ConcurrentQueue<Type> _exceptionTypes = new();

        public IReadOnlyList<Type> ExceptionTypes => [.. _exceptionTypes];

        public ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            _exceptionTypes.Enqueue(exception.GetType());
            return ValueTask.FromResult(false);
        }
    }

    // Delegates to the system clock until armed, then throws on every read. Arm and Disarm may run on another thread
    // than the request that reads the clock, so the flag is read and written with volatile semantics.
    private sealed class ThrowingTimeProvider : TimeProvider
    {
        private int _armed;

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
                throw new InvalidOperationException(SecretMarker);
            }

            return TimeProvider.System.GetUtcNow();
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
}