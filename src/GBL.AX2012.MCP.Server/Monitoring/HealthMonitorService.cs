using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GBL.AX2012.MCP.Core.Options;
using GBL.AX2012.MCP.Core.Interfaces;
using GBL.AX2012.MCP.AxConnector.Interfaces;
using GBL.AX2012.MCP.Server.Notifications;

namespace GBL.AX2012.MCP.Server.Monitoring;

public class HealthMonitorOptions
{
    public const string SectionName = "HealthMonitor";
    public int CheckIntervalSeconds { get; set; } = 30;
    public int ErrorRateThresholdPercent { get; set; } = 5;
    public int ErrorRateWindowMinutes { get; set; } = 5;
    public bool Enabled { get; set; } = true;
}

public class HealthMonitorService : BackgroundService
{
    private readonly ILogger<HealthMonitorService> _logger;
    private readonly HealthMonitorOptions _options;
    private readonly IBusinessConnector _businessConnector;
    private readonly INotificationService _notifications;
    private readonly ICircuitBreaker _circuitBreaker;
    
    private static readonly Prometheus.Gauge AosConnectivity = Prometheus.Metrics.CreateGauge(
        "mcp_aos_connectivity", "AOS connectivity status (1=connected, 0=disconnected)");
    private static readonly Prometheus.Gauge HealthCheckDuration = Prometheus.Metrics.CreateGauge(
        "mcp_health_check_duration_ms", "Duration of health check in milliseconds");
    private static readonly Prometheus.Counter HealthCheckFailures = Prometheus.Metrics.CreateCounter(
        "mcp_health_check_failures_total", "Total number of health check failures");
    
    private readonly List<(DateTime Timestamp, bool Success)> _recentCalls = new();
    private readonly object _lock = new();
    private bool _lastAosStatus = true;
    private int _consecutiveFailures = 0;
    
    public HealthMonitorService(
        ILogger<HealthMonitorService> logger,
        IOptions<HealthMonitorOptions> options,
        IBusinessConnector businessConnector,
        INotificationService notifications,
        ICircuitBreaker circuitBreaker)
    {
        _logger = logger;
        _options = options.Value;
        _businessConnector = businessConnector;
        _notifications = notifications;
        _circuitBreaker = circuitBreaker;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Health Monitor is disabled");
            return;
        }
        
        _logger.LogInformation("Health Monitor started with {Interval}s interval", _options.CheckIntervalSeconds);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformHealthCheckAsync(stoppingToken);
                await CheckErrorRateSpikeAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");
            }
            
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.CheckIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    private async Task PerformHealthCheckAsync(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            var isHealthy = await _businessConnector.TestConnectionAsync();
            sw.Stop();
            
            HealthCheckDuration.Set(sw.ElapsedMilliseconds);
            AosConnectivity.Set(isHealthy ? 1 : 0);
            
            if (isHealthy)
            {
                _consecutiveFailures = 0;
                
                // Recover from previous failure
                if (!_lastAosStatus)
                {
                    _logger.LogWarning("AOS connectivity restored after {Failures} failures", _consecutiveFailures);
                    await _notifications.SendAlertAsync(
                        "AOS Connectivity Restored",
                        $"Connection to AX AOS has been restored. Health check latency: {sw.ElapsedMilliseconds}ms",
                        NotificationSeverity.Info);
                }
                
                _lastAosStatus = true;
            }
            else
            {
                HandleHealthCheckFailure("AOS returned unhealthy status");
            }
            
            RecordCall(isHealthy);
        }
        catch (Exception ex)
        {
            sw.Stop();
            HealthCheckDuration.Set(sw.ElapsedMilliseconds);
            AosConnectivity.Set(0);
            HealthCheckFailures.Inc();
            
            HandleHealthCheckFailure($"Exception: {ex.Message}");
            RecordCall(false);
        }
    }
    
    private void HandleHealthCheckFailure(string reason)
    {
        _consecutiveFailures++;
        _logger.LogWarning("Health check failed ({Failures} consecutive): {Reason}", _consecutiveFailures, reason);
        
        // Alert on first failure or every 5th consecutive failure
        if (_lastAosStatus || _consecutiveFailures % 5 == 0)
        {
            _ = _notifications.SendAlertAsync(
                "AOS Connectivity Lost",
                $"Cannot connect to AX AOS. Consecutive failures: {_consecutiveFailures}. Reason: {reason}",
                _consecutiveFailures >= 3 ? NotificationSeverity.Critical : NotificationSeverity.Warning);
        }
        
        _lastAosStatus = false;
    }
    
    private async Task CheckErrorRateSpikeAsync()
    {
        lock (_lock)
        {
            // Clean old entries
            var cutoff = DateTime.UtcNow.AddMinutes(-_options.ErrorRateWindowMinutes);
            _recentCalls.RemoveAll(c => c.Timestamp < cutoff);
            
            if (_recentCalls.Count < 10) return; // Need minimum sample size
            
            var errorRate = (double)_recentCalls.Count(c => !c.Success) / _recentCalls.Count * 100;
            
            if (errorRate > _options.ErrorRateThresholdPercent)
            {
                _logger.LogWarning("Error rate spike detected: {ErrorRate:F1}% (threshold: {Threshold}%)", 
                    errorRate, _options.ErrorRateThresholdPercent);
                
                _ = _notifications.SendAlertAsync(
                    "Error Rate Spike Detected",
                    $"Error rate is {errorRate:F1}% over the last {_options.ErrorRateWindowMinutes} minutes. Threshold: {_options.ErrorRateThresholdPercent}%",
                    NotificationSeverity.Warning);
            }
        }
    }
    
    public void RecordCall(bool success)
    {
        lock (_lock)
        {
            _recentCalls.Add((DateTime.UtcNow, success));
        }
    }
    
    public HealthStatus GetCurrentStatus()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-_options.ErrorRateWindowMinutes);
            var recent = _recentCalls.Where(c => c.Timestamp >= cutoff).ToList();
            
            return new HealthStatus
            {
                AosConnected = _lastAosStatus,
                ConsecutiveFailures = _consecutiveFailures,
                RecentCallCount = recent.Count,
                RecentErrorCount = recent.Count(c => !c.Success),
                ErrorRatePercent = recent.Any() ? (double)recent.Count(c => !c.Success) / recent.Count * 100 : 0,
                CircuitBreakerState = _circuitBreaker.State.ToString()
            };
        }
    }
}

public class HealthStatus
{
    public bool AosConnected { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int RecentCallCount { get; set; }
    public int RecentErrorCount { get; set; }
    public double ErrorRatePercent { get; set; }
    public string CircuitBreakerState { get; set; } = "";
}
