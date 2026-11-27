using System.Globalization;

using Npgsql;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Supplies the API's secrets: the database connection string and the JWT signing key.
/// </summary>
/// <remarks>
/// Where the values come from is up to each implementation: the production one, <see cref="EnvironmentSecretSource"/>,
/// reads the process environment, while a replacement registered in its place, such as one holding test credentials
/// in memory, supplies its own. Whatever the source, secret values must never be logged, written to the console,
/// placed in exception messages or returned in a response.
/// </remarks>
public interface ISecretSource
{
    /// <summary>
    /// Gets the full Npgsql connection string for the SmartHotel database, or <see langword="null"/>
    /// for a source that has none configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The implementation found its database configuration invalid. The message names the settings only, never a
    /// value.
    /// </exception>
    string? DatabaseConnectionString { get; }

    /// <summary>
    /// Gets the HS256 signing key text for bearer tokens, or <see langword="null"/> when none is configured.
    /// </summary>
    string? JwtSigningKey { get; }
}

/// <summary>
/// Reads the API's secrets from process environment variables through
/// <see cref="Environment.GetEnvironmentVariable(string)"/>, once, when it is constructed.
/// </summary>
/// <remarks>
/// <para>
/// It reads only process environment variables, never <c>appsettings*.json</c>, command-line arguments or any other
/// configuration provider.
/// </para>
/// <para>
/// The database connection string is <c>ConnectionStrings__SmartHotel</c>, unchanged, whenever that variable is set,
/// even to a blank value, which is rejected rather than replaced by the parts below. A set value must parse as an
/// Npgsql connection string, set a non-blank <c>Password</c> and leave <c>Passfile</c> unset. Only when the variable
/// is unset is the string assembled from <c>DB_HOST</c>, <c>DB_PORT</c> (default 5432), <c>DB_NAME</c>,
/// <c>DB_USER</c> and <c>DB_PASSWORD</c>: <c>DB_PORT</c> must then be blank or an integer between 1 and 65535, and
/// the other four must be set and not blank; their values are used unaltered. The assembled string caps the
/// connection pool at 50, while a whole <c>ConnectionStrings__SmartHotel</c> keeps its own <c>Maximum Pool Size</c>
/// or Npgsql's default. Either way the database password comes from these variables and nowhere else: Npgsql falls
/// back to a password file or <c>PGPASSWORD</c> only when a connection string has no password.
/// The signing key is <c>Jwt__SigningKey</c>, unaltered; its length is validated with the JWT options.
/// </para>
/// <para>
/// Every database failure is recorded when the source is constructed, and reading
/// <see cref="DatabaseConnectionString"/> then throws one <see cref="InvalidOperationException"/> that lists them all,
/// so the signing key is still read and one start can report every invalid setting.
/// </para>
/// <para>
/// This is a class rather than a record because a record's synthesized <c>ToString</c> would print the secrets.
/// </para>
/// </remarks>
public sealed class EnvironmentSecretSource : ISecretSource
{
    private const string ConnectionStringVariable = "ConnectionStrings__SmartHotel";
    private const string HostVariable = "DB_HOST";
    private const string PortVariable = "DB_PORT";
    private const string DatabaseVariable = "DB_NAME";
    private const string UserVariable = "DB_USER";
    private const string PasswordVariable = "DB_PASSWORD";
    private const string JwtSigningKeyVariable = "Jwt__SigningKey";
    private const int DefaultPort = 5432;
    private const int MaxPort = 65535;

    // Npgsql's default pool of 100 equals PostgreSQL's default max_connections, and the role the API logs in as can be
    // a superuser that also takes the reserved slots, so a saturated pool would lock out admin and maintenance
    // sessions. Fifty keeps that headroom and stays well above the 20 concurrent users the API is sized for.
    private const int MaximumPoolSize = 50;

    private readonly string? _databaseConnectionString;
    private readonly string? _databaseFailure;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentSecretSource"/> class from the current
    /// process environment.
    /// </summary>
    /// <remarks>
    /// The constructor does not throw for invalid settings. It records every database failure, which reading
    /// <see cref="DatabaseConnectionString"/> then reports, and still reads <see cref="JwtSigningKey"/>.
    /// </remarks>
    public EnvironmentSecretSource()
    {
        // Read once: the container entrypoint exports these variables before the process starts, so a single read at
        // construction suffices. A database failure is recorded rather than thrown, so resolving this source, as the
        // JWT options do for the signing key, still succeeds and the host can report every invalid setting together.
        List<string> databaseFailures = [];
        _databaseConnectionString = ReadDatabaseConnectionString(databaseFailures);
        _databaseFailure = databaseFailures.Count == 0
            ? null
            : "The SmartHotel database configuration is invalid. " + string.Join(" ", databaseFailures);
        JwtSigningKey = ReadNonEmpty(JwtSigningKeyVariable);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// <c>ConnectionStrings__SmartHotel</c> is set but is blank or is not a valid Npgsql connection string, or it sets
    /// <c>Passfile</c>, has no non-blank <c>Password</c>, or both; or that variable is unset and any of
    /// <c>DB_HOST</c>, <c>DB_NAME</c>, <c>DB_USER</c> and <c>DB_PASSWORD</c> is missing or blank, or <c>DB_PORT</c> is
    /// set but is not an integer between 1 and 65535. The message lists every failure, naming the variable or setting
    /// only, never a value.
    /// </exception>
    public string? DatabaseConnectionString =>
        _databaseFailure is null ? _databaseConnectionString : throw new InvalidOperationException(_databaseFailure);

    /// <inheritdoc />
    public string? JwtSigningKey { get; }

    private static string? ReadDatabaseConnectionString(List<string> failures)
    {
        string? connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (connectionString is not null)
        {
            // Once set, even to a blank value, the variable is the connection string and the DB_* parts are not
            // consulted: falling back to them would connect with settings other than the ones given here.
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                failures.Add("ConnectionStrings__SmartHotel must not be blank when it is set.");
                return null;
            }

            // Validated, then used whole: the variable's own text, not a rebuilt form, reaches the provider. Its own
            // Maximum Pool Size, or Npgsql's default, governs the pool.
            return CheckConnectionStringOverride(connectionString, failures) ? connectionString : null;
        }

        // Every part is checked before any is used, so one start names each missing or invalid variable. Only DB_PORT
        // has a default: an omitted database or user would otherwise be left to Npgsql's defaults, such as the user's
        // name for the database and the operating-system account for the user, and could reach a database other than
        // the intended one. Npgsql looks for a password in a password file (the Passfile setting, PGPASSFILE or
        // ~/.pgpass) and in PGPASSWORD only when the connection string carries none, so requiring DB_PASSWORD keeps the
        // credential to this variable.
        string? host = ReadRequired(HostVariable, failures);
        int? port = ReadPort(failures);
        string? database = ReadRequired(DatabaseVariable, failures);
        string? username = ReadRequired(UserVariable, failures);
        string? password = ReadRequired(PasswordVariable, failures);
        if (host is null || port is null || database is null || username is null || password is null)
        {
            return null;
        }

        // The builder quotes every value it writes, so DB_PASSWORD needs no escaping. Npgsql's error-detail
        // option stays unset: PostgreSQL DETAIL text can carry key values and must remain redacted.
        NpgsqlConnectionStringBuilder builder = new()
        {
            Host = host,
            Port = port.Value,
            Database = database,
            Username = username,
            Password = password,
            MaxPoolSize = MaximumPoolSize,
        };

        return builder.ConnectionString;
    }

    private static bool CheckConnectionStringOverride(string connectionString, List<string> failures)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Npgsql reports every parse failure as an ArgumentException whose message, or inner exception, can quote
            // a keyword or value from the string, and so part of a password. Neither is carried forward. A string
            // that does not parse offers no setting to check, so this is its only failure.
            failures.Add("ConnectionStrings__SmartHotel is not a valid Npgsql connection string.");
            return false;
        }

        // Npgsql consults a password file, and PGPASSWORD, only when the string carries no password, so a string that
        // names a password file or has no password could take the credential from outside this variable. Both checks
        // run, so one start names each one the string fails. The messages name the setting only.
        bool valid = true;
        if (!string.IsNullOrEmpty(builder.Passfile))
        {
            failures.Add("ConnectionStrings__SmartHotel must not set Passfile.");
            valid = false;
        }

        if (string.IsNullOrWhiteSpace(builder.Password))
        {
            failures.Add("ConnectionStrings__SmartHotel must set a non-blank Password.");
            valid = false;
        }

        return valid;
    }

    private static int? ReadPort(List<string> failures)
    {
        string? value = Environment.GetEnvironmentVariable(PortVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultPort;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port < 1
            || port > MaxPort)
        {
            // The message names the variable only; the rejected value is never echoed.
            failures.Add("DB_PORT must be an integer between 1 and 65535.");
            return null;
        }

        return port;
    }

    private static string? ReadRequired(string variable, List<string> failures)
    {
        // A whitespace-only value is as unusable as a missing one. An accepted value is returned unaltered, because
        // surrounding spaces can belong to a password. The message names the variable only, never the value.
        string? value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{variable} must be set to a non-blank value when {ConnectionStringVariable} is not set.");
            return null;
        }

        return value;
    }

    private static string? ReadNonEmpty(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}