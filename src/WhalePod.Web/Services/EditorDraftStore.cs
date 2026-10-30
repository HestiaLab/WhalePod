namespace WhalePod.Web.Services;

public sealed record EditorPendingImage(
    Guid Id,
    string Url,
    int Width,
    int Height,
    string MimeType,
    string AltText);

public sealed record EditorArtifact(
    string Kind,
    string Name,
    string Note,
    string Content);

public sealed record EditorImageRef(Guid Id, string AltText);

public sealed record EditorSnapshot(
    string Title,
    string CategoryId,
    string BodyMd,
    string Kernel,
    string Platform,
    string Model,
    IReadOnlyList<string> Tags,
    IReadOnlyList<EditorArtifact> Artifacts,
    IReadOnlyList<EditorImageRef> Images);

public sealed class EditorDraftStore(TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    private const int MaxUsers = 200;
    private const int MaxImages = 20;
    private const long MaxSnapshotChars = 1_500_000;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = new();

    public IReadOnlyList<EditorPendingImage> GetImages(Guid userId)
    {
        lock (_gate)
        {
            var entry = GetLiveEntry(userId);
            return entry is null ? [] : entry.Images.ToList();
        }
    }

    public void AddImage(Guid userId, EditorPendingImage image)
    {
        lock (_gate)
        {
            var entry = GetOrCreateEntry(userId);
            entry.Images.RemoveAll(candidate => candidate.Id == image.Id);
            entry.Images.Add(image);
            if (entry.Images.Count > MaxImages)
            {
                entry.Images.RemoveRange(0, entry.Images.Count - MaxImages);
            }

            entry.UpdatedAt = timeProvider.GetUtcNow();
        }
    }

    public bool RemoveImage(Guid userId, Guid imageId)
    {
        lock (_gate)
        {
            var entry = GetLiveEntry(userId);
            if (entry is null)
            {
                return false;
            }

            entry.UpdatedAt = timeProvider.GetUtcNow();
            return entry.Images.RemoveAll(candidate => candidate.Id == imageId) > 0;
        }
    }

    public void SaveSnapshot(Guid userId, EditorSnapshot snapshot)
    {
        var size = snapshot.Title.Length
            + snapshot.BodyMd.Length
            + snapshot.Kernel.Length
            + snapshot.Platform.Length
            + snapshot.Model.Length
            + snapshot.Tags.Sum(tag => tag.Length)
            + snapshot.Artifacts.Sum(artifact =>
                artifact.Kind.Length + artifact.Name.Length + artifact.Note.Length + artifact.Content.Length);
        if (size > MaxSnapshotChars)
        {
            return;
        }

        lock (_gate)
        {
            var entry = GetOrCreateEntry(userId);
            entry.Snapshot = snapshot;
            entry.UpdatedAt = timeProvider.GetUtcNow();
        }
    }

    public EditorSnapshot? GetSnapshot(Guid userId)
    {
        lock (_gate)
        {
            return GetLiveEntry(userId)?.Snapshot;
        }
    }

    public void Clear(Guid userId)
    {
        lock (_gate)
        {
            _entries.Remove(userId);
        }
    }

    private Entry GetOrCreateEntry(Guid userId)
    {
        PruneExpired();
        if (_entries.TryGetValue(userId, out var existing))
        {
            return existing;
        }

        if (_entries.Count >= MaxUsers)
        {
            var oldest = _entries.MinBy(pair => pair.Value.UpdatedAt);
            _entries.Remove(oldest.Key);
        }

        var entry = new Entry(timeProvider.GetUtcNow());
        _entries[userId] = entry;
        return entry;
    }

    private Entry? GetLiveEntry(Guid userId)
    {
        PruneExpired();
        return _entries.TryGetValue(userId, out var entry) ? entry : null;
    }

    private void PruneExpired()
    {
        var cutoff = timeProvider.GetUtcNow() - Lifetime;
        foreach (var pair in _entries.Where(pair => pair.Value.UpdatedAt < cutoff).ToList())
        {
            _entries.Remove(pair.Key);
        }
    }

    private sealed class Entry(DateTimeOffset createdAt)
    {
        public List<EditorPendingImage> Images { get; } = [];
        public EditorSnapshot? Snapshot { get; set; }
        public DateTimeOffset UpdatedAt { get; set; } = createdAt;
    }
}