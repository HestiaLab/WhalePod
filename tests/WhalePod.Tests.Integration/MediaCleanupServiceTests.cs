using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Storage;

namespace WhalePod.Tests.Integration;

public sealed class MediaCleanupServiceTests
{
    [DatabaseFact]
    public async Task Cleanup_deletes_expired_and_deleted_uploads_but_keeps_fresh_and_attached()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();
        await ResetMediaUploadsAsync(db);
        using var media = new TestMediaStorage();

        var author = ServiceTestSupport.NewUser("cleanup-author");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        db.Users.Add(author);
        db.Categories.Add(category);
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var expired = await AddUploadAsync(
            db,
            media,
            author.Id,
            MediaUploadStatuses.Temp,
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            postId: null);
        var fresh = await AddUploadAsync(
            db,
            media,
            author.Id,
            MediaUploadStatuses.Temp,
            now.AddHours(1),
            postId: null);
        var deleted = await AddUploadAsync(
            db,
            media,
            author.Id,
            MediaUploadStatuses.Deleted,
            new DateTimeOffset(2000, 1, 1, 0, 0, 1, TimeSpan.Zero),
            postId: null);
        var attached = await AddUploadAsync(
            db,
            media,
            author.Id,
            MediaUploadStatuses.Attached,
            now.AddHours(1),
            post.Id);
        await db.SaveChangesAsync();

        var candidates = await db.MediaUploads.CountAsync(upload =>
            upload.Status == MediaUploadStatuses.Deleted
            || (upload.Status == MediaUploadStatuses.Temp && upload.ExpiresAt <= now));
        Assert.True(candidates >= 2, $"Expected at least 2 cleanup candidates, found {candidates}.");

        var cleanup = ServiceTestSupport.CreateMediaCleanupService(db, media.Storage);
        var result = await cleanup.CleanupOnceAsync(100);
        Assert.True(result.Deleted >= 2, $"Deleted={result.Deleted}, Failed={result.Failed}, Candidates={candidates}.");

        db.ChangeTracker.Clear();
        var remainingIds = await db.MediaUploads
            .AsNoTracking()
            .Where(upload => upload.Id == expired.Id
                || upload.Id == fresh.Id
                || upload.Id == deleted.Id
                || upload.Id == attached.Id)
            .Select(upload => upload.Id)
            .ToListAsync();
        Assert.Equal(2, remainingIds.Count);
        Assert.Contains(fresh.Id, remainingIds);
        Assert.Contains(attached.Id, remainingIds);
        Assert.False(File.Exists(media.ResolvePath(expired.Url)));
        Assert.False(File.Exists(media.ResolvePath(deleted.Url)));
        Assert.True(File.Exists(media.ResolvePath(fresh.Url)));
        Assert.True(File.Exists(media.ResolvePath(attached.Url)));

        var second = await cleanup.CleanupOnceAsync(100);
        db.ChangeTracker.Clear();
        Assert.False(await db.MediaUploads.AnyAsync(upload => upload.Id == expired.Id));
        Assert.False(await db.MediaUploads.AnyAsync(upload => upload.Id == deleted.Id));
        Assert.True(await db.MediaUploads.AnyAsync(upload => upload.Id == fresh.Id));
        Assert.True(await db.MediaUploads.AnyAsync(upload => upload.Id == attached.Id));
    }

    [DatabaseFact]
    public async Task Cleanup_keeps_rows_when_storage_deletion_fails()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();
        await ResetMediaUploadsAsync(db);
        using var media = new TestMediaStorage();

        var author = ServiceTestSupport.NewUser("cleanup-failure");
        db.Users.Add(author);
        await db.SaveChangesAsync();

        var expired = await AddUploadAsync(
            db,
            media,
            author.Id,
            MediaUploadStatuses.Temp,
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            postId: null);
        await db.SaveChangesAsync();

        var cleanup = ServiceTestSupport.CreateMediaCleanupService(db, new FailingMediaStorage());
        var result = await cleanup.CleanupOnceAsync(100);
        Assert.True(result.Failed >= 1);

        db.ChangeTracker.Clear();
        Assert.True(await db.MediaUploads.AnyAsync(upload => upload.Id == expired.Id));
        Assert.True(File.Exists(media.ResolvePath(expired.Url)));
    }

    private static async Task ResetMediaUploadsAsync(WhalePodDbContext db)
    {
        await db.PostImages.ExecuteDeleteAsync();
        await db.MediaUploads.ExecuteDeleteAsync();
    }

    private static async Task<MediaUpload> AddUploadAsync(
        WhalePodDbContext db,
        TestMediaStorage media,
        Guid ownerId,
        string status,
        DateTimeOffset expiresAt,
        Guid? postId)
    {
        var objectKey = $"2026/10/{Guid.NewGuid():N}.webp";
        var url = await media.Storage.SaveAsync(
            new byte[] { 1, 2, 3 },
            objectKey,
            "image/webp");
        var upload = new MediaUpload
        {
            OwnerId = ownerId,
            Url = url,
            Width = 4,
            Height = 4,
            MimeType = "image/webp",
            ByteSize = 3,
            Status = status,
            PostId = postId,
            ExpiresAt = expiresAt
        };
        db.MediaUploads.Add(upload);
        return upload;
    }

    private sealed class FailingMediaStorage : IMediaStorage
    {
        public string ProviderName => "failing";

        public Task<string> SaveAsync(
            ReadOnlyMemory<byte> content,
            string objectKey,
            string contentType,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(string publicUrl, CancellationToken cancellationToken = default)
        {
            throw new IOException("Simulated storage failure.");
        }
    }
}