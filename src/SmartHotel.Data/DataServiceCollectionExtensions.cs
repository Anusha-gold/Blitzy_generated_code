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
    /// No retrying execution strategy is enabled, because booking creation and seeding open their own transactions,
    /// which such a strategy rejects. Sensitive-data logging and detailed errors stay off, so command-failure logs
    /// show parameter placeholders rather than values.
    /// </para>
    /// <para>
    /// The connection string is checked before the provider receives it. Npgsql's <c>Include Error Detail</c> would
    /// copy PostgreSQL's <c>DETAIL</c> text, which can quote key values such as a guest's email address, into exception
    /// messages, and its <c>Log Parameters</c> would write parameter values into Npgsql's command logs. A string that
    /// enables either setting is therefore refused, as is a string Npgsql cannot parse.
    /// </para>
    /// <para>
    /// EF Core's query and save failure events are ignored: <see cref="CoreEventId.QueryIterationFailed"/>,
    /// <see cref="CoreEventId.SaveChangesFailed"/>, <see cref="RelationalEventId.NonQueryOperationFailed"/>,
    /// <see cref="RelationalEventId.ExecuteDeleteFailed"/>, <see cref="RelationalEventId.ExecuteUpdateFailed"/>,
    /// <see cref="RelationalEventId.BatchExecutorFailedToRollbackToSavepoint"/> and
    /// <see cref="RelationalEventId.BatchExecutorFailedToReleaseSavepoint"/>. Each would log the original exception,
    /// whose message can quote database values, ahead of the host's sanitized exception record. Only their logging
    /// stops; the exceptions still propagate, and every other event, the command-failure record included, is kept.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add the data services to.</param>
    /// <param name="connectionString">
    /// Returns the Npgsql connection string from the application's services. It is invoked whenever the context
    /// options are built, once per scope, and never during registration. That resolution throws
    /// <see cref="InvalidOperationException"/>, with a fixed message that quotes no part of the string, when the result
    /// is <see langword="null"/>, empty or whitespace, is not a valid Npgsql connection string, or enables Npgsql's
    /// <c>Include Error Detail</c> or <c>Log Parameters</c> under any of their keywords. Any other string reaches the
    /// provider unchanged.
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
            string? value = connectionString(serviceProvider);
            if (string.IsNullOrWhiteSpace(value))
            {
                // The message is fixed and never quotes the value, because a connection string can carry a password.
                throw new InvalidOperationException("The SmartHotel database connection string is not configured.");
            }

            EnsureDatabaseValuesStayRedacted(value);

            // The check only reads the string; Npgsql receives it exactly as it was supplied.
            options
                .UseNpgsql(value)
                // Each of these failure events hands the logger the original exception, whose message can quote
                // database values, before the host's exception handler writes its sanitized record. Ignoring them
                // changes logging only: the exception still reaches the caller, and the command-failure records
                // with parameter placeholders are kept.
                .ConfigureWarnings(warnings => warnings.Ignore(
                    CoreEventId.QueryIterationFailed,
                    CoreEventId.SaveChangesFailed,
                    RelationalEventId.NonQueryOperationFailed,
                    RelationalEventId.ExecuteDeleteFailed,
                    RelationalEventId.ExecuteUpdateFailed,
                    RelationalEventId.BatchExecutorFailedToRollbackToSavepoint,
                    RelationalEventId.BatchExecutorFailedToReleaseSavepoint));
        });

        return services;
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