using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class DatabaseReadinessTests
{
    [DatabaseFact]
    public async Task Migrated_database_is_ready()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var result = await DatabaseReadiness.CheckAsync(db);

        Assert.True(result.Ready);
        Assert.Null(result.Reason);
    }

    [DatabaseFact]
    public async Task Empty_database_is_not_ready_until_migrations_run()
    {
        var databaseName = $"whalepod_test_ready_{ServiceTestSupport.NewSuffix()}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(ServiceTestSupport.ConnectionString)
        {
            Database = "postgres"
        };
        var testBuilder = new NpgsqlConnectionStringBuilder(ServiceTestSupport.ConnectionString)
        {
            Database = databaseName
        };

        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<WhalePodDbContext>()
                .UseNpgsql(testBuilder.ConnectionString)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var db = new WhalePodDbContext(options);

            var empty = await DatabaseReadiness.CheckAsync(db);
            Assert.False(empty.Ready);
            Assert.Equal("migrations_pending", empty.Reason);

            await db.Database.MigrateAsync();
            var migrated = await DatabaseReadiness.CheckAsync(db);
            Assert.True(migrated.Ready);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{databaseName}'",
                admin);
            await drop.ExecuteNonQueryAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\"", admin);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }
}

[Collection("api-http")]
public sealed class ReadinessHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Web_and_api_ready_endpoints_report_current_schema()
    {
        await using (var db = ServiceTestSupport.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        using var webClient = WebFormTestSupport.CreateClient(environment.WebFactory);
        using (var web = await webClient.GetAsync("/healthz/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, web.StatusCode);
            using var document = JsonDocument.Parse(await web.Content.ReadAsStringAsync());
            Assert.Equal("ready", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "strict-origin-when-cross-origin",
                web.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("nosniff", web.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("SAMEORIGIN", web.Headers.GetValues("X-Frame-Options").Single());
            Assert.True(web.Headers.Contains("Content-Security-Policy-Report-Only"));
        }

        using var apiClient = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        using (var api = await apiClient.GetAsync("/healthz/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, api.StatusCode);
            using var document = JsonDocument.Parse(await api.Content.ReadAsStringAsync());
            Assert.Equal("ready", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "strict-origin-when-cross-origin",
                api.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("nosniff", api.Headers.GetValues("X-Content-Type-Options").Single());
        }
    }
}