using WhalePod.Domain;

namespace WhalePod.Domain.Content;

public sealed class Category
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public string Status { get; set; } = CategoryStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Post
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AuthorId { get; set; }
    public Guid CategoryId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string BodyMd { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;
    public string Excerpt { get; set; } = string.Empty;
    public string? EnvKernel { get; set; }
    public string? EnvPlatform { get; set; }
    public string? EnvModel { get; set; }
    public string ModerationStatus { get; set; } = ModerationStatuses.Pending;
    public bool IsSolved { get; set; }
    public Guid? AcceptedCommentId { get; set; }
    public int ViewCount { get; set; }
    public int CopyCount { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class PostImage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PostId { get; set; }
    public Guid UploadId { get; set; }
    public string Url { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string? AltText { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public long ByteSize { get; set; }
    public short SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MediaUpload
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public string Url { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public long ByteSize { get; set; }
    public string Status { get; set; } = MediaUploadStatuses.Temp;
    public Guid? PostId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AttachedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(24);
}

public sealed class Tag
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = TagStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PostTag
{
    public Guid PostId { get; set; }
    public Guid TagId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Artifact
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PostId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string Content { get; set; } = string.Empty;
    public int CopyCount { get; set; }
    public short SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Comment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PostId { get; set; }
    public Guid AuthorId { get; set; }
    public Guid? ParentId { get; set; }
    public string BodyMd { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;
    public string ModerationStatus { get; set; } = ModerationStatuses.Pending;
    public bool IsAccepted { get; set; }
    public int LikeCount { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}
public sealed class CopyDedupe
{
    public byte[] KeyHash { get; set; } = Array.Empty<byte>();
    public DateTimeOffset ExpiresAt { get; set; }
}