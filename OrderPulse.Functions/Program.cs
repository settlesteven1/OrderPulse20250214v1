using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.AI;
using OrderPulse.Infrastructure.AI.Parsers;
using OrderPulse.Infrastructure.Data;
using OrderPulse.Infrastructure.Services;
using OrderPulse.Functions;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        // ── Database ──
        services.AddSingleton<ITenantProvider, FunctionsTenantProvider>();
        services.AddSingleton<TenantSessionInterceptor>();
        services.AddDbContext<OrderPulseDbContext>((sp, options) =>
        {
            options.UseSqlServer(config.GetConnectionString("OrderPulseDb"));
            options.AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>());
        });

        // ── Service Bus ──
        services.AddSingleton(sp =>
        {
            var connectionString = config["ServiceBusConnection"]
                ?? throw new InvalidOperationException("ServiceBusConnection is not configured");
            return new ServiceBusClient(connectionString);
        });

        // ── Graph API ──
        services.AddSingleton<GraphMailService>();

        // ── Blob Storage ──
        services.AddSingleton<EmailBlobStorageService>();

        // ── AI Services ──
        services.AddSingleton<AzureOpenAIService>();
        services.AddSingleton<IEmailClassifier, EmailClassifierService>();
        services.AddSingleton<IEmailParser<OrderParserResult>, OrderParserService>();
        services.AddSingleton<IEmailParser<ShipmentParserResult>, ShipmentParserService>();
        services.AddSingleton<IEmailParser<DeliveryParserResult>, DeliveryParserService>();
        services.AddSingleton<IEmailParser<ReturnParserResult>, ReturnParserService>();
        services.AddSingleton<IEmailParser<RefundParserResult>, RefundParserService>();
        services.AddSingleton<IEmailParser<CancellationParserResult>, CancellationParserService>();
        services.AddSingleton<IEmailParser<PaymentParserResult>, PaymentParserService>();

        // ── Domain Services ──
        services.AddScoped<RetailerMatcher>();
        services.AddScoped<OrderStateMachine>();
        services.AddSingleton<ProcessingLogger>();
        services.AddScoped<IEmailProcessingOrchestrator, EmailProcessingOrchestrator>();
    })
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

host.Run();
