using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace IronGate.Extensions;

public static class OtelExtensions
{
    public static IServiceCollection AddIronGateOtel(this IServiceCollection services, IConfiguration config)
    {
        string serviceName = config["ServiceName"] ?? "IronGate";
        string otelEndpoint = config["Otel:Endpoint"] ?? "http://localhost:4317";
        string environment = config["Environment"] ?? "Development";

        services.Configure<OtelOptions>(config.GetSection("Otel"));

        // Resource with service info
        ResourceBuilder resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName: serviceName, serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0")
            .AddAttributes(new[]
            {
                new KeyValuePair<string, object>("deployment.environment", environment),
                new KeyValuePair<string, object>("service.instance.id", Environment.MachineName)
            });

        // OpenTelemetry setup
        services.AddOpenTelemetry()
            .ConfigureResource(r => r = resourceBuilder)
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Microsoft.AspNetCore.RateLimiting")
                    .AddMeter("Polly")
                    .AddPrometheusExporter(); // /metrics endpoint
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(serviceName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.Filter = context => !IsHealthCheck(context.Request.Path);
                    })
                    .AddHttpClientInstrumentation(options =>
                    {
                        options.RecordException = true;
                    })
                    .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint));
            });

        // OpenTelemetry Logging
        services.AddLogging(logging =>
        {
            logging.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(resourceBuilder);
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint));
            });
        });

        return services;
    }

    private static bool IsHealthCheck(PathString path)
    {
        return path.Value?.StartsWith("/health") == true || path.Value?.StartsWith("/metrics") == true;
    }
}

public class OtelOptions
{
    public string Endpoint { get; set; } = "http://localhost:4317";
    public bool Enabled { get; set; } = true;
}