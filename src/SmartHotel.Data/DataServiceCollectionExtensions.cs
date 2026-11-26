using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    /// </remarks>
    /// <param name="services">The service collection to add the data services to.</param>
    /// <param name="connectionString">
    /// Returns the Npgsql connection string from the application's services. It is invoked whenever the context
    /// options are built, once per scope, and never during registration. A <see langword="null"/>, empty or
    /// whitespace result makes that resolution throw <see cref="InvalidOperationException"/>.
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

            options.UseNpgsql(value);
        });

        return services;
    }
}