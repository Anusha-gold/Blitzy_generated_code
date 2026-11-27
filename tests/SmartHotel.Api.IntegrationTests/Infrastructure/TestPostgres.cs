using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Npgsql;

// The attribute sits outside the file-scoped namespace, so the fixture type is named in full.
[assembly: AssemblyFixture(typeof(SmartHotel.Api.IntegrationTests.Infrastructure.TestPostgres))]

namespace SmartHotel.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The real PostgreSQL 16 server behind every integration test: an xUnit v3 assembly fixture that selects the server
/// once per run, waits until it accepts connections, and creates and drops the per-class test databases on it, each
/// with a login role of its own.
/// </summary>
/// <remarks>
/// <para>
/// When the <c>SMARTHOTEL_TEST_POSTGRES</c> environment variable is set to a non-blank value, its connection string
/// selects the server and no Compose call is made; it must set <c>Host</c>. A blank value counts as unset. Otherwise
/// the fixture starts the repository's <c>db-test</c> Compose service, a passwordless server published on host
/// loopback port 55432, in its own Compose project <c>smarthotel-test</c>, and leaves it running: its data lives on
/// tmpfs and the next run reuses it. The server user must be a superuser, or have <c>CREATEDB</c> and
/// <c>CREATEROLE</c>.
/// </para>
/// <para>
/// The fixture never sets an environment variable. Neither the server connection string nor the variable's value is
/// ever written to output or put into an exception message, because a full connection string can carry a password.
/// Its failures report fixed text, exception type names and SQLSTATE values only, and never carry a provider
/// exception as their inner exception, because PostgreSQL's and Npgsql's messages can quote the username or the
/// database. A failure that involves the server names <c>SMARTHOTEL_TEST_POSTGRES</c>, never its value, in wording
/// that fits how the server was selected: by the variable, or as the <c>db-test</c> Compose service. The server
/// connection string never leaves the fixture either: it serves only the readiness check and the statements that
/// create and drop test databases and roles on the maintenance database.
/// </para>
/// <para>
/// The only connection strings the fixture hands out are those of test databases. Each one authenticates as its
/// database's own login role, with a random password, so no two test databases share a credential, and each keeps
/// <c>Include Error Detail</c> off, so PostgreSQL's <c>DETAIL</c> text, which can contain key values, stays redacted
/// in exceptions and logs.
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

    // Bounds a failure description, however deep or wide the exception graph behind it is.
    private const int MaxDescribedExceptions = 32;

    private const string Hint =
        "Start Docker, or set SMARTHOTEL_TEST_POSTGRES to a PostgreSQL 16 server connection string.";

    // The hint once SMARTHOTEL_TEST_POSTGRES has selected the server: no Compose call was made, so Docker is beside the
    // point. It names the variable, never its value.
    private const string ConfiguredServerHint =
        "Check that SMARTHOTEL_TEST_POSTGRES selects a reachable PostgreSQL 16 server that accepts its login, or unset "
        + "it to use the db-test Compose service.";

    // Close every failure of an operation on the selected server, so its message says which server the test databases
    // are on. Each names SMARTHOTEL_TEST_POSTGRES, never its value.
    private const string ConfiguredServerNote =
        "The test databases are on the PostgreSQL server that SMARTHOTEL_TEST_POSTGRES selects.";

    private const string ComposeServerNote =
        "The test databases are on the db-test Compose service; set SMARTHOTEL_TEST_POSTGRES to a PostgreSQL 16 "
        + "server connection string to use another server.";

    private const string NotInitialized =
        "TestPostgres has not been initialized, so no PostgreSQL server is selected yet.";

    // SQLSTATE class 28, invalid authorization: the server answered and refused the login, which no retry changes.
    private const string LoginRejectedSqlStateClass = "28";

    // 32 random bytes as lowercase hex: plain ASCII, which SCRAM's SASLprep leaves unchanged.
    private const int PasswordLength = 64;

    // PostgreSQL's own defaults for the verifiers it computes: scram_iterations and a 16-byte salt.
    private const int ScramIterations = 4096;
    private const int ScramSaltLength = 16;

    private const string InvalidDatabaseConnectionString =
        "The value is not a connection string that CreateDatabaseConnectionString returned.";

    // A first run may have to pull the postgres image before db-test can become healthy.
    private static readonly TimeSpan ComposeTimeout = TimeSpan.FromMinutes(5);

    // How often the Compose call is made before a non-zero exit counts as a failure, and the pause between calls. All
    // attempts and pauses share ComposeTimeout.
    private const int ComposeAttempts = 3;
    private static readonly TimeSpan ComposeRetryDelay = TimeSpan.FromSeconds(2);

    // How long Compose's output streams may take to close once the process has exited or been killed.
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(10);

    // Well-known Docker and Compose phrases, each mapped to a fixed cause written here. A failure message names only
    // the cause, never the output it was found in. The first cause with a matching phrase wins.
    private static readonly (string Cause, string[] Phrases)[] ComposeFailureCauses =
    [
        ("the Docker daemon is not reachable",
            ["failed to connect to the docker API", "Cannot connect to the Docker daemon", "error during connect",
                "permission denied while trying to connect to the docker"]),
        ("the docker CLI has no Compose v2 plugin",
            ["unknown command: docker compose", "'compose' is not a docker command"]),
        ("a container with db-test's name already exists or another run is creating it",
            ["is already in use by container"]),
        ("host port 55432 is already in use",
            ["port is already allocated", "address already in use", "ports are not available",
                "only one usage of each socket address"]),
        ("the postgres image could not be pulled",
            ["pull access denied", "manifest unknown", "failed to resolve reference", "toomanyrequests"]),
        ("the db-test container did not become healthy", ["is unhealthy", "exited ("]),
    ];

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadyRetryDelay = TimeSpan.FromMilliseconds(500);

    // Compose's own selection variables. Each could redirect the call to another project, file, profile or env file.
    private static readonly string[] ComposeSelectionVariables =
        ["COMPOSE_PROJECT_NAME", "COMPOSE_FILE", "COMPOSE_PROFILES", "COMPOSE_ENV_FILES"];

    // Keep the text of every statement, of a failing one and of a slow one out of the server log until the transaction
    // they run in ends. Only a superuser may change these settings.
    private static readonly string[] StatementLoggingOff =
    [
        "SET LOCAL log_statement = 'none'",
        "SET LOCAL log_min_error_statement = 'panic'",
        "SET LOCAL log_min_duration_statement = -1",
    ];

    // Both set once by InitializeAsync, before xUnit creates any test class, and only read afterwards.
    private string? _server;
    private bool _selectedByVariable;

    /// <summary>
    /// Selects the server and waits until it accepts connections. xUnit v3 calls this once, before the first test of
    /// the assembly runs.
    /// </summary>
    /// <returns>A task that completes once the server answers <c>SELECT 1</c> on its maintenance database.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>SMARTHOTEL_TEST_POSTGRES</c> is not a valid connection string or sets no <c>Host</c>; the repository root
    /// cannot be found; the <c>docker</c> CLI cannot be started, or <c>docker compose up</c> fails or times out; the
    /// server rejects the login (SQLSTATE class 28), which ends the wait at once; a connection attempt fails in a way
    /// no retry changes; or the server does not accept connections within 15 seconds.
    /// </exception>
    /// <exception cref="OperationCanceledException">The test run was cancelled.</exception>
    public async ValueTask InitializeAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        string? configured = Environment.GetEnvironmentVariable(ServerVariable);
        NpgsqlConnectionStringBuilder server;
        bool selectedByVariable;

        // A blank value counts as unset, as an empty one does: it selects no server.
        if (string.IsNullOrWhiteSpace(configured))
        {
            await StartComposeServiceAsync(cancellationToken);
            server = new NpgsqlConnectionStringBuilder(DefaultServer);
            selectedByVariable = false;
        }
        else
        {
            server = ParseConfiguredServer(configured);
            selectedByVariable = true;
        }

        // Applied to a supplied string as well: whatever it says, PostgreSQL's DETAIL text stays redacted.
        server.IncludeErrorDetail = false;
        _server = server.ConnectionString;
        _selectedByVariable = selectedByVariable;

        await WaitUntilReadyAsync(selectedByVariable, cancellationToken);
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
    /// Builds the connection string of a new test database and of its own login role, which has the database's name
    /// and a fresh random password. Nothing is created on the server until <see cref="CreateDatabaseAsync"/> runs.
    /// </summary>
    /// <param name="name">
    /// The database name, which must be <c>smarthotel_it_</c> followed by 32 lowercase hex digits. It names the login
    /// role as well.
    /// </param>
    /// <returns>
    /// The server connection string's host, port and transport settings, with <paramref name="name"/> as both its
    /// database and its user, a password of 64 random lowercase hex characters, no password file and no client
    /// certificate, and <c>Include Error Detail</c> off. It carries the password, so callers must never write it to
    /// output.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> does not have the test database form.</exception>
    /// <exception cref="InvalidOperationException">The fixture has not been initialized.</exception>
    public string CreateDatabaseConnectionString(string name)
    {
        EnsureTestDatabaseName(name);

        // Every credential the server string may hold is replaced or removed: its user and password, its password file,
        // and a client certificate with its key, so no test database inherits the server user's identity.
        NpgsqlConnectionStringBuilder database = new(ServerConnectionString())
        {
            Database = name,
            Username = name,
            Password = RandomNumberGenerator.GetHexString(PasswordLength, lowercase: true),
            Passfile = null,
            SslCertificate = null,
            SslKey = null,
            SslPassword = null,
            IncludeErrorDetail = false,
        };

        return database.ConnectionString;
    }

    /// <summary>
    /// Creates the login role and the empty test database that a connection string from
    /// <see cref="CreateDatabaseConnectionString"/> names. The role may log in with that string's password and has no
    /// other attribute. The database is owned by the role, and only the role and superusers may connect to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The password reaches the server only as a SCRAM-SHA-256 verifier computed here, so its plain text never appears
    /// in a statement or a server log. Owning the database lets the role create the trusted <c>btree_gist</c>
    /// extension and the schema that the API's migration applies.
    /// </para>
    /// <para>
    /// The role is created without a password, so the statements that fail when the server user lacks
    /// <c>CREATEROLE</c> or <c>CREATEDB</c> carry no secret into the server's error log. The verifier is set
    /// afterwards, by an <c>ALTER ROLE</c> in a transaction of its own. When the server user is a superuser, that
    /// transaction first turns off, for itself alone, each setting that writes a statement's text to the server log,
    /// so no server log records the verifier. Any other server user cannot change those settings, so for it only the
    /// server's own statement logging can still record the verifier, never the plain text: <c>log_statement</c> set
    /// to <c>ddl</c> or <c>all</c> by the server's operator, and, under the default <c>log_min_error_statement</c>, a
    /// failure of that <c>ALTER ROLE</c> itself. The exceptions raised here never quote a statement, so neither the
    /// password nor the verifier appears in their messages.
    /// </para>
    /// <para>
    /// When a step fails, the database and the role are dropped again before the failure is reported, so a failed call
    /// leaves nothing behind. The failure is reported as <see cref="DescribeServerFailure"/> describes it, with no
    /// inner exception, because PostgreSQL's and Npgsql's messages can quote the username or the database.
    /// </para>
    /// </remarks>
    /// <param name="connectionString">
    /// A connection string that <see cref="CreateDatabaseConnectionString"/> returned.
    /// </param>
    /// <param name="cancellationToken">Cancels the statements. The cleanup after a failure is never cancelled.</param>
    /// <returns>A task that completes once the role and the database exist.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="connectionString"/> is not a connection string that
    /// <see cref="CreateDatabaseConnectionString"/> returned.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The fixture has not been initialized, or a step failed; the message is then the one
    /// <see cref="DescribeServerFailure"/> builds, which also says when dropping the database and the role again
    /// failed, and carries no inner exception.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, and the database and the role were dropped again.
    /// </exception>
    public async Task CreateDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        (string name, string password) = ParseTestDatabase(connectionString);
        string maintenance = MaintenanceConnectionString();
        string verifier = CreateScramSha256Verifier(password);

        // Only the validated name and the verifier's base64 parts reach the SQL text. CREATE DATABASE cannot run inside
        // a transaction, so each of these statements runs on its own. PostgreSQL 16 lets a server user that is not a
        // superuser create a database owned by another role only when it may SET ROLE to it, and treats it as that
        // owner, which DROP DATABASE and REVOKE require, only when it inherits the role: the grant gives both, and is
        // redundant for a superuser. Revoking PUBLIC's CONNECT keeps every other test database's role out of this one.
        // CREATE ROLE has no PASSWORD clause because the server's error log records the text of a failing statement,
        // and CREATE ROLE and CREATE DATABASE are the statements a server user without CREATEROLE or CREATEDB fails
        // on: the verifier is set separately, once the role exists, by SetLoginPasswordAsync.
        string[] roleStatements =
        [
            $"CREATE ROLE \"{name}\" WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS",
            $"GRANT \"{name}\" TO CURRENT_USER WITH INHERIT TRUE, SET TRUE",
        ];
        string[] databaseStatements =
        [
            $"CREATE DATABASE \"{name}\" OWNER \"{name}\"",
            $"REVOKE CONNECT, TEMPORARY ON DATABASE \"{name}\" FROM PUBLIC",
        ];

        try
        {
            await using NpgsqlConnection connection = new(maintenance);
            await connection.OpenAsync(cancellationToken);
            await ExecuteStatementsAsync(connection, roleStatements, transaction: null, cancellationToken);
            await SetLoginPasswordAsync(connection, name, verifier, cancellationToken);
            await ExecuteStatementsAsync(connection, databaseStatements, transaction: null, cancellationToken);
        }
        catch (Exception creationFailure)
        {
            // Both drops are conditional, so a step that never ran, or whose outcome a cancellation left unknown, is
            // covered as well.
            Exception? cleanupFailure = null;
            try
            {
                await DropDatabaseAndRoleAsync(maintenance, name, CancellationToken.None);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            if (cleanupFailure is not null)
            {
                // Reported even when the run is being cancelled: the database or the role may be left behind.
                throw new InvalidOperationException(DescribeServerFailure(
                    "Creating a test database and its login role failed, and so did dropping them again.",
                    new AggregateException(creationFailure, cleanupFailure)));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // Raised afresh, so that no provider exception rides along as its inner exception.
                throw new OperationCanceledException(cancellationToken);
            }

            throw new InvalidOperationException(DescribeServerFailure(
                "Creating a test database and its login role failed.",
                creationFailure));
        }
    }

    /// <summary>
    /// Drops the test database and the login role that a connection string from
    /// <see cref="CreateDatabaseConnectionString"/> names, ending any session still connected to the database. The
    /// role is dropped even when dropping the database fails, and neither one has to exist, so this also cleans up
    /// after a creation that failed partway or never ran.
    /// </summary>
    /// <param name="connectionString">
    /// A connection string that <see cref="CreateDatabaseConnectionString"/> returned.
    /// </param>
    /// <param name="cancellationToken">Cancels the statements.</param>
    /// <returns>A task that completes once neither the database nor the role exists.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="connectionString"/> is not a connection string that
    /// <see cref="CreateDatabaseConnectionString"/> returned.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The fixture has not been initialized, or dropping the database, the role or both failed; the message is then
    /// the one <see cref="DescribeServerFailure"/> builds, and carries no inner exception.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task DropDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        (string name, _) = ParseTestDatabase(connectionString);
        string maintenance = MaintenanceConnectionString();

        // ClearPool takes a connection instance and clears the pool its connection string selects: this database's
        // own string, which the API and the tests' own connections pool under.
        using (NpgsqlConnection pooled = new(connectionString))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        try
        {
            await DropDatabaseAndRoleAsync(maintenance, name, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Raised afresh, so that no provider exception rides along as its inner exception.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(DescribeServerFailure(
                "Dropping a test database and its login role failed.",
                exception));
        }
    }

    /// <summary>
    /// Describes a failed operation on the selected server, such as creating or dropping a test database, or starting,
    /// stopping or connecting a host that uses one, for test output without any value the failure carries.
    /// </summary>
    /// <remarks>
    /// The description holds <paramref name="summary"/>, the type names of <paramref name="exception"/> and of every
    /// exception inside it, outermost first, and the SQLSTATE of the first <see cref="PostgresException"/> among
    /// them, as <see cref="DescribeFailure"/> builds it. A fixed sentence then says which server the test databases
    /// are on: the one <c>SMARTHOTEL_TEST_POSTGRES</c> selects, or the <c>db-test</c> Compose service, with the
    /// variable named as the way to use another server. The variable's value is never quoted. Callers throw a new
    /// exception with this description and never attach <paramref name="exception"/> as its inner exception.
    /// </remarks>
    /// <param name="summary">A fixed sentence naming the operation that failed.</param>
    /// <param name="exception">The failure to describe.</param>
    /// <returns>The description, one line of text.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="summary"/> is <see langword="null"/>, empty or blank.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The fixture has not been initialized.</exception>
    internal string DescribeServerFailure(string summary, Exception exception)
    {
        string description = DescribeFailure(summary, exception);
        if (_server is null)
        {
            throw new InvalidOperationException(NotInitialized);
        }

        return description + " " + (_selectedByVariable ? ConfiguredServerNote : ComposeServerNote);
    }

    /// <summary>
    /// Describes a failed database or host operation for test output without any value the failure carries.
    /// </summary>
    /// <remarks>
    /// The description holds <paramref name="summary"/>, the type names of <paramref name="exception"/> and of every
    /// exception inside it, outermost first, and the SQLSTATE of the first <see cref="PostgresException"/> among
    /// them. Messages, <see cref="Exception.Data"/> and every other PostgreSQL field, such as the message text,
    /// detail, hint and the names of the constraint, table or column, are left out, because they can quote the
    /// username, the database, part of the connection string or a key value. Callers therefore throw a new exception
    /// with this description and never attach <paramref name="exception"/> as its inner exception.
    /// </remarks>
    /// <param name="summary">A fixed sentence naming the operation that failed.</param>
    /// <param name="exception">The failure to describe.</param>
    /// <returns>The description, one line of text.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="summary"/> is <see langword="null"/>, empty or blank.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    private static string DescribeFailure(string summary, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(exception);

        // Breadth-first over inner exceptions and aggregate items, each exception once, so a cycle cannot loop.
        List<string> types = [];
        PostgresException? postgres = null;
        bool truncated = false;
        HashSet<Exception> seen = new(ReferenceEqualityComparer.Instance);
        Queue<Exception> pending = new();
        pending.Enqueue(exception);
        while (pending.TryDequeue(out Exception? current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            if (types.Count == MaxDescribedExceptions)
            {
                truncated = true;
                break;
            }

            Type type = current.GetType();
            types.Add(type.FullName ?? type.Name);
            postgres ??= current as PostgresException;

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Enqueue(current.InnerException);
            }
        }

        StringBuilder description = new(summary);
        description.Append(" Exception types, outermost first: ").AppendJoin(", ", types);
        description.Append(truncated ? ", and more." : ".");
        if (postgres is not null)
        {
            description.Append(" SQLSTATE ").Append(postgres.SqlState).Append('.');
        }

        return description.ToString();
    }

    private static NpgsqlConnectionStringBuilder ParseConfiguredServer(string configured)
    {
        NpgsqlConnectionStringBuilder server;
        try
        {
            server = new NpgsqlConnectionStringBuilder(configured);
        }
        catch (ArgumentException)
        {
            // The inner exception is left out on purpose: its message can quote part of the value.
            throw new InvalidOperationException($"{ServerVariable} is not a valid Npgsql connection string.");
        }

        // Npgsql would otherwise fail only on the first open, with a message that does not name the variable.
        if (string.IsNullOrWhiteSpace(server.Host))
        {
            throw new InvalidOperationException($"{ServerVariable} must set Host.");
        }

        return server;
    }

    // Concurrent cold starts race to create the one db-test container, and every run but the winner then fails on the
    // container name. A non-zero exit therefore issues the identical command again, up to ComposeAttempts calls in all:
    // a later up --wait adopts the container another run created and waits until it is healthy. db-test is never
    // stopped, so the next run reuses it.
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

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ComposeTimeout);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                (int exitCode, string? cause) = await RunComposeAsync(startInfo, deadline.Token);
                if (exitCode == 0)
                {
                    return;
                }

                if (attempt == ComposeAttempts)
                {
                    throw new InvalidOperationException(
                        $"docker compose up {ComposeService} failed {ComposeAttempts} times; the last attempt exited "
                        + $"with code {exitCode}."
                        + (cause is null ? string.Empty : $" Likely cause: {cause}.")
                        + $"{Environment.NewLine}{Hint}");
                }

                await Task.Delay(ComposeRetryDelay, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                // The runner's cancellation stays a cancellation; only the deadline becomes a failure.
                cancellationToken.ThrowIfCancellationRequested();

                throw new InvalidOperationException(
                    $"docker compose up {ComposeService} did not finish within 5 minutes (attempt {attempt} of "
                    + $"{ComposeAttempts}).{Environment.NewLine}{Hint}");
            }
        }
    }

    // Makes one Compose call and returns its exit code, with the fixed cause FindComposeFailureCause names for a
    // non-zero exit. When the deadline passes first, the call's whole process tree is killed and the cancellation
    // propagates.
    private static async Task<(int ExitCode, string? Cause)> RunComposeAsync(
        ProcessStartInfo startInfo, CancellationToken deadline)
    {
        using Process process = new() { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                DescribeFailure("The docker CLI could not be started.", exception) + " " + Hint);
        }

        // Both streams are drained concurrently from the start, so a full pipe buffer can never stall Compose.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(deadline);
        Task<string> standardError = process.StandardError.ReadToEndAsync(deadline);
        try
        {
            await process.WaitForExitAsync(deadline);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            _ = await DrainOutputAsync(standardOutput, standardError);
            throw;
        }

        // Compose's output is never reported, not even in part: the Docker CLI and daemon can echo environment
        // values, paths and registry responses into it. A failure names only a fixed cause found in it.
        string? output = await DrainOutputAsync(standardOutput, standardError);
        return process.ExitCode == 0 ? (0, null) : (process.ExitCode, FindComposeFailureCause(output));
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

    // Waits for both output reads to end, so none is left running once the process is disposed. The wait is bounded,
    // because a killed Compose may leave a stream open. The combined text is for FindComposeFailureCause only and must
    // never be reported. Null means the streams did not close in time or the deadline cancelled the reads.
    private static async Task<string?> DrainOutputAsync(Task<string> standardOutput, Task<string> standardError)
    {
        try
        {
            await Task.WhenAll(standardOutput, standardError).WaitAsync(OutputDrainTimeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return string.Concat(await standardOutput, Environment.NewLine, await standardError);
    }

    // Returns the fixed cause whose phrases occur in Compose's output, or null when none does. Only text from
    // ComposeFailureCauses is ever returned, so no part of the output can reach a message.
    private static string? FindComposeFailureCause(string? output)
    {
        if (output is null)
        {
            return null;
        }

        foreach ((string cause, string[] phrases) in ComposeFailureCauses)
        {
            if (phrases.Any(phrase => output.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            {
                return cause;
            }
        }

        return null;
    }

    // One deadline bounds the whole wait: every connection attempt, every SELECT 1 and every pause between them. Npgsql
    // does not observe a token while it waits for a server that accepted the connection to reply, so each attempt is
    // awaited through the deadline's token: the wait ends as soon as the deadline passes or the caller cancels, and an
    // attempt still in flight is abandoned to its own limits. Cancellation by the caller's token stays cancellation,
    // raised afresh so that no provider exception rides along as its inner exception. Failures are described, never
    // attached, because PostgreSQL's and Npgsql's messages can quote the username or the database. A rejected login
    // ends the wait at once. The hint fits how the server was selected: by SMARTHOTEL_TEST_POSTGRES, or as the db-test
    // Compose service.
    private async Task WaitUntilReadyAsync(bool selectedByVariable, CancellationToken cancellationToken)
    {
        string hint = selectedByVariable ? ConfiguredServerHint : Hint;
        string loginRejected = selectedByVariable
            ? $"The PostgreSQL server {ServerVariable} selects rejected the login."
            : $"The {ComposeService} PostgreSQL server rejected the login.";

        // Unpooled, so the per-attempt limits set below never leave a pool behind for each value they take. Npgsql
        // accepts multiplexing only on a pooled connection, so the probe also turns off a multiplexing the server
        // string asks for; the strings CreateDatabaseConnectionString hands out keep
        // it. A cancellation timeout of -1 abandons a
        // timed-out or cancelled attempt at once, instead of first waiting up to two seconds for an answer to a
        // cancellation request, which a stalled server never sends.
        NpgsqlConnectionStringBuilder probe = new(MaintenanceConnectionString())
        {
            Pooling = false,
            Multiplexing = false,
            CancellationTimeout = -1,
        };
        int connectLimit = probe.Timeout;
        int commandLimit = probe.CommandTimeout;

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReadyTimeout);
        Stopwatch elapsed = Stopwatch.StartNew();

        // The latest failure that was retried, reported when the deadline then ends a stalled attempt or a pause.
        Exception? lastFailure = null;
        while (true)
        {
            // A backstop only: each attempt's own limits, in whole seconds, are cut to the time left, so an attempt the
            // deadline abandons on a server that never answers ends, and closes its connection, less than a second
            // after the deadline. Zero means no limit.
            int secondsLeft = Math.Max(1, (int)Math.Ceiling((ReadyTimeout - elapsed.Elapsed).TotalSeconds));
            probe.Timeout = connectLimit == 0 ? secondsLeft : Math.Min(connectLimit, secondsLeft);
            probe.CommandTimeout = commandLimit == 0 ? secondsLeft : Math.Min(commandLimit, secondsLeft);

            Task attempt = ProbeAsync(probe.ConnectionString, deadline.Token);
            try
            {
                await attempt.WaitAsync(deadline.Token);
                return;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                ObserveAbandonedAttempt(attempt);
                throw new OperationCanceledException(cancellationToken);
            }
            catch (PostgresException exception)
                when (exception.SqlState.StartsWith(LoginRejectedSqlStateClass, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(DescribeFailure(loginRejected, exception) + " " + hint);
            }
            catch (Exception exception) when (deadline.IsCancellationRequested)
            {
                // The deadline ended this attempt, whether the wait, Npgsql or the attempt's own limit reports it.
                ObserveAbandonedAttempt(attempt);
                throw ReadinessExpired(exception is OperationCanceledException ? lastFailure : exception, hint);
            }
            catch (Exception exception) when (exception is NpgsqlException or SocketException or TimeoutException)
            {
                lastFailure = exception;
            }
            catch (Exception exception)
            {
                // Anything else, such as a setting Npgsql rejects, fails the same way on every attempt.
                throw new InvalidOperationException(
                    DescribeFailure("Connecting to PostgreSQL failed.", exception) + " " + hint);
            }

            try
            {
                await Task.Delay(ReadyRetryDelay, deadline.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw ReadinessExpired(lastFailure, hint);
            }
        }
    }

    // One connection attempt and its SELECT 1. The attempt disposes its own connection once the operation in flight has
    // completed, so a connection the wait abandons is never disposed while Npgsql is still using it.
    private static async Task ProbeAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    // An abandoned attempt runs on until its own limits end it. Its eventual failure is observed here, so it never
    // surfaces as an unobserved task exception; nothing reports it, because the wait has already failed or been
    // cancelled.
    private static void ObserveAbandonedAttempt(Task attempt)
    {
        _ = attempt.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static InvalidOperationException ReadinessExpired(Exception? lastFailure, string hint)
    {
        const string summary = "PostgreSQL did not accept connections within 15 seconds.";
        string description = lastFailure is null
            ? summary + " No connection attempt completed."
            : DescribeFailure(summary, lastFailure);
        return new InvalidOperationException(description + " " + hint);
    }

    // The selected server's own connection string, with the server user's credentials. It stays inside this class.
    private string ServerConnectionString()
    {
        return _server ?? throw new InvalidOperationException(NotInitialized);
    }

    private string MaintenanceConnectionString()
    {
        return new NpgsqlConnectionStringBuilder(ServerConnectionString()) { Database = MaintenanceDatabase }
            .ConnectionString;
    }

    // Runs the statements in order on one connection, each as its own autocommit command, and stops at the first that
    // fails.
    private static async Task ExecuteOnMaintenanceDatabaseAsync(
        string maintenance,
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(maintenance);
        await connection.OpenAsync(cancellationToken);
        await ExecuteStatementsAsync(connection, statements, transaction: null, cancellationToken);
    }

    // Runs the statements in order on an open connection, each as its own command, and stops at the first that fails.
    // Without a transaction, each one commits on its own.
    private static async Task ExecuteStatementsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> statements,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // Gives the role its password with the one statement whose text carries the verifier, in a transaction of its own.
    // For a superuser server user, the transaction first turns off the settings that would copy that text into the
    // server log, whether the ALTER ROLE succeeds, fails or runs slowly. SET LOCAL lasts only until the transaction
    // ends, so every other statement is logged as the server is configured. Any other server user cannot change those
    // settings, so for it only the server's own statement logging can still record the verifier, never the plain text:
    // log_statement ddl or all, which the server's operator has to enable, and, under the default
    // log_min_error_statement, a failure of the ALTER ROLE itself. Disposing the transaction uncommitted rolls it back,
    // so a failure leaves the role without a password.
    private static async Task SetLoginPasswordAsync(
        NpgsqlConnection connection,
        string name,
        string verifier,
        CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        bool superuser;
        await using (NpgsqlCommand check = new(
            "SELECT current_setting('is_superuser') = 'on'",
            connection,
            transaction))
        {
            superuser = await check.ExecuteScalarAsync(cancellationToken) is true;
        }

        if (superuser)
        {
            await ExecuteStatementsAsync(connection, StatementLoggingOff, transaction, cancellationToken);
        }

        await ExecuteStatementsAsync(
            connection,
            [$"ALTER ROLE \"{name}\" PASSWORD '{verifier}'"],
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Each drop opens its own connection, so the role is still dropped when the database drop fails, even on a broken
    // connection. A role that owns a database cannot be dropped, so the database goes first. The provider exceptions
    // thrown here are only ever described by the callers, never attached to what they throw.
    private static async Task DropDatabaseAndRoleAsync(
        string maintenance,
        string name,
        CancellationToken cancellationToken)
    {
        Exception? databaseFailure = null;
        try
        {
            await DropTestDatabaseAsync(maintenance, name, cancellationToken);
        }
        catch (Exception exception)
        {
            databaseFailure = exception;
        }

        try
        {
            await ExecuteOnMaintenanceDatabaseAsync(
                maintenance,
                [$"DROP ROLE IF EXISTS \"{name}\""],
                cancellationToken);
        }
        catch (Exception roleFailure) when (databaseFailure is not null)
        {
            throw new AggregateException(
                "Dropping a test database failed, and so did dropping its login role.",
                databaseFailure,
                roleFailure);
        }

        if (databaseFailure is not null)
        {
            ExceptionDispatchInfo.Throw(databaseFailure);
        }
    }

    // FORCE (PostgreSQL 13+) ends the sessions that remain, such as connections a host's data source still pools. It
    // refuses with 42501, before it ends anything, when one of them is a process the server user holds no privileges
    // of: for a server user that is neither a superuser nor a member of pg_signal_backend, an autovacuum worker that
    // happens to be processing the database. The fallback then ends the role's own sessions, which the GRANT in
    // CreateDatabaseAsync lets the server user do, and runs a plain DROP DATABASE: that locks the database against new
    // sessions, stops autovacuum workers itself and waits up to 5 seconds for the ended sessions to exit.
    private static async Task DropTestDatabaseAsync(
        string maintenance,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteOnMaintenanceDatabaseAsync(
                maintenance,
                [$"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)"],
                cancellationToken);
        }
        catch (PostgresException refused) when (refused.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            await ExecuteOnMaintenanceDatabaseAsync(
                maintenance,
                [
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity "
                        + $"WHERE datname = '{name}' AND usename = '{name}'",
                    $"DROP DATABASE IF EXISTS \"{name}\"",
                ],
                cancellationToken);
        }
    }

    // A test database's connection string is accepted only in the form CreateDatabaseConnectionString builds, so the
    // name that reaches SQL text is validated and the role is the database's own. The messages never quote the
    // string, which carries a password.
    private static (string Name, string Password) ParseTestDatabase(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // The inner exception is left out on purpose: its message can quote part of the value.
            throw new ArgumentException(InvalidDatabaseConnectionString, nameof(connectionString));
        }

        string? name = builder.Database;
        string? password = builder.Password;
        if (name is null
            || !DatabaseNamePattern().IsMatch(name)
            || !string.Equals(builder.Username, name, StringComparison.Ordinal)
            || password is null
            || !PasswordPattern().IsMatch(password))
        {
            throw new ArgumentException(InvalidDatabaseConnectionString, nameof(connectionString));
        }

        return (name, password);
    }

    // The verifier PostgreSQL stores for a SCRAM-SHA-256 password (RFC 5802, RFC 7677), which ALTER ROLE accepts in
    // place of the plain text: SCRAM-SHA-256$<iterations>:<salt>$<StoredKey>:<ServerKey>, each key base64.
    private static string CreateScramSha256Verifier(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(ScramSaltLength);
        byte[] saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            ScramIterations,
            HashAlgorithmName.SHA256,
            SHA256.HashSizeInBytes);
        byte[] storedKey = SHA256.HashData(HMACSHA256.HashData(saltedPassword, "Client Key"u8));
        byte[] serverKey = HMACSHA256.HashData(saltedPassword, "Server Key"u8);
        CryptographicOperations.ZeroMemory(saltedPassword);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"SCRAM-SHA-256${ScramIterations}:{Convert.ToBase64String(salt)}"
                + $"${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}");
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

    [GeneratedRegex(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();
}