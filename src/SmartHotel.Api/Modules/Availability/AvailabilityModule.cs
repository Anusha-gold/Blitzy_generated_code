namespace SmartHotel.Api.Modules.Availability;

/// <summary>
/// Registers the Availability module's services with dependency injection.
/// </summary>
public static class AvailabilityModule
{
    /// <summary>
    /// Registers <see cref="AvailabilityQueries"/>, the shared availability queries used by the Hotels and Bookings
    /// modules. <c>Program.cs</c> calls this before <c>AddHotelsModule()</c> and <c>AddBookingsModule()</c>.
    /// </summary>
    /// <remarks>
    /// The queries are registered scoped, so they share the request's <c>SmartHotelDbContext</c> with the module
    /// service that calls them. <see cref="AvailabilityQueries.FindFreeRoomAsync"/> therefore runs on the connection,
    /// and inside the transaction, in which <c>BookingService</c> holds the room type's row lock.
    /// </remarks>
    /// <param name="services">The service collection to add the availability services to.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddAvailabilityModule(this IServiceCollection services)
    {
        services.AddScoped<AvailabilityQueries>();
        return services;
    }
}