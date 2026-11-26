using Microsoft.EntityFrameworkCore;

using SmartHotel.Data.Entities;

namespace SmartHotel.Data;

/// <summary>
/// The Entity Framework Core session over the SmartHotel PostgreSQL database: guests, the hotel catalogue, rooms and
/// bookings.
/// </summary>
/// <remarks>
/// <para>
/// The context never chooses its own connection. The connection string reaches it only through the
/// <see cref="DbContextOptions{TContext}"/> it is constructed with: from the API's environment-only secret source at
/// run time, and from a passwordless placeholder when <c>dotnet ef</c> builds it at design time.
/// </para>
/// <para>
/// Every table, column, key, index and check constraint is mapped in the <c>Configurations</c> folder, one
/// configuration class per entity. The exclusion constraint <c>EX_Bookings_Room_NoOverlap</c> and the expression index
/// <c>IX_Hotels_City_Lower</c> cannot be modelled and are written by hand in the <c>InitialCreate</c> migration.
/// </para>
/// </remarks>
public sealed class SmartHotelDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SmartHotelDbContext"/> class.
    /// </summary>
    /// <param name="options">
    /// The options that select the Npgsql provider and carry the connection string, built by dependency injection or by
    /// the design-time factory.
    /// </param>
    public SmartHotelDbContext(DbContextOptions<SmartHotelDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Gets the registered guests, stored in the <c>Guests</c> table with their normalized emails.
    /// </summary>
    public DbSet<Guest> Guests => Set<Guest>();

    /// <summary>
    /// Gets the hotels in the catalogue, stored in the <c>Hotels</c> table.
    /// </summary>
    public DbSet<Hotel> Hotels => Set<Hotel>();

    /// <summary>
    /// Gets the room types each hotel offers, stored in the <c>RoomTypes</c> table. Booking creation locks one of these
    /// rows <c>FOR UPDATE</c> while it allocates a room.
    /// </summary>
    public DbSet<RoomType> RoomTypes => Set<RoomType>();

    /// <summary>
    /// Gets the physical rooms, stored in the <c>Rooms</c> table.
    /// </summary>
    public DbSet<Room> Rooms => Set<Room>();

    /// <summary>
    /// Gets the guests' bookings, stored in the <c>Bookings</c> table. Availability queries filter this set with the
    /// shared confirmed-overlap predicate and correlate it with rooms by <see cref="Booking.RoomId"/>.
    /// </summary>
    public DbSet<Booking> Bookings => Set<Booking>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The hand-written EX_Bookings_Room_NoOverlap constraint compares the integer "RoomId" with = inside a GiST
        // index, which only btree_gist provides; this annotation makes the migration create the extension first.
        modelBuilder.HasPostgresExtension("btree_gist");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmartHotelDbContext).Assembly);
    }
}