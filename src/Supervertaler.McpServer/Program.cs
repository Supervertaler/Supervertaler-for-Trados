using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Supervertaler.McpServer;

// Supervertaler MCP Server – stdio MCP server that fronts the localhost HTTP
// bridge hosted by Supervertaler for Trados inside Trados Studio.
//
// AI apps (Claude Desktop, ChatGPT desktop, Claude Code, …) launch this exe
// and speak MCP over stdin/stdout. Every tool call is forwarded to the
// plugin's bridge, discovered via its handshake file. Nothing leaves the
// machine: the bridge is loopback-only and token-authenticated.
//
// IMPORTANT: stdout belongs to the MCP protocol – all logging goes to stderr.

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<BridgeClient>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
