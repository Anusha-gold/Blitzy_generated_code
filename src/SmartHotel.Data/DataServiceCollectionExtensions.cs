using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace SmartHotel.Data;

/// <summary>
/// Registers the SmartHotel data layer with dependency injection.
/// </summary>
public static class DataServiceCollectionExtensions
{
    // Npgsql and its EF Core provider read this switch once per process, at their first use, and cache it. Setting it
    // in code after that has no effect, so each host sets it through its runtime configuration (runtimeconfig.json).
    private const string DisableDateTimeInfinityConversionsSwitch = "Npgsql.DisableDateTimeInfinityConversions";

    // A failure Npgsql classes as transient, such as a pooled connection that a database restart ended (SqlState
    // 57P01) or a refused connection, is retried at most this many times. EF Core retries the first time at once and
    // the second after about 1 s, and every later delay is capped, so a request that meets a database that stays down
    // still receives its 500 after about 3 s of waiting.
    private const int TransientFailureMaxRetryCount = 3;

    private static readonly TimeSpan TransientFailureMaxRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Registers <see cref="SmartHotelDbContext"/> on the Npgsql provider, with its connection string resolved each
    /// time the context options are built rather than at registration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The context and its options keep the default scoped lifetimes, so the connection string is read once per scope
    /// from the services the host finally built. A registration that replaces the connection string's source after
    /// this call, as the integration tests do with the secret source, therefore decides which database the context
    /// reaches. A pooled context or a context factory would build the options once at the root, which is why neither
    /// is registered.
    /// </para>
    /// <para>
    /// Npgsql's retrying execution strategy is enabled. An operation that fails with an error Npgsql classes as
    /// transient, such as a pooled connection that a database restart ended (SqlState <c>57P01</c>) or a refused or
    /// dropped connection, runs again up to three times, at once and then after delays capped at two seconds, so a
    /// request that meets a restarted database succeeds and one that meets a database that stays down fails within a
    /// few seconds. Constraint violations are not transient, so they reach their callers on the first failure. The
    /// strategy rejects a transaction begun outside it, so booking creation and seeding each run their transaction
    /// through <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.CreateExecutionStrategy"/> as one
    /// retriable unit. A write the strategy would otherwise replay on its own after a commit whose acknowledgement was
    /// lost, a guest's registration or a booking's cancellation, runs through it the same way, with a check that
    /// recognizes the committed write, so the replay never reports its own row as a conflict. Sensitive-data logging
    /// and detailed errors stay off, so EF Core's command logs show parameter placeholders rather than values.
    /// </para>
    /// <para>
    /// The connection string is checked before the provider receives it. Npgsql's <c>Include Error Detail</c> would
    /// copy PostgreSQL's <c>DETAIL</c> text, which can quote key values such as a guest's email address, into exception
    /// messages, and its <c>Log Parameters</c> would write parameter values into Npgsql's command logs. A string that
    /// enables either setting is therefore refused, as is a string Npgsql cannot parse.
    /// </para>
    /// <para>
    /// EF Core's failure events are ignored, so a database failure that reaches the host leaves one Error record, the
    /// host's sanitized exception record, and an outcome the API translates from a constraint, such as a registration
    /// race on <c>UX_Guests_Email</c> or a booking conflict on <c>EX_Bookings_Room_NoOverlap</c>, leaves none.
    /// The query and save failure events, <see cref="CoreEventId.QueryIterationFailed"/>,
    /// <see cref="CoreEventId.SaveChangesFailed"/>, <see cref="RelationalEventId.NonQueryOperationFailed"/>,
    /// <see cref="RelationalEventId.ExecuteDeleteFailed"/>, <see cref="RelationalEventId.ExecuteUpdateFailed"/>,
    /// <see cref="RelationalEventId.BatchExecutorFailedToRollbackToSavepoint"/> and
    /// <see cref="RelationalEventId.BatchExecutorFailedToReleaseSavepoint"/>, would log the original exception, whose
    /// message can quote database values. <see cref="CoreEventId.ExecutionStrategyRetrying"/> would copy each transient
    /// exception, its message included, into its text. <see cref="RelationalEventId.CommandError"/>,
    /// <see cref="RelationalEventId.ConnectionError"/> and <see cref="RelationalEventId.TransactionError"/> are Error
    /// records written before the caller sees the exception, without its trace identifier, and the connection record
    /// names the database and the server from the connection string. Only their logging stops; the exceptions still
    /// propagate. A transient failure that a retry overcomes therefore leaves no record. EF Core's Debug lifecycle
    /// records that name the database and the server from the connection string are ignored as well, whatever level
    /// the logging configuration sets for their categories, because a level is not a redaction boundary:
    /// <see cref="RelationalEventId.ConnectionOpening"/>, <see cref="RelationalEventId.ConnectionOpened"/>,
    /// <see cref="RelationalEventId.ConnectionClosing"/>, <see cref="RelationalEventId.ConnectionClosed"/>,
    /// <see cref="RelationalEventId.ConnectionDisposing"/>, <see cref="RelationalEventId.ConnectionDisposed"/>,
    /// <see cref="RelationalEventId.DataReaderClosing"/>, <see cref="RelationalEventId.DataReaderDisposing"/> and
    /// <see cref="RelationalEventId.MigrateUsingConnection"/>. The other connection and command records are kept.
    /// </para>
    /// <para>
    /// <see cref="RelationalEventId.MigrationsNotApplied"/> is ignored as well. A host that migrates on every start
    /// finds the database already up to date on all but the first, and records that outcome itself, so the event would
    /// only repeat it. The migration still runs; only that record is dropped.
    /// </para>
    /// <para>
    /// Building the context options first requires the <c>Npgsql.DisableDateTimeInfinityConversions</c> switch to be
    /// enabled in the host's runtime configuration. Without it Npgsql writes <see cref="DateOnly.MinValue"/> and
    /// <see cref="DateOnly.MaxValue"/> as <c>-infinity</c> and <c>infinity</c>, so a stay ending 9999-12-31 would be
    /// stored and compared as infinity. Npgsql reads the switch once, at its first use in the process, so only the
    /// runtime configuration sets it reliably. The switch is checked whenever the options are built, never at
    /// registration, so registering reads nothing from the host's configuration. The check runs before the connection
    /// string is resolved and before any Npgsql type is used, and throws <see cref="InvalidOperationException"/>,
    /// naming the switch, when it is not enabled. A host without the switch therefore fails the first time it resolves
    /// the context, before any connection is opened, instead of storing infinite dates.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add the data services to.</param>
    /// <param name="connectionString">
    /// Returns the Npgsql connection string from the application's services. It is invoked whenever the context
    /// options are built, once per scope, and never during registration. It runs only after the options' check of the
    /// <c>Npgsql.DisableDateTimeInfinityConversions</c> switch passes, so a host without the switch fails before the
    /// function is called. Its resolution throws <see cref="InvalidOperationException"/>, with a fixed message that
    /// quotes no part of the string, when the result is <see langword="null"/>, empty or whitespace, is not a valid
    /// Npgsql connection string, or enables Npgsql's <c>Include Error Detail</c> or <c>Log Parameters</c> under any of
    /// their keywords. Any other string reaches the provider unchanged.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="connectionString"/> is <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddSmartHotelData(
        this IServiceCollection services,
        Func<IServiceProvider, string?> connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionString);

        services.AddDbContext<SmartHotelDbContext>((serviceProvider, options) =>
        {
            // The switch is part of the host's runtime configuration, so it is checked when the options are built,
            // after the host is, and never at registration. The check comes before the connection string is resolved
            // and before any Npgsql type is touched.
            EnsureFiniteDateConversions();

            string? value = connectionString(serviceProvider);
            if (string.IsNullOrWhiteSpace(value))
            {
                // The message is fixed and never quotes the value, because a connection string can carry a password.
                throw new InvalidOperationException("The SmartHotel database connection string is not configured.");
            }

            EnsureDatabaseValuesStayRedacted(value);

            // The check only reads the string; Npgsql receives it exactly as it was supplied.
            options
                .UseNpgsql(value, npgsql => npgsql.EnableRetryOnFailure(
                    TransientFailureMaxRetryCount,
                    TransientFailureMaxRetryDelay,
                    errorCodesToAdd: null))
                // EF Core writes each of these failure events before the caller sees the exception. Some hand the
                // logger the original exception, whose message can quote database values, and the retry record
                // copies the transient exception into its text. The command, connection and transaction records are
                // Error records of their own, so a constraint violation the API translates to 409 would still count as
                // an error, and the connection record names the database and the server. With all of them ignored,
                // the host's sanitized exception record is the only record of a failure. Ignoring them changes logging
                // only: the exception still reaches the caller. The Debug connection, data reader and migration
                // lifecycle records listed after them name the database and the server from the connection string,
                // so they are ignored too, whatever level the logging configuration admits for their categories.
                .ConfigureWarnings(warnings => warnings.Ignore(
                    CoreEventId.QueryIterationFailed,
                    CoreEventId.SaveChangesFailed,
                    CoreEventId.ExecutionStrategyRetrying,
                    RelationalEventId.CommandError,
                    RelationalEventId.ConnectionError,
                    RelationalEventId.TransactionError,
                    RelationalEventId.NonQueryOperationFailed,
                    RelationalEventId.ExecuteDeleteFailed,
                    RelationalEventId.ExecuteUpdateFailed,
                    RelationalEventId.BatchExecutorFailedToRollbackToSavepoint,
                    RelationalEventId.BatchExecutorFailedToReleaseSavepoint,
                    RelationalEventId.ConnectionOpening,
                    RelationalEventId.ConnectionOpened,
                    RelationalEventId.ConnectionClosing,
                    RelationalEventId.ConnectionClosed,
                    RelationalEventId.ConnectionDisposing,
                    RelationalEventId.ConnectionDisposed,
                    RelationalEventId.DataReaderClosing,
                    RelationalEventId.DataReaderDisposing,
                    RelationalEventId.MigrateUsingConnection,
                    // Not a failure: migrating an up-to-date database is routine on every start after the first, and
                    // the host records that outcome itself, so this record would only repeat it.
                    RelationalEventId.MigrationsNotApplied));
        });

        return services;
    }

    /// <summary>
    /// Ensures Npgsql keeps the calendar extremes finite in this process: <see cref="DateOnly.MaxValue"/> is sent and
    /// stored as the date 9999-12-31, and <see cref="DateOnly.MinValue"/> as 0001-01-01, never as <c>infinity</c> or
    /// <c>-infinity</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The process does not have the <c>Npgsql.DisableDateTimeInfinityConversions</c> switch enabled; each host enables
    /// it in its runtime configuration.
    /// </exception>
    internal static void EnsureFiniteDateConversions()
    {
        if (AppContext.TryGetSwitch(DisableDateTimeInfinityConversionsSwitch, out bool enabled) && enabled)
        {
            return;
        }

        throw new InvalidOperationException(
            "The " + DisableDateTimeInfinityConversionsSwitch + " switch must be enabled in the host's runtime "
            + "configuration (a RuntimeHostConfigurationOption written to runtimeconfig.json), so that the calendar "
            + "extremes are stored as finite dates rather than as infinity.");
    }

    // Npgsql's Include Error Detail copies PostgreSQL's DETAIL text, which can quote key values, into exception
    // messages, and its Log Parameters writes parameter values into Npgsql's command logs, so a string enabling either
    // is refused. The builder's typed properties also cover each setting's alternative keywords.
    private static void EnsureDatabaseValuesStayRedacted(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Npgsql's message and inner exception can quote part of the string, the password included, so neither is
            // carried into the replacement.
            throw new InvalidOperationException(
                "The SmartHotel database connection string is not a valid Npgsql connection string.");
        }

        if (builder.IncludeErrorDetail)
        {
            throw new InvalidOperationException(
                "The SmartHotel database connection string must not enable Include Error Detail, because PostgreSQL "
                + "error details can contain key values.");
        }

        if (builder.LogParameters)
        {
            throw new InvalidOperationException(
                "The SmartHotel database connection string must not enable Log Parameters, because parameter values "
                + "would then be logged.");
        }
    }
}