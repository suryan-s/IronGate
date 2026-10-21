using IronGate.Domain;
using IronGate.Infrastructure.Resilience;
using Yarp.ReverseProxy.Forwarder;

namespace IronGate.Extensions;

public static class ResilienceExtensions
{
    public static IServiceCollection AddIronGateResilience(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ResilienceOptions>(
            configuration.GetSection(ResilienceOptions.SectionName));

        // Replace YARP's default forwarder factory with our Polly v8 Hedging + CircuitBreaker factory
        services.AddSingleton<IForwarderHttpClientFactory, ResilienceForwarderHttpClientFactory>();

        return services;
    }
}