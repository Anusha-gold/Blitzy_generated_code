using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmartHotel.Data;

/// <summary>
/// Builds <see cref="SmartHotelDbContext"/> for the Entity Framework Core design-time tools (<c>dotnet ef</c>) and for
/// nothing else; the application never uses it at run time.
/// </summary>
/// <remarks>
/// <para>
/// The tools build the API host to discover the context type, then find this factory in the context's assembly and
/// create the context through it rather than through the host. While they build the host, <c>AddSmartHotelData</c>
/// configures the provider without resolving its connection string, so generating or inspecting migrations needs none
/// of the API's secrets or environment variables. At run time the API registers the context through
/// <c>AddSmartHotelData</c>, with the connection string from its environment-only secret source.
/// </para>
/// <para>
/// The connection string here is a passwordless placeholder. <c>dotnet ef migrations add</c> builds the model without
/// ever connecting to it, and the API applies migrations itself at startup, so no credential belongs in this file.
/// </para>
/// <para>
/// Like the runtime registration, the factory first requires the <c>Npgsql.DisableDateTimeInfinityConversions</c>
/// switch, which keeps <see cref="DateOnly.MinValue"/> and <see cref="DateOnly.MaxValue"/> finite instead of
/// <c>-infinity</c> and <c>infinity</c>. The tools run with the startup project's runtime configuration, and the API
/// project enables the switch there, so the tools and the API convert dates the same way.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SmartHotelDbContext>
{
    // Kept free of credentials and of Npgsql's error-detail option: it is committed source, and the design-time tools
    // only need the provider, not a reachable server.
    private const string PlaceholderConnectionString =
        "Host=localhost;Port=5432;Database=smarthotel_design;Username=smarthotel";

    /// <summary>
    /// Creates a context that uses the Npgsql provider with the placeholder connection string. The runtime
    /// registration configures the same provider and adds Npgsql's retrying execution strategy and a list of ignored
    /// logging events. Neither changes the model, so the design-time model equals the model the API runs with.
    /// </summary>
    /// <param name="args">
    /// The arguments the design-time tools pass after <c>--</c>. They are ignored, because the factory reads no
    /// configuration.
    /// </param>
    /// <returns>A new <see cref="SmartHotelDbContext"/> that has not opened a connection.</returns>
    /// <exception cref="InvalidOperationException">
    /// The startup project's runtime configuration, which the tools run with, does not enable the
    /// <c>Npgsql.DisableDateTimeInfinityConversions</c> switch.
    /// </exception>
    public SmartHotelDbContext CreateDbContext(string[] args)
    {
        DataServiceCollectionExtensions.EnsureFiniteDateConversions();

        DbContextOptions<SmartHotelDbContext> options = new DbContextOptionsBuilder<SmartHotelDbContext>()
            .UseNpgsql(PlaceholderConnectionString)
            .Options;

        return new SmartHotelDbContext(options);
    }
}