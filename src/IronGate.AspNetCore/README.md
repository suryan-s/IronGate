# IronGate.AspNetCore

[![NuGet](https://img.shields.io/nuget/v/IronGate.AspNetCore.svg)](https://www.nuget.org/packages/IronGate.AspNetCore/)
[![License](https://img.shields.io/github/license/suryan-s/IronGate.svg)](https://github.com/suryan-s/IronGate/blob/main/LICENSE)

Companion library for downstream ASP.NET Core services that sit behind the **IronGate** API Gateway.

## Features

- **Zero-trust verification**: Validates `X-IronGate-Gateway-Secret` HMAC using constant-time comparison
- **Automatic ClaimsPrincipal construction**: Reads `X-IronGate-User-Id`, `X-IronGate-Username`, `X-IronGate-Roles` headers
- **Correlation ID propagation**: Extracts `X-Correlation-Id` for distributed tracing
- **Flexible configuration**: Optional strict mode, custom header names, role separators

## Installation

```bash
dotnet add package IronGate.AspNetCore --source https://nuget.pkg.github.com/suryan-s/index.json
```

Or from GitHub Packages:
```bash
dotnet add package IronGate.AspNetCore --source https://nuget.pkg.github.com/suryan-s/index.json
```

## Quick Start

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Register IronGate downstream auth
builder.Services.AddIronGateDownstreamAuth(options =>
{
    options.RequireHeaders = true;           // Fail if X-IronGate-User-Id missing
    options.SharedSecret = "your-hmac-secret"; // Zero-trust verification (optional)
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseIronGateCorrelationId(); // Optional: extracts X-Correlation-Id

app.MapGet("/api/data", [Authorize] () => Results.Ok(new { data = "secret" }));
app.Run();
```

## Configuration

```csharp
builder.Services.AddIronGateDownstreamAuth(options =>
{
    options.UserIdHeader = "X-IronGate-User-Id";        // Default: X-IronGate-User-Id
    options.UsernameHeader = "X-IronGate-Username";     // Default: X-IronGate-Username
    options.RolesHeader = "X-IronGate-Roles";           // Default: X-IronGate-Roles
    options.CorrelationIdHeader = "X-Correlation-Id";   // Default: X-Correlation-Id
    options.GatewaySecretHeader = "X-IronGate-Gateway-Secret"; // Default
    
    // Zero-trust: validate HMAC from gateway (recommended)
    options.SharedSecret = "your-shared-secret";         // Default: null (disabled)
    
    options.RequireHeaders = true;  // Fail auth if headers missing
    options.RolesSeparator = ',';   // Comma-separated roles
};
```

## How It Works

| IronGate Header | Maps To |
|-----------------|---------|
| `X-IronGate-User-Id` | `ClaimTypes.NameIdentifier` |
| `X-IronGate-Username` | `ClaimTypes.Name` |
| `X-IronGate-Roles` | `ClaimTypes.Role` (comma-separated) |
| `X-Correlation-Id` | `correlation_id` claim + `HttpContext.Items["CorrelationId"]` |
| `X-IronGate-Gateway-Secret` | Validated via `FixedTimeEquals` if `SharedSecret` set |

## Zero-Trust Verification

When `SharedSecret` is configured, the handler validates `X-IronGate-Gateway-Secret` using `CryptographicOperations.FixedTimeEquals` to prevent timing attacks. This ensures only requests that passed through IronGate can access your downstream service.

## Requirements

- .NET 10.0+
- ASP.NET Core 10.0+

## License

MIT License - see [LICENSE](https://github.com/suryan-s/IronGate/blob/main/LICENSE)