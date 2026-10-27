using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhalePod.Agent;

public sealed class McpStdioServer(
    IAgentApiClient client,
    TextReader input,
    TextWriter output,
    TextWriter error)
{
    private readonly HashSet<string> _claimedTaskIds = new(StringComparer.Ordinal);
    private bool _shutdownRequested;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var startup = await client.InitializeAsync(cancellationToken);
        await error.WriteLineAsync(
            $"WhalePod MCP {McpProtocol.ServerVersion} started; instance={startup.InstanceId}; paused={startup.SuggestionsPaused}");
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_shutdownRequested)
            {
                var line = await input.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonNode? request;
                try
                {
                    request = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    await WriteAsync(ErrorResponse(null, -32700, "Parse error"), cancellationToken);
                    continue;
                }

                var response = await HandleRequestAsync(request);
                if (response is not null)
                {
                    await WriteAsync(response, cancellationToken);
                }
            }
        }
        finally
        {
            await ReleaseClaimsAsync();
        }
    }

    public async Task<JsonObject?> HandleRequestAsync(JsonNode? request)
    {
        var requestObject = request as JsonObject;
        var method = requestObject?["method"] is JsonValue methodValue
            && methodValue.TryGetValue<string>(out var parsedMethod)
            ? parsedMethod
            : null;
        if (requestObject is null || string.IsNullOrWhiteSpace(method))
        {
            return ErrorResponse(requestObject?["id"], -32600, "Invalid Request");
        }

        var id = requestObject["id"]?.DeepClone();
        if (method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            return null;
        }

        var parameters = requestObject["params"] as JsonObject ?? new JsonObject();
        switch (method)
        {
            case "initialize":
                return ResultResponse(id, new JsonObject
                {
                    ["protocolVersion"] = McpProtocol.ProtocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject()
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = McpProtocol.ServerName,
                        ["version"] = McpProtocol.ServerVersion
                    },
                    ["_meta"] = new JsonObject
                    {
                        ["toolSchemaVersion"] = McpProtocol.ToolSchemaVersion
                    }
                });
            case "ping":
                return ResultResponse(id, new JsonObject());
            case "tools/list":
                return ResultResponse(id, new JsonObject
                {
                    ["tools"] = McpToolCatalog.ToJsonArray()
                });
            case "tools/call":
                return ResultResponse(id, await InvokeToolCallAsync(parameters));
            case "shutdown":
                _shutdownRequested = true;
                return ResultResponse(id, new JsonObject());
            default:
                return ErrorResponse(id, -32601, "Method not found");
        }
    }

    private async Task<JsonObject> InvokeToolCallAsync(JsonObject parameters)
    {
        var name = parameters["name"] is JsonValue nameValue
            && nameValue.TryGetValue<string>(out var parsedName)
            ? parsedName
            : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolError("INVALID_ARGUMENTS", "缺少工具名称。");
        }

        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            var (data, taskContent) = name switch
            {
                "moderation_list_tasks" => (await ListTasksAsync(arguments), true),
                "moderation_get_task" => (await GetTaskAsync(arguments), true),
                "moderation_claim_task" => (await ClaimTaskAsync(arguments), true),
                "moderation_submit_suggestion" => (await SubmitSuggestionAsync(arguments), false),
                "moderation_release_task" => (await ReleaseTaskAsync(arguments), false),
                "moderation_heartbeat" => (await HeartbeatAsync(arguments), false),
                "moderation_get_policy" => (await PolicyAsync(arguments), false),
                _ => (null, false)
            };
            if (data is null)
            {
                return ToolError("UNKNOWN_TOOL", $"未知工具：{name}");
            }

            return ToolResult(data, taskContent);
        }
        catch (AgentDisabledException)
        {
            return ToolError("AGENT_DISABLED", "AI/MCP 已关闭。");
        }
        catch (AgentApiException exception)
        {
            await error.WriteLineAsync(
                $"MCP tool {name} failed: {exception.Code} ({exception.StatusCode})");
            return ToolError(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or FormatException)
        {
            await error.WriteLineAsync($"MCP tool {name} invalid arguments.");
            return ToolError("INVALID_ARGUMENTS", exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"MCP tool {name} failed unexpectedly.");
            return ToolError("MCP_TOOL_ERROR", "工具调用失败，请查看本机 Agent 日志。");
        }
    }

    private async Task<JsonObject> ListTasksAsync(JsonObject arguments)
    {
        EnsureAllowed(arguments, "status", "limit", "cursor");
        var status = OptionalString(arguments, "status", 20);
        if (status is not null && status is not ("queued" or "claimed"))
        {
            throw new ArgumentException("status 必须是 queued 或 claimed。");
        }

        var limit = OptionalInt(arguments, "limit") ?? 20;
        if (limit is < 1 or > 50)
        {
            throw new ArgumentException("limit 必须是 1 到 50。");
        }

        return await client.ListTasksAsync(
            status,
            limit,
            OptionalString(arguments, "cursor", 512));
    }

    private async Task<JsonObject> GetTaskAsync(JsonObject arguments)
    {
        EnsureAllowed(arguments, "taskId");
        return await client.GetTaskAsync(RequiredTaskId(arguments));
    }

    private async Task<JsonObject> ClaimTaskAsync(JsonObject arguments)
    {
        EnsureAllowed(arguments, "taskId");
        var taskId = RequiredTaskId(arguments);
        var data = await client.ClaimTaskAsync(taskId);
        _claimedTaskIds.Add(taskId);
        return data;
    }

    private async Task<JsonObject> SubmitSuggestionAsync(JsonObject arguments)
    {
        EnsureAllowed(
            arguments,
            "taskId",
            "suggestion",
            "policyCode",
            "reason",
            "confidence",
            "contentVersion",
            "idempotencyKey",
            "latencyMs",
            "tokenUsage");
        var taskId = RequiredTaskId(arguments);
        var suggestion = RequiredString(arguments, "suggestion", 20);
        if (suggestion is not ("approve" or "reject" or "escalate"))
        {
            throw new ArgumentException("suggestion 必须是 approve、reject 或 escalate。");
        }

        var confidence = RequiredDouble(arguments, "confidence");
        if (confidence is < 0 or > 1)
        {
            throw new ArgumentException("confidence 必须在 0 到 1 之间。");
        }

        var contentVersion = RequiredInt(arguments, "contentVersion");
        if (contentVersion < 1)
        {
            throw new ArgumentException("contentVersion 必须大于 0。");
        }

        var idempotencyKey = RequiredString(arguments, "idempotencyKey", 100);
        if (idempotencyKey.Length < 8)
        {
            throw new ArgumentException("idempotencyKey 至少 8 个字符。");
        }

        var latencyMs = OptionalInt(arguments, "latencyMs");
        if (latencyMs is < 0)
        {
            throw new ArgumentException("latencyMs 不能为负数。");
        }

        var tokenUsage = arguments["tokenUsage"] as JsonObject;
        var data = await client.SubmitSuggestionAsync(
            taskId,
            new McpSuggestionInput(
                suggestion,
                RequiredString(arguments, "policyCode", 80),
                RequiredString(arguments, "reason", 2000),
                confidence,
                contentVersion,
                idempotencyKey,
                latencyMs,
                tokenUsage?.DeepClone().AsObject()));
        _claimedTaskIds.Remove(taskId);
        return data;
    }

    private async Task<JsonObject> ReleaseTaskAsync(JsonObject arguments)
    {
        EnsureAllowed(arguments, "taskId", "reason");
        var taskId = RequiredTaskId(arguments);
        var data = await client.ReleaseTaskAsync(
            taskId,
            OptionalString(arguments, "reason", 200));
        _claimedTaskIds.Remove(taskId);
        return data;
    }

    private async Task<JsonObject> HeartbeatAsync(JsonObject arguments)
    {
        EnsureAllowed(arguments, "queueDepth", "lastProcessedAt");
        var queueDepth = OptionalInt(arguments, "queueDepth") ?? 0;
        if (queueDepth < 0)
        {
            throw new ArgumentException("queueDepth 不能为负数。");
        }

        DateTimeOffset? lastProcessedAt = null;
        var lastProcessedText = OptionalString(arguments, "lastProcessedAt", 64);
        if (!string.IsNullOrWhiteSpace(lastProcessedText))
        {
            if (!DateTimeOffset.TryParse(
                    lastProcessedText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                throw new ArgumentException("lastProcessedAt 必须是 ISO 8601 时间。");
            }

            lastProcessedAt = parsed;
        }

        return await client.HeartbeatAsync(queueDepth, lastProcessedAt);
    }

    private async Task<JsonObject> PolicyAsync(JsonObject arguments)
    {
        EnsureAllowed(arguments);
        return await client.GetPolicyAsync();
    }

    private async Task ReleaseClaimsAsync()
    {
        foreach (var taskId in _claimedTaskIds.ToList())
        {
            try
            {
                await client.ReleaseTaskAsync(taskId, "mcp_disconnect");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await error.WriteLineAsync($"Failed to release MCP task lease on exit: {exception.GetType().Name}");
            }
        }

        _claimedTaskIds.Clear();
    }

    private static string RequiredTaskId(JsonObject arguments)
    {
        var value = RequiredString(arguments, "taskId", 64);
        return Guid.TryParse(value, out _)
            ? value
            : throw new ArgumentException("taskId 必须是 UUID。");
    }

    private static string RequiredString(JsonObject arguments, string name, int maxLength)
    {
        var value = arguments[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maxLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException($"{name} 不能为空、超长或包含控制字符。");
        }

        return value;
    }

    private static string? OptionalString(JsonObject arguments, string name, int maxLength)
    {
        if (arguments[name] is null)
        {
            return null;
        }

        var value = arguments[name]!.GetValue<string>();
        if (value.Length > maxLength || value.Any(char.IsControl))
        {
            throw new ArgumentException($"{name} 超长或包含控制字符。");
        }

        return value;
    }

    private static int? OptionalInt(JsonObject arguments, string name)
    {
        if (arguments[name] is null)
        {
            return null;
        }

        return arguments[name]!.GetValue<int>();
    }

    private static int RequiredInt(JsonObject arguments, string name)
    {
        return arguments[name]?.GetValue<int>()
            ?? throw new ArgumentException($"{name} 是必需整数。");
    }

    private static double RequiredDouble(JsonObject arguments, string name)
    {
        return arguments[name]?.GetValue<double>()
            ?? throw new ArgumentException($"{name} 是必需数字。");
    }

    private static void EnsureAllowed(JsonObject arguments, params string[] allowed)
    {
        foreach (var pair in arguments)
        {
            if (!allowed.Contains(pair.Key, StringComparer.Ordinal))
            {
                throw new ArgumentException($"未知字段：{pair.Key}");
            }
        }
    }

    private static JsonObject ToolResult(JsonObject data, bool taskContent)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = data.ToJsonString()
                }
            },
            ["structuredContent"] = new JsonObject
            {
                ["untrusted_content"] = taskContent,
                ["data"] = data.DeepClone()
            },
            ["isError"] = false
        };
    }

    private static JsonObject ToolError(string code, string message)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"{code}: {message}"
                }
            },
            ["structuredContent"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            },
            ["isError"] = true
        };
    }

    private static JsonObject ResultResponse(JsonNode? id, JsonObject result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        };
    }

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
    }

    private async Task WriteAsync(JsonObject response, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(response.ToJsonString());
        await output.FlushAsync(cancellationToken);
    }
}