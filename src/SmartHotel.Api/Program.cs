using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;

using SmartHotel.Api.Common.Security;
using SmartHotel.Api.Infrastructure;
using SmartHotel.Api.Modules.Auth;
using SmartHotel.Api.Modules.Availability;
using SmartHotel.Api.Modules.Bookings;
using SmartHotel.Api.Modules.Hotels;
using SmartHotel.Data;

// Nothing here reads configuration or resolves a service before builder.Build(). Every configuration-dependent
// setting resolves lazily through DI, so the registrations a test host adds while the host is built (a replacement
// ISecretSource or TimeProvider, in-memory Cors:AllowedOrigin, the Development environment) are in place before
// anything reads them.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// By default Kestrel reads its listeners and certificates from the Kestrel section of the merged configuration, which
// would also take a certificate password from appsettings*.json, user secrets or the command line. Secrets come only
// from the environment, so Kestrel gets a configuration of its own: the framework's provider for the Kestrel__
// environment variables, then a view of the merged section that answers every key except a certificate password, so
// every other key keeps its usual precedence. A password therefore comes from Kestrel__Certificates__Default__Password
// or its endpoint forms alone, and no application code looks it up. The delegate runs when Kestrel's options are
// created after Build(), replaces the default configuration loader, and leaves loading the certificate to Kestrel.
builder.WebHost.ConfigureKestrel(static (context, kestrel) => kestrel.Configure(
    KestrelConfigurationView.Create(context.Configuration),
    reloadOnChange: true));

// The one clock: today's UTC date, CreatedAt and the token times all come from it.
builder.Services.AddSingleton(TimeProvider.System);

// Secrets come only from the process environment; a test host registers its own source after this one.
builder.Services.AddSingleton<ISecretSource, EnvironmentSecretSource>();

// The signing key is overwritten from the secret source unconditionally, even with null, so a key placed in any
// configuration source is ignored and validation fails when the environment supplies none.
builder.Services
    .AddOptions<JwtOptions>()
    .BindConfiguration(JwtOptions.SectionName)
    .PostConfigure<ISecretSource>(static (options, secrets) => options.SigningKey = secrets.JwtSigningKey);
builder.Services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
builder.Services
    .AddOptions<ApiCorsOptions>()
    .BindConfiguration(ApiCorsOptions.SectionName);
builder.Services.AddSingleton<IValidateOptions<ApiCorsOptions>, ApiCorsOptionsValidator>();

builder.Services.AddSmartHotelData(
    static serviceProvider => serviceProvider.GetRequiredService<ISecretSource>().DatabaseConnectionString);

// The Hotels and Bookings services inject the scoped SmartHotelDbContext and AvailabilityQueries, so the host also
// registers both in this collection, through AddSmartHotelData above and AddAvailabilityModule below. These calls may
// come in any order, because no registration resolves a service: both need only be registered by the time the
// provider is built and resolves the service.
builder.Services
    .AddAvailabilityModule()
    .AddAuthModule()
    .AddHotelsModule()
    .AddBookingsModule();

// ProblemDetailsService uses the first writer whose CanWrite is true, and AddControllers registers a writer of its
// own. AddApiProblemDetails puts the API's always-JSON writer at the front; calling it first keeps that order plain.
builder.Services.AddApiProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// The metadata provider keys validation errors for body properties by their camelCase JSON names. No XML formatter
// is added and ReturnHttpNotAcceptable stays false, so every problem negotiates to application/problem+json, even
// for Accept: application/xml. The enum converter has no naming policy: statuses are written "Confirmed" and
// "Cancelled" exactly.
//
// A binding 400 echoes nothing the client sent and names nothing of the implementation. The JSON formatter keeps
// System.Text.Json's exception messages, which quote .NET type names and payload positions, out of model state; the
// binding messages that would quote an attempted value or a member name are fixed sentences; and the response for
// invalid model state rebuilds the errors object under the JSON paths the body's contract defines. It is configured
// after AddControllers, whose own ApiBehaviorOptions setup installs MVC's default response.
builder.Services
    .AddControllers(static options =>
    {
        options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider());
        ProblemDetailsSetup.UseFixedBindingMessages(options.ModelBindingMessageProvider);
    })
    .AddJsonOptions(static options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.AllowInputFormatterExceptionMessages = false;
    })
    .ConfigureApiBehaviorOptions(static options =>
        options.InvalidModelStateResponseFactory = ProblemDetailsSetup.CreateInvalidModelStateResponse);

// The single-origin policy is built from the validated options when the policy provider first reads them, after
// Build(). It allows no credentials, and browsers may reuse a preflight for ApiCorsOptions.PreflightMaxAgeSeconds.
builder.Services.AddCors();
builder.Services
    .AddOptions<CorsOptions>()
    .Configure<IOptions<ApiCorsOptions>>(static (cors, api) =>
    {
        CorsPolicy policy = new CorsPolicyBuilder()
            .WithMethods(HttpMethods.Get, HttpMethods.Post, HttpMethods.Delete)
            .WithHeaders(HeaderNames.Authorization, HeaderNames.ContentType)
            .SetPreflightMaxAge(TimeSpan.FromSeconds(ApiCorsOptions.PreflightMaxAgeSeconds))
            .Build();

        // WithOrigins is not used: it rebuilds an IPv6 origin through Uri, which writes an address that embeds an
        // IPv4 address with a dotted tail no browser sends. The validated value is already the browser's serialized
        // origin, so it is added as is and matched ordinally against the Origin header.
        policy.Origins.Add(api.Value.AllowedOrigin!);
        cors.AddPolicy(ApiCorsOptions.PolicyName, policy);
    });

// The default challenge is kept: a bodyless 401 with WWW-Authenticate: Bearer, which status-code pages turn into a
// problem without touching the header.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<ISecretSource, IOptions<JwtOptions>>(static (bearer, secrets, jwtOptions) =>
    {
        // Reading Value runs JwtOptionsValidator first, so a missing or short key fails before key bytes are built.
        JwtOptions jwt = jwtOptions.Value;

        // Claims keep their JWT names, so the guest id is read from sub.
        bearer.MapInboundClaims = false;

        // The challenge stays a bare WWW-Authenticate: Bearer, so a rejected token's issuer, audience or lifetime
        // values are never echoed in error or error_description parameters.
        bearer.IncludeErrorDetails = false;

        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,

            // The audience must match the configured value exactly: IdentityModel would otherwise also accept it with
            // a trailing slash added.
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secrets.JwtSigningKey!)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.Zero,
            NameClaimType = JwtRegisteredClaimNames.Name,
        };

        // A token that passes every check above but carries no valid guest id in sub fails authentication here, with a
        // fixed reason that holds no claim value. Authorization then challenges with the standard 401 before MVC binds
        // the body, so such a token never reaches an action that reads the caller's id.
        bearer.Events = new JwtBearerEvents
        {
            OnTokenValidated = static context =>
            {
                if (context.Principal is null || !context.Principal.TryGetGuestId(out _))
                {
                    context.Fail("The token carries no valid guest identifier.");
                }

                return Task.CompletedTask;
            },
        };
    });

// No fallback policy: only the [Authorize] bookings endpoints require a token.
builder.Services.AddAuthorization();

WebApplication app = builder.Build();

// Building the database context's options reads the connection string, which runs the secret source's database
// checks, and then runs the data layer's own connection-string checks (parseable as Npgsql, Include Error Detail and
// Log Parameters off) without opening a connection; those options are scoped, so a scope that lives only for the check
// builds them. Reading Value runs the options validation. All of it runs after any test registrations and before any
// database work. Every check runs even when an earlier one fails, so one start names every missing or invalid setting
// before the process stops with a non-zero exit. These failures are not handled below with the database work: their
// messages name settings only and never contain a secret, so they are the actionable diagnostic.
List<Exception> startupFailures = [];
CollectStartupFailure(startupFailures, () =>
{
    using IServiceScope optionsScope = app.Services.CreateScope();
    return optionsScope.ServiceProvider.GetRequiredService<DbContextOptions<SmartHotelDbContext>>();
});
CollectStartupFailure(startupFailures, () => app.Services.GetRequiredService<IOptions<JwtOptions>>().Value);
CollectStartupFailure(startupFailures, () => app.Services.GetRequiredService<IOptions<ApiCorsOptions>>().Value);
if (startupFailures.Count > 0)
{
    throw new AggregateException("The API configuration is invalid.", startupFailures);
}

static void CollectStartupFailure(List<Exception> failures, Func<object?> read)
{
    try
    {
        _ = read();
    }
    catch (InvalidOperationException exception)
    {
        failures.Add(exception);
    }
    catch (OptionsValidationException exception)
    {
        failures.Add(exception);
    }
}

// The exception handler is the first middleware added here, so it catches an exception before the Development
// exception page that WebApplication wraps around the pipeline, and no stack trace reaches the response.
app.UseExceptionHandler();
app.UseStatusCodePages();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();

    // UseHttpsRedirection only logs a warning and passes a request on when it finds no HTTPS port to redirect to, so a
    // plaintext request that reaches this point is one it could not redirect. It is refused here, before CORS and
    // authentication, so a password or a bearer token sent over cleartext is never processed. (With several HTTPS
    // ports and no HTTPS_PORT setting, redirection throws instead, and the exception handler answers 500 before
    // anything else runs.) The writer adds the shared problem defaults; nothing from the request is copied into the
    // body.
    app.Use(static async (context, next) =>
    {
        if (context.Request.IsHttps)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Detail = "This API accepts HTTPS requests only." },
        });
    });

    // Kestrel publishes the addresses it bound once it listens. With no HTTPS address among them the guard above would
    // refuse every request, so the process stops with exit code 1 and names the settings to supply, never their
    // values. A server without network listeners, such as the in-process test server, publishes no addresses and is
    // not checked.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        ICollection<string>? addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null
            || addresses.Count == 0
            || addresses.Any(static address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        app.Logger.LogCritical(
            new EventId(5201, "NoHttpsListener"),
            "Outside Development the API serves HTTPS only, but Kestrel bound no HTTPS address, so the API is "
            + "stopping. Set ASPNETCORE_HTTPS_PORTS or an https:// entry in ASPNETCORE_URLS, and the certificate "
            + "through Kestrel__Certificates__Default__Path and Kestrel__Certificates__Default__Password.");
        Environment.ExitCode = 1;
        app.Lifetime.StopApplication();
    });
}

app.UseCors(ApiCorsOptions.PolicyName);
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// The readiness probe matches GET only, so any other method gets routing's 405 as a problem.
app.MapGet("/api/health", static () => Results.Text("Healthy", "text/plain"));

// Migrate, then seed, before the server listens: once the probe answers, the database is ready. The host's console
// lifetime handles SIGINT, SIGQUIT and SIGTERM only once app.Run() starts it, so until then these registrations turn
// such a signal into a stop requested through the host, which cancels that work instead of exiting abruptly: the
// migration's and the seed's open transactions roll back. The lifetime is resolved once, because app.Lifetime
// resolves it from the host's services each time and app.Run() disposes them.
IHostApplicationLifetime lifetime = app.Lifetime;
using CancellationTokenSource initializationStop =
    CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);

// Like the console lifetime's handler: the signal does not end the process, and the host is asked to stop instead,
// which cancels the work through the linked token. The runtime can run this handler for a signal received while it was
// registered even after the registration is disposed, so the stop goes to the host, which honours it at any point of
// startup, rather than to a token only the work watches.
void StopInitialization(PosixSignalContext context)
{
    context.Cancel = true;
    lifetime.StopApplication();
}

try
{
    using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, StopInitialization);
    using PosixSignalRegistration quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, StopInitialization);
    using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, StopInitialization);

    await DatabaseInitializer.InitializeAsync(app.Services, initializationStop.Token);
}
catch (OperationCanceledException) when (initializationStop.IsCancellationRequested)
{
    DatabaseInitializer.ReportCanceled(app.Services);
    await app.DisposeAsync();
    return 1;
}
catch (Exception exception)
{
    // The runtime's report of an exception escaping the entry point would print its message and those of its inner
    // exceptions, which can name the database host, the role or the database. The failed host is disposed instead,
    // which also flushes the logger, and the process exits non-zero without ever listening.
    DatabaseInitializer.ReportFailure(app.Services, exception);
    await app.DisposeAsync();
    return 1;
}

// The registrations are disposed by now. A stop they received as the last step completed had nothing left to cancel,
// so it is honoured here: the server never starts listening after a stop.
if (initializationStop.IsCancellationRequested)
{
    DatabaseInitializer.ReportCanceled(app.Services);
    await app.DisposeAsync();
    return 1;
}

// An exception that escapes Run, most often a listener or certificate Kestrel cannot set up while starting, such as a
// certificate whose password came from a JSON file or the command line and was therefore ignored, would reach the
// runtime, which prints it with its message. The host's own "Hosting failed to start" record carries the message too,
// and appsettings.json turns that category off. Instead one Critical event names the exception types and the settings
// to check, never a message or a value, and the process exits with code 1. Run has already disposed the host by then,
// and the console logger writes the event directly once its background queue is closed.
try
{
    app.Run();
}
catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
{
    // A stop requested after the check above and before the host started, such as one from a signal handler the
    // runtime ran late, cancels the start: app.Run() throws before the server listens. A stop requested once the host
    // has started shuts it down gracefully instead, and app.Run() returns. app.Run() has already disposed the host and
    // its logging, so nothing can be logged here; the process exits non-zero, as after any other canceled startup.
    return 1;
}
catch (Exception exception)
{
    app.Logger.LogCritical(
        new EventId(5202, "HostRunFailed"),
        "The API host failed and the API is exiting with code 1. Exception types: {ExceptionTypes}. Their messages are "
        + "not logged, because they can hold secrets. A failure at start-up most often means a listener or certificate "
        + "setting is missing or wrong: check ASPNETCORE_URLS or ASPNETCORE_HTTPS_PORTS, and "
        + "Kestrel__Certificates__Default__Path and Kestrel__Certificates__Default__Password, which Kestrel reads only "
        + "from the environment.",
        DescribeExceptionTypes(exception));
    return 1;
}

// A normal shutdown exits with code 0. Outside Development the HTTPS listener check stops a host that bound no
// HTTPS address and sets the exit code to 1 first, so that code is returned instead.
return Environment.ExitCode;

// The type names of the exception and of the exceptions inside it, breadth-first over InnerException and the items of
// an AggregateException, each distinct exception once and at most 32 of them. Only the types are read, because an
// exception's message and data can hold secrets.
static string[] DescribeExceptionTypes(Exception exception)
{
    const int maxTypes = 32;
    List<string> types = [];
    HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
    Queue<Exception> pending = new();
    pending.Enqueue(exception);
    while (pending.Count > 0 && types.Count < maxTypes)
    {
        Exception current = pending.Dequeue();
        if (!visited.Add(current))
        {
            continue;
        }

        Type type = current.GetType();
        types.Add(type.FullName ?? type.Name);
        if (current is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                pending.Enqueue(inner);
            }
        }
        else if (current.InnerException is not null)
        {
            pending.Enqueue(current.InnerException);
        }
    }

    return [.. types];
}

/// <summary>
/// The view of the merged <c>Kestrel</c> configuration section that hides its certificate passwords, a key whose last
/// segment is <c>Password</c>, such as <c>Certificates:Default:Password</c> or
/// <c>Endpoints:&lt;name&gt;:Certificate:Password</c>. <see cref="Create"/> places it after an environment-variables
/// provider for the <c>Kestrel__</c> variables in the configuration Kestrel's configuration loader reads. The view
/// answers every other key from the merged section when it is asked for, so those keys keep their usual precedence and
/// follow reloads. It declines a password without looking anything up, so the framework's environment provider alone
/// answers it: a value from a JSON file, user secrets or the command line is ignored, an unset variable means no
/// password, and no application code reads the value.
/// </summary>
file sealed class KestrelConfigurationView : IConfigurationProvider
{
    private const string SectionName = "Kestrel";
    private const string PasswordSegment = "Password";

    private readonly IConfigurationSection _kestrel;

    private KestrelConfigurationView(IConfigurationSection kestrel)
    {
        _kestrel = kestrel;
    }

    /// <summary>
    /// Creates the configuration Kestrel reads: the <c>Kestrel__</c> environment variables, then this view of the
    /// <c>Kestrel</c> section of <paramref name="configuration"/>.
    /// </summary>
    /// <param name="configuration">The application's merged configuration.</param>
    /// <returns>
    /// A configuration whose keys are relative to the <c>Kestrel</c> section, whose certificate passwords come only
    /// from the environment and whose reload token follows <paramref name="configuration"/>'s.
    /// </returns>
    public static IConfiguration Create(IConfiguration configuration)
    {
        // A configuration root asks its providers last to first, and the view answers every key but a password, so a
        // password alone reaches the environment provider. That provider strips the "Kestrel:" prefix from the
        // normalized variable names: Kestrel__Certificates__Default__Password becomes Certificates:Default:Password.
        // The root loads both providers, merges their child keys and relays the view's reload token.
        return new ConfigurationRoot(
        [
            new EnvironmentVariablesConfigurationProvider(SectionName + ConfigurationPath.KeyDelimiter),
            new KestrelConfigurationView(configuration.GetSection(SectionName)),
        ]);
    }

    /// <summary>
    /// Answers every key but a password with the merged section's value, an absent one included, so the root never asks
    /// the environment provider for such a key and provider precedence is that of the merged configuration. A password
    /// is declined without a lookup, so the root falls through to the environment provider.
    /// </summary>
    /// <param name="key">The key, relative to the <c>Kestrel</c> section.</param>
    /// <param name="value">
    /// The merged section's value for <paramref name="key"/>, or <see langword="null"/> for a password.
    /// </param>
    /// <returns><see langword="false"/> for a password; otherwise <see langword="true"/>.</returns>
    public bool TryGet(string key, out string? value)
    {
        if (IsPassword(key))
        {
            value = null;
            return false;
        }

        value = _kestrel[key];
        return true;
    }

    /// <inheritdoc />
    public void Set(string key, string? value)
    {
        throw new NotSupportedException("The Kestrel configuration view is read-only.");
    }

    /// <summary>
    /// Returns the merged configuration's reload token, so Kestrel's reload-on-change follows changes to its sources.
    /// </summary>
    /// <returns>The reload token of the merged <c>Kestrel</c> section.</returns>
    public IChangeToken GetReloadToken()
    {
        return _kestrel.GetReloadToken();
    }

    /// <summary>
    /// Loads nothing: the view holds no data of its own and reads the merged section whenever a key is asked for.
    /// </summary>
    public void Load()
    {
        // Every answer comes from the merged section at the time it is asked for, so there is no snapshot to refresh.
    }

    /// <summary>
    /// Adds the merged section's child keys under <paramref name="parentPath"/> to <paramref name="earlierKeys"/>,
    /// which hold the environment provider's. A password leaf is left out by its key name alone, so a password the
    /// environment does not supply is absent altogether; a key named <c>Password</c> that holds a section, such as an
    /// endpoint of that name, is kept, and its own keys follow these rules. No value is read.
    /// </summary>
    /// <param name="earlierKeys">The child keys the providers ahead of this one returned.</param>
    /// <param name="parentPath">
    /// The parent's path relative to the <c>Kestrel</c> section, or <see langword="null"/> for its top level.
    /// </param>
    /// <returns>The earlier keys and this view's keys, sorted as configuration keys.</returns>
    public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
    {
        IConfiguration parent = parentPath is null ? _kestrel : _kestrel.GetSection(parentPath);

        List<string> keys = [.. earlierKeys];
        foreach (IConfigurationSection child in parent.GetChildren())
        {
            if (!IsPassword(child.Key) || child.GetChildren().Any())
            {
                keys.Add(child.Key);
            }
        }

        keys.Sort(ConfigurationKeyComparer.Instance);
        return keys;
    }

    private static bool IsPassword(string key)
    {
        return string.Equals(ConfigurationPath.GetSectionKey(key), PasswordSegment, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The API's entry-point type. It is public so the integration tests can host the API with
/// <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;