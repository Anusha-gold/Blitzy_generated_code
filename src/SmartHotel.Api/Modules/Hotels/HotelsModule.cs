namespace SmartHotel.Api.Modules.Hotels;

/// <summary>
/// Registers the Hotels module's services with dependency injection: the hotel search and the hotel detail.
/// </summary>
/// <remarks>
/// The module registers only its own service. MVC discovers <c>HotelsController</c>, and the host registers the
/// <see cref="TimeProvider"/> the controller reads.
/// </remarks>
public static class HotelsModule
{
    /// <summary>
    /// Registers <see cref="HotelQueryService"/> as scoped.
    /// </summary>
    /// <remarks>
    /// The service injects the request's scoped <c>SmartHotelDbContext</c>, registered by <c>AddSmartHotelData</c>,
    /// and the scoped <c>AvailabilityQueries</c>, registered by <c>AddAvailabilityModule</c>, so both must already
    /// have been called, as <c>Program.cs</c> does before this method. A singleton would capture those scoped
    /// services, which scope validation rejects.
    /// </remarks>
    /// <param name="services">The API host's service collection.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddHotelsModule(this IServiceCollection services)
    {
        services.AddScoped<HotelQueryService>();
        return services;
    }
}