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
/// Both steps are idempotent. A second start finds no pending migration in <c>__EFMigrationsHistory</c>, and the
/// seeder finds the existing hotels and writes nothing. Failures are never caught here: an unreachable database, a
/// missing connection string or a failed migration propagates and stops the startup.
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
    /// The database connection string is missing or refused when the context is resolved.
    /// </exception>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A dedicated scope gives the seeder the fresh context it requires: tracking nothing, with no transaction open.
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        SmartHotelDbContext db = scope.ServiceProvider.GetRequiredService<SmartHotelDbContext>();

        // A static class cannot be the type argument of ILogger<T>, so the category is created from its type.
        ILogger logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DatabaseInitializer));

        int pendingCount = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        LogApplyingMigrations(logger, pendingCount);

        await db.Database.MigrateAsync(cancellationToken);
        LogMigrationsApplied(logger);

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
}