using System.Net;
using System.Text.RegularExpressions;

using Npgsql;

namespace WhalePod.Tests.Integration;

internal static partial class TestDatabaseGuard
{
    public const string EnvironmentVariableName = "WHALEPOD_TEST_DB_CONNECTION";

    private static readonly object VerificationLock = new();
    private static readonly HashSet<string> VerifiedConnectionStrings = new(StringComparer.Ordinal);

    [GeneratedRegex("^whalepod_test(_[a-z0-9_]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TestDatabaseNamePattern();

    public static bool TryValidate(string? connectionString, out string reason)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            reason = $"Set {EnvironmentVariableName} to a disposable database named whalepod_test or whalepod_test_<run>.";
            return false;
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            reason = $"{EnvironmentVariableName} is not a valid PostgreSQL connection string.";
            return false;
        }

        if (!TestDatabaseNamePattern().IsMatch(builder.Database ?? string.Empty))
        {
            reason =
                $"Refusing to run database tests against database '{builder.Database}': the name must be whalepod_test or match whalepod_test_<suffix>.";
            return false;
        }

        var hosts = (builder.Host ?? string.Empty).Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 || hosts.Any(host => !IsLoopbackHost(host)))
        {
            reason =
                $"Refusing to run database tests against host '{builder.Host}': only loopback hosts (127.0.0.1, ::1, localhost) are allowed.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static string FromEnvironment()
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!TryValidate(connectionString, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        return connectionString!;
    }

    public static void EnsureServerMatches(string connectionString)
    {
        lock (VerificationLock)
        {
            if (VerifiedConnectionStrings.Contains(connectionString))
            {
                return;
            }

            VerifyServerMatches(connectionString);
            VerifiedConnectionStrings.Add(connectionString);
        }
    }

    public static async Task EnsureServerMatchesAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        lock (VerificationLock)
        {
            if (VerifiedConnectionStrings.Contains(connectionString))
            {
                return;
            }
        }

        await VerifyServerMatchesAsync(connectionString, cancellationToken);
        lock (VerificationLock)
        {
            VerifiedConnectionStrings.Add(connectionString);
        }
    }

    private static void VerifyServerMatches(string connectionString)
    {
        var expectedDatabase = ValidateAndReadDatabase(connectionString);

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database(), COALESCE(host(inet_server_addr()), '')";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("Database target check returned no rows.");
        }

        AssertServerMatches(expectedDatabase, reader.GetString(0), reader.GetString(1));
    }

    private static async Task VerifyServerMatchesAsync(string connectionString, CancellationToken cancellationToken)
    {
        var expectedDatabase = ValidateAndReadDatabase(connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database(), COALESCE(host(inet_server_addr()), '')";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Database target check returned no rows.");
        }

        AssertServerMatches(expectedDatabase, reader.GetString(0), reader.GetString(1));
    }

    private static string ValidateAndReadDatabase(string connectionString)
    {
        if (!TryValidate(connectionString, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        return new NpgsqlConnectionStringBuilder(connectionString).Database
            ?? throw new InvalidOperationException("The connection string does not specify a database.");
    }

    private static void AssertServerMatches(string expectedDatabase, string actualDatabase, string serverAddress)
    {
        if (!string.Equals(actualDatabase, expectedDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Connected database '{actualDatabase}' does not match the requested database '{expectedDatabase}'.");
        }

        if (string.IsNullOrEmpty(serverAddress) || !IsLoopbackAddress(serverAddress))
        {
            // GitHub Actions service containers connect through Docker NAT, so the server
            // reports the bridge address while the client still targets loopback.
            if (!AllowNonLoopbackServer)
            {
                throw new InvalidOperationException(
                    $"Database connection is not a loopback TCP connection (server address '{serverAddress}').");
            }
        }
    }

    private static bool AllowNonLoopbackServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("WHALEPOD_TEST_ALLOW_NON_LOOPBACK"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopbackHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "::1";
    }

    private static bool IsLoopbackAddress(string address)
    {
        return IPAddress.TryParse(address, out var parsed) && IPAddress.IsLoopback(parsed);
    }
}