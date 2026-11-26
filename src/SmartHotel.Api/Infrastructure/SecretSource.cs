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
    /// when none is configured.
    /// </summary>
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
/// The database connection string is <c>ConnectionStrings__SmartHotel</c>, unchanged, when that variable is set and
/// not blank; it must parse as an Npgsql connection string, set a non-blank <c>Password</c> and leave <c>Passfile</c>
/// unset. Otherwise it is assembled from <c>DB_HOST</c>, <c>DB_PORT</c> (default 5432), <c>DB_NAME</c>,
/// <c>DB_USER</c> and <c>DB_PASSWORD</c>, which must then be set and not blank; it is <see langword="null"/> when
/// <c>DB_HOST</c> is missing or blank. Either way the database password comes from these variables and nowhere else:
/// Npgsql falls back to a password file or <c>PGPASSWORD</c> only when a connection string has no password.
/// The signing key is <c>Jwt__SigningKey</c>, unaltered; its length is validated with the JWT options.
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

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentSecretSource"/> class from the current
    /// process environment.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <c>ConnectionStrings__SmartHotel</c> is set but is not a valid Npgsql connection string, sets <c>Passfile</c>,
    /// or has no non-blank <c>Password</c>; or the connection string is being assembled from its parts and
    /// <c>DB_PORT</c> is set but is not an integer between 1 and 65535, or <c>DB_PASSWORD</c> is missing or blank.
    /// The message names the variable or setting only, never a value.
    /// </exception>
    public EnvironmentSecretSource()
    {
        // Read once into get-only properties: the container entrypoint exports these variables before the
        // process starts, so a single read at construction suffices.
        DatabaseConnectionString = ReadDatabaseConnectionString();
        JwtSigningKey = ReadNonEmpty(JwtSigningKeyVariable);
    }

    /// <inheritdoc />
    public string? DatabaseConnectionString { get; }

    /// <inheritdoc />
    public string? JwtSigningKey { get; }

    private static string? ReadDatabaseConnectionString()
    {
        string? connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            // Validated, then used whole: the variable's own text, not a rebuilt form, reaches the provider.
            ValidateConnectionStringOverride(connectionString);
            return connectionString;
        }

        string? host = Environment.GetEnvironmentVariable(HostVariable);
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        // The builder quotes every value it writes, so DB_PASSWORD needs no escaping. Npgsql's error-detail
        // option stays unset: PostgreSQL DETAIL text can carry key values and must remain redacted.
        NpgsqlConnectionStringBuilder builder = new()
        {
            Host = host,
            Port = ReadPort(),
        };

        string? database = ReadNonEmpty(DatabaseVariable);
        if (database is not null)
        {
            builder.Database = database;
        }

        string? username = ReadNonEmpty(UserVariable);
        if (username is not null)
        {
            builder.Username = username;
        }

        // Npgsql looks for a password in a password file (the Passfile setting, PGPASSFILE or ~/.pgpass) and in
        // PGPASSWORD only when the connection string carries none, so requiring DB_PASSWORD keeps the credential to
        // this variable. The value is assigned unaltered, because surrounding spaces can belong to a password.
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrWhiteSpace(password))
        {
            // The message names the variable only; the rejected value is never echoed.
            throw new InvalidOperationException("DB_PASSWORD must be set to a non-blank value when DB_HOST is set.");
        }

        builder.Password = password;

        return builder.ConnectionString;
    }

    private static void ValidateConnectionStringOverride(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Npgsql reports every parse failure as an ArgumentException whose message, or inner exception, can quote
            // a keyword or value from the string, and so part of a password. Neither is carried forward.
            throw new InvalidOperationException(
                "ConnectionStrings__SmartHotel is not a valid Npgsql connection string.");
        }

        // Npgsql consults a password file, and PGPASSWORD, only when the string carries no password, so a string that
        // names a password file or has no password could take the credential from outside this variable. The
        // messages name the setting only.
        if (!string.IsNullOrEmpty(builder.Passfile))
        {
            throw new InvalidOperationException("ConnectionStrings__SmartHotel must not set Passfile.");
        }

        if (string.IsNullOrWhiteSpace(builder.Password))
        {
            throw new InvalidOperationException("ConnectionStrings__SmartHotel must set a non-blank Password.");
        }
    }

    private static int ReadPort()
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
            throw new InvalidOperationException("DB_PORT must be an integer between 1 and 65535.");
        }

        return port;
    }

    private static string? ReadNonEmpty(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}