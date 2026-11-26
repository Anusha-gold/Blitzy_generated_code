using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmartHotel.Data;

/// <summary>
/// Builds <see cref="SmartHotelDbContext"/> for the Entity Framework Core design-time tools (<c>dotnet ef</c>) and for
/// nothing else; the application never uses it at run time.
/// </summary>
/// <remarks>
/// <para>
/// The tools find this factory in the context's assembly and prefer it to the API host, so generating or inspecting
/// migrations needs none of the API's secrets or environment variables. At run time the API registers the context
/// through <c>AddSmartHotelData</c>, with the connection string from its environment-only secret source.
/// </para>
/// <para>
/// The connection string here is a passwordless placeholder. <c>dotnet ef migrations add</c> builds the model without
/// ever connecting to it, and the API applies migrations itself at startup, so no credential belongs in this file.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SmartHotelDbContext>
{
    // Kept free of credentials and of Npgsql's error-detail option: it is committed source, and the design-time tools
    // only need the provider, not a reachable server.
    private const string PlaceholderConnectionString =
        "Host=localhost;Port=5432;Database=smarthotel_design;Username=smarthotel";

    /// <summary>
    /// Creates a context that uses the Npgsql provider with the placeholder connection string. The provider is
    /// configured with the same plain <c>UseNpgsql</c> call as the runtime registration, so the design-time model
    /// equals the model the API runs with.
    /// </summary>
    /// <param name="args">
    /// The arguments the design-time tools pass after <c>--</c>. They are ignored, because the factory reads no
    /// configuration.
    /// </param>
    /// <returns>A new <see cref="SmartHotelDbContext"/> that has not opened a connection.</returns>
    public SmartHotelDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<SmartHotelDbContext> options = new DbContextOptionsBuilder<SmartHotelDbContext>()
            .UseNpgsql(PlaceholderConnectionString)
            .Options;

        return new SmartHotelDbContext(options);
    }
}