using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Serilog;
using GBL.AX2012.MCP.Core.Interfaces;
using GBL.AX2012.MCP.Core.Options;
using GBL.AX2012.MCP.Server;
using GBL.AX2012.MCP.Server.Middleware;
using GBL.AX2012.MCP.Server.Security;
using GBL.AX2012.MCP.Server.Tools;
using GBL.AX2012.MCP.Server.Metrics;
using GBL.AX2012.MCP.Server.Transport;
using GBL.AX2012.MCP.Server.Approval;
using GBL.AX2012.MCP.Server.Notifications;
using GBL.AX2012.MCP.Server.Monitoring;
using GBL.AX2012.MCP.AxConnector.Clients;
using GBL.AX2012.MCP.AxConnector.Interfaces;
using GBL.AX2012.MCP.Audit.Services;
using Microsoft.EntityFrameworkCore;
using GBL.AX2012.MCP.Server.Configuration;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("MachineName", Environment.MachineName)
    .WriteTo.File(
        path: "logs/mcp-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateLogger();

try
{
    // Pin content root to the executable directory so appsettings.json and
    // Serilog logs resolve regardless of the working directory
    // (e.g. `dotnet run` invoked from the repository root).
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);

    Log.Information("Starting GBL-AX2012-MCP Server");
    
    var builder = Host.CreateApplicationBuilder(args);
    
    // Don't stop host when background services fail (e.g. stdio EOF)
    builder.Services.Configure<HostOptions>(options =>
    {
        options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
    });
    
    // Add Serilog
    builder.Services.AddSerilog();
    
    // Configure Options
    builder.Services.Configure<McpServerOptions>(builder.Configuration.GetSection(McpServerOptions.SectionName));
    builder.Services.Configure<AifClientOptions>(builder.Configuration.GetSection(AifClientOptions.SectionName));
    builder.Services.Configure<WcfClientOptions>(builder.Configuration.GetSection(WcfClientOptions.SectionName));
    builder.Services.Configure<BusinessConnectorOptions>(builder.Configuration.GetSection(BusinessConnectorOptions.SectionName));
    builder.Services.Configure<RateLimiterOptions>(builder.Configuration.GetSection(RateLimiterOptions.SectionName));
    builder.Services.Configure<CircuitBreakerOptions>(builder.Configuration.GetSection(CircuitBreakerOptions.SectionName));
    builder.Services.Configure<AuditOptions>(builder.Configuration.GetSection(AuditOptions.SectionName));
    builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
    builder.Services.Configure<HttpTransportOptions>(builder.Configuration.GetSection(HttpTransportOptions.SectionName));
    
    // Register Middleware
    builder.Services.AddSingleton<IRateLimiter, RateLimiter>();
    builder.Services.AddSingleton<ICircuitBreaker, CircuitBreaker>();
    builder.Services.AddSingleton<IIdempotencyStore, MemoryIdempotencyStore>();
    
    // Register Security
    builder.Services.AddSingleton<IAuthenticationService, WindowsAuthenticationService>();
    builder.Services.AddSingleton<IAuthorizationService, AuthorizationService>();
    
    // Register Audit
    builder.Services.AddSingleton<IAuditService, FileAuditService>();
    
    // Register Approval
    builder.Services.AddSingleton<IApprovalService, ApprovalService>();
    
    // Register Notifications
    builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection("Notifications"));
    builder.Services.AddHttpClient("Notifications");
    builder.Services.AddSingleton<INotificationService, NullNotificationService>(); // Use NotificationService when webhooks configured
    
    // Register Event Bus
    builder.Services.AddSingleton<GBL.AX2012.MCP.Server.Events.IEventBus, GBL.AX2012.MCP.Server.Events.EventBus>();
    
    // Register Webhook Service (Database-backed)
    builder.Services.Configure<GBL.AX2012.MCP.Server.Webhooks.WebhookServiceOptions>(
        builder.Configuration.GetSection(GBL.AX2012.MCP.Server.Webhooks.WebhookServiceOptions.SectionName));
    
    // Register Webhook DbContext
    builder.Services.AddDbContextFactory<GBL.AX2012.MCP.Audit.Data.WebhookDbContext>(options =>
        options.UseSqlServer(builder.Configuration.GetConnectionString("AuditDb")));
    
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<GBL.AX2012.MCP.Server.Webhooks.IWebhookService, GBL.AX2012.MCP.Server.Webhooks.DatabaseWebhookService>();
    
    // Register Kill Switch
    builder.Services.AddSingleton<IKillSwitchService, KillSwitchService>();
    builder.Services.AddSingleton<KillSwitchInputValidator>();
    
    // Register AX Connectors
    // Register HTTP AIF Client
    builder.Services.AddHttpClient<AifClient>()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            UseDefaultCredentials = true,
            PreAuthenticate = true
        });
    
    // Register NetTcp AIF Client
    builder.Services.AddSingleton<AifNetTcpClient>();
    
    // Register AIF Client Adapter (with automatic fallback)
    builder.Services.AddSingleton<IAifClient, AifClientAdapter>();
    builder.Services.AddSingleton<IWcfClient, WcfClient>();
    
    // Register Business Connector (wrapper or direct)
    var bcOptions = builder.Configuration.GetSection(BusinessConnectorOptions.SectionName).Get<BusinessConnectorOptions>() 
        ?? new BusinessConnectorOptions();
    
    if (bcOptions.UseWrapper && !string.IsNullOrEmpty(bcOptions.WrapperUrl))
    {
        // Use HTTP wrapper service (.NET Framework BC.Wrapper)
        builder.Services.AddHttpClient<IBusinessConnector, BusinessConnectorWrapperClient>();
        Log.Information("Using BC.Wrapper service at {Url}", bcOptions.WrapperUrl);
    }
    else
    {
        // Use direct BC.NET (requires .NET Framework, will fail on .NET 8)
        builder.Services.AddSingleton<IBusinessConnector, BusinessConnectorClient>();
        Log.Warning("Using direct BC.NET - may not work on .NET 8. Consider using BC.Wrapper service.");
    }
    
    // Register Validators
    builder.Services.AddSingleton<GetCustomerInputValidator>();
    builder.Services.AddSingleton<GetSalesOrderInputValidator>();
    builder.Services.AddSingleton<CheckInventoryInputValidator>();
    builder.Services.AddSingleton<SimulatePriceInputValidator>();
    builder.Services.AddSingleton<CreateSalesOrderInputValidator>();
    builder.Services.AddSingleton<ReserveSalesLineInputValidator>();
    builder.Services.AddSingleton<PostShipmentInputValidator>();
    builder.Services.AddSingleton<CreateInvoiceInputValidator>();
    builder.Services.AddSingleton<GetCustomerAgingInputValidator>();
    builder.Services.AddSingleton<PostPaymentInputValidator>();
    builder.Services.AddSingleton<SettleInvoiceInputValidator>();
    builder.Services.AddSingleton<CloseSalesOrderInputValidator>();
    builder.Services.AddSingleton<RequestApprovalInputValidator>();
    builder.Services.AddSingleton<GetApprovalStatusInputValidator>();
    builder.Services.AddSingleton<GetItemInputValidator>();
    builder.Services.AddSingleton<UpdateSalesOrderInputValidator>();
    builder.Services.AddSingleton<GetInvoiceInputValidator>();
    builder.Services.AddSingleton<AddNoteInputValidator>();
    builder.Services.AddSingleton<CheckCreditInputValidator>();
    builder.Services.AddSingleton<QueryAuditInputValidator>();
    builder.Services.AddSingleton<CheckAvailabilityForecastInputValidator>();
    builder.Services.AddSingleton<UpdateDeliveryDateInputValidator>();
    builder.Services.AddSingleton<AddSalesLineInputValidator>();
    builder.Services.AddSingleton<ReleaseForPickingInputValidator>();
    builder.Services.AddSingleton<SendOrderConfirmationInputValidator>();
    builder.Services.AddSingleton<GetReservationQueueInputValidator>();
    builder.Services.AddSingleton<SplitOrderByCreditInputValidator>();
    builder.Services.AddSingleton<BatchOperationsInputValidator>();
    builder.Services.AddSingleton<SubscribeWebhookInputValidator>();
    builder.Services.AddSingleton<UnsubscribeWebhookInputValidator>();
    builder.Services.AddSingleton<GetRoiMetricsInputValidator>();
    builder.Services.AddSingleton<BulkImportInputValidator>();
    
    // Register Tools - Phase 1: Order Capture
    builder.Services.AddSingleton<ITool, HealthCheckTool>();
    builder.Services.AddSingleton<ITool, GetCustomerTool>();
    builder.Services.AddSingleton<ITool, GetSalesOrderTool>();
    builder.Services.AddSingleton<ITool, CheckInventoryTool>();
    builder.Services.AddSingleton<ITool, SimulatePriceTool>();
    builder.Services.AddSingleton<ITool, CreateSalesOrderTool>();
    
    // Register Tools - Phase 2: Fulfillment
    builder.Services.AddSingleton<ITool, ReserveSalesLineTool>();
    builder.Services.AddSingleton<ITool, PostShipmentTool>();
    
    // Register Tools - Phase 3: Invoice & Dunning
    builder.Services.AddSingleton<ITool, CreateInvoiceTool>();
    builder.Services.AddSingleton<ITool, GetCustomerAgingTool>();
    
    // Register Tools - Phase 4: Payment & Close
    builder.Services.AddSingleton<ITool, PostPaymentTool>();
    builder.Services.AddSingleton<ITool, SettleInvoiceTool>();
    builder.Services.AddSingleton<ITool, CloseSalesOrderTool>();
    
    // Register Tools - Approval Workflow
    builder.Services.AddSingleton<ITool, RequestApprovalTool>();
    builder.Services.AddSingleton<ITool, GetApprovalStatusTool>();
    
    // Register Tools - Master Data & Utilities
    builder.Services.AddSingleton<ITool, GetItemTool>();
    builder.Services.AddSingleton<ITool, UpdateSalesOrderTool>();
    builder.Services.AddSingleton<ITool, GetInvoiceTool>();
    builder.Services.AddSingleton<ITool, AddNoteTool>();
    builder.Services.AddSingleton<ITool, CheckCreditTool>();
    builder.Services.AddSingleton<ITool, QueryAuditTool>();
    
    // Register Tools - Admin
    builder.Services.AddSingleton<ITool, KillSwitchTool>();
    
    // Register Tools - Extended P1 Features
    builder.Services.AddSingleton<ITool, CheckAvailabilityForecastTool>();
    builder.Services.AddSingleton<ITool, UpdateDeliveryDateTool>();
    builder.Services.AddSingleton<ITool, AddSalesLineTool>();
    builder.Services.AddSingleton<ITool, ReleaseForPickingTool>();
    
    // Register Tools - P2 Features
    builder.Services.AddSingleton<ITool, SendOrderConfirmationTool>();
    builder.Services.AddSingleton<ITool, GetReservationQueueTool>();
    builder.Services.AddSingleton<ITool, SplitOrderByCreditTool>();
    
    // Register Tools - Epic 7: Batch Operations & Webhooks
    builder.Services.AddSingleton<ITool, BatchOperationsTool>();
    builder.Services.AddSingleton<ITool, SubscribeWebhookTool>();
    builder.Services.AddSingleton<ITool, ListWebhooksTool>();
    builder.Services.AddSingleton<ITool, UnsubscribeWebhookTool>();
    builder.Services.AddSingleton<ITool, GetRoiMetricsTool>();
    builder.Services.AddSingleton<ITool, BulkImportTool>();
    
    // Register Tools - Epic 8: Self-Healing
    builder.Services.AddSingleton<ITool, GetSelfHealingStatusTool>();
    
    // Register Health Monitor
    builder.Services.Configure<HealthMonitorOptions>(builder.Configuration.GetSection(HealthMonitorOptions.SectionName));
    builder.Services.AddSingleton<HealthMonitorService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthMonitorService>());
    
    // Register Connection Pool Monitor
    builder.Services.AddSingleton<GBL.AX2012.MCP.Server.Resilience.IConnectionPoolMonitor, GBL.AX2012.MCP.Server.Resilience.ConnectionPoolMonitor>();
    builder.Services.AddHostedService<GBL.AX2012.MCP.Server.Resilience.ConnectionPoolMonitor>(sp => 
        (GBL.AX2012.MCP.Server.Resilience.ConnectionPoolMonitor)sp.GetRequiredService<GBL.AX2012.MCP.Server.Resilience.IConnectionPoolMonitor>());
    
    // Register Self-Healing Service
    builder.Services.AddSingleton<GBL.AX2012.MCP.Server.Resilience.ISelfHealingService, GBL.AX2012.MCP.Server.Resilience.SelfHealingService>();
    builder.Services.AddHostedService<GBL.AX2012.MCP.Server.Resilience.SelfHealingService>(sp => 
        (GBL.AX2012.MCP.Server.Resilience.SelfHealingService)sp.GetRequiredService<GBL.AX2012.MCP.Server.Resilience.ISelfHealingService>());
    
    // Register Configuration Validator
    builder.Services.AddSingleton<ConfigurationValidator>();
    
    // Register MCP Server (stdio)
    builder.Services.AddHostedService<McpServer>();
    
    // Register HTTP Transport (port 8080)
    builder.Services.AddHostedService<HttpTransport>();
    
    // Register Metrics Server (port 9090)
    builder.Services.AddHostedService<MetricsServer>();
    
    var host = builder.Build();
    
    // Validate configuration before starting
    try
    {
        var configValidator = host.Services.GetRequiredService<GBL.AX2012.MCP.Server.Configuration.ConfigurationValidator>();
        await configValidator.ValidateAsync();
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Configuration validation failed. Application will not start.");
        Environment.ExitCode = 1;
        return;
    }
    
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}
