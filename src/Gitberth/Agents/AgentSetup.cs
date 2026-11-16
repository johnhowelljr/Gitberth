namespace Gitberth.Agents;

/// <summary>One client's ready-to-paste setup for a new token.</summary>
/// <param name="Id">claude-code, vscode, cursor, claude-desktop, generic, curl.</param>
/// <param name="Where">Where it goes (a command to run, a file to put it in).</param>
public sealed record AgentClientSetup(string Id, string Label, string Where, string Code, string? Note = null);

/// <summary>
/// The setup Settings › AI agents and API shows once a token is made, for each client: Claude Code, VS Code, Cursor,
/// Claude Desktop (through mcp-remote), any MCP client, and curl for the HTTP API. Formats as each client's docs give them.
/// </summary>
public static class AgentSetup
{
    /// <summary>The server's name in each client's config.</summary>
    public const string ServerName = "gitberth";

    /// <summary>The MCP endpoint at Gitberth's address: http://10.1.1.80:8080/mcp.</summary>
    public static string McpUrl(string root) => root.TrimEnd('/') + AgentPaths.Mcp;

    /// <summary>The HTTP API's base: http://10.1.1.80:8080/api/v1.</summary>
    public static string ApiUrl(string root) => root.TrimEnd('/') + AgentPaths.ApiV1;

    public static IReadOnlyList<AgentClientSetup> For(string root, string token)
    {
        var mcp = McpUrl(root);
        var api = ApiUrl(root);
        // mcp-remote refuses plain HTTP except on localhost unless it's told the network is trusted.
        var plainHttp = mcp.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLocal(mcp);
        return
        [
            new("claude-code", "Claude Code", "Run in a terminal",
                $"claude mcp add --transport http {ServerName} {mcp} --header \"Authorization: Bearer {token}\"",
                "Add --scope user to have it in every project. /mcp in Claude Code shows it connected."),
            new("vscode", "VS Code", ".vscode/mcp.json (or MCP: Open User Configuration)",
                $$"""
                {
                  "servers": {
                    "{{ServerName}}": {
                      "type": "http",
                      "url": "{{mcp}}",
                      "headers": { "Authorization": "Bearer ${input:gitberth-token}" }
                    }
                  },
                  "inputs": [
                    { "type": "promptString", "id": "gitberth-token", "description": "Gitberth API token", "password": true }
                  ]
                }
                """,
                "VS Code asks for the token when the server first starts: paste it then. It's kept in VS Code's secret storage, not in the file."),
            new("cursor", "Cursor", "~/.cursor/mcp.json (or .cursor/mcp.json in a project)",
                $$"""
                {
                  "mcpServers": {
                    "{{ServerName}}": {
                      "url": "{{mcp}}",
                      "headers": { "Authorization": "Bearer {{token}}" }
                    }
                  }
                }
                """),
            new("claude-desktop", "Claude Desktop", "claude_desktop_config.json (Settings › Developer › Edit Config)",
                $$"""
                {
                  "mcpServers": {
                    "{{ServerName}}": {
                      "command": "npx",
                      "args": ["mcp-remote", "{{mcp}}", {{(plainHttp ? "\"--allow-http\", " : "")}}"--header", "Authorization:${GITBERTH_AUTH}"],
                      "env": { "GITBERTH_AUTH": "Bearer {{token}}" }
                    }
                  }
                }
                """,
                "Claude Desktop reaches Gitberth through mcp-remote (it needs Node.js). Restart Claude Desktop after saving. " +
                "Its own custom connectors are reached from Anthropic's cloud and can't send this header, so they don't work for a server on your network."),
            new("generic", "Other MCP clients", "Any client that speaks Streamable HTTP",
                $"""
                URL:     {mcp}
                Header:  Authorization: Bearer {token}
                """,
                "Stateless Streamable HTTP: no sessions, so it works behind any proxy. Requests from a browser page (with an Origin header) are refused."),
            new("curl", "HTTP API (curl)", "Scripts and agent frameworks without MCP",
                $"""
                curl -s {api}/tools -H "Authorization: Bearer {token}"
                curl -s -X POST {api}/tools/list_apps -H "Authorization: Bearer {token}" -H "Content-Type: application/json" -d '{"{}"}'
                """,
                $"The same tools as MCP: POST a JSON object of a tool's arguments to {api}/tools/<name>. {api}/openapi.json describes them all."),
        ];
    }

    private static bool IsLocal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
}
