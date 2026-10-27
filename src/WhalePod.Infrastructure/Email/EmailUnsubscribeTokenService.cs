using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace WhalePod.Infrastructure.Email;

public sealed class EmailUnsubscribeTokenService(
    IOptions<EmailOptions> options,
    TimeProvider timeProvider)
{
    private readonly EmailOptions _options = options.Value;
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        string.IsNullOrWhiteSpace(options.Value.UnsubscribeSecret)
            ? throw new InvalidOperationException("Email:UnsubscribeSecret is required.")
            : options.Value.UnsubscribeSecret);

    public string Create(Guid userId, string preferenceType)
    {
        if (!EmailPreferencePolicy.IsUnsubscribable(preferenceType))
        {
            throw new ArgumentException("Security email preferences cannot be unsubscribed.", nameof(preferenceType));
        }

        var expiresAt = timeProvider.GetUtcNow().AddDays(_options.UnsubscribeTokenLifetimeDays);
        var payload = $"{userId:N}|{preferenceType}|{expiresAt.ToUnixTimeSeconds()}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signature = HMACSHA256.HashData(_key, payloadBytes);
        return $"{ToBase64Url(payloadBytes)}.{ToBase64Url(signature)}";
    }

    public bool TryValidate(
        string? token,
        out Guid userId,
        out string preferenceType,
        out DateTimeOffset expiresAt)
    {
        userId = default;
        preferenceType = string.Empty;
        expiresAt = default;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512)
        {
            return false;
        }

        var parts = token.Split('.', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        byte[] payloadBytes;
        byte[] suppliedSignature;
        try
        {
            payloadBytes = FromBase64Url(parts[0]);
            suppliedSignature = FromBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedSignature = HMACSHA256.HashData(_key, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
        {
            return false;
        }

        var payload = Encoding.UTF8.GetString(payloadBytes);
        var fields = payload.Split('|');
        if (fields.Length != 3
            || !Guid.TryParseExact(fields[0], "N", out userId)
            || !long.TryParse(fields[2], out var expirySeconds))
        {
            return false;
        }

        preferenceType = fields[1];
        expiresAt = DateTimeOffset.FromUnixTimeSeconds(expirySeconds);
        return EmailPreferencePolicy.IsUnsubscribable(preferenceType)
            && expiresAt > timeProvider.GetUtcNow();
    }

    private static string ToBase64Url(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}