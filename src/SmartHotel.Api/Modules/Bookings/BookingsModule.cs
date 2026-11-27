namespace SmartHotel.Api.Modules.Bookings;

/// <summary>
/// Registers the Bookings module's services with dependency injection: booking creation, the guest's booking list
/// and cancellation.
/// </summary>
/// <remarks>
/// The module registers only its own service. MVC discovers <c>BookingsController</c>, and the host registers what
/// <see cref="BookingService"/> consumes: <see cref="TimeProvider"/>, <c>SmartHotelDbContext</c> and logging.
/// </remarks>
public static class BookingsModule
{
    /// <summary>
    /// Registers <see cref="BookingService"/> as scoped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service injects the request's scoped <c>SmartHotelDbContext</c>, registered by <c>AddSmartHotelData</c>,
    /// and the scoped <c>AvailabilityQueries</c>, registered by <c>AddAvailabilityModule</c>, so <c>Program.cs</c>
    /// calls this method after <c>AddAvailabilityModule()</c>.
    /// </para>
    /// <para>
    /// The lifetime must be scoped. Within one request the service and the availability queries then share one
    /// <c>SmartHotelDbContext</c>, so the free-room query runs on the connection, and inside the transaction, in which
    /// <see cref="BookingService"/> holds the room type's row lock. A singleton would capture those scoped services,
    /// which scope validation rejects.
    /// </para>
    /// </remarks>
    /// <param name="services">The API host's service collection.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddBookingsModule(this IServiceCollection services)
    {
        services.AddScoped<BookingService>();
        return services;
    }
}