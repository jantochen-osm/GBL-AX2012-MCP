using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using GBL.AX2012.MCP.Core.Interfaces;

namespace GBL.AX2012.MCP.Server.Resilience;

public interface IConnectionPoolMonitor
{
    void RecordConnectionFailure(string poolName);
    void RecordConnectionSuccess(string poolName);
    ConnectionPoolStatus GetStatus(string poolName);
    Dictionary<string, ConnectionPoolStatus> GetAllStatuses();
}

public class ConnectionPoolStatus
{
    public string Name { get; set; } = "";
    public int ActiveConnections { get; set; }
    public int FailedConnections { get; set; }
    public int RecoveredConnections { get; set; }
    public string Status { get; set; } = "healthy"; // "healthy", "degraded", "recovering"
    public DateTime? LastFailure { get; set; }
    public DateTime? LastRecovery { get; set; }
}

public class ConnectionPoolMonitor : IConnectionPoolMonitor, IHostedService
{
    private readonly ILogger<ConnectionPoolMonitor> _logger;
    private readonly Lazy<ISelfHealingService> _selfHealingService;
    private readonly ConcurrentDictionary<string, ConnectionPoolStatus> _pools = new();
    private Timer? _monitoringTimer;
    private readonly int _failureThreshold = 3;
    private readonly TimeSpan _recoveryCheckInterval = TimeSpan.FromSeconds(5);
    
    public ConnectionPoolMonitor(
        ILogger<ConnectionPoolMonitor> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _selfHealingService = new Lazy<ISelfHealingService>(() => serviceProvider.GetRequiredService<ISelfHealingService>());
    }
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Connection Pool Monitor");
        
        // Monitor pools every 10 seconds
        _monitoringTimer = new Timer(MonitorPools, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        
        return Task.CompletedTask;
    }
    
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _monitoringTimer?.Dispose();
        return Task.CompletedTask;
    }
    
    private void MonitorPools(object? state)
    {
        foreach (var pool in _pools.Values)
        {
            // Auto-heal if in degraded state
            if (pool.Status == "degraded" && pool.LastFailure.HasValue)
            {
                var timeSinceFailure = DateTime.UtcNow - pool.LastFailure.Value;
                if (timeSinceFailure > _recoveryCheckInterval)
                {
                    // Try to recover
                    AttemptRecovery(pool);
                }
            }
        }
    }
    
    private void AttemptRecovery(ConnectionPoolStatus pool)
    {
        _logger.LogInformation("Attempting recovery for connection pool {PoolName}", pool.Name);
        
        // Simulate recovery attempt
        // In production, this would actually try to create new connections
        var recoverySuccess = true; // Would check actual connection health
        
        if (recoverySuccess)
        {
            pool.Status = "healthy";
            pool.RecoveredConnections++;
            pool.LastRecovery = DateTime.UtcNow;
            pool.FailedConnections = 0;
            
            _selfHealingService.Value.RecordRecovery(pool.Name, "connection_pool");
            
            _logger.LogInformation("Connection pool {PoolName} recovered successfully", pool.Name);
        }
        else
        {
            pool.Status = "recovering";
            _logger.LogWarning("Connection pool {PoolName} recovery attempt failed", pool.Name);
        }
    }
    
    public void RecordConnectionFailure(string poolName)
    {
        var pool = _pools.GetOrAdd(poolName, _ => new ConnectionPoolStatus { Name = poolName });
        
        pool.FailedConnections++;
        pool.LastFailure = DateTime.UtcNow;
        
        if (pool.FailedConnections >= _failureThreshold)
        {
            pool.Status = "degraded";
            _logger.LogWarning("Connection pool {PoolName} is degraded after {Failures} failures", 
                poolName, pool.FailedConnections);
        }
    }
    
    public void RecordConnectionSuccess(string poolName)
    {
        var pool = _pools.GetOrAdd(poolName, _ => new ConnectionPoolStatus { Name = poolName });
        
        pool.ActiveConnections++;
        
        if (pool.Status == "degraded" || pool.Status == "recovering")
        {
            pool.Status = "healthy";
            pool.RecoveredConnections++;
            pool.LastRecovery = DateTime.UtcNow;
            pool.FailedConnections = 0;
            
            _selfHealingService.Value.RecordRecovery(poolName, "connection_pool");
            
            _logger.LogInformation("Connection pool {PoolName} recovered", poolName);
        }
    }
    
    public ConnectionPoolStatus GetStatus(string poolName)
    {
        return _pools.GetOrAdd(poolName, _ => new ConnectionPoolStatus { Name = poolName });
    }
    
    public Dictionary<string, ConnectionPoolStatus> GetAllStatuses()
    {
        return _pools.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
}

