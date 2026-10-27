using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhalePod.Infrastructure.Services;

public static class AuditMetadataRedactor
{
    private static readonly string[] SensitiveKeyParts =
    [
        "token",
        "secret",
        "password",
        "ip",
        "email",
        "github",
        "hmac",
        "hash",
        "authorization",
        "cookie"
    ];

    public static string Redact(JsonDocument? document)
    {
        if (document is null)
        {
            return "{}";
        }

        try
        {
            var node = JsonNode.Parse(document.RootElement.GetRawText());
            RedactNode(node);
            return node?.ToJsonString() ?? "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static void RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var key in jsonObject.Select(property => property.Key).ToList())
                {
                    if (IsSensitiveKey(key))
                    {
                        jsonObject[key] = "[redacted]";
                    }
                    else
                    {
                        RedactNode(jsonObject[key]);
                    }
                }

                break;
            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    RedactNode(item);
                }

                break;
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        return SensitiveKeyParts.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));
    }
}