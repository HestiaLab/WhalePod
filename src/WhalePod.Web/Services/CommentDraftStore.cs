namespace WhalePod.Web.Services;

public sealed record CommentDraft(Guid PostId, Guid? ParentId, string BodyMd);

public sealed class CommentDraftStore(TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    private const int MaxEntries = 500;
    private const int MaxBodyLength = 10_000;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = new();

    public void Save(Guid userId, Guid postId, Guid? parentId, string bodyMd)
    {
        var body = bodyMd.Length > MaxBodyLength ? bodyMd[..MaxBodyLength] : bodyMd;
        lock (_gate)
        {
            PruneExpired();
            if (_entries.Count >= MaxEntries)
            {
                var oldest = _entries.MinBy(pair => pair.Value.UpdatedAt);
                _entries.Remove(oldest.Key);
            }

            _entries[userId] = new Entry(new CommentDraft(postId, parentId, body), timeProvider.GetUtcNow());
        }
    }

    public CommentDraft? Get(Guid userId)
    {
        lock (_gate)
        {
            PruneExpired();
            return _entries.TryGetValue(userId, out var entry) ? entry.Draft : null;
        }
    }

    public void Clear(Guid userId)
    {
        lock (_gate)
        {
            _entries.Remove(userId);
        }
    }

    private void PruneExpired()
    {
        var cutoff = timeProvider.GetUtcNow() - Lifetime;
        foreach (var pair in _entries.Where(pair => pair.Value.UpdatedAt < cutoff).ToList())
        {
            _entries.Remove(pair.Key);
        }
    }

    private sealed record Entry(CommentDraft Draft, DateTimeOffset UpdatedAt);
}