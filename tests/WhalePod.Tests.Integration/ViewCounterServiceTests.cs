using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class ViewCounterServiceTests
{
    [DatabaseFact]
    public async Task Concurrent_views_for_the_same_key_are_counted_once()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("view-counter-author");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var pending = ServiceTestSupport.NewPost(
            author.Id,
            category.Id,
            ModerationStatuses.Pending);
        db.Users.Add(author);
        db.Categories.Add(category);
        db.Posts.AddRange(post, pending);
        await db.SaveChangesAsync();

        var service = new ViewCounterService(
            db,
            Options.Create(new ViewCounterOptions
            {
                DedupeSecret = new string('v', 64),
                DedupeWindowSeconds = 600
            }),
            TimeProvider.System,
            NullLogger<ViewCounterService>.Instance);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => service.RecordViewAsync(post.Id, "10.0.0.1")));
        Assert.Equal(1, results.Count(recorded => recorded));

        db.ChangeTracker.Clear();
        var stored = await db.Posts.AsNoTracking().SingleAsync(candidate => candidate.Id == post.Id);
        Assert.Equal(1, stored.ViewCount);

        Assert.True(await service.RecordViewAsync(post.Id, "10.0.0.2"));
        Assert.False(await service.RecordViewAsync(pending.Id, "10.0.0.1"));
    }
}