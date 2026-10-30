using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;

namespace WhalePod.Tests.Integration;

internal static class TestDatabaseBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            TestDatabaseGuard.EnvironmentVariableName);
        if (!TestDatabaseGuard.TryValidate(connectionString, out _))
        {
            return;
        }

        try
        {
            using var db = ServiceTestSupport.CreateContext();
            db.Database.Migrate();
        }
        catch
        {
            // Individual tests report the actual database failure.
        }
    }
}
