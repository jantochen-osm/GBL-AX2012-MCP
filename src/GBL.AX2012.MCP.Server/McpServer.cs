using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GBL.AX2012.MCP.Core.Exceptions;
using GBL.AX2012.MCP.Core.Interfaces;
using GBL.AX2012.MCP.Core.Models;
using GBL.AX2012.MCP.Core.Options;
using GBL.AX2012.MCP.Server.Security;

namespace GBL.AX2012.MCP.Server;

public class McpServer : BackgroundService
{
    private readonly ILogger<McpServer> _logger;
    private readonly IServiceProvider _services;
    private readonly McpServerOptions _options;
    private readonly IEnumerable<ITool> _tools;
    private readonly IAuthenticationService _authService;
    private readonly IAuthorizationService _authzService;
    private readonly IRateLimiter _rateLimiter;
    
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    
    public McpServer(
        ILogger<McpServer> logger,
        IServiceProvider services,
        IOptions<McpServerOptions> options,
        IEnumerable<ITool> tools,
        IAuthenticationService authService,
        IAuthorizationService authzService,
        IRateLimiter rateLimiter)
    {
        _logger = logger;
        _services = services;
        _options = options.Value;
        _tools = tools;
        _authService = authService;
        _authzService = authzService;
        _rateLimiter = rateLimiter;
    }
    
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting MCP Server {Name} v{Version} on {Transport}",
            _options.ServerName, _options.ServerVersion, _options.Transport);
        
        var toolList = _tools.ToList();
        _logger.LogInformation("Registered {Count} tools: {Tools}",
            toolList.Count, string.Join(", ", toolList.Select(t => t.Name)));
        
        await base.StartAsync(cancellationToken);
    }
    
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping MCP Server");
        return base.StopAsync(cancellationToken);
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Transport != "stdio")
        {
            // In HTTP mode, just keep the service alive
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            return;
        }
        
        await RunMcpProtocolAsync(stoppingToken);
    }
    
    private async Task RunMcpProtocolAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput());
            using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                var response = await ProcessMessageAsync(line, cancellationToken);
                if (response != null)
                {
                    await writer.WriteLineAsync(response);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MCP protocol handler cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP protocol handler error");
        }
    }
    
    private async Task<string?> ProcessMessageAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            var request = JsonSerializer.Deserialize<McpRequest>(message, _jsonOptions);
            if (request == null)
            {
                return CreateErrorResponse(null, -32700, "Parse error");
            }
            
            _logger.LogDebug("Received MCP request: {Method}", request.Method);
            
            return request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "initialized" => null, // No response needed
                "tools/list" => HandleToolsList(request),
                "tools/call" => await HandleToolCallAsync(request, cancellationToken),
                "ping" => HandlePing(request),
                _ => CreateErrorResponse(request.Id, -32601, $"Method not found: {request.Method}")
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse MCP message");
            return CreateErrorResponse(null, -32700, "Parse error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing MCP message");
            return CreateErrorResponse(null, -32603, "Internal error");
        }
    }
    
    private string HandleInitialize(McpRequest request)
    {
        _logger.LogInformation("MCP Initialize request received");
        
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = request.Id,
            result = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new
                {
                    tools = new { listChanged = false }
                },
                serverInfo = new
                {
                    name = _options.ServerName,
                    version = _options.ServerVersion
                }
            }
        }, _jsonOptions);
    }
    
    private string HandleToolsList(McpRequest request)
    {
        var tools = _tools.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = t.InputSchema
        });
        
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = request.Id,
            result = new { tools }
        }, _jsonOptions);
    }
    
    private async Task<string> HandleToolCallAsync(McpRequest request, CancellationToken cancellationToken)
    {
        var toolName = request.Params?.GetProperty("name").GetString();
        if (string.IsNullOrEmpty(toolName))
        {
            return CreateErrorResponse(request.Id, -32602, "Invalid params: missing tool name");
        }
        
        var tool = _tools.FirstOrDefault(t => t.Name == toolName);
        if (tool == null)
        {
            return CreateErrorResponse(request.Id, -32602, $"Tool not found: {toolName}");
        }
        
        // Create authenticated context
        var authResult = await _authService.AuthenticateAsync(cancellationToken);
        var context = new ToolContext
        {
            UserId = authResult.UserId ?? "anonymous",
            Roles = authResult.Roles,
            CorrelationId = Guid.NewGuid().ToString()
        };
        
        // Check rate limit
        if (!await _rateLimiter.TryAcquireAsync(context.UserId, cancellationToken))
        {
            return CreateErrorResponse(request.Id, -32000, "Rate limit exceeded");
        }
        
        // Check authorization
        var requiredRoles = ToolRoleMapping.GetRequiredRoles(toolName);
        try
        {
            _authzService.EnsureAuthorized(context, requiredRoles);
        }
        catch (ForbiddenException ex)
        {
            return CreateErrorResponse(request.Id, -32000, ex.Message);
        }
        
        // Execute tool
        var arguments = request.Params?.TryGetProperty("arguments", out var args) == true 
            ? args 
            : JsonSerializer.SerializeToElement(new { });
        
        var result = await tool.ExecuteAsync(arguments, context, cancellationToken);
        
        if (result.Success)
        {
            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = request.Id,
                result = new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = JsonSerializer.Serialize(result.Data, _jsonOptions)
                        }
                    }
                }
            }, _jsonOptions);
        }
        else
        {
            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = request.Id,
                result = new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = JsonSerializer.Serialize(new
                            {
                                error = result.ErrorCode,
                                message = result.ErrorMessage
                            }, _jsonOptions)
                        }
                    },
                    isError = true
                }
            }, _jsonOptions);
        }
    }
    
    private string HandlePing(McpRequest request)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = request.Id,
            result = new { }
        }, _jsonOptions);
    }
    
    private string CreateErrorResponse(object? id, int code, string message)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message }
        }, _jsonOptions);
    }
}

public class McpRequest
{
    public string Jsonrpc { get; set; } = "2.0";
    public object? Id { get; set; }
    public string Method { get; set; } = "";
    public JsonElement? Params { get; set; }
}
