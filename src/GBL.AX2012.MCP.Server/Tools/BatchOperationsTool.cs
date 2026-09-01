using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GBL.AX2012.MCP.Core.Interfaces;
using GBL.AX2012.MCP.Core.Models;

namespace GBL.AX2012.MCP.Server.Tools;

public class BatchRequest
{
    public string Tool { get; set; } = "";
    public JsonElement Arguments { get; set; }
}

public class BatchOperationsInput
{
    public List<BatchRequest> Requests { get; set; } = new();
    public bool StopOnError { get; set; } = false;
    public int MaxParallel { get; set; } = 5;
}

public class BatchOperationResult
{
    public int Index { get; set; }
    public bool Success { get; set; }
    public JsonElement? Output { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public long DurationMs { get; set; }
}

public class BatchOperationsOutput
{
    public int Total { get; set; }
    public int Successful { get; set; }
    public int Failed { get; set; }
    public List<BatchOperationResult> Results { get; set; } = new();
    public long DurationMs { get; set; }
}

public class BatchOperationsInputValidator : AbstractValidator<BatchOperationsInput>
{
    public BatchOperationsInputValidator()
    {
        RuleFor(x => x.Requests)
            .NotEmpty()
            .WithMessage("At least one request is required");
        
        RuleFor(x => x.Requests.Count)
            .LessThanOrEqualTo(100)
            .WithMessage("Maximum 100 requests per batch");
        
        RuleFor(x => x.MaxParallel)
            .GreaterThan(0)
            .LessThanOrEqualTo(10)
            .WithMessage("max_parallel must be between 1 and 10");
        
        RuleForEach(x => x.Requests).ChildRules(req =>
        {
            req.RuleFor(r => r.Tool)
                .NotEmpty()
                .WithMessage("tool is required for each request");
        });
    }
}

public class BatchOperationsTool : ToolBase<BatchOperationsInput, BatchOperationsOutput>
{
    private readonly IServiceProvider _serviceProvider;
    
    public override string Name => "ax_batch_operations";
    public override string Description => "Execute multiple operations in a single batch call";
    
    public BatchOperationsTool(
        ILogger<BatchOperationsTool> logger,
        IAuditService audit,
        BatchOperationsInputValidator validator,
        IServiceProvider serviceProvider)
        : base(logger, audit, validator)
    {
        _serviceProvider = serviceProvider;
    }
    
    protected override async Task<BatchOperationsOutput> ExecuteCoreAsync(
        BatchOperationsInput input,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<BatchOperationResult>();
        var tools = _serviceProvider.GetServices<ITool>();
        var toolDict = tools.ToDictionary(t => t.Name, t => t);
        
        // Process requests in parallel batches
        var semaphore = new SemaphoreSlim(input.MaxParallel);
        var tasks = new List<Task<BatchOperationResult>>();
        
        for (int i = 0; i < input.Requests.Count; i++)
        {
            var index = i;
            var request = input.Requests[i];
            
            tasks.Add(ProcessBatchRequestAsync(
                index,
                request,
                toolDict,
                context,
                semaphore,
                cancellationToken));
        }
        
        var batchResults = await Task.WhenAll(tasks);
        results.AddRange(batchResults);
        
        // Stop on error if requested
        if (input.StopOnError)
        {
            var firstError = results.FirstOrDefault(r => !r.Success);
            if (firstError != null)
            {
                var errorIndex = results.IndexOf(firstError);
                // Remove results after first error
                results = results.Take(errorIndex + 1).ToList();
            }
        }
        
        stopwatch.Stop();
        
        return new BatchOperationsOutput
        {
            Total = results.Count,
            Successful = results.Count(r => r.Success),
            Failed = results.Count(r => !r.Success),
            Results = results,
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }
    
    private async Task<BatchOperationResult> ProcessBatchRequestAsync(
        int index,
        BatchRequest request,
        Dictionary<string, ITool> toolDict,
        ToolContext context,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!toolDict.TryGetValue(request.Tool, out var tool))
            {
                return new BatchOperationResult
                {
                    Index = index,
                    Success = false,
                    ErrorCode = "TOOL_NOT_FOUND",
                    ErrorMessage = $"Tool '{request.Tool}' not found",
                    DurationMs = stopwatch.ElapsedMilliseconds
                };
            }
            
            // Create new context for this operation (preserve user but new correlation ID)
            var operationContext = new ToolContext
            {
                UserId = context.UserId,
                Roles = context.Roles,
                CorrelationId = Guid.NewGuid().ToString()
            };
            
            var response = await tool.ExecuteAsync(request.Arguments, operationContext, cancellationToken);
            
            stopwatch.Stop();
            
            return new BatchOperationResult
            {
                Index = index,
                Success = response.Success,
                Output = response.Success ? JsonSerializer.SerializeToElement(response.Data) : null,
                ErrorCode = response.ErrorCode,
                ErrorMessage = response.ErrorMessage,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error processing batch request {Index}", index);
            
            return new BatchOperationResult
            {
                Index = index,
                Success = false,
                ErrorCode = "INTERNAL_ERROR",
                ErrorMessage = ex.Message,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }
        finally
        {
            semaphore.Release();
        }
    }
}

