using Microsoft.EntityFrameworkCore;

using Npgsql;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class SitemapShardingTests
{
    [DatabaseFact]
    public async Task Sitemap_index_shards_posts_beyond_the_page_size()
    {
        var databaseName = $"whalepod_test_sitemap_{ServiceTestSupport.NewSuffix()}";
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
            await db.Database.MigrateAsync();

            var author = ServiceTestSupport.NewUser("sitemap-author");
            var category = ServiceTestSupport.NewCategory();
            db.Users.Add(author);
            db.Categories.Add(category);
            for (var index = 0; index < 5; index++)
            {
                var post = ServiceTestSupport.NewPost(
                    author.Id,
                    category.Id,
                    title: $"站点地图分片帖子 {index} 标题");
                post.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-index);
                db.Posts.Add(post);
            }

            await db.SaveChangesAsync();
            var discovery = new PublicDiscoveryService(db);

            var indexDocument = await discovery.GetSitemapIndexAsync(postsPerShard: 2);
            var postShards = indexDocument.Shards
                .Where(shard => shard.Path.StartsWith("/sitemap-posts-", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(3, postShards.Count);
            Assert.Contains(
                indexDocument.Shards,
                shard => shard.Path == "/sitemap-pages.xml");

            var first = await discovery.GetPostSitemapShardAsync(1, postsPerShard: 2);
            Assert.NotNull(first);
            Assert.Equal(2, first!.Entries.Count);
            var third = await discovery.GetPostSitemapShardAsync(3, postsPerShard: 2);
            Assert.NotNull(third);
            Assert.Single(third!.Entries);
            Assert.Null(await discovery.GetPostSitemapShardAsync(4, postsPerShard: 2));

            var pages = await discovery.GetPageSitemapAsync();
            Assert.Contains(pages.Entries, entry => entry.Path == "/");
            Assert.Contains(pages.Entries, entry => entry.Path == "/tags");
            Assert.Contains(pages.Entries, entry => entry.Path.StartsWith("/users/", StringComparison.Ordinal));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var terminate = new NpgsqlCommand(
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{databaseName}'",
                admin);
            await terminate.ExecuteNonQueryAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}