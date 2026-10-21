using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using IronGate.Domain;

namespace IronGate.Extensions;

public static class AuthExtensions
{
    public static IServiceCollection AddIronGateAuth(this IServiceCollection services, IConfiguration config)
    {
        // Register identity providers and configure auth using the extension methods
        return services
            .AddIdentityProviders(config)
            .AddIronGateAuthInternal(config);
    }
}