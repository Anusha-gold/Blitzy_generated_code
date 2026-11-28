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
    /// The service injects the request's scoped <c>SmartHotelDbContext</c> and the scoped <c>AvailabilityQueries</c>,
    /// so the host must also register both in the same collection, through <c>AddSmartHotelData</c> and
    /// <c>AddAvailabilityModule</c>. Those calls may come before or after this one, because no registration resolves a
    /// service: both need only be registered by the time the provider is built and resolves the service. A singleton
    /// would capture those scoped services, which scope validation rejects.
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