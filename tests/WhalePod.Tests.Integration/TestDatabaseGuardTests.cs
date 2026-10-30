namespace WhalePod.Tests.Integration;

public sealed class TestDatabaseGuardTests
{
    private static readonly object EnvironmentLock = new();

    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Database=whalepod_prod;Username=app;Password=whalepod_test")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=whalepod_prod;Username=app;Application Name=whalepod_test_runner")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=app;Username=whalepod_test_user")]
    [InlineData("Host=10.20.30.40;Port=5432;Database=whalepod_test_remote;Username=app")]
    [InlineData("Host=localhost.evil.example;Port=5432;Database=whalepod_test_remote;Username=app")]
    [InlineData("Host=db.prod,127.0.0.1;Port=5432;Database=whalepod_test_multi;Username=app")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=postgres;Username=app")]
    [InlineData("Host=127.0.0.1;Port=5432;Username=app")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=whalepod_testfix;Username=app")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=whalepod_test-fix;Username=app")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=whalepod_test_FIX;Username=app")]
    [InlineData("not a connection string")]
    [InlineData("")]
    public void Rejects_connection_strings_that_are_not_disposable_loopback_test_databases(string connectionString)
    {
        Assert.False(TestDatabaseGuard.TryValidate(connectionString, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Rejects_missing_connection_string()
    {
        Assert.False(TestDatabaseGuard.TryValidate(null, out var reason));
        Assert.Contains(TestDatabaseGuard.EnvironmentVariableName, reason);
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=55438;Database=whalepod_test_fix_20261028;Username=review")]
    [InlineData("Host=localhost;Port=5432;Database=whalepod_test;Username=postgres;Password=postgres")]
    [InlineData("Host=::1;Port=5432;Database=whalepod_test_ipv6;Username=app")]
    [InlineData("Host=127.0.0.1,localhost;Port=5432;Database=whalepod_test_multi;Username=app")]
    public void Accepts_loopback_test_databases(string connectionString)
    {
        Assert.True(TestDatabaseGuard.TryValidate(connectionString, out var reason), reason);
    }

    [Fact]
    public void Database_fact_rejects_connection_string_whose_password_contains_test_token()
    {
        var attribute = CreateAttribute(
            "Host=127.0.0.1;Port=5432;Database=whalepod_prod;Username=app;Password=whalepod_test");

        Assert.NotNull(attribute.Skip);
        Assert.Contains("Refusing", attribute.Skip);
    }

    [Fact]
    public void Database_fact_rejects_remote_host()
    {
        var attribute = CreateAttribute(
            "Host=10.20.30.40;Port=5432;Database=whalepod_test_remote;Username=app");

        Assert.NotNull(attribute.Skip);
    }

    [Fact]
    public void Database_fact_allows_loopback_test_database()
    {
        var attribute = CreateAttribute(
            "Host=127.0.0.1;Port=55438;Database=whalepod_test_fix_20261028;Username=review");

        Assert.Null(attribute.Skip);
    }

    [Fact]
    public void Database_fact_allows_pipeline_database_name_without_suffix()
    {
        var attribute = CreateAttribute(
            "Host=localhost;Port=5432;Database=whalepod_test;Username=postgres;Password=postgres");

        Assert.Null(attribute.Skip);
    }

    [Fact]
    public void Database_fact_skips_when_environment_variable_is_missing()
    {
        var attribute = CreateAttribute(null);

        Assert.NotNull(attribute.Skip);
        Assert.Contains(TestDatabaseGuard.EnvironmentVariableName, attribute.Skip);
    }

    [Fact]
    public void Environment_connection_string_passes_the_guard_when_present()
    {
        var connectionString = Environment.GetEnvironmentVariable(TestDatabaseGuard.EnvironmentVariableName);
        if (connectionString is null)
        {
            return;
        }

        Assert.True(TestDatabaseGuard.TryValidate(connectionString, out var reason), reason);
    }

    [DatabaseFact]
    public async Task Server_check_confirms_the_configured_database()
    {
        await TestDatabaseGuard.EnsureServerMatchesAsync(ServiceTestSupport.ConnectionString);
    }

    [DatabaseFact]
    public void Synchronous_server_check_confirms_the_configured_database()
    {
        TestDatabaseGuard.EnsureServerMatches(ServiceTestSupport.ConnectionString);
    }

    private static DatabaseFactAttribute CreateAttribute(string? connectionString)
    {
        lock (EnvironmentLock)
        {
            var previous = Environment.GetEnvironmentVariable(TestDatabaseGuard.EnvironmentVariableName);
            try
            {
                Environment.SetEnvironmentVariable(TestDatabaseGuard.EnvironmentVariableName, connectionString);
                return new DatabaseFactAttribute();
            }
            finally
            {
                Environment.SetEnvironmentVariable(TestDatabaseGuard.EnvironmentVariableName, previous);
            }
        }
    }
}