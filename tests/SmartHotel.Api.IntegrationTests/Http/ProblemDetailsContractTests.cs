using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SmartHotel.Api.IntegrationTests.Infrastructure;

namespace SmartHotel.Api.IntegrationTests.Http;

/// <summary>
/// The error contract for responses the framework produces rather than an action: routing's 405 and 404, requests
/// whose <c>Accept</c> header admits no JSON, and an unhandled exception. Each must still be an
/// <c>application/problem+json</c> document that keeps its protocol headers and carries no exception data.
/// </summary>
public sealed class ProblemDetailsContractTests : IClassFixture<SmartHotelApiFactory>
{
    private const string ProblemMediaType = "application/problem+json";
    private const string UnknownRoutePath = "/api/does-not-exist";
    private const string SecretMarker = "secret-marker";

    private const string InvalidDateSearchScenario = "invalid-date-search";
    private const string BookingsWithoutTokenScenario = "bookings-without-token";
    private const string UnknownRouteScenario = "unknown-route";

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
        Assert.Equal(ProblemMediaType, response.Content.Headers.ContentType?.MediaType);

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

        // The dates come from the real clock, so the query is valid and only the controller's clock read fails.
        string checkIn = ApiTestClient.Iso(ApiTestClient.TodayUtc.AddDays(30));
        string checkOut = ApiTestClient.Iso(ApiTestClient.TodayUtc.AddDays(32));

        clock.Arm();
        try
        {
            using HttpResponseMessage response = await client.SearchAsync(
                "Seattle",
                checkIn,
                checkOut,
                "2",
                cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            await ProblemAssert.AssertProblemAsync(
                response,
                HttpStatusCode.InternalServerError,
                cancellationToken: cancellationToken);

            Assert.DoesNotContain(SecretMarker, body, StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(ThrowingTimeProvider), body, StringComparison.Ordinal);
            Assert.DoesNotContain("GetUtcNow", body, StringComparison.Ordinal);
        }
        finally
        {
            // Disarmed before the variant is disposed, so the host's shutdown never meets a throwing clock.
            clock.Disarm();
        }
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
}