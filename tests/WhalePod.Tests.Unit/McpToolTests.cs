using System.Text.Json;
using System.Text.Json.Nodes;

using WhalePod.Agent;

namespace WhalePod.Tests.Unit;

public sealed class McpToolTests
{
    [Fact]
    public void Catalog_contains_only_the_controlled_tools()
    {
        var names = McpToolCatalog.Tools.Select(tool => tool.Name).ToList();
        Assert.Equal(7, names.Count);
        Assert.Equal(
            [
                "moderation_list_tasks",
                "moderation_get_task",
                "moderation_claim_task",
                "moderation_submit_suggestion",
                "moderation_release_task",
                "moderation_heartbeat",
                "moderation_get_policy"
            ],
            names);
        Assert.DoesNotContain(names, name =>
            name.Contains("shell", StringComparison.OrdinalIgnoreCase)
            || name.Contains("filesystem", StringComparison.OrdinalIgnoreCase)
            || name.Contains("http", StringComparison.OrdinalIgnoreCase)
            || name.Contains("admin", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ban", StringComparison.OrdinalIgnoreCase));

        foreach (var tool in McpToolCatalog.Tools)
        {
            Assert.Equal("object", tool.InputSchema["type"]?.GetValue<string>());
            Assert.False(tool.InputSchema["additionalProperties"]?.GetValue<bool>() ?? true);
        }

        var json = McpToolCatalog.ToJsonArray();
        Assert.Equal(7, json.Count);
        Assert.Equal(
            McpProtocol.ToolSchemaVersion,
            json[0]!["_meta"]!["schemaVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Server_initializes_and_lists_tools()
    {
        var server = NewServer(out _);
        var initialize = await server.HandleRequestAsync(
            JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize"}"""));
        Assert.NotNull(initialize);
        Assert.Equal(
            McpProtocol.ProtocolVersion,
            initialize!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal(
            McpProtocol.ServerName,
            initialize["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        var tools = await server.HandleRequestAsync(
            JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));
        Assert.Equal(7, tools!["result"]!["tools"]!.AsArray().Count);
        Assert.Null(await server.HandleRequestAsync(
            JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")));
        var unknown = await server.HandleRequestAsync(
            JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/unknown"}"""));
        Assert.Equal(-32601, unknown!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Server_marks_task_content_untrusted_and_rejects_bad_arguments()
    {
        var server = NewServer(out _);
        var taskId = Guid.NewGuid().ToString();
        var response = await server.HandleRequestAsync(JsonNode.Parse(
            $$"""
            {
              "jsonrpc": "2.0",
              "id": 1,
              "method": "tools/call",
              "params": {
                "name": "moderation_get_task",
                "arguments": { "taskId": "{{taskId}}" }
              }
            }
            """));
        Assert.NotNull(response);
        Assert.False(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.True(response["result"]!["structuredContent"]!["untrusted_content"]!.GetValue<bool>());
        Assert.Equal(
            "测试任务标题",
            response["result"]!["structuredContent"]!["data"]!["content"]!["title"]!.GetValue<string>());

        var policy = await server.HandleRequestAsync(JsonNode.Parse(
            """
            {
              "jsonrpc": "2.0",
              "id": 2,
              "method": "tools/call",
              "params": { "name": "moderation_get_policy", "arguments": {} }
            }
            """));
        Assert.False(policy!["result"]!["structuredContent"]!["untrusted_content"]!.GetValue<bool>());

        var unknownTool = await server.HandleRequestAsync(JsonNode.Parse(
            """
            {
              "jsonrpc": "2.0",
              "id": 3,
              "method": "tools/call",
              "params": { "name": "shell_exec", "arguments": {} }
            }
            """));
        Assert.True(unknownTool!["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(
            "UNKNOWN_TOOL",
            unknownTool["result"]!["structuredContent"]!["code"]!.GetValue<string>());

        var unknownArgument = await server.HandleRequestAsync(JsonNode.Parse(
            """
            {
              "jsonrpc": "2.0",
              "id": 4,
              "method": "tools/call",
              "params": {
                "name": "moderation_get_policy",
                "arguments": { "force": true }
              }
            }
            """));
        Assert.True(unknownArgument!["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(
            "INVALID_ARGUMENTS",
            unknownArgument["result"]!["structuredContent"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Server_releases_claimed_task_when_stdio_closes()
    {
        var taskId = Guid.NewGuid();
        var input = new StringReader(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "moderation_claim_task",
                arguments = new { taskId }
            }
        }));
        var client = new FakeAgentApiClient();
        var server = new McpStdioServer(client, input, new StringWriter(), new StringWriter());
        await server.RunAsync();
        Assert.Contains(taskId.ToString(), client.ReleasedTaskIds);
    }

    [Fact]
    public async Task Tool_timeout_does_not_end_the_session()
    {
        var client = new FakeAgentApiClient { SlowPolicy = true };
        var input = new StringReader(string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"moderation_get_policy","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"ping"}"""));
        var output = new StringWriter();
        var server = new McpStdioServer(
            client,
            input,
            output,
            new StringWriter(),
            TimeSpan.FromMilliseconds(200));
        await server.RunAsync();

        var responses = ParseResponses(output.ToString());
        Assert.Equal([1, 2], responses.Keys.OrderBy(id => id).ToList());
        Assert.True(responses[1]!["result"]!["isError"]!.GetValue<bool>());
        Assert.NotNull(responses[2]!["result"]);
    }

    [Fact]
    public async Task Cancellation_notification_cancels_the_in_flight_call()
    {
        var client = new FakeAgentApiClient { SlowPolicy = true };
        var input = new StringReader(string.Join('\n',
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"moderation_get_policy","arguments":{}}}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""",
            """{"jsonrpc":"2.0","id":8,"method":"ping"}"""));
        var output = new StringWriter();
        var server = new McpStdioServer(
            client,
            input,
            output,
            new StringWriter(),
            TimeSpan.FromSeconds(30));
        await server.RunAsync();

        var responses = ParseResponses(output.ToString());
        Assert.Equal([7, 8], responses.Keys.OrderBy(id => id).ToList());
        Assert.True(responses[7]!["result"]!["isError"]!.GetValue<bool>());
        Assert.NotNull(responses[8]!["result"]);
    }

    private static Dictionary<int, JsonNode?> ParseResponses(string output)
    {
        var responses = new Dictionary<int, JsonNode?>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var node = JsonNode.Parse(line);
            responses[node!["id"]!.GetValue<int>()] = node;
        }

        return responses;
    }

    private static McpStdioServer NewServer(out FakeAgentApiClient client)
    {
        client = new FakeAgentApiClient();
        return new McpStdioServer(client, new StringReader(string.Empty), new StringWriter(), new StringWriter());
    }

    private sealed class FakeAgentApiClient : IAgentApiClient
    {
        public string InstanceId => Guid.NewGuid().ToString();
        public List<string> ReleasedTaskIds { get; } = [];
        public bool SlowPolicy { get; set; }

        public Task<AgentStartupInfo> InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentStartupInfo(InstanceId, "policy-test", false));
        }

        public Task<JsonObject> HeartbeatAsync(
            int queueDepth,
            DateTimeOffset? lastProcessedAt,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Data(new { instanceId = InstanceId, suggestionsPaused = false }));
        }

        public async Task<JsonObject> GetPolicyAsync(CancellationToken cancellationToken = default)
        {
            if (SlowPolicy)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Data(new
            {
                enabled = true,
                suggestionsPaused = false,
                policyVersion = "policy-test"
            });
        }

        public Task<JsonObject> ListTasksAsync(
            string? status,
            int limit,
            string? cursor,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Data(new { items = Array.Empty<object>(), nextCursor = (string?)null }));
        }

        public Task<JsonObject> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Data(new
            {
                id = taskId,
                content = new { title = "测试任务标题", untrustedContent = true }
            }));
        }

        public Task<JsonObject> ClaimTaskAsync(string taskId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Data(new { task = new { id = taskId }, leaseExpiresAt = DateTimeOffset.UtcNow }));
        }

        public Task<JsonObject> ReleaseTaskAsync(
            string taskId,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            ReleasedTaskIds.Add(taskId);
            return Task.FromResult(Data(new { released = true, taskId, taskStatus = "queued" }));
        }

        public Task<JsonObject> SubmitSuggestionAsync(
            string taskId,
            McpSuggestionInput input,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Data(new
            {
                suggestionId = Guid.NewGuid(),
                taskId,
                status = "recorded",
                contentStatus = "pending",
                humanDecided = false
            }));
        }

        private static JsonObject Data(object value)
        {
            return JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))!
                .AsObject();
        }
    }
}