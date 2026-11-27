using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using Npgsql;

// The attribute sits outside the file-scoped namespace, so the fixture type is named in full.
[assembly: AssemblyFixture(typeof(SmartHotel.Api.IntegrationTests.Infrastructure.TestPostgres))]

namespace SmartHotel.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The real PostgreSQL 16 server behind every integration test: an xUnit v3 assembly fixture that selects the server
/// once per run, waits until it accepts connections, and creates and drops the per-class test databases on it.
/// </summary>
/// <remarks>
/// <para>
/// When the <c>SMARTHOTEL_TEST_POSTGRES</c> environment variable is set, its connection string selects the server and
/// no Compose call is made. Otherwise the fixture starts the repository's <c>db-test</c> Compose service, a
/// passwordless server published on host loopback port 55432, in its own Compose project <c>smarthotel-test</c>, and
/// leaves it running: its data lives on tmpfs and the next run reuses it.
/// </para>
/// <para>
/// The fixture never sets an environment variable. Neither the server connection string nor the variable's value is
/// ever written to output or put into an exception message, because a full connection string can carry a password.
/// </para>
/// <para>
/// Every connection string the fixture hands out keeps <c>Include Error Detail</c> off, so PostgreSQL's
/// <c>DETAIL</c> text, which can contain key values, stays redacted in exceptions and logs.
/// </para>
/// <para>
/// The members are safe to call concurrently from test classes that run in parallel.
/// </para>
/// </remarks>
public sealed partial class TestPostgres : IAsyncLifetime
{
    private const string ServerVariable = "SMARTHOTEL_TEST_POSTGRES";
    private const string DefaultServer = "Host=127.0.0.1;Port=55432;Username=postgres";
    private const string MaintenanceDatabase = "postgres";
    private const string ComposeProject = "smarthotel-test";
    private const string ComposeService = "db-test";
    private const string ComposeProfile = "test";
    private const string SolutionFileName = "SmartHotel.sln";
    private const string ProjectVariablePrefix = "SMARTHOTEL_";
    private const string Hint =
        "Start Docker, or set SMARTHOTEL_TEST_POSTGRES to a PostgreSQL 16 server connection string.";

    // A first run may have to pull the postgres image before db-test can become healthy.
    private static readonly TimeSpan ComposeTimeout = TimeSpan.FromMinutes(5);

    // How long Compose's output streams may take to close once the process has exited or been killed.
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadyRetryDelay = TimeSpan.FromMilliseconds(500);

    // Compose's own selection variables. Each could redirect the call to another project, file, profile or env file.
    private static readonly string[] ComposeSelectionVariables =
        ["COMPOSE_PROJECT_NAME", "COMPOSE_FILE", "COMPOSE_PROFILES", "COMPOSE_ENV_FILES"];

    // Set once by InitializeAsync, before xUnit creates any test class, and only read afterwards.
    private string? _server;

    /// <summary>
    /// Selects the server and waits until it accepts connections. xUnit v3 calls this once, before the first test of
    /// the assembly runs.
    /// </summary>
    /// <returns>A task that completes once the server answers <c>SELECT 1</c> on its maintenance database.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>SMARTHOTEL_TEST_POSTGRES</c> is not a valid connection string; the repository root cannot be found; the
    /// <c>docker</c> CLI cannot be started, or <c>docker compose up</c> fails or times out; or the server does not
    /// accept connections within 15 seconds.
    /// </exception>
    public async ValueTask InitializeAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        string? configured = Environment.GetEnvironmentVariable(ServerVariable);
        NpgsqlConnectionStringBuilder server;
        if (string.IsNullOrEmpty(configured))
        {
            await StartComposeServiceAsync(cancellationToken);
            server = new NpgsqlConnectionStringBuilder(DefaultServer);
        }
        else
        {
            server = ParseConfiguredServer(configured);
        }

        // Applied to a supplied string as well: whatever it says, PostgreSQL's DETAIL text stays redacted.
        server.IncludeErrorDetail = false;
        _server = server.ConnectionString;

        await WaitUntilReadyAsync(cancellationToken);
    }

    /// <summary>
    /// Clears every Npgsql connection pool of the test process. The server itself is left running.
    /// </summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Builds the connection string for one database on the selected server.
    /// </summary>
    /// <param name="database">The database to connect to. The builder quotes the value.</param>
    /// <returns>
    /// The server connection string with <paramref name="database"/> as its database and <c>Include Error Detail</c>
    /// off. It can carry a password, so callers must never write it to output.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="database"/> is <see langword="null"/>, empty or blank.
    /// </exception>
    /// <exception cref="InvalidOperationException">The fixture has not been initialized.</exception>
    public string ConnectionStringFor(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        string server = _server
            ?? throw new InvalidOperationException(
                "TestPostgres has not been initialized, so no PostgreSQL server is selected yet.");

        return new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString;
    }

    /// <summary>
    /// Creates an empty test database on the selected server.
    /// </summary>
    /// <param name="name">
    /// The database name, which must be <c>smarthotel_it_</c> followed by 32 lowercase hex digits.
    /// </param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>A task that completes once the database exists.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> does not have the test database form.</exception>
    /// <exception cref="InvalidOperationException">The fixture has not been initialized.</exception>
    public async Task CreateDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        EnsureTestDatabaseName(name);

        await ExecuteOnMaintenanceDatabaseAsync($"CREATE DATABASE \"{name}\"", cancellationToken);
    }

    /// <summary>
    /// Drops a test database from the selected server, ending any session still connected to it. A database that does
    /// not exist is not an error.
    /// </summary>
    /// <param name="name">
    /// The database name, which must be <c>smarthotel_it_</c> followed by 32 lowercase hex digits.
    /// </param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>A task that completes once the database no longer exists.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> does not have the test database form.</exception>
    /// <exception cref="InvalidOperationException">The fixture has not been initialized.</exception>
    public async Task DropDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        EnsureTestDatabaseName(name);

        // ClearPool takes a connection instance and clears the pool its connection string selects.
        using (NpgsqlConnection pooled = new(ConnectionStringFor(name)))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        // FORCE (PostgreSQL 13+) ends the sessions that remain, such as connections a host's data source still pools.
        await ExecuteOnMaintenanceDatabaseAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", cancellationToken);
    }

    private static NpgsqlConnectionStringBuilder ParseConfiguredServer(string configured)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(configured);
        }
        catch (ArgumentException)
        {
            // The inner exception is left out on purpose: its message can quote part of the value.
            throw new InvalidOperationException($"{ServerVariable} is not a valid Npgsql connection string.");
        }
    }

    private static async Task StartComposeServiceAsync(CancellationToken cancellationToken)
    {
        string root = FindRepositoryRoot();

        ProcessStartInfo startInfo = new("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // One item per argument and no shell, so a root path with spaces reaches Compose intact. The empty env file
        // keeps Compose from reading a developer's .env.
        string[] arguments =
        [
            "compose",
            "-p", ComposeProject,
            "-f", Path.Combine(root, "docker-compose.yml"),
            "--project-directory", root,
            "--env-file", Path.Combine(root, "deploy", "acceptance.env"),
            "--profile", ComposeProfile,
            "up", "-d", "--wait", ComposeService,
        ];
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        NeutralizeComposeEnvironment(startInfo.Environment);

        using Process process = new() { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("The docker CLI could not be started. " + Hint, exception);
        }

        // Both streams are drained concurrently from the start, so a full pipe buffer can never stall Compose.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ComposeTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            cancellationToken.ThrowIfCancellationRequested();

            string partialOutput = await ReadCombinedOutputAsync(standardOutput, standardError);
            throw new InvalidOperationException(
                $"docker compose up {ComposeService} did not finish within 5 minutes.{Environment.NewLine}"
                + $"{partialOutput}{Environment.NewLine}{Hint}");
        }

        if (process.ExitCode != 0)
        {
            // Safe to report: db-test has no secret, and the child environment carries no SMARTHOTEL_* value.
            string output = await ReadCombinedOutputAsync(standardOutput, standardError);
            throw new InvalidOperationException(
                $"docker compose up {ComposeService} failed with exit code {process.ExitCode}.{Environment.NewLine}"
                + $"{output}{Environment.NewLine}{Hint}");
        }

        // Drained before the process is disposed, so no read is left running on its streams.
        _ = await ReadCombinedOutputAsync(standardOutput, standardError);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find {SolutionFileName} above the test output directory. " + Hint);
    }

    // db-test reads none of the SMARTHOTEL_* variables, and a developer's malformed port value would otherwise break
    // interpolation of the shared Compose file, so they go along with Compose's own selection variables. Names are
    // compared ignoring case because Windows environment names are case-insensitive; on other hosts the extra matches
    // are names nothing here reads.
    private static void NeutralizeComposeEnvironment(IDictionary<string, string?> environment)
    {
        List<string> neutralized = environment.Keys
            .Where(static key => ComposeSelectionVariables.Contains(key, StringComparer.OrdinalIgnoreCase)
                || key.StartsWith(ProjectVariablePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (string key in neutralized)
        {
            environment.Remove(key);
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The process exited between the timeout and the kill, so nothing is left to stop.
        }
    }

    // Bounded, because a killed Compose may leave a stream open; its output is then reported as unavailable.
    private static async Task<string> ReadCombinedOutputAsync(Task<string> standardOutput, Task<string> standardError)
    {
        try
        {
            await Task.WhenAll(standardOutput, standardError).WaitAsync(OutputDrainTimeout);
        }
        catch (TimeoutException)
        {
            return "(Compose output is unavailable: its streams did not close.)";
        }

        StringBuilder combined = new();
        combined.Append((await standardOutput).TrimEnd());
        string error = (await standardError).TrimEnd();
        if (combined.Length > 0 && error.Length > 0)
        {
            combined.AppendLine();
        }

        combined.Append(error);
        return combined.Length > 0 ? combined.ToString() : "(Compose wrote no output.)";
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        string maintenance = ConnectionStringFor(MaintenanceDatabase);
        Stopwatch elapsed = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                await using NpgsqlConnection connection = new(maintenance);
                await connection.OpenAsync(cancellationToken);
                await using NpgsqlCommand command = new("SELECT 1", connection);
                await command.ExecuteScalarAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException or SocketException)
            {
                // Npgsql messages never contain the password, so the last failure is kept as the inner exception.
                if (elapsed.Elapsed + ReadyRetryDelay >= ReadyTimeout)
                {
                    throw new InvalidOperationException(
                        "PostgreSQL did not accept connections within 15 seconds. " + Hint, exception);
                }
            }

            await Task.Delay(ReadyRetryDelay, cancellationToken);
        }
    }

    private async Task ExecuteOnMaintenanceDatabaseAsync(string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(ConnectionStringFor(MaintenanceDatabase));
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Identifiers cannot be parameters, so a name reaches SQL text only in this exact form. \z, not $, because $ also
    // matches before a trailing newline.
    private static void EnsureTestDatabaseName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!DatabaseNamePattern().IsMatch(name))
        {
            throw new ArgumentException(
                "Database name must match smarthotel_it_<32 lowercase hex digits>.", nameof(name));
        }
    }

    [GeneratedRegex(@"^smarthotel_it_[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNamePattern();
}