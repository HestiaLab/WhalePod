using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WhalePod.Tests.Integration;

public sealed class ApiContractTests
{
    [Fact]
    public void Openapi_operations_match_registered_api_routes()
    {
        var openApiOperations = ReadOpenApiOperations();
        Assert.NotEmpty(openApiOperations);

        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"whalepod-contract-dpkeys-{Guid.NewGuid():N}");
        var mediaRoot = Path.Combine(
            Path.GetTempPath(),
            $"whalepod-contract-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);
        using var factory = new ContractApiFactory(keysDirectory, mediaRoot);
        _ = factory.Services;
        var endpoints = LoadApiOperations(factory);
        if (endpoints.Count == 0)
        {
            using var client = factory.CreateClient();
            endpoints = LoadApiOperations(factory);
        }

        var missing = openApiOperations.Except(endpoints).OrderBy(value => value).ToList();
        var extra = endpoints.Except(openApiOperations).OrderBy(value => value).ToList();
        Assert.True(
            missing.Count == 0,
            $"OpenAPI operations missing from API routes: {string.Join(", ", missing)}");
        Assert.True(
            extra.Count == 0,
            $"API routes missing from OpenAPI: {string.Join(", ", extra)}");
    }

    private static HashSet<string> LoadApiOperations(WebApplicationFactory<WhalePod.Api.Endpoints.ApiApplicationMarker> factory)
    {
        var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in factory.Services.GetServices<EndpointDataSource>())
        {
            foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                var raw = endpoint.RoutePattern.RawText;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var path = NormalizeRoute(raw);
                if (!path.StartsWith("/api/v1", StringComparison.Ordinal)
                    && path != "/api/v1")
                {
                    continue;
                }

                path = path["/api/v1".Length..];
                if (path.Length == 0)
                {
                    path = "/";
                }

                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                if (methods is null)
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    operations.Add($"{method.ToUpperInvariant()} {path}");
                }
            }
        }

        return operations;
    }

    private static HashSet<string> ReadOpenApiOperations()
    {
        var path = FindRepositoryFile(Path.Combine("docs", "api", "openapi.yaml"));
        var operations = new HashSet<string>(StringComparer.Ordinal);
        var inPaths = false;
        string? currentPath = null;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.TrimEnd();
            if (!inPaths)
            {
                if (line == "paths:")
                {
                    inPaths = true;
                }

                continue;
            }

            if (line.Length > 0
                && char.IsLetter(line[0]))
            {
                break;
            }

            if (line.StartsWith("  /", StringComparison.Ordinal))
            {
                currentPath = line.Trim();
                if (currentPath.EndsWith(':'))
                {
                    currentPath = currentPath[..^1];
                }

                continue;
            }

            if (currentPath is not null
                && Regex.IsMatch(line, @"^    (get|post|put|patch|delete|head|options):$"))
            {
                operations.Add($"{line.Trim().TrimEnd(':').ToUpperInvariant()} {currentPath}");
            }
        }

        return operations;
    }

    private static string NormalizeRoute(string raw)
    {
        var path = raw.StartsWith('/') ? raw : $"/{raw}";
        path = Regex.Replace(path, @"\{([^}:]+)(?::[^}]+)?\}", "{$1}");
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from the test output directory.");
    }

    private sealed class ContractApiFactory(string keysDirectory, string mediaRoot)
        : WebApplicationFactory<WhalePod.Api.Endpoints.ApiApplicationMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:WhalePod"] = "Host=127.0.0.1;Port=5432;Database=whalepod_contract;Username=app",
                    ["DataProtection:KeysDirectory"] = keysDirectory,
                    ["Audit:IpHashSecret"] = "contract-only-audit-ip-hash-secret-0123456789",
                    ["Email:PollIntervalSeconds"] = "3600",
                    ["MediaStorage:Provider"] = "Local",
                    ["MediaStorage:LocalRootPath"] = mediaRoot,
                    ["MediaStorage:PublicBaseUrl"] = "/media"
                });
            });
        }
    }
}