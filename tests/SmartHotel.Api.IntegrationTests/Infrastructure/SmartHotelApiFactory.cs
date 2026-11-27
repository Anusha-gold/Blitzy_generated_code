using System.Security.Cryptography;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Npgsql;

using SmartHotel.Api.Infrastructure;
using SmartHotel.Data;

namespace SmartHotel.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API pipeline in-process for one test class, on a PostgreSQL database, a database login role and a
/// JWT signing key of its own: an xUnit v3 class fixture built on <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InitializeAsync"/> creates the database <see cref="DatabaseName"/> and its login role on the
/// <see cref="TestPostgres"/> server and starts the host, so the API migrates and seeds that database during fixture
/// setup. <see cref="DisposeAsync"/> stops the host and drops the database and the role.
/// </para>
/// <para>
/// A test that must reach the real server, such as its rejection of a malformed request, which the in-process server
/// never parses, starts a second host of the same API through <see cref="StartLoopbackListenerAsync"/>.
/// </para>
/// <para>
/// The host runs in the Development environment with in-memory <c>Cors:AllowedOrigin</c> configuration, and its
/// <see cref="ISecretSource"/> is replaced by one that holds this factory's connection string and signing key. The
/// connection string authenticates as this factory's own role with a random password, never as the server user, and
/// no process environment variable is read for a secret or set, so no credential is shared with another test class.
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

    // Port 0 lets the operating system choose a free port, so concurrent test runs never collide.
    private const string LoopbackListenerUrl = "http://127.0.0.1:0";

    private static readonly TimeSpan ListenerStartTimeout = TimeSpan.FromMinutes(1);

    private readonly TestPostgres _postgres;

    // Serializes starting the listener and stopping it, so it is started at most once and never outlives the factory.
    // It is never disposed: without its wait handle it holds no unmanaged resource, and a disposed gate would fail a
    // caller that is still waiting for it with the wrong exception.
    private readonly SemaphoreSlim _listenerGate = new(1, 1);

    // Set to 1 by the first DisposeAsync. The base class's synchronous Dispose also calls DisposeAsync, so the
    // shutdown and the drop must run only once however the factory is disposed.
    private int _disposed;

    // The builder the in-process host was built from, recorded by CreateHost, and the listening host built from it.
    private IHostBuilder? _hostBuilder;
    private IHost? _listener;
    private Uri? _listenerAddress;

    /// <summary>
    /// Creates a factory with a new database name, a new random password for the login role of the same name, and a
    /// new random signing key. Nothing is created or started until <see cref="InitializeAsync"/> runs.
    /// </summary>
    /// <param name="postgres">The assembly's PostgreSQL server fixture, which xUnit v3 injects.</param>
    /// <exception cref="ArgumentNullException"><paramref name="postgres"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The server fixture has not been initialized.</exception>
    public SmartHotelApiFactory(TestPostgres postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        _postgres = postgres;
        DatabaseName = DatabaseNamePrefix + Guid.NewGuid().ToString("N");
        DatabaseConnectionString = postgres.CreateDatabaseConnectionString(DatabaseName);
        SigningKey = RandomNumberGenerator.GetHexString(SigningKeyLength, lowercase: true);
        AllowedOrigin = WebOrigin;
    }

    /// <summary>
    /// The name of this factory's database: <c>smarthotel_it_</c> followed by 32 lowercase hex digits.
    /// </summary>
    public string DatabaseName { get; }

    /// <summary>
    /// The Npgsql connection string for <see cref="DatabaseName"/>, the one the API uses, for tests that open their
    /// own connections. It authenticates as the database's owner, a login role named <see cref="DatabaseName"/> with a
    /// random password, so it must never be written to output.
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
    /// Creates <see cref="DatabaseName"/> and its login role, then builds and starts the host, which validates its
    /// options, applies the migrations and seeds the catalogue before it serves requests.
    /// </summary>
    /// <remarks>
    /// When the API fails to start, its exception is replaced by an <see cref="InvalidOperationException"/> with no
    /// inner exception, because provider and host messages can quote the username, the database or a configuration
    /// value. Its message names only the operation, the exception types and any SQLSTATE, and says which server the
    /// database is on, naming <c>SMARTHOTEL_TEST_POSTGRES</c> without its value. Invalid options, or a connection
    /// string the data layer refuses, are reported by their own exception types. When database initialization fails,
    /// for example on a failed migration, the API writes its sanitized initialization-failure record, disposes its
    /// host and returns from its entry point without serving, so the reported type is the
    /// <see cref="ObjectDisposedException"/> of the disposed host rather than the original failure's. The database
    /// and the role are dropped when the factory is disposed, which xUnit also does for a fixture whose initialization
    /// threw; a creation that fails partway drops them itself.
    /// </remarks>
    /// <returns>A task that completes once the host is serving requests.</returns>
    /// <exception cref="InvalidOperationException">
    /// The database could not be created, or the host failed to start. The message carries only the operation, the
    /// exception types, any SQLSTATE and the server note naming <c>SMARTHOTEL_TEST_POSTGRES</c>.
    /// </exception>
    /// <exception cref="OperationCanceledException">The test run was cancelled.</exception>
    public async ValueTask InitializeAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // Outside the try below: a failure here is already described by TestPostgres.
        await _postgres.CreateDatabaseAsync(DatabaseConnectionString, cancellationToken);

        try
        {
            // Reading Server builds and starts the host synchronously; Task.Run keeps that start off xUnit's
            // synchronization context.
            await Task.Run(() => _ = Server, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Raised afresh, so that no host exception rides along as its inner exception.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                _postgres.DescribeServerFailure("Starting the API host failed.", exception));
        }
    }

    /// <summary>
    /// Starts, on the first call, a second host of the API that serves this factory's database through Kestrel on a
    /// loopback port the operating system chooses, and returns its address. Later calls return the same address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The in-process host is built and started first, if it is not running yet. The listener is then built from the
    /// same host builder, so <c>Program</c> runs again with every override of this factory: its migration and seed find
    /// the database ready and change nothing. The two hosts share the database and the signing key, so a token one
    /// issues is valid on the other.
    /// </para>
    /// <para>
    /// The listener answers real HTTP/1.1 connections, so a test can send bytes the in-process server never parses,
    /// such as an over-long request line or a malformed header. <see cref="DisposeAsync"/> stops and disposes it before
    /// the in-process host. Concurrent calls are safe; they start one listener.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels waiting for the in-process host and for the listener to start.</param>
    /// <returns>The listener's base address, such as <c>http://127.0.0.1:41234/</c>.</returns>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    /// <exception cref="TimeoutException">The listener did not start within one minute.</exception>
    /// <exception cref="InvalidOperationException">
    /// The listener failed to start or reported no single bound address. The message carries only the operation, the
    /// exception types, any SQLSTATE and the server note naming <c>SMARTHOTEL_TEST_POSTGRES</c>, and no inner
    /// exception, because provider and host messages can quote the username or the database.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<Uri> StartLoopbackListenerAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        await _listenerGate.WaitAsync(cancellationToken);
        try
        {
            // Checked again under the gate: DisposeAsync may have stopped the hosts while this call waited.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (_listenerAddress is not null)
            {
                return _listenerAddress;
            }

            // Reading Server builds and starts the in-process host, which records the builder in CreateHost. It runs
            // synchronously, so Task.Run keeps it off xUnit's synchronization context.
            await Task.Run(() => _ = Server, cancellationToken);
            IHostBuilder builder = _hostBuilder
                ?? throw new InvalidOperationException("The in-process host was not built from a host builder.");

            // Kestrel replaces the in-process server in the services of the next build, and the URL replaces the
            // default listening ports.
            builder.ConfigureWebHost(static web => web.UseKestrel().UseUrls(LoopbackListenerUrl));
            IHost? listener = null;
            try
            {
                listener = await Task.Run(builder.Build, cancellationToken);
                Uri address = await WaitForListenerAddressAsync(listener, cancellationToken);
                _listener = listener;
                _listenerAddress = address;
                return address;
            }
            catch (Exception exception)
            {
                if (listener is not null)
                {
                    await StopListenerAsync(listener);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    // Raised afresh, so that no host exception rides along as its inner exception.
                    throw new OperationCanceledException(cancellationToken);
                }

                if (exception is TimeoutException)
                {
                    throw;
                }

                // As for the in-process host: Program's startup failures can quote the username or the database.
                throw new InvalidOperationException(
                    _postgres.DescribeServerFailure("Starting the loopback listener failed.", exception));
            }
        }
        finally
        {
            _listenerGate.Release();
        }
    }

    /// <summary>
    /// Stops and disposes the loopback listener, if one was started, then the in-process host and its clients, and
    /// finally drops <see cref="DatabaseName"/> and its login role, even when a shutdown throws or
    /// <see cref="InitializeAsync"/> never created them. Calls after the first do nothing.
    /// </summary>
    /// <returns>
    /// A task that completes once both hosts are stopped and neither the database nor the role exists.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A host failed to stop, or the database or the role could not be dropped. The message carries only the
    /// operation, the exception types, any SQLSTATE and the server note naming <c>SMARTHOTEL_TEST_POSTGRES</c>, and no
    /// inner exception.
    /// </exception>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            try
            {
                await DisposeListenerAsync();
            }
            finally
            {
                await base.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                _postgres.DescribeServerFailure("Stopping the API host failed.", exception));
        }
        finally
        {
            // Not the test's token: the database and the role must be dropped even when the run is being cancelled.
            await _postgres.DropDatabaseAsync(DatabaseConnectionString, CancellationToken.None);
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
    /// Opens a new connection of the test's own to <see cref="DatabaseName"/>, outside the host's services, for a
    /// session the API does not control, such as one that holds a row lock in a transaction of its own.
    /// </summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open connection, with no transaction begun; the caller disposes it.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection could not be opened. The message names only the operation, the exception types, any SQLSTATE
    /// and the server note naming <c>SMARTHOTEL_TEST_POSTGRES</c> without its value, and carries no inner exception,
    /// because a provider message can quote the username or the database.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<NpgsqlConnection> OpenDatabaseConnectionAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new(DatabaseConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (Exception exception)
        {
            // A connection that failed to open is never handed out, so it is disposed here.
            await connection.DisposeAsync();
            if (cancellationToken.IsCancellationRequested)
            {
                // Raised afresh, so that no provider exception rides along as its inner exception.
                throw new OperationCanceledException(cancellationToken);
            }

            throw new InvalidOperationException(
                _postgres.DescribeServerFailure("Opening a connection to the test database failed.", exception));
        }
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

    /// <summary>
    /// Records the host builder, from which <see cref="StartLoopbackListenerAsync"/> builds the listener, then builds
    /// and starts the in-process host as the base class does.
    /// </summary>
    /// <param name="builder">The host builder that runs the API's <c>Program</c> with this factory's overrides.</param>
    /// <returns>The started in-process host.</returns>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _hostBuilder = builder;
        return base.CreateHost(builder);
    }

    // The deferred host that Build returns shares its start signal with the in-process host, which has already started,
    // so its own StartAsync returns at once. Program starts the listener itself, and Kestrel has bound its address only
    // once the listener's lifetime reports that it started. A Program that fails before it starts the host never
    // signals, so the wait is bounded.
    private static async Task<Uri> WaitForListenerAddressAsync(IHost listener, CancellationToken cancellationToken)
    {
        IHostApplicationLifetime lifetime = listener.Services.GetRequiredService<IHostApplicationLifetime>();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (lifetime.ApplicationStarted.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            started))
        {
            await started.Task.WaitAsync(ListenerStartTimeout, cancellationToken);
        }

        ICollection<string>? addresses = listener.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;

        return addresses is { Count: 1 }
            ? new Uri(addresses.Single())
            : throw new InvalidOperationException("The listener reported no single bound address.");
    }

    // Waits for a start in progress, so a listener it creates is stopped as well.
    private async Task DisposeListenerAsync()
    {
        await _listenerGate.WaitAsync(CancellationToken.None);
        try
        {
            IHost? listener = _listener;
            _listener = null;
            if (listener is not null)
            {
                await StopListenerAsync(listener);
            }
        }
        finally
        {
            _listenerGate.Release();
        }
    }

    private static async Task StopListenerAsync(IHost listener)
    {
        try
        {
            await listener.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (listener is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                listener.Dispose();
            }
        }
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