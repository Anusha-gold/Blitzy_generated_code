using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
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
/// selects the server and no Compose call is made; it must set <c>Host</c>, and <c>SSL Mode=VerifyFull</c> unless
/// every host it names is <c>localhost</c>, a loopback address or a Unix-domain socket, as
/// <see cref="InitializeAsync"/> describes. A blank value counts as unset. Otherwise the fixture starts the
/// repository's <c>db-test</c> Compose service, a passwordless server without TLS published on host loopback port
/// 55432, in its own Compose project <c>smarthotel-test</c>, and leaves it running: its data lives on tmpfs and the
/// next run reuses it. The server user must be a superuser, or have <c>CREATEDB</c> and
/// <c>CREATEROLE</c> and, for each server log setting that could record the statement that sets a test role's
/// password, either the <c>SET</c> privilege on it or a server value that keeps statement text out of the log; the
/// remarks on <see cref="CreateDatabaseAsync"/> list those settings and values.
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
/// in exceptions and logs. Each one is pooled without multiplexing, at Npgsql's default pool limits, whatever pool
/// settings the server connection string carries, so a test can always derive an unpooled string from it. None names
/// a client certificate. Those on <c>db-test</c>, which offers no TLS, have <c>SSL Mode=Disable</c>. The others keep
/// the <c>SSL Mode</c> of the string <c>SMARTHOTEL_TEST_POSTGRES</c> supplies, which is <c>VerifyFull</c> whenever one
/// of its hosts is not local, and the fixture refuses to start whenever Npgsql could present an ambient certificate
/// on them, as <see cref="InitializeAsync"/> describes.
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
        "The value does not have the form of a connection string that CreateDatabaseConnectionString returns.";

    // Npgsql 8's own defaults for the pool settings, which every test database's connection string carries whatever
    // the server string sets: no idle session kept open, at most 100 sessions, an idle one closed after 300 seconds and
    // looked for every 10 seconds, and no limit on a session's age. The lifetimes are in seconds.
    private const int DefaultMinPoolSize = 0;
    private const int DefaultMaxPoolSize = 100;
    private const int DefaultConnectionIdleLifetime = 300;
    private const int DefaultConnectionPruningInterval = 10;
    private const int DefaultConnectionLifetime = 0;

    // Where Npgsql 8 looks for a client certificate that a connection string does not name, in this order: the file
    // the PGSSLCERT environment variable names, and postgresql.crt in the PostgreSQL directory under the user's home,
    // which is .postgresql under HOME, or postgresql under APPDATA on Windows.
    private const string ClientCertificateVariable = "PGSSLCERT";
    private const string ClientCertificateFile = "postgresql.crt";

    // Where the ambient certificate that a test database's connection would present comes from, in fixed words that
    // name neither the variable's value nor the file's path.
    private const string ClientCertificateFromVariable = "the PGSSLCERT environment variable is set";
    private const string ClientCertificateFromFile =
        "a postgresql.crt file exists in the PostgreSQL directory under the user's home (.postgresql under HOME, or "
        + "postgresql under APPDATA on Windows)";

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

    // PostgreSQL decides whether to log every statement of a transaction when the transaction starts, so this setting
    // is changed for the session before the transaction that sets a password begins, and reset once it has ended.
    private const string TransactionSampleRate = "log_transaction_sample_rate";

    // Every way the server log can record the text of the statement that sets a login role's password. Each way is
    // the settings that open it, each with the value, as pg_settings shows it, that keeps the text out; the way is
    // closed when the server user may change one of its settings, or one of them already has that value. In order:
    // log_statement records every DDL statement unless it is none; log_min_error_statement attaches the statement to
    // any server log message below panic, such as a failure, a cancellation or a lock wait; log_min_duration_statement
    // records a slow statement; log_min_duration_sample and log_statement_sample_rate record a sampled one, unless
    // either is off; log_transaction_sample_rate records every statement of a sampled transaction; and the three
    // debug_print settings write the statement's parse, rewritten or plan tree, which holds the verifier.
    private static readonly (string Name, string Off)[][] StatementTextLogging =
    [
        [("log_statement", "none")],
        [("log_min_error_statement", "panic")],
        [("log_min_duration_statement", "-1")],
        [("log_min_duration_sample", "-1"), ("log_statement_sample_rate", "0")],
        [(TransactionSampleRate, "0")],
        [("debug_print_parse", "off")],
        [("debug_print_rewritten", "off")],
        [("debug_print_plan", "off")],
    ];

    // Both set once by InitializeAsync, before xUnit creates any test class, and only read afterwards.
    private string? _server;
    private bool _selectedByVariable;

    /// <summary>
    /// Selects the server and waits until it accepts connections. xUnit v3 calls this once, before the first test of
    /// the assembly runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server connection string's <c>SSL Mode</c> and <c>Root Certificate</c> reach every connection the fixture
    /// makes and every test database's connection string. The <c>db-test</c> Compose service is published on host
    /// loopback only and offers no TLS, so the fixture connects to it with <c>SSL Mode=Disable</c>. A string that
    /// <c>SMARTHOTEL_TEST_POSTGRES</c> supplies keeps its own <c>SSL Mode</c> and <c>Root Certificate</c>, and any
    /// <c>SSL Mode</c> is accepted while every host it names is local: <c>localhost</c>, a loopback address or a
    /// Unix-domain socket, each host of a multi-host <c>Host</c> list counted, without its <c>:port</c> suffix. Once
    /// one host is not local, the string must set <c>SSL Mode=VerifyFull</c>, which encrypts the connection and
    /// verifies the server certificate's chain and host name: under any other mode the server user's login and the
    /// statements that provision the test roles could cross the network in plain text, or reach a server whose
    /// identity nothing checked. The fixture refuses a string that breaks this rule as soon as it is parsed, before it
    /// first connects to the server.
    /// </para>
    /// <para>
    /// A test database's connection string names no client certificate, and Npgsql 8 then presents an ambient one on
    /// every connection that negotiates TLS: the file the <c>PGSSLCERT</c> environment variable names, even when it is
    /// empty, or else <c>postgresql.crt</c> in the PostgreSQL directory under the user's home. No connection string
    /// setting turns that fallback off except <c>SSL Mode=Disable</c>, and it would give every test database's login
    /// role the same TLS identity. Whether a test database's connection negotiates TLS is settled only when it opens,
    /// under that database's own login and on whichever host of the string answers it, so no connection the fixture
    /// makes beforehand can rule it out. Once the server string is selected, and before the fixture first connects to
    /// the server, the fixture therefore fails whenever the string's <c>SSL Mode</c> is not <c>Disable</c> and Npgsql
    /// would find such a certificate. A <c>db-test</c> run is never refused for one, because its connections skip the
    /// TLS request. A server whose hosts are all local may avoid the certificate with <c>SSL Mode=Disable</c>; any
    /// other server uses <c>VerifyFull</c>, so its run needs the certificate out of Npgsql's reach. The environment and
    /// the certificate are only read, never changed.
    /// </para>
    /// </remarks>
    /// <returns>
    /// A task that completes once the server connection string's transport is accepted, no ambient client certificate
    /// could reach a test database's connection, and the server answers <c>SELECT 1</c> on its maintenance database.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// <c>SMARTHOTEL_TEST_POSTGRES</c> is not a valid connection string, sets no <c>Host</c>, or names a host that is
    /// neither <c>localhost</c>, a loopback address nor a Unix-domain socket without setting
    /// <c>SSL Mode=VerifyFull</c>, which ends the run before any connection to the server; the repository root cannot
    /// be found; the <c>docker</c> CLI cannot be started, or <c>docker compose up</c> fails or times out; Npgsql would
    /// present an ambient client certificate on the test databases' connections, which ends the run before any
    /// connection to the server; the server rejects the login (SQLSTATE class 28), which ends the wait at once; a
    /// connection attempt fails in a way no retry changes; or the server does not accept connections within 15
    /// seconds.
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

            // db-test offers no TLS, so its connections skip the TLS request and no ambient client certificate can
            // reach them; the default run is therefore never refused for one. The test databases' strings carry this
            // SSL Mode over.
            server = new NpgsqlConnectionStringBuilder(DefaultServer)
            {
                SslMode = SslMode.Disable,
            };
            selectedByVariable = false;
        }
        else
        {
            server = ParseConfiguredServer(configured);
            selectedByVariable = true;
        }

        // Applied to a supplied string as well: whatever it says, PostgreSQL's DETAIL text stays redacted.
        server.IncludeErrorDetail = false;

        // The test databases' strings take their SSL Mode from the server string. A refused string is never kept, the
        // server is never reached with it, and no test database exists yet.
        RejectAmbientClientCertificate(server.SslMode);

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
    /// certificate, and <c>Include Error Detail</c> off. It is pooled without multiplexing, with Npgsql's default pool
    /// settings: <c>Minimum Pool Size</c> 0, <c>Maximum Pool Size</c> 100, <c>Connection Idle Lifetime</c> 300,
    /// <c>Connection Pruning Interval</c> 10 and <c>Connection Lifetime</c> 0. It carries the password, so callers
    /// must never write it to output.
    /// </returns>
    /// <remarks>
    /// The pool and multiplexing settings of the server connection string never reach the string returned here, so
    /// every test database's pool behaves the same whichever server is selected. The API pools its sessions under
    /// this string. A caller that needs sessions outside that pool derives an unpooled string from it by setting
    /// <c>Pooling</c> to <see langword="false"/>, which Npgsql accepts because multiplexing is off. Every other
    /// setting of the server connection string, such as <c>SSL Mode</c>, <c>Root Certificate</c>, the timeouts and
    /// the keepalive, carries over unchanged.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> does not have the test database form.</exception>
    /// <exception cref="InvalidOperationException">The fixture has not been initialized.</exception>
    public string CreateDatabaseConnectionString(string name)
    {
        EnsureTestDatabaseName(name);

        // Every credential the server string may hold is replaced or removed: its user and password, its password file,
        // and a client certificate with its key, so no test database inherits the server user's identity. The pool
        // settings are replaced as well: pooled, so the tests can observe the API's pooled sessions, and without
        // multiplexing, which Npgsql rejects on the unpooled strings the tests derive, at Npgsql's default limits.
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
            Pooling = true,
            Multiplexing = false,
            MinPoolSize = DefaultMinPoolSize,
            MaxPoolSize = DefaultMaxPoolSize,
            ConnectionIdleLifetime = DefaultConnectionIdleLifetime,
            ConnectionPruningInterval = DefaultConnectionPruningInterval,
            ConnectionLifetime = DefaultConnectionLifetime,
        };

        return database.ConnectionString;
    }

    /// <summary>
    /// Creates the login role and the empty test database that a connection string from
    /// <see cref="CreateDatabaseConnectionString"/> names. The role may log in with that string's password and has no
    /// other attribute. The database is owned by the role, and <c>PUBLIC</c>'s <c>CONNECT</c> and <c>TEMPORARY</c>
    /// privileges on it are revoked, so no other test database's role may connect to it. The role, superusers and the
    /// server user that provisions it may connect: the server user is granted the role with <c>INHERIT TRUE</c>, so it
    /// inherits the owner's privileges.
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
    /// afterwards, by an <c>ALTER ROLE</c> in a transaction of its own, and only once no server log setting can record
    /// that statement's text: <c>log_statement</c>, <c>log_min_error_statement</c>,
    /// <c>log_min_duration_statement</c>, <c>log_min_duration_sample</c> with <c>log_statement_sample_rate</c>,
    /// <c>log_transaction_sample_rate</c>, and <c>debug_print_parse</c>, <c>debug_print_rewritten</c> and
    /// <c>debug_print_plan</c>. Each of them that the server user may change is turned off for that transaction
    /// alone, so every other statement is logged as the server is configured: a superuser may change all of them, and
    /// any other server user the <c>debug_print</c> settings and each setting it holds the <c>SET</c> privilege on.
    /// Each one it may not change must already keep the text out: <c>log_statement</c> <c>none</c>,
    /// <c>log_min_error_statement</c> <c>panic</c>, <c>log_min_duration_statement</c> <c>-1</c>,
    /// <c>log_min_duration_sample</c> <c>-1</c> or <c>log_statement_sample_rate</c> <c>0</c>, and
    /// <c>log_transaction_sample_rate</c> <c>0</c>. Otherwise the <c>ALTER ROLE</c> is never sent, and the call fails
    /// with a message that names those settings and the remedies: a superuser as the server user,
    /// <c>GRANT SET ON PARAMETER</c> on them to the server user, or server values that keep statement text out of the
    /// log. The server's own settings are never changed. The exceptions raised here never quote a statement, so
    /// neither the password nor the verifier appears in their messages.
    /// </para>
    /// <para>
    /// When a step fails, dropping the database and dropping the role are both attempted, each only if it exists and
    /// neither cancelled by <paramref name="cancellationToken"/>, before the failure is reported. When both drops
    /// succeed, the failed call leaves nothing behind, and a cancelled <paramref name="cancellationToken"/> is reported
    /// as an <see cref="OperationCanceledException"/>. When either fails, the database or the role may remain, and the
    /// message says that dropping them failed too, even when <paramref name="cancellationToken"/> was cancelled. Except
    /// for that cancellation, the failure is reported as <see cref="DescribeServerFailure"/> describes it, in an
    /// <see cref="InvalidOperationException"/>. None carries an inner exception, because PostgreSQL's and Npgsql's
    /// messages can quote the username or the database; a refusal to set the password adds its own fixed message,
    /// which names server settings only.
    /// </para>
    /// </remarks>
    /// <param name="connectionString">
    /// A connection string that <see cref="CreateDatabaseConnectionString"/> returned.
    /// </param>
    /// <param name="cancellationToken">Cancels the statements. The cleanup after a failure is never cancelled.</param>
    /// <returns>A task that completes once the role and the database exist.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="connectionString"/> is <see langword="null"/> (an <see cref="ArgumentNullException"/>), empty or
    /// blank, does not parse as an Npgsql connection string, or does not have the form
    /// <see cref="CreateDatabaseConnectionString"/> gives it: a <c>Database</c> of <c>smarthotel_it_</c> and 32
    /// lowercase hex digits, a <c>Username</c> exactly equal to it, and a <c>Password</c> of 64 lowercase hex
    /// characters. No other setting is compared, so a string that passes need not be one this fixture returned.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The fixture has not been initialized, or a step failed, including the refusal to set the password while the
    /// server log could record it; the message is then the one <see cref="DescribeServerFailure"/> builds, which also
    /// says when dropping the database and the role again failed and, for that refusal, names the settings, and it
    /// carries no inner exception. A failed drop is reported this way even when <paramref name="cancellationToken"/>
    /// was cancelled, and the database or the role may then remain.
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

        // Every statement runs in one session that ends with this call: unpooled, so the setting SetLoginPasswordAsync
        // changes for the session never passes to a later user of the connection, even when a step fails before it is
        // reset. The maintenance string is never multiplexed, which Npgsql allows only on a pooled connection and which
        // could spread the statements over several sessions.
        string provisioning = new NpgsqlConnectionStringBuilder(maintenance)
        {
            Pooling = false,
        }.ConnectionString;

        try
        {
            await using NpgsqlConnection connection = new(provisioning);
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

            // A refusal to set the password is fixed text that names server settings only, so its message is reported.
            string refusal = creationFailure is StatementLoggingUnsafeException unsafeLogging
                ? " " + unsafeLogging.Message
                : string.Empty;

            if (cleanupFailure is not null)
            {
                // Reported even when the run is being cancelled: the database or the role may be left behind.
                throw new InvalidOperationException(DescribeServerFailure(
                    "Creating a test database and its login role failed, and so did dropping them again." + refusal,
                    new AggregateException(creationFailure, cleanupFailure)));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // Raised afresh, so that no provider exception rides along as its inner exception.
                throw new OperationCanceledException(cancellationToken);
            }

            throw new InvalidOperationException(DescribeServerFailure(
                "Creating a test database and its login role failed." + refusal,
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
    /// <paramref name="connectionString"/> is <see langword="null"/> (an <see cref="ArgumentNullException"/>), empty or
    /// blank, does not parse as an Npgsql connection string, or does not have the form
    /// <see cref="CreateDatabaseConnectionString"/> gives it: a <c>Database</c> of <c>smarthotel_it_</c> and 32
    /// lowercase hex digits, a <c>Username</c> exactly equal to it, and a <c>Password</c> of 64 lowercase hex
    /// characters. No other setting is compared, so a string that passes need not be one this fixture returned.
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
    /// The description holds <paramref name="summary"/>, the exception type names and any SQLSTATE, as
    /// <see cref="DescribeFailure"/> builds them: at most <see cref="MaxDescribedExceptions"/> (32) type names from a
    /// breadth-first walk of <paramref name="exception"/> and the exceptions inside it, outermost first, ending with
    /// <c>, and more.</c> when the walk stopped short, and the SQLSTATE of the first <see cref="PostgresException"/>
    /// among the exceptions it visited. A fixed sentence then says which server the test databases are on: the one
    /// <c>SMARTHOTEL_TEST_POSTGRES</c> selects, or the <c>db-test</c> Compose service, with the variable named as the
    /// way to use another server. The variable's value is never quoted. Callers throw a new exception with this
    /// description and never attach <paramref name="exception"/> as its inner exception.
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
    /// The description holds <paramref name="summary"/>, then the type names of the exceptions a breadth-first walk
    /// visits, in that order and so outermost first. The walk starts at <paramref name="exception"/> and goes from an
    /// <see cref="AggregateException"/> to each item of its <see cref="AggregateException.InnerExceptions"/>, and
    /// from any other exception to its <see cref="Exception.InnerException"/>. Each distinct exception, compared by
    /// reference, is visited once, so a cycle ends the walk. The walk stops after
    /// <see cref="MaxDescribedExceptions"/> (32) type names: when it reaches one more distinct exception, the list
    /// ends with <c>, and more.</c> instead of <c>.</c>, and the exceptions left unvisited add nothing. The SQLSTATE
    /// of the first <see cref="PostgresException"/> among the visited exceptions, in that order, follows the list.
    /// Messages, <see cref="Exception.Data"/> and every other PostgreSQL field, such as the message text, detail, hint
    /// and the names of the constraint, table or column, are left out, because they can quote the username, the
    /// database, part of the connection string or a key value. Callers therefore throw a new exception with this
    /// description and never attach <paramref name="exception"/> as its inner exception.
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

    // Parses the string SMARTHOTEL_TEST_POSTGRES supplies and checks it before anything connects with it. Npgsql reads
    // neither PGHOST nor PGSSLMODE, so the Host and SSL Mode checked here are the ones every connection uses.
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

        // Off the machine, every other SSL Mode exposes the server user's login and the statements that provision the
        // test roles: Disable never encrypts, Allow and Prefer fall back to plain text silently, Require verifies no
        // certificate and VerifyCA no host name. A local host may keep any SSL Mode, because its connections never
        // leave the machine. The message names neither the value nor a host.
        if (server.SslMode != SslMode.VerifyFull && !AreAllHostsLocal(server.Host))
        {
            throw new InvalidOperationException(
                $"{ServerVariable} names a host that is neither localhost, a loopback address nor a Unix-domain "
                + "socket, so it must set SSL Mode=VerifyFull, which encrypts the connection and verifies the server "
                + "certificate's chain and host name. Every host of a multi-host Host list counts, and SSL Mode "
                + "applies to all of them. No connection was made. Set SSL Mode=VerifyFull, with Root Certificate "
                + "naming the certificate authority when the system does not trust the server's certificate, or "
                + "select a test server on the loopback interface.");
        }

        return server;
    }

    // Whether every host of a Host value is local, so that no connection to it leaves the machine. The value is read
    // as Npgsql 8 reads it, and Npgsql orders its Unix-domain socket test and its :port split differently for the two
    // shapes a value takes, so each shape is classified in Npgsql's order for it. A value without a comma is one host,
    // which Npgsql takes exactly as written: it tests the whole value for a socket first, and only when that fails
    // splits off a :port and tests the host left. That value is therefore classified untrimmed in the same order: a
    // padded value such as ' localhost' is not local, and /tmp:5432, or a:5432 on Windows, is a socket path. A value
    // with a comma is a multi-host list, which Npgsql splits at its commas, trims entry by entry and splits each
    // entry's :port before it tests the host left for a socket. Each trimmed entry is therefore split first and never
    // tested whole: as a list entry, a:5432 on Windows names host a, which Npgsql reaches over TCP. Any entry that is
    // not local makes the whole value not local.
    private static bool AreAllHostsLocal(string hosts)
    {
        if (!hosts.Contains(','))
        {
            return IsUnixSocket(hosts) || IsLocalHost(SplitOffPort(hosts));
        }

        foreach (string entry in hosts.Split(','))
        {
            if (!IsLocalHost(SplitOffPort(entry.Trim())))
            {
                return false;
            }
        }

        return true;
    }

    // Classifies one host, its :port already split off, as Npgsql 8 connects to it. The host is local when it is a
    // Unix-domain socket, localhost in any case, or an address IPAddress parses as a loopback address: [::1], any
    // 127.0.0.0/8 address in a form IPAddress accepts, or its IPv4-mapped form. Anything else, a blank host included,
    // is not local, so a value this cannot classify is held to the stricter rule.
    private static bool IsLocalHost(ReadOnlySpan<char> host)
    {
        return IsUnixSocket(host)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address));
    }

    // Npgsql 8's Unix-domain socket test: a path the running platform treats as rooted, or a name that starts with @,
    // an abstract socket. A blank host is no socket.
    private static bool IsUnixSocket(ReadOnlySpan<char> host)
    {
        return !host.IsEmpty && (Path.IsPathRooted(host) || host[0] == '@');
    }

    // Npgsql 8's :port split. The host is the part before the last colon, taken only when no other colon precedes it
    // or the ] of a bracketed IPv6 literal lies between the two, so [::1]:5432 gives [::1] and ::1 keeps its colons.
    // Any other value is the host whole. The port is never parsed, so this never throws: Npgsql rejects a malformed
    // port itself, when it first connects.
    private static ReadOnlySpan<char> SplitOffPort(ReadOnlySpan<char> entry)
    {
        int portColon = entry.LastIndexOf(':');
        if (portColon != -1)
        {
            int previousColon = entry[..portColon].LastIndexOf(':');
            int closingBracket = entry.LastIndexOf(']');
            if (previousColon == -1 || (portColon > closingBracket && previousColon < closingBracket))
            {
                return entry[..portColon];
            }
        }

        return entry;
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

        // Unpooled, so the per-attempt limits set below never leave a pool behind for each value they take, which
        // Npgsql accepts because the maintenance string is never multiplexed. A cancellation timeout of -1 abandons a
        // timed-out or cancelled attempt at once, instead of first waiting up to two seconds for an answer to a
        // cancellation request, which a stalled server never sends.
        NpgsqlConnectionStringBuilder probe = new(MaintenanceConnectionString())
        {
            Pooling = false,
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

    // Fails when a test database's connection could present an ambient client certificate, as InitializeAsync
    // describes. Npgsql 8 sends a TLS request on every connection whose SSL Mode is not Disable, under Allow once a
    // login without TLS has failed, and offers the certificate in the TLS handshake whenever the other end accepts that
    // request. Each test database's connection settles that for itself, under its own login and on whichever host
    // answers it, so only SSL Mode=Disable keeps an ambient certificate away from it. The message names where the
    // certificate comes from, never its value or path.
    private static void RejectAmbientClientCertificate(SslMode sslMode)
    {
        if (sslMode == SslMode.Disable)
        {
            return;
        }

        string? source = FindAmbientClientCertificate();
        if (source is null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Npgsql would present one ambient TLS client certificate on the connections of every test database, "
            + $"because {source}, so the test databases' login roles would share one TLS identity. For the test run, "
            + $"unset {ClientCertificateVariable} and move {ClientCertificateFile} out of the PostgreSQL directory "
            + $"under the user's home. Only a test server reached through localhost, a loopback address or a "
            + $"Unix-domain socket may instead be selected through {ServerVariable} with SSL Mode=Disable in its "
            + $"connection string; a server on any other host requires SSL Mode=VerifyFull.");
    }

    // Mirrors where Npgsql 8 finds a client certificate for a connection string that names none, and returns which
    // source it would use, or null when it would find none. PGSSLCERT counts once it is set, even to an empty value,
    // as it does for Npgsql, which then never looks for the file. Npgsql looks for the key only once it has found a
    // certificate, so the key's sources decide nothing here.
    private static string? FindAmbientClientCertificate()
    {
        if (Environment.GetEnvironmentVariable(ClientCertificateVariable) is not null)
        {
            return ClientCertificateFromVariable;
        }

        bool windows = OperatingSystem.IsWindows();
        string? home = Environment.GetEnvironmentVariable(windows ? "APPDATA" : "HOME");
        return home is not null
            && File.Exists(Path.Combine(home, windows ? "postgresql" : ".postgresql", ClientCertificateFile))
            ? ClientCertificateFromFile
            : null;
    }

    // The selected server's own connection string, with the server user's credentials. It stays inside this class.
    private string ServerConnectionString()
    {
        return _server ?? throw new InvalidOperationException(NotInitialized);
    }

    // The server connection string on the maintenance database, where the fixture's statements run one after another.
    // It is never multiplexed, whatever the server string asks: Npgsql accepts multiplexing only on a pooled
    // connection, and the server string may turn pooling off, or a caller may. Its pooling and every other setting
    // stay as the server string has them.
    private string MaintenanceConnectionString()
    {
        return new NpgsqlConnectionStringBuilder(ServerConnectionString())
        {
            Database = MaintenanceDatabase,
            Multiplexing = false,
        }.ConnectionString;
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

    // Gives the role its password with the one statement whose text carries the verifier, in a transaction of its own,
    // and sends that statement only once no way in StatementTextLogging can copy its text into the server log, whether
    // it succeeds, fails, is cancelled or runs slowly. Every setting of those ways that the server user may change is
    // turned off for that transaction alone: log_transaction_sample_rate for the session, before the transaction
    // begins, and reset once it has committed, and every other one with SET LOCAL, which lasts only until the
    // transaction ends. Every other statement is therefore logged as the server is configured. When the server user
    // may change none of a way's settings and the server's values keep it open, the statement is never sent and the
    // StatementLoggingUnsafeException raised names those settings. Disposing the transaction uncommitted rolls it
    // back, so a failure leaves the role without a password. The connection must be unpooled and not multiplexed, so
    // that the session's setting ends with its one session when a step fails before the reset. The setting names and
    // values reach the SQL text only from StatementTextLogging.
    private static async Task SetLoginPasswordAsync(
        NpgsqlConnection connection,
        string name,
        string verifier,
        CancellationToken cancellationToken)
    {
        List<(string Name, string Off)> changes = await PlanStatementLoggingChangesAsync(connection, cancellationToken);
        List<string> sessionChanges = changes
            .Where(static change => change.Name == TransactionSampleRate)
            .Select(static change => $"SET {change.Name} = '{change.Off}'")
            .ToList();
        List<string> transactionChanges = changes
            .Where(static change => change.Name != TransactionSampleRate)
            .Select(static change => $"SET LOCAL {change.Name} = '{change.Off}'")
            .ToList();

        // Npgsql sends BEGIN only with the transaction's first command, so this runs before the transaction starts.
        await ExecuteStatementsAsync(connection, sessionChanges, transaction: null, cancellationToken);

        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await ExecuteStatementsAsync(connection, transactionChanges, transaction, cancellationToken);
            await ExecuteStatementsAsync(
                connection,
                [$"ALTER ROLE \"{name}\" PASSWORD '{verifier}'"],
                transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (sessionChanges.Count > 0)
        {
            await ExecuteStatementsAsync(
                connection,
                [$"RESET {TransactionSampleRate}"],
                transaction: null,
                cancellationToken);
        }
    }

    // Reads each setting of StatementTextLogging in this session and returns the ones to turn off: every one the
    // server user may change. A superuser may change each of them, any user one whose context is user, and any other
    // user one it holds the SET privilege on. A way that none of its settings closes, because the server user may
    // change none of them and none already has its Off value, raises StatementLoggingUnsafeException naming all of its
    // settings; a setting that pg_settings does not show counts as one that keeps its way open.
    private static async Task<List<(string Name, string Off)>> PlanStatementLoggingChangesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        Dictionary<string, (string? Value, bool Settable)> current = new(StringComparer.Ordinal);
        await using (NpgsqlCommand read = new(
            "SELECT name, setting, current_setting('is_superuser') = 'on' OR context = 'user' "
                + "OR has_parameter_privilege(name, 'SET') FROM pg_settings WHERE name = ANY(@names)",
            connection))
        {
            read.Parameters.AddWithValue(
                "names",
                StatementTextLogging.SelectMany(static way => way).Select(static setting => setting.Name).ToArray());
            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string? value = reader.IsDBNull(1) ? null : reader.GetString(1);
                current[reader.GetString(0)] = (value, reader.GetBoolean(2));
            }
        }

        List<(string Name, string Off)> changes = [];
        List<string> open = [];
        foreach ((string Name, string Off)[] way in StatementTextLogging)
        {
            bool closed = false;
            foreach ((string Name, string Off) setting in way)
            {
                if (!current.TryGetValue(setting.Name, out (string? Value, bool Settable) state))
                {
                    continue;
                }

                if (state.Settable)
                {
                    changes.Add(setting);
                    closed = true;
                }
                else if (string.Equals(state.Value, setting.Off, StringComparison.Ordinal))
                {
                    closed = true;
                }
            }

            if (!closed)
            {
                open.AddRange(way.Select(static setting => setting.Name));
            }
        }

        if (open.Count > 0)
        {
            throw new StatementLoggingUnsafeException(open);
        }

        return changes;
    }

    // Raised instead of sending the statement that sets a login role's password, while the server log could record its
    // text. The message is fixed text naming the settings that keep a way open, never a value, a username or a
    // statement, so CreateDatabaseAsync reports it in full.
    private sealed class StatementLoggingUnsafeException(IReadOnlyList<string> settings)
        : Exception(Describe(settings))
    {
        private static string Describe(IReadOnlyList<string> settings)
        {
            string names = string.Join(", ", settings);
            return "The login role's password was not set, because the server log could record the statement that "
                + $"sets it: the server user may not change the server settings {names}, and their values let that "
                + "statement's text reach the log. Use a superuser as the server user, grant the server user SET on "
                + $"them (GRANT SET ON PARAMETER {names} TO <server user>), or configure them on the server to "
                + "values that keep statement text out of the log.";
        }
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
    // sessions, stops autovacuum workers itself and waits up to 5 seconds for the ended sessions to exit. Both run on
    // one connection, in that order. The query that finds the sessions binds the database and role names as
    // parameters, because there they are values. DROP DATABASE takes the name as an identifier, which cannot be a
    // parameter, so there it reaches the SQL text quoted, in the form ParseTestDatabase validated.
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
            await using NpgsqlConnection connection = new(maintenance);
            await connection.OpenAsync(cancellationToken);

            await using (NpgsqlCommand terminate = new(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND usename = @role",
                connection))
            {
                terminate.Parameters.AddWithValue("database", name);
                terminate.Parameters.AddWithValue("role", name);
                await terminate.ExecuteNonQueryAsync(cancellationToken);
            }

            await ExecuteStatementsAsync(
                connection,
                [$"DROP DATABASE IF EXISTS \"{name}\""],
                transaction: null,
                cancellationToken);
        }
    }

    // Checks the form of a test database's connection string, not where it came from: it must be non-blank and parse,
    // its Database must be smarthotel_it_ and 32 lowercase hex digits, its Username must equal that name exactly, and
    // its Password must be 64 lowercase hex characters, as in every string CreateDatabaseConnectionString builds. No
    // other setting is compared, so passing a string that method returned stays the caller's duty. The checks keep the
    // name that reaches SQL text in its validated form and make the role the database's own. The messages never quote
    // the string, which carries a password.
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