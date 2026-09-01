using System.Diagnostics;
using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using GBL.AX2012.MCP.Core.Exceptions;
using GBL.AX2012.MCP.Core.Interfaces;
using GBL.AX2012.MCP.Core.Models;

namespace GBL.AX2012.MCP.Server.Tools;

public abstract class ToolBase<TInput, TOutput> : ITool
    where TInput : class
    where TOutput : class
{
    protected readonly ILogger _logger;
    protected readonly IAuditService _audit;
    protected readonly IValidator<TInput>? _validator;
    
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual JsonElement InputSchema => GetDefaultSchema();
    
    protected ToolBase(ILogger logger, IAuditService audit, IValidator<TInput>? validator = null)
    {
        _logger = logger;
        _audit = audit;
        _validator = validator;
    }
    
    public async Task<ToolResponse> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var auditEntry = new AuditEntry
        {
            Id = Guid.NewGuid(),
            ToolName = Name,
            UserId = context.UserId,
            CorrelationId = context.CorrelationId,
            Timestamp = DateTime.UtcNow,
            Input = input.ToString()
        };
        
        try
        {
            _logger.LogDebug("Executing tool {Tool} for user {User}", Name, context.UserId);
            
            // Deserialize input
            var typedInput = JsonSerializer.Deserialize<TInput>(input, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            if (typedInput == null)
            {
                throw new FluentValidation.ValidationException("Invalid input: could not deserialize");
            }
            
            // Validate input
            if (_validator != null)
            {
                var validationResult = await _validator.ValidateAsync(typedInput, cancellationToken);
                if (!validationResult.IsValid)
                {
                    var errors = string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage));
                    throw new FluentValidation.ValidationException(errors);
                }
            }
            
            // Execute tool logic
            var output = await ExecuteCoreAsync(typedInput, context, cancellationToken);
            
            auditEntry.Success = true;
            auditEntry.Output = JsonSerializer.Serialize(output);
            auditEntry.DurationMs = stopwatch.ElapsedMilliseconds;
            
            _logger.LogInformation("Tool {Tool} completed in {Duration}ms", Name, stopwatch.ElapsedMilliseconds);
            
            return new ToolResponse
            {
                Success = true,
                Data = output,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (FluentValidation.ValidationException ex)
        {
            auditEntry.Success = false;
            auditEntry.Error = ex.Message;
            auditEntry.DurationMs = stopwatch.ElapsedMilliseconds;
            
            _logger.LogWarning("Validation failed for {Tool}: {Error}", Name, ex.Message);
            
            return ToolResponse.Error("VALIDATION_ERROR", ex.Message);
        }
        catch (AxException ex)
        {
            auditEntry.Success = false;
            auditEntry.Error = ex.Message;
            auditEntry.DurationMs = stopwatch.ElapsedMilliseconds;
            
            _logger.LogError(ex, "AX error in {Tool}", Name);
            
            return ToolResponse.Error(ex.ErrorCode, ex.Message);
        }
        catch (CircuitBreakerOpenException ex)
        {
            auditEntry.Success = false;
            auditEntry.Error = ex.Message;
            auditEntry.DurationMs = stopwatch.ElapsedMilliseconds;

            _logger.LogWarning("Circuit breaker open for {Tool}", Name);

            return ToolResponse.Error("CIRCUIT_OPEN", ex.Message);
        }
        catch (ForbiddenException ex)
        {
            auditEntry.Success = false;
            auditEntry.Error = ex.Message;
            auditEntry.DurationMs = stopwatch.ElapsedMilliseconds;

            _logger.LogWarning("Forbidden for {Tool}: {Error}", Name, ex.Message);

            return ToolResponse.Error("FORBIDDEN", ex.Message);
        }
        catch (Exception ex)
        {
            auditEntry.Success = false;
            auditEntry.Error = ex.Message;
            auditEntry.DurationMs = stopwatch.ElapsedMilliseconds;
            
            _logger.LogError(ex, "Unexpected error in {Tool}", Name);
            
            return ToolResponse.Error("INTERNAL_ERROR", "An unexpected error occurred");
        }
        finally
        {
            await _audit.LogAsync(auditEntry, cancellationToken);
        }
    }
    
    protected abstract Task<TOutput> ExecuteCoreAsync(TInput input, ToolContext context, CancellationToken cancellationToken);
    
    private JsonElement GetDefaultSchema()
    {
        var schema = new { type = "object", properties = new { } };
        return JsonSerializer.SerializeToElement(schema);
    }
}
