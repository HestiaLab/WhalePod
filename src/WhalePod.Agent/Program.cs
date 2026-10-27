using System.Text;

using WhalePod.Agent;

try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
}
catch (Exception)
{
}

if (args.Contains("--version", StringComparer.Ordinal))
{
    Console.WriteLine(
        $"whalepod-agent {McpProtocol.ServerVersion} (MCP {McpProtocol.ProtocolVersion}, tool schema {McpProtocol.ToolSchemaVersion})");
    return 0;
}

if (args.Contains("--print-tools", StringComparer.Ordinal))
{
    Console.WriteLine(McpToolCatalog.ToJsonArray().ToJsonString());
    return 0;
}

AgentCliOptions options;
try
{
    options = AgentCliOptions.FromEnvironment();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Agent 配置无效：{exception.Message}");
    return 1;
}

using var http = new HttpClient
{
    BaseAddress = new Uri(options.ApiBaseUrl),
    Timeout = TimeSpan.FromSeconds(30)
};
var client = new AgentApiClient(options, http);
var server = new McpStdioServer(client, Console.In, Console.Out, Console.Error);
try
{
    await server.RunAsync();
    return 0;
}
catch (AgentDisabledException)
{
    Console.Error.WriteLine("AI/MCP 已关闭；MCP Server 不启动。");
    return 0;
}
catch (AgentApiException exception)
{
    Console.Error.WriteLine($"Agent API 错误：{exception.Code}");
    return 1;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"MCP Server 启动失败：{exception.GetType().Name}");
    return 1;
}