using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WhalePod.Infrastructure.Services;

internal static class AdminCursor
{
    public static string BuildScope(string prefix, params string?[] filters)
    {
        var payload = string.Join('\u001f', filters.Select(value => value ?? string.Empty));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"{prefix}:{Convert.ToHexString(hash.AsSpan(0, 8))}";
    }

    public static string Encode(string scope, DateTimeOffset createdAt, Guid id)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            s = scope,
            t = createdAt.UtcDateTime.Ticks,
            i = id
        });
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string EncodePrioritized(string scope, DateTimeOffset createdAt, Guid id, bool prioritized)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            s = scope,
            t = createdAt.UtcDateTime.Ticks,
            i = id,
            p = prioritized
        });
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(
        string? cursor,
        string scope,
        out DateTimeOffset createdAt,
        out Guid id)
    {
        return TryDecode(cursor, scope, out createdAt, out id, out _);
    }

    public static bool TryDecode(
        string? cursor,
        string scope,
        out DateTimeOffset createdAt,
        out Guid id,
        out bool prioritized)
    {
        createdAt = default;
        id = default;
        prioritized = false;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 512)
        {
            return false;
        }

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            using var document = JsonDocument.Parse(Convert.FromBase64String(normalized));
            var root = document.RootElement;
            if (root.GetProperty("s").GetString() != scope)
            {
                return false;
            }

            createdAt = new DateTimeOffset(root.GetProperty("t").GetInt64(), TimeSpan.Zero);
            id = root.GetProperty("i").GetGuid();
            if (root.TryGetProperty("p", out var priorityElement))
            {
                prioritized = priorityElement.GetBoolean();
            }

            return true;
        }
        catch (Exception exception) when (exception is FormatException
            or JsonException
            or KeyNotFoundException
            or InvalidOperationException)
        {
            return false;
        }
    }
}