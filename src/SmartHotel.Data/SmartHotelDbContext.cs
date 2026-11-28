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

    /// <summary>
    /// Folds text to lower case with PostgreSQL's built-in <c>lower</c> function, under the database's collation. It
    /// is a query-only function: inside a LINQ query it translates to <c>lower(value)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied to both operands of a comparison, it folds both with the same function and collation in the same
    /// statement. <see cref="string.ToLower()"/> translates to the same SQL only on a column: on a value captured from
    /// outside the query, EF Core evaluates it in .NET, whose casing rules differ from the database's, and sends the
    /// result as a parameter. A mapped database function is never evaluated in .NET, so the database folds the
    /// parameter itself.
    /// </para>
    /// <para>
    /// Applied to <see cref="Hotel.City"/>, it yields <c>lower("City")</c>, the expression the hand-written index
    /// <c>IX_Hotels_City_Lower</c> is built on.
    /// </para>
    /// </remarks>
    /// <param name="value">The text to fold.</param>
    /// <returns>Nothing: the database computes the folded text, and a call outside a query always throws.</returns>
    /// <exception cref="NotSupportedException">
    /// The method is called directly rather than inside a query that EF Core translates to SQL.
    /// </exception>
    public static string Lower(string value)
    {
        throw new NotSupportedException(
            $"{nameof(SmartHotelDbContext)}.{nameof(Lower)} is PostgreSQL's lower function and can only be used "
            + "inside a LINQ query that EF Core translates to SQL.");
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The hand-written EX_Bookings_Room_NoOverlap constraint compares the integer "RoomId" with = inside a GiST
        // index, which only btree_gist provides; this annotation makes the migration create the extension first.
        modelBuilder.HasPostgresExtension("btree_gist");

        // lower is built in, so it is written unquoted and without a schema. It returns NULL only for a NULL argument,
        // so a comparison of non-null operands needs no null compensation and keeps its index-matching shape.
        modelBuilder.HasDbFunction(() => Lower(default!))
            .HasName("lower")
            .IsBuiltIn()
            .HasParameter("value")
            .PropagatesNullability();

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmartHotelDbContext).Assembly);
    }
}