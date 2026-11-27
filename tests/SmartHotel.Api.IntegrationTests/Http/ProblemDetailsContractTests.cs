using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
public sealed class ProblemDetailsContractTests : IClassFixture<SmartHotelApiFactory>
{
    private const string ProblemMediaType = "application/problem+json";
    private const string JsonMediaType = "application/json";
    private const string UnknownRoutePath = "/api/does-not-exist";
    private const string LoginRoute = "api/auth/login";
    private const string RegisterRoute = "api/auth/register";
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