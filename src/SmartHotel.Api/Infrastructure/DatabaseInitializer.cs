using Microsoft.EntityFrameworkCore;

using SmartHotel.Data;
using SmartHotel.Data.Seed;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Brings the SmartHotel database up to date at startup: it applies every pending migration, then seeds the hotel
/// catalogue when the database holds no hotel yet.
/// </summary>
/// <remarks>
/// <para>
/// The host awaits <see cref="InitializeAsync"/> after its options are validated and before the server starts
/// listening, so a host that answers requests always runs on a migrated and seeded database.
/// </para>
/// <para>
/// Both steps are idempotent. A second start finds no pending migration in <c>__EFMigrationsHistory</c>, so the
/// migrator applies nothing and the start logs that the schema is up to date, and the seeder finds the existing hotels
/// and writes nothing.
/// </para>
/// <para>
/// Failures and cancellation are never caught here: an unreachable database, a missing connection string, a failed
/// migration or seed, or a canceled token propagates to the caller, and the migration's or the seed's uncommitted
/// transaction rolls back. The host reports a failure through <see cref="ReportFailure"/> and a cancellation through
/// <see cref="ReportCanceled"/>, each as one record that carries no exception text, and then stops without listening.
/// </para>
/// <para>
/// Startup migration assumes a single API instance, so no lock or leader election guards it.
/// </para>
/// </remarks>
public static partial class DatabaseInitializer
{
    /// <summary>
    /// Applies the pending migrations, then runs the seed, in a service scope of its own.
    /// </summary>
    /// <param name="services">
    /// The host's root service provider, which registers <see cref="SmartHotelDbContext"/> and logging.
    /// </param>
    /// <param name="cancellationToken">Cancels the migration or the seed.</param>
    /// <returns>A task that completes once the database is migrated and seeded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The database connection string is missing or refused, or the runtime configuration does not enable the
    /// <c>Npgsql.DisableDateTimeInfinityConversions</c> switch, when the context is resolved.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled; the uncommitted migration or seed rolls back.
    /// </exception>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A dedicated scope gives the seeder the fresh context it requires: tracking nothing, with no transaction open.
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        SmartHotelDbContext db = scope.ServiceProvider.GetRequiredService<SmartHotelDbContext>();
        ILogger logger = CreateLogger(scope.ServiceProvider);

        // The migrator runs on every start, and the count taken first decides which outcome is logged once it returns:
        // the migrations applied, or a schema that was already up to date. The data layer ignores the migrator's own
        // record that it applied nothing, so a start with nothing pending writes the up-to-date record alone. Startup
        // migration assumes a single API instance, so nothing becomes pending between the count and the migration.
        int pendingCount = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        if (pendingCount > 0)
        {
            LogApplyingMigrations(logger, pendingCount);
        }

        await db.Database.MigrateAsync(cancellationToken);

        if (pendingCount == 0)
        {
            LogMigrationsUpToDate(logger);
        }
        else
        {
            LogMigrationsApplied(logger);
        }

        // The seeder opens its own transaction, so none is opened around it.
        if (await DbSeeder.SeedAsync(db, cancellationToken))
        {
            LogSeedInserted(logger);
        }
        else
        {
            LogSeedSkipped(logger);
        }
    }

    /// <summary>
    /// Writes the one record of a failed initialization: a fixed Error message that names the database settings to
    /// check, followed by the sanitized description of <paramref name="exception"/> that the unhandled-exception
    /// event also uses.
    /// </summary>
    /// <remarks>
    /// The exception is never passed to the logger, and its message, <see cref="Exception.Data"/>, inner messages and
    /// formatted stack trace are never read, because they can quote a host, a role, a database name or part of a
    /// connection string. Only the exception type names, PostgreSQL's SqlState and constraint name, and stack frames
    /// reduced to type and method names are written, and the settings are named without their values.
    /// </remarks>
    /// <param name="services">The host's root service provider, which registers logging.</param>
    /// <param name="exception">The exception <see cref="InitializeAsync"/> propagated.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="exception"/> is <see langword="null"/>.
    /// </exception>
    internal static void ReportFailure(IServiceProvider services, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(exception);

        (string[] exceptionTypes,
            bool exceptionTypesTruncated,
            string? sqlState,
            string? constraintName,
            string[] stackFrames,
            bool stackFramesTruncated) = ApiExceptionHandler.DescribeSanitized(exception);

        LogInitializationFailed(
            CreateLogger(services),
            exceptionTypes,
            exceptionTypesTruncated,
            sqlState,
            constraintName,
            stackFrames,
            stackFramesTruncated);
    }

    /// <summary>
    /// Writes the one record of an initialization that a stop request canceled: a fixed Warning message, with no
    /// exception.
    /// </summary>
    /// <param name="services">The host's root service provider, which registers logging.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    internal static void ReportCanceled(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        LogInitializationCanceled(CreateLogger(services));
    }

    // A static class cannot be the type argument of ILogger<T>, so the category is created from its type.
    private static ILogger CreateLogger(IServiceProvider services)
    {
        return services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitializer));
    }

    [LoggerMessage(
        EventId = 5101,
        EventName = "ApplyingMigrations",
        Level = LogLevel.Information,
        Message = "Applying {PendingMigrationCount} pending database migrations.")]
    private static partial void LogApplyingMigrations(ILogger logger, int pendingMigrationCount);

    [LoggerMessage(
        EventId = 5102,
        EventName = "MigrationsApplied",
        Level = LogLevel.Information,
        Message = "Migrations applied.")]
    private static partial void LogMigrationsApplied(ILogger logger);

    [LoggerMessage(
        EventId = 5105,
        EventName = "MigrationsUpToDate",
        Level = LogLevel.Information,
        Message = "The database schema is up to date; no migrations to apply.")]
    private static partial void LogMigrationsUpToDate(ILogger logger);

    [LoggerMessage(
        EventId = 5103,
        EventName = "SeedInserted",
        Level = LogLevel.Information,
        Message = "Seed data inserted.")]
    private static partial void LogSeedInserted(ILogger logger);

    [LoggerMessage(
        EventId = 5104,
        EventName = "SeedSkipped",
        Level = LogLevel.Information,
        Message = "Seed data skipped: the database already holds hotels.")]
    private static partial void LogSeedSkipped(ILogger logger);

    [LoggerMessage(
        EventId = 5106,
        EventName = "InitializationFailed",
        Level = LogLevel.Error,
        Message = "Database initialization failed, so the API stops without listening. Check that "
            + "ConnectionStrings__SmartHotel, or DB_HOST, DB_PORT, DB_NAME, DB_USER and DB_PASSWORD, are set, and that "
            + "the PostgreSQL server they name is reachable and accepts that database and role. "
            + "Types={ExceptionTypes} TypesTruncated={ExceptionTypesTruncated} SqlState={SqlState} "
            + "Constraint={ConstraintName} Frames={StackFrames} FramesTruncated={StackFramesTruncated}")]
    private static partial void LogInitializationFailed(
        ILogger logger,
        string[] exceptionTypes,
        bool exceptionTypesTruncated,
        string? sqlState,
        string? constraintName,
        string[] stackFrames,
        bool stackFramesTruncated);

    [LoggerMessage(
        EventId = 5107,
        EventName = "InitializationCanceled",
        Level = LogLevel.Warning,
        Message = "Database initialization was canceled because the API is stopping, so it stops without listening. "
            + "Migration and seed work not yet committed is rolled back.")]
    private static partial void LogInitializationCanceled(ILogger logger);
}