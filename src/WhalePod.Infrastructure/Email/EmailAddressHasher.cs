using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace WhalePod.Infrastructure.Email;

public sealed class EmailAddressHasher(IOptions<EmailOptions> options)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        string.IsNullOrWhiteSpace(options.Value.SuppressionHashSecret)
            ? throw new InvalidOperationException("Email:SuppressionHashSecret is required.")
            : options.Value.SuppressionHashSecret);

    public byte[] Hash(string emailAddress)
    {
        var normalized = (emailAddress ?? string.Empty).Trim().ToLowerInvariant();
        return HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalized));
    }
}