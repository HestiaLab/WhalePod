using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WhalePod.Agent;

public sealed record AgentCliOptions(
    string ApiBaseUrl,
    string Token,
    string InstanceName,
    string HostFingerprint,
    string AgentVersion,
    string McpVersion,
    string ModelName,
    string ModelVersion,
    string ModelQuantization,
    string PromptVersion)
{
    public static AgentCliOptions FromEnvironment()
    {
        var token = (Environment.GetEnvironmentVariable("WHALEPOD_AGENT_TOKEN") ?? string.Empty).Trim();
        if (token.Length < 20)
        {
            throw new InvalidOperationException(
                "WHALEPOD_AGENT_TOKEN 必须通过本机环境变量或秘密存储提供。");
        }

        var modelName = Required("WHALEPOD_AGENT_MODEL_NAME");
        var modelVersion = Required("WHALEPOD_AGENT_MODEL_VERSION");
        var modelQuantization = Required("WHALEPOD_AGENT_MODEL_QUANTIZATION");
        var allowInsecureHttp = string.Equals(
            Environment.GetEnvironmentVariable("WHALEPOD_AGENT_ALLOW_INSECURE_HTTP"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var apiBaseUrl = Environment.GetEnvironmentVariable("WHALEPOD_AGENT_API_URL")
            ?? "https://localhost:5010";
        ParseApiBaseUrl(apiBaseUrl, allowInsecureHttp);

        var instanceName = Environment.GetEnvironmentVariable("WHALEPOD_AGENT_INSTANCE_NAME");
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            instanceName = $"whalepod-agent-{Environment.MachineName}";
        }

        instanceName = instanceName.Trim();
        if (instanceName.Length > 80 || instanceName.Any(char.IsControl))
        {
            throw new InvalidOperationException("Agent 实例名不能超过 80 个字符或包含控制字符。");
        }

        var hostFingerprint = Environment.GetEnvironmentVariable("WHALEPOD_AGENT_HOST_FINGERPRINT");
        if (string.IsNullOrWhiteSpace(hostFingerprint))
        {
            hostFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{Environment.MachineName}|{System.Runtime.InteropServices.RuntimeInformation.OSDescription}")));
        }

        hostFingerprint = hostFingerprint.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(hostFingerprint, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("WHALEPOD_AGENT_HOST_FINGERPRINT 必须是 64 位小写十六进制。");
        }

        var agentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        var promptVersion = Environment.GetEnvironmentVariable("WHALEPOD_AGENT_PROMPT_VERSION")
            ?? "moderation-prompt-v1";
        return new AgentCliOptions(
            apiBaseUrl.TrimEnd('/'),
            token,
            instanceName,
            hostFingerprint,
            agentVersion,
            McpProtocol.ServerVersion,
            modelName,
            modelVersion,
            modelQuantization,
            promptVersion.Trim());
    }

    public static Uri ParseApiBaseUrl(string value, bool allowInsecureHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("WHALEPOD_AGENT_API_URL 必须是 HTTP(S) 绝对地址。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("WHALEPOD_AGENT_API_URL 不能包含用户信息。");
        }

        if (uri.Scheme == "http" && !(allowInsecureHttp && uri.IsLoopback))
        {
            throw new InvalidOperationException(
                "Agent API 默认必须使用 HTTPS；仅显式设置 WHALEPOD_AGENT_ALLOW_INSECURE_HTTP=true 且地址为回环时才允许 HTTP。");
        }

        return uri;
    }

    private static string Required(string name)
    {
        var value = (Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException($"{name} 是必需配置。");
        }

        return value;
    }
}

public sealed record AgentStartupInfo(
    string InstanceId,
    string PolicyVersion,
    bool SuggestionsPaused);