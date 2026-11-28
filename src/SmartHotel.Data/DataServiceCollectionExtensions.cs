using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

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
    // 57P01) or a refused connection, is retried at most this many times, so an operation makes at most four attempts.
    // The count and the delay cap below bound the retries, not how long an operation takes; see AddSmartHotelData.
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
    /// dropped connection, runs again up to three times, so it makes at most four attempts. EF Core starts the first
    /// retry at once, the second after about one second and the third after two seconds, the cap on every delay. The
    /// retry count and the delay cap bound only the waiting between attempts, about three seconds in all, not how long
    /// an operation takes. Each attempt adds the time its own work takes. The connection string's <c>Timeout</c> and
    /// <c>Command Timeout</c>, which these options do not override, are factors in that time, not caps on it: Npgsql
    /// applies the first to opening a connection and the second to each wait for the server rather than to a command as
    /// a whole, and an attempt can run several commands. Cancelling the caller's token stops a pending delay and,
    /// because EF Core checks the token before each attempt, keeps a new attempt from starting. EF Core also passes the
    /// token to each attempt, whose work stops only where that work observes it, so an attempt already running can
    /// still complete. A retry therefore overcomes a database restart only when an attempt the strategy still allows
    /// can connect and complete. Otherwise the operation fails: after a fourth transient failure with
    /// <see cref="RetryLimitExceededException"/>, whose inner exception is that failure; with the first failure that is
    /// not transient, rethrown unchanged; or with <see cref="OperationCanceledException"/> when cancellation stops the
    /// strategy. A session failure that Npgsql reports through its disposed-transaction exception is judged by that
    /// exception's inner failure and checked and retried the same way. That happens when a session ends while EF Core
    /// releases the savepoint around a save inside a transaction: EF Core does not report the failure, and the commit
    /// that follows throws <see cref="ObjectDisposedException"/> with the session's failure as its inner exception.
    /// Constraint violations are not transient, so they reach their callers on the first failure. The strategy rejects
    /// a transaction begun outside it, so booking creation and seeding each run their transaction through
    /// <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.CreateExecutionStrategy"/> as one
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
    /// <para>
    /// The Entity Framework Core design-time tools are the one exception to resolving the connection string. While
    /// <c>dotnet ef</c> builds the host, <see cref="EF.IsDesignTime"/> is <see langword="true"/>, and the options are
    /// configured after the switch check without calling <paramref name="connectionString"/>: the provider receives the
    /// same retrying execution strategy and the same ignored events, but no connection string. The tools read these
    /// options only to discover the context type, create the context through <see cref="DesignTimeDbContextFactory"/>,
    /// and connect, when a command needs a database, with the connection string of their <c>--connection</c> option or
    /// the factory's placeholder. <c>dotnet ef</c> therefore needs none of the host's secrets or database settings,
    /// while a host running the application resolves and checks its connection string as described above.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add the data services to.</param>
    /// <param name="connectionString">
    /// Returns the Npgsql connection string from the application's services. It is invoked whenever the context
    /// options are built, once per scope, and never during registration, nor while the Entity Framework Core
    /// design-time tools build them (<see cref="EF.IsDesignTime"/> is <see langword="true"/>), so <c>dotnet ef</c>
    /// needs none of the secrets the function reads. It runs only after the options' check of the
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

            if (EF.IsDesignTime)
            {
                // The design-time tools (dotnet ef) build the host only to discover the context type from these
                // options. They create the context through DesignTimeDbContextFactory, whose placeholder connection
                // string their --connection option replaces, so the application's connection string, and the secrets
                // it is made from, are never resolved here, and the provider is configured without one.
                options
                    .UseNpgsql(EnableRetryOnTransientFailure)
                    .ConfigureWarnings(IgnoreUnredactedEvents);
                return;
            }

            string? value = connectionString(serviceProvider);
            if (string.IsNullOrWhiteSpace(value))
            {
                // The message is fixed and never quotes the value, because a connection string can carry a password.
                throw new InvalidOperationException("The SmartHotel database connection string is not configured.");
            }

            EnsureDatabaseValuesStayRedacted(value);

            // The check only reads the string; Npgsql receives it exactly as it was supplied.
            options
                .UseNpgsql(value, EnableRetryOnTransientFailure)
                .ConfigureWarnings(IgnoreUnredactedEvents);
        });

        return services;
    }

    // Shared by the run-time and the design-time options, so both use one execution strategy: Npgsql's retrying
    // strategy, which also judges a failure reported through the disposed-transaction exception by its inner one.
    private static void EnableRetryOnTransientFailure(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.ExecutionStrategy(static dependencies => new TransientFailureExecutionStrategy(dependencies));
    }

    // EF Core writes each of these failure events before the caller sees the exception. Some hand the logger the
    // original exception, whose message can quote database values, and the retry record copies the transient exception
    // into its text. The command, connection and transaction records are Error records of their own, so a constraint
    // violation the API translates to 409 would still count as an error, and the connection record names the database
    // and the server. With all of them ignored, the host's sanitized exception record is the only record of a failure.
    // Ignoring them changes logging only: the exception still reaches the caller. The Debug connection, data reader and
    // migration lifecycle records listed after them name the database and the server from the connection string, so
    // they are ignored too, whatever level the logging configuration admits for their categories. Shared by the
    // run-time and the design-time options, so both ignore the same events.
    private static void IgnoreUnredactedEvents(WarningsConfigurationBuilder warnings)
    {
        warnings.Ignore(
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
            // Not a failure: migrating an up-to-date database is routine on every start after the first, and the
            // host records that outcome itself, so this record would only repeat it.
            RelationalEventId.MigrationsNotApplied);
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

    // Npgsql's retrying strategy, which also recognizes a transient failure that Npgsql reports through its
    // disposed-transaction exception. A session can end while EF Core releases, or rolls back to, the savepoint it
    // wraps a save in. EF Core does not report that failure, while Npgsql breaks the connection and disposes the
    // transaction, so the commit or rollback that follows throws ObjectDisposedException with the session's failure as
    // its inner exception. That failure is judged as if it had surfaced directly, so a unit that meets it is checked
    // and run again like any other transient failure: EF Core decides whether to run a unit's check of a committed
    // write through the same judgement.
    private sealed class TransientFailureExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : NpgsqlRetryingExecutionStrategy(
            dependencies,
            TransientFailureMaxRetryCount,
            TransientFailureMaxRetryDelay,
            errorCodesToAdd: null)
    {
        protected override bool ShouldRetryOn(Exception? exception)
        {
            return base.ShouldRetryOn(exception)
                || (exception is ObjectDisposedException { InnerException: { } inner } && base.ShouldRetryOn(inner));
        }
    }
}