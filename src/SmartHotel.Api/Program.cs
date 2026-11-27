using System.Text;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.Options;
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

// Availability comes first: the Hotels and Bookings services consume its scoped queries.
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
// Build(). It allows no credentials.
builder.Services.AddCors();
builder.Services
    .AddOptions<CorsOptions>()
    .Configure<IOptions<ApiCorsOptions>>(static (cors, api) => cors.AddPolicy(
        ApiCorsOptions.PolicyName,
        policy => policy
            .WithOrigins(api.Value.AllowedOrigin!)
            .WithMethods(HttpMethods.Get, HttpMethods.Post, HttpMethods.Delete)
            .WithHeaders(HeaderNames.Authorization, HeaderNames.ContentType)));

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

// Reading the connection string runs the secret source's database checks and reading Value runs the options
// validation, after any test registrations and before any database work. Every check runs even when an earlier one
// fails, so one start names every missing or invalid setting before the process stops with a non-zero exit. The
// messages name settings only and never contain a secret.
List<Exception> startupFailures = [];
CollectStartupFailure(startupFailures, () => app.Services.GetRequiredService<ISecretSource>().DatabaseConnectionString);
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
}

app.UseCors(ApiCorsOptions.PolicyName);
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// The readiness probe matches GET only, so any other method gets routing's 405 as a problem.
app.MapGet("/api/health", static () => Results.Text("Healthy", "text/plain"));

// Migrate, then seed, before the server listens: once the probe answers, the database is ready.
await DatabaseInitializer.InitializeAsync(app.Services, CancellationToken.None);

app.Run();

/// <summary>
/// The API's entry-point type. It is public so the integration tests can host the API with
/// <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;