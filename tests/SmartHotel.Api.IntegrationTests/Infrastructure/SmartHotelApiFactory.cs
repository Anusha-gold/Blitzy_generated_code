using System.Security.Cryptography;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using SmartHotel.Api.Infrastructure;
using SmartHotel.Data;

namespace SmartHotel.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API pipeline in-process for one test class, on a PostgreSQL database and a JWT signing key of its
/// own: an xUnit v3 class fixture built on <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InitializeAsync"/> creates the database <see cref="DatabaseName"/> on the <see cref="TestPostgres"/>
/// server and starts the host, so the API migrates and seeds that database during fixture setup.
/// <see cref="DisposeAsync"/> stops the host and drops the database.
/// </para>
/// <para>
/// The host runs in the Development environment with in-memory <c>Cors:AllowedOrigin</c> configuration, and its
/// <see cref="ISecretSource"/> is replaced by one that holds this factory's connection string and signing key. No
/// process environment variable is read for a secret or set, so nothing is shared with another test class.
/// </para>
/// <para>
/// Every member is safe to call concurrently once the host has started. The connection string and the signing key are
/// test credentials; they are never written to output.
/// </para>
/// </remarks>
public sealed class SmartHotelApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string DatabaseNamePrefix = "smarthotel_it_";
    private const string WebOrigin = "http://localhost:4200";
    private const int SigningKeyLength = 64;

    private readonly TestPostgres _postgres;

    // Set to 1 by the first DisposeAsync. The base class's synchronous Dispose also calls DisposeAsync, so the
    // shutdown and the drop must run only once however the factory is disposed.
    private int _disposed;

    /// <summary>
    /// Creates a factory with a new database name and a new random signing key. Nothing is created or started until
    /// <see cref="InitializeAsync"/> runs.
    /// </summary>
    /// <param name="postgres">The assembly's PostgreSQL server fixture, which xUnit v3 injects.</param>
    /// <exception cref="ArgumentNullException"><paramref name="postgres"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The server fixture has not been initialized.</exception>
    public SmartHotelApiFactory(TestPostgres postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        _postgres = postgres;
        DatabaseName = DatabaseNamePrefix + Guid.NewGuid().ToString("N");
        DatabaseConnectionString = postgres.ConnectionStringFor(DatabaseName);
        SigningKey = RandomNumberGenerator.GetHexString(SigningKeyLength, lowercase: true);
        AllowedOrigin = WebOrigin;
    }

    /// <summary>
    /// The name of this factory's database: <c>smarthotel_it_</c> followed by 32 lowercase hex digits.
    /// </summary>
    public string DatabaseName { get; }

    /// <summary>
    /// The Npgsql connection string for <see cref="DatabaseName"/>, the one the API uses, for tests that open their
    /// own connections. It can carry a password, so it must never be written to output.
    /// </summary>
    public string DatabaseConnectionString { get; }

    /// <summary>
    /// The HS256 signing key the API issues and validates tokens with: 64 random lowercase hex characters, which is
    /// 64 bytes in UTF-8. Tests forge tokens with it.
    /// </summary>
    public string SigningKey { get; }

    /// <summary>
    /// The single origin the API's CORS policy allows, <c>http://localhost:4200</c>: a serialized <c>http</c> origin,
    /// which the Development environment accepts.
    /// </summary>
    public string AllowedOrigin { get; }

    /// <summary>
    /// Replaces further services after the API's own registrations and the test secret source, such as a
    /// <see cref="TimeProvider"/> that throws. It is read when the host is built, during <see cref="InitializeAsync"/>,
    /// so a value set in an object initializer applies.
    /// </summary>
    public Action<IServiceCollection>? ServiceOverrides { get; init; }

    /// <summary>
    /// The token issuer (<c>iss</c>) the host issues and validates, read from its validated options.
    /// </summary>
    public string JwtIssuer => Services.GetRequiredService<IOptions<JwtOptions>>().Value.Issuer;

    /// <summary>
    /// The token audience (<c>aud</c>) the host issues and validates, read from its validated options.
    /// </summary>
    public string JwtAudience => Services.GetRequiredService<IOptions<JwtOptions>>().Value.Audience;

    /// <summary>
    /// Creates <see cref="DatabaseName"/>, then builds and starts the host, which validates its options, applies the
    /// migrations and seeds the catalogue before it serves requests.
    /// </summary>
    /// <remarks>
    /// When the API fails to start, for example on invalid options or a failed migration, its exception propagates
    /// unchanged. The database is dropped when the factory is disposed, which xUnit also does for a fixture whose
    /// initialization threw.
    /// </remarks>
    /// <returns>A task that completes once the host is serving requests.</returns>
    public async ValueTask InitializeAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await _postgres.CreateDatabaseAsync(DatabaseName, cancellationToken);

        // Reading Server builds and starts the host synchronously; Task.Run keeps that start off xUnit's
        // synchronization context.
        await Task.Run(() => _ = Server, cancellationToken);
    }

    /// <summary>
    /// Stops and disposes the host and its clients, then drops <see cref="DatabaseName"/>, even when the shutdown
    /// throws. Calls after the first do nothing.
    /// </summary>
    /// <returns>A task that completes once the host is stopped and the database no longer exists.</returns>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            // Not the test's token: the database must be dropped even when the test run is being cancelled.
            await _postgres.DropDatabaseAsync(DatabaseName, CancellationToken.None);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with a <see cref="SmartHotelDbContext"/> from a new service scope, to arrange
    /// data or assert results. No transaction is opened, and nothing tracked survives the call.
    /// </summary>
    /// <param name="action">The work to run; it receives the context and <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Passed to <paramref name="action"/> for its database calls.</param>
    /// <returns>A task that completes once <paramref name="action"/> has completed and the scope is disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    public async Task ExecuteDbAsync(
        Func<SmartHotelDbContext, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        SmartHotelDbContext db = scope.ServiceProvider.GetRequiredService<SmartHotelDbContext>();
        await action(db, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with a <see cref="SmartHotelDbContext"/> from a new service scope and returns
    /// its result. No transaction is opened, and nothing tracked survives the call.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="action">The work to run; it receives the context and <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Passed to <paramref name="action"/> for its database calls.</param>
    /// <returns>The result of <paramref name="action"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    public async Task<T> ExecuteDbAsync<T>(
        Func<SmartHotelDbContext, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        SmartHotelDbContext db = scope.ServiceProvider.GetRequiredService<SmartHotelDbContext>();
        return await action(db, cancellationToken);
    }

    /// <summary>
    /// Creates a request helper over a new client of the in-process host. The client does not follow redirects, so
    /// tests see each response exactly as the API sends it.
    /// </summary>
    /// <returns>A new <see cref="ApiTestClient"/>; the factory disposes its client.</returns>
    public ApiTestClient CreateApiClient()
    {
        return new ApiTestClient(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));
    }

    /// <summary>
    /// Looks up a seeded hotel's id by its catalogue name, so tests never hard-code ids.
    /// </summary>
    /// <param name="hotelName">The hotel's name, such as <c>SmartHotel Seattle Downtown</c>.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The hotel's id.</returns>
    /// <exception cref="ArgumentException"><paramref name="hotelName"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="InvalidOperationException">No hotel, or more than one, has that name.</exception>
    public async Task<int> GetHotelIdAsync(string hotelName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(hotelName);

        int? id = await ExecuteDbAsync(
            (db, ct) => db.Hotels
                .AsNoTracking()
                .Where(hotel => hotel.Name == hotelName)
                .Select(static hotel => (int?)hotel.Id)
                .SingleOrDefaultAsync(ct),
            cancellationToken);

        return id ?? throw new InvalidOperationException($"No seeded hotel is named '{hotelName}'.");
    }

    /// <summary>
    /// Looks up a seeded room type by its hotel's name and its own name, so tests never hard-code ids or prices.
    /// </summary>
    /// <param name="hotelName">The hotel's name, such as <c>SmartHotel Seattle Downtown</c>.</param>
    /// <param name="roomTypeName">
    /// The room type's name: <c>Standard</c>, <c>Deluxe</c>, <c>Family</c> or <c>Suite</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The room type's ids, nightly price and capacity.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="hotelName"/> or <paramref name="roomTypeName"/> is <see langword="null"/> or empty.
    /// </exception>
    /// <exception cref="InvalidOperationException">No room type, or more than one, matches both names.</exception>
    public async Task<SeededRoomType> GetRoomTypeAsync(
        string hotelName,
        string roomTypeName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(hotelName);
        ArgumentException.ThrowIfNullOrEmpty(roomTypeName);

        SeededRoomType? roomType = await ExecuteDbAsync(
            (db, ct) => db.RoomTypes
                .AsNoTracking()
                .Where(type => type.Hotel.Name == hotelName && type.Name == roomTypeName)
                .Select(static type => new SeededRoomType(type.Id, type.HotelId, type.NightlyPrice, type.MaxGuests))
                .SingleOrDefaultAsync(ct),
            cancellationToken);

        return roomType ?? throw new InvalidOperationException(
            $"No seeded room type is named '{roomTypeName}' in hotel '{hotelName}'.");
    }

    /// <summary>
    /// Lists the rooms of one room type, ordered by room number.
    /// </summary>
    /// <param name="roomTypeId">The room type's id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>
    /// The rooms in ordinal room-number order, which for the seeded three-digit numbers is <c>x01</c> to <c>x10</c>;
    /// empty when no room has that type.
    /// </returns>
    public async Task<IReadOnlyList<SeededRoom>> GetRoomsAsync(int roomTypeId, CancellationToken cancellationToken)
    {
        List<SeededRoom> rooms = await ExecuteDbAsync(
            (db, ct) => db.Rooms
                .AsNoTracking()
                .Where(room => room.RoomTypeId == roomTypeId)
                .Select(static room => new SeededRoom(room.Id, room.RoomNumber))
                .ToListAsync(ct),
            cancellationToken);

        // Sorted here, not in SQL, so the order is ordinal whatever the database's collation.
        return [.. rooms.OrderBy(static room => room.RoomNumber, StringComparer.Ordinal)];
    }

    // The overrides below are in time for everything the API checks at startup. Program.cs reads no configuration
    // and resolves no service before Build(): AddSmartHotelData's connection-string callback, the JwtBearer options,
    // the JwtOptions signing-key post-configuration and the CORS policy all resolve the secret source and the options
    // through DI. WebApplicationFactory's deferred host applies these registrations while Program builds its host,
    // then lets the entry point continue past Build(), so options validation and DatabaseInitializer (migrate, then
    // seed) run against them before app.Run() starts the TestServer. Removing Program's EnvironmentSecretSource means
    // it is never constructed, so the process environment is never read for a secret.

    /// <summary>
    /// Selects the Development environment, adds the allowed origin as in-memory configuration, and replaces the
    /// secret source with this factory's credentials, followed by <see cref="ServiceOverrides"/>.
    /// </summary>
    /// <param name="builder">The API's web host builder.</param>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        // Added after the API's own configuration sources, so it wins over any Cors__AllowedOrigin variable.
        string allowedOriginKey = ConfigurationPath.Combine(
            ApiCorsOptions.SectionName,
            nameof(ApiCorsOptions.AllowedOrigin));
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { [allowedOriginKey] = AllowedOrigin }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISecretSource>();
            services.AddSingleton<ISecretSource>(new TestSecretSource(DatabaseConnectionString, SigningKey));
            ServiceOverrides?.Invoke(services);
        });
    }

    /// <summary>
    /// A seeded room type, as <see cref="GetRoomTypeAsync"/> returns it.
    /// </summary>
    /// <param name="Id">The room type's id.</param>
    /// <param name="HotelId">The owning hotel's id.</param>
    /// <param name="NightlyPrice">The price per night in USD.</param>
    /// <param name="MaxGuests">The largest party one room of this type can host.</param>
    public sealed record SeededRoomType(int Id, int HotelId, decimal NightlyPrice, int MaxGuests);

    /// <summary>
    /// A seeded room, as <see cref="GetRoomsAsync"/> returns it.
    /// </summary>
    /// <param name="Id">The room's id, the <c>RoomId</c> of its bookings.</param>
    /// <param name="RoomNumber">The room's number, unique within its hotel, such as <c>101</c>.</param>
    public sealed record SeededRoom(int Id, string RoomNumber);

    // A class rather than a record: a record's synthesized ToString would print the credentials.
    private sealed class TestSecretSource(string databaseConnectionString, string jwtSigningKey) : ISecretSource
    {
        public string? DatabaseConnectionString { get; } = databaseConnectionString;

        public string? JwtSigningKey { get; } = jwtSigningKey;
    }
}