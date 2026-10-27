using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhalePod.Agent;

public sealed class AgentApiException(
    string code,
    HttpStatusCode statusCode,
    string message) : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class AgentDisabledException() : Exception("AI/MCP 已关闭。");

public sealed record McpSuggestionInput(
    string Suggestion,
    string PolicyCode,
    string Reason,
    double Confidence,
    int ContentVersion,
    string IdempotencyKey,
    int? LatencyMs,
    JsonObject? TokenUsage);

public interface IAgentApiClient
{
    string InstanceId { get; }
    Task<AgentStartupInfo> InitializeAsync(CancellationToken cancellationToken = default);
    Task<JsonObject> HeartbeatAsync(int queueDepth, DateTimeOffset? lastProcessedAt, CancellationToken cancellationToken = default);
    Task<JsonObject> GetPolicyAsync(CancellationToken cancellationToken = default);
    Task<JsonObject> ListTasksAsync(string? status, int limit, string? cursor, CancellationToken cancellationToken = default);
    Task<JsonObject> GetTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task<JsonObject> ClaimTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task<JsonObject> ReleaseTaskAsync(string taskId, string? reason, CancellationToken cancellationToken = default);
    Task<JsonObject> SubmitSuggestionAsync(string taskId, McpSuggestionInput input, CancellationToken cancellationToken = default);
}

public sealed class AgentApiClient(AgentCliOptions options, HttpClient http) : IAgentApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string? _policyVersion;

    public string InstanceId { get; private set; } = string.Empty;

    public async Task<AgentStartupInfo> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var heartbeat = await HeartbeatAsync(0, null, cancellationToken);
        InstanceId = heartbeat["instanceId"]?.GetValue<string>()
            ?? throw new AgentApiException(
                "INVALID_RESPONSE",
                HttpStatusCode.BadGateway,
                "心跳响应缺少 instanceId。");
        var policy = await GetPolicyAsync(cancellationToken);
        var enabled = policy["enabled"]?.GetValue<bool>() ?? false;
        if (!enabled)
        {
            throw new AgentDisabledException();
        }

        _policyVersion = policy["policyVersion"]?.GetValue<string>()
            ?? throw new AgentApiException(
                "INVALID_RESPONSE",
                HttpStatusCode.BadGateway,
                "策略响应缺少 policyVersion。");
        return new AgentStartupInfo(
            InstanceId,
            _policyVersion,
            policy["suggestionsPaused"]?.GetValue<bool>() ?? false);
    }

    public Task<JsonObject> HeartbeatAsync(
        int queueDepth,
        DateTimeOffset? lastProcessedAt,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(
            HttpMethod.Post,
            "/api/v1/agent/heartbeat",
            new
            {
                instanceName = options.InstanceName,
                hostFingerprint = options.HostFingerprint,
                agentVersion = options.AgentVersion,
                mcpVersion = options.McpVersion,
                runtimeLocation = "local",
                model = Model(),
                queueDepth,
                lastProcessedAt
            },
            requireInstance: false,
            cancellationToken);
    }

    public async Task<JsonObject> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        var policy = await SendAsync(
            HttpMethod.Get,
            "/api/v1/agent/moderation/policy",
            null,
            requireInstance: false,
            cancellationToken);
        _policyVersion = policy["policyVersion"]?.GetValue<string>() ?? _policyVersion;
        return policy;
    }

    public Task<JsonObject> ListTasksAsync(
        string? status,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"limit={limit}" };
        if (!string.IsNullOrWhiteSpace(status))
        {
            query.Add($"status={Uri.EscapeDataString(status)}");
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        return SendAsync(
            HttpMethod.Get,
            $"/api/v1/agent/moderation/tasks?{string.Join('&', query)}",
            null,
            requireInstance: true,
            cancellationToken);
    }

    public Task<JsonObject> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return SendAsync(
            HttpMethod.Get,
            $"/api/v1/agent/moderation/tasks/{Uri.EscapeDataString(taskId)}",
            null,
            requireInstance: true,
            cancellationToken);
    }

    public Task<JsonObject> ClaimTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return SendAsync(
            HttpMethod.Post,
            $"/api/v1/agent/moderation/tasks/{Uri.EscapeDataString(taskId)}/claim",
            null,
            requireInstance: true,
            cancellationToken);
    }

    public Task<JsonObject> ReleaseTaskAsync(
        string taskId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(
            HttpMethod.Post,
            $"/api/v1/agent/moderation/tasks/{Uri.EscapeDataString(taskId)}/release",
            string.IsNullOrWhiteSpace(reason) ? null : new { reason },
            requireInstance: true,
            cancellationToken);
    }

    public Task<JsonObject> SubmitSuggestionAsync(
        string taskId,
        McpSuggestionInput input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_policyVersion))
        {
            throw new InvalidOperationException("策略尚未初始化。");
        }

        var body = new JsonObject
        {
            ["suggestion"] = input.Suggestion,
            ["policyCode"] = input.PolicyCode,
            ["reason"] = input.Reason,
            ["confidence"] = input.Confidence,
            ["contentVersion"] = input.ContentVersion,
            ["policyVersion"] = _policyVersion,
            ["model"] = ModelNode(),
            ["modelRuntime"] = "local",
            ["runtimeLocation"] = "local",
            ["promptVersion"] = options.PromptVersion,
            ["idempotencyKey"] = input.IdempotencyKey,
            ["latencyMs"] = input.LatencyMs,
            ["tokenUsage"] = input.TokenUsage?.DeepClone()
        };
        return SendAsync(
            HttpMethod.Post,
            $"/api/v1/agent/moderation/tasks/{Uri.EscapeDataString(taskId)}/suggestion",
            body,
            requireInstance: true,
            cancellationToken);
    }

    private async Task<JsonObject> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        bool requireInstance,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.Token);
        if (requireInstance)
        {
            if (string.IsNullOrWhiteSpace(InstanceId))
            {
                throw new InvalidOperationException("Agent 尚未完成心跳，缺少实例标识。");
            }

            request.Headers.Add("X-Agent-Instance-Id", InstanceId);
        }

        if (body is not null)
        {
            var node = body is JsonNode jsonNode
                ? jsonNode
                : JsonSerializer.SerializeToNode(body, JsonOptions) ?? new JsonObject();
            request.Content = new StringContent(
                node.ToJsonString(),
                System.Text.Encoding.UTF8,
                "application/json");
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var code = "AGENT_API_ERROR";
            var message = "Agent API 请求失败。";
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.TryGetProperty("code", out var codeElement)
                    && codeElement.ValueKind == JsonValueKind.String)
                {
                    code = codeElement.GetString() ?? code;
                }

                if (document.RootElement.TryGetProperty("message", out var messageElement)
                    && messageElement.ValueKind == JsonValueKind.String)
                {
                    message = messageElement.GetString() ?? message;
                }
            }
            catch (JsonException)
            {
            }

            if (code == "AGENT_DISABLED")
            {
                throw new AgentDisabledException();
            }

            throw new AgentApiException(code, response.StatusCode, message);
        }

        try
        {
            var document = JsonNode.Parse(content);
            if (document?["data"] is not JsonObject data)
            {
                throw new AgentApiException(
                    "INVALID_RESPONSE",
                    HttpStatusCode.BadGateway,
                    "Agent API 响应缺少 data。");
            }

            return data;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new AgentApiException(
                "INVALID_RESPONSE",
                HttpStatusCode.BadGateway,
                "Agent API 返回了非法 JSON。");
        }
    }

    private object Model()
    {
        return new
        {
            name = options.ModelName,
            version = options.ModelVersion,
            quantization = options.ModelQuantization,
            runtime = "local"
        };
    }

    private JsonObject ModelNode()
    {
        return new JsonObject
        {
            ["name"] = options.ModelName,
            ["version"] = options.ModelVersion,
            ["quantization"] = options.ModelQuantization,
            ["runtime"] = "local"
        };
    }
}