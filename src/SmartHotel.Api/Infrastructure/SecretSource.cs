using System.Globalization;

using Npgsql;

namespace SmartHotel.Api.Infrastructure;

/// <summary>
/// Supplies the API's secrets: the database connection string and the JWT signing key.
/// </summary>
/// <remarks>
/// Implementations read secrets from the process environment only, never from <c>appsettings*.json</c>,
/// command-line arguments or any other configuration provider. Secret values must never be logged,
/// written to the console, placed in exception messages or returned in a response.
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
/// The database connection string is <c>ConnectionStrings__SmartHotel</c> when that variable is set and not blank.
/// Otherwise it is assembled from <c>DB_HOST</c>, <c>DB_PORT</c> (default 5432), <c>DB_NAME</c>, <c>DB_USER</c>
/// and <c>DB_PASSWORD</c>, and it is <see langword="null"/> when <c>DB_HOST</c> is missing or blank.
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
    /// <c>DB_PORT</c> is set but is not an integer between 1 and 65535, and the connection string is being
    /// assembled from its parts.
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

        string? password = ReadNonEmpty(PasswordVariable);
        if (password is not null)
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
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