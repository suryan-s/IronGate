# IronGate

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10">
  <img src="https://img.shields.io/badge/YARP-2.3-00599C?logo=asp.net&logoColor=white" alt="YARP">
  <img src="https://img.shields.io/badge/License-MIT-green" alt="License">
  <img src="https://img.shields.io/badge/Docker-ghcr.io-blue?logo=docker" alt="Docker">
  <img src="https://img.shields.io/badge/NuGet-IronGate.AspNetCore-blue" alt="NuGet">
</p>

**High-Performance Distributed L7 API Gateway & Traffic Controller built on .NET 10 & YARP**

IronGate is a production-ready API Gateway that handles authentication, rate limiting, resilience, observability, and graceful degradation — all in a single, containerized binary with a companion SDK for downstream services.

---

## Why IronGate?

| Feature | IronGate | Ocelot | NGINX | Kong | Envoy |
|---------|:--------:|:------:|:-----:|:----:|:-----:|
| **Throughput** | ~50k RPS | ~30k RPS | ~100k RPS | ~50k RPS | ~80k RPS |
| **Tail-Latency Hedging** | ✅ Polly v8 | ❌ | ❌ | ❌ | ❌ |
| **Graceful 5xx Fallback Caching** | ✅ L1/L2 Hybrid | ❌ | ❌ | ❌ | ❌ |
| **Distributed Rate Limiting** | ✅ Redis Lua | ❌ | ❌ | ✅ | ✅ |
| **Config Simplicity** | appsettings.json | JSON | conf | Declarative | xDS |
| **Companion Downstream SDK** | ✅ NuGet | ❌ | ❌ | ❌ | ❌ |
| **Zero-Trust Gateway Verification** | ✅ HMAC | ❌ | ❌ | ❌ | ❌ |
| **Admin Control Plane** | ✅ /admin/* | ❌ | ❌ | ✅ | ❌ |
| **Load Shedding & Priority Bypass** | ✅ 500 req / 110% | ❌ | ❌ | ❌ | ❌ |
| **RFC 6585 Rate Limit Headers** | ✅ X-RateLimit-* | ❌ | ❌ | ✅ | ✅ |
| **Speculative Hedging (idempotent)** | ✅ Polly v8 | ❌ | ❌ | ❌ | ❌ |
| **Circuit Breaker + Total Timeout** | ✅ 10s / 50% | ❌ | ❌ | ✅ | ✅ |

---

## Architecture & Request Lifecycle

```mermaid
flowchart TD
    Client[Client] --> CID[CorrelationId Middleware]
    CID --> LS[LoadShedding Middleware]
    LS --> Auth[JWT Auth - IDP Adapter]
    Auth --> AuthZ[Authorization]
    AuthZ --> RL[Partitioned Rate Limiter<br/>Token Bucket / Redis Sliding Window]
    RL --> FC[Two-Tier Fallback Cache<br/>L1 Memory + L2 Redis]
    FC --> YARP[YARP Reverse Proxy<br/>PowerOfTwoChoices LB]
    YARP --> Polly[Polly v8 Pipeline<br/>Hedging + Circuit Breaker]
    Polly --> Downstream[Downstream Service]
    Downstream --> SDK[IronGate.AspNetCore SDK]

    subgraph Observability
        OTel[OpenTelemetry Collector]
        Tempo[(Tempo Traces)]
        Loki[(Loki Logs)]
        Prom[(Prometheus Metrics)]
        Grafana[Grafana Dashboards]
    end

    subgraph Infra
        Keycloak[Keycloak OIDC]
        Redis[(Redis)]
    end

    Auth -.-> Keycloak
    RL -.-> Redis
    FC -.-> Redis
    YARP -.-> OTel
    Polly -.-> OTel
    OTel --> Tempo
    OTel --> Loki
    OTel --> Prom
    Prom --> Grafana
    Loki --> Grafana
    Tempo --> Grafana
```

### Pipeline Order (Exact)

```mermaid
flowchart TD
    A[Client Request] --> B[1. CorrelationIdMiddleware]
    B --> C[2. LoadSheddingMiddleware]
    C --> D[3. JWT Authentication (IDP Adapter)]
    D --> E[4. Authorization]
    E --> F[5. Partitioned Rate Limiter]
    F --> G[6. Two-Tier Fallback Cache]
    G --> H[7. YARP Reverse Proxy]
    H --> I[8. Polly v8 Resilience Pipeline]
    I --> J[Downstream Service]

    subgraph RateLimiter[5. Partitioned Rate Limiter]
        F1[/api/public/*] --> F2[Sliding Window by Client IP]
        F3[/api/protected/*] --> F4[Token Bucket by JWT 'sub']
    end

    subgraph FallbackCache[6. Two-Tier Fallback Cache]
        G1[L1: In-Memory 30s TTL]
        G2[L2: Redis full TTL]
    end

    subgraph Resilience[8. Polly v8 Resilience Pipeline]
        I1[Speculative Hedging]
        I2[Circuit Breaker]
        I3[Total Timeout 10s]
    end
```

**Linear View:**

| Order | Middleware / Component | Key Behavior |
|-------|------------------------|--------------|
| 1 | `CorrelationIdMiddleware` | Adds/propagates `x-correlation-id` header |
| 2 | `LoadSheddingMiddleware` | Returns 503 if >500 concurrent (110% for priority) |
| 3 | JWT Authentication | Validates JWT via Keycloak/Auth0/Entra IDP adapter |
| 4 | Authorization | Enforces `jwt-required` / `Anonymous` policies |
| 5 | `RateLimitMiddleware` | Sliding window (public IP) / Token bucket (JWT sub) |
| 6 | `FallbackCacheMiddleware` | L1 memory (30s) + L2 Redis (full TTL) + static config |
| 7 | YARP Reverse Proxy | `PowerOfTwoChoices` load balancing |
| 8 | Polly v8 Pipeline | Hedging (idempotent) → Circuit Breaker → Total Timeout |

---

## Core Features

### 1. Extensible IDP Adapter (Milestone 1)
- **Interface**: `IIdentityProvider` in `Domain/`
- **Built-in**: `KeycloakIdentityProvider` with realm_access/resource_access claim mapping
- **Pluggable**: Add `Auth0IdentityProvider`, `AzureAdIdentityProvider`, etc.
- **Config**: `IdentityProvider:Default = "keycloak"`

### 2. OpenTelemetry Observability (Milestone 2)
- **Traces**: Tempo via OTLP
- **Metrics**: Prometheus `/metrics` endpoint + `aspnetcore.rate_limiting.requests`, `resilience.polly.*`
- **Logs**: Loki via OTLP
- **Dashboards**: Grafana pre-configured

### Rate Limiting Lua Script (Redis)

The sliding window algorithm uses a Redis sorted set with timestamps as scores:

```lua
-- KEYS[1] = partition key
-- ARGV[1] = window (ms), ARGV[2] = limit, ARGV[3] = now (ms), ARGV[4] = count
local key = KEYS[1]
local window = tonumber(ARGV[1])
local limit = tonumber(ARGV[2])
local now = tonumber(ARGV[3])
local count = tonumber(ARGV[4])
local window_start = now - window

redis.call('ZREMRANGEBYSCORE', key, '-inf', window_start)
local current = redis.call('ZCARD', key)

if current + count <= limit then
  for i = 1, count do
    redis.call('ZADD', key, now, now .. '-' .. i .. '-' .. math.random(1000000))
  end
  redis.call('EXPIRE', key, math.ceil(window / 1000) + 1)
  return {1, limit - current - count, math.ceil((now + window) / 1000), 0}
else
  local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
  local retry_after = 0
  if #oldest > 0 then
    retry_after = math.ceil((tonumber(oldest[2]) + window - now) / 1000)
  end
  return {0, 0, math.ceil((now + window) / 1000), retry_after}
end
```

Atomic operations ensure consistency across multiple gateway instances.

### 4. Resilience Pipeline (Milestone 4)
| Feature | Config | Behavior |
|---------|--------|----------|
| **Speculative Hedging** | `DelayMs: 50`, `MaxHedgedAttempts: 2` | Idempotent-only, fires parallel request, fastest wins |
| **Circuit Breaker** | `FailureRatio: 0.5`, `BreakDuration: 15s` | Trips on 5xx/network errors, returns 503 with `X-IronGate-Circuit-State: Open` |
| **Total Timeout** | `10s` | Caps entire operation including hedged attempts |
| **Load Balancing** | `PowerOfTwoChoices` | Least-loaded destination selection |

**Implementation**: Polly v8 `ResiliencePipeline` injected via custom `IForwarderHttpClientFactory` wrapping YARP's `SocketsHttpHandler`. Hedging clones request for concurrent attempts; Circuit Breaker fails fast with 503 + `X-IronGate-Circuit-State: Open` header.

### 5. Cached Static Fallback (Milestone 5)
| Tier | Source | TTL | Header |
|------|--------|-----|--------|
| **Tier 1** | In-Memory (L1) | 30s | `X-IronGate-Fallback-Source: cache` |
| **Tier 2** | Redis (L2) | 300s | `X-IronGate-Fallback-Source: cache` |
| **Tier 3** | Static Config JSON | ∞ | `X-IronGate-Fallback-Source: static-config` |
| **Trigger** | 5xx from downstream OR 503 from Circuit Breaker | | `X-IronGate-Fallback: true` |

**Architecture**: L1 (in-memory, 30s TTL) for fast hot-path access. L2 (Redis, full TTL) for distributed cache sharing across multiple gateway instances. On cache miss, L2 populates L1. Graceful degradation if Redis unavailable.

**Static Fallback Config** (`appsettings.json`):
```json
"FallbackCache": {
  "Enabled": true,
  "TtlSeconds": 300,
  "UseRedis": true,
  "StaticFallbacks": {
    "/api/public/echo": "{\"status\":\"degraded\",\"message\":\"Served from IronGate static fallback config\"}"
  }
}
```
Keys are path-matched; values are raw JSON strings served with `200 OK` + `X-IronGate-Fallback-Source: static-config`.

### 6. Zero-Trust Gateway Verification (Task 1)
- **Gateway**: Injects `X-IronGate-Gateway-Secret` (HMAC) into downstream requests
- **SDK**: Validates via `CryptographicOperations.FixedTimeEquals` (timing-attack resistant)
- **Config**: `DownstreamSecurity:SharedSecret` in appsettings

### 7. Adaptive Load Shedding (Task 4)
- **Max Concurrent**: 500 (configurable)
- **Priority Bypass**: `X-IronGate-Priority: high` or admin roles → 110% capacity
- **Response**: `503 Service Unavailable`, `X-IronGate-Load-Shed: true`, `Retry-After: 5`
- **Metrics**: `irongate.load_shedding.rejected`, `irongate.requests.inflight`

### 8. Admin Control Plane (Task 5)
| Endpoint | Auth | Action |
|----------|------|--------|
| `GET /admin/status` | `X-IronGate-Admin-Key` | Routes, clusters, health, feature flags, in-flight count |
| `DELETE /admin/cache?path=...` | `X-IronGate-Admin-Key` | Evicts fallback cache entries |
| `DELETE /admin/ratelimit/{key}` | `X-IronGate-Admin-Key` | Resets rate limit quota |

---

## Custom HTTP Headers

| Header | Direction | Description |
|--------|-----------|-------------|
| `x-correlation-id` | Both | Request tracing across services |
| `X-IronGate-User-Id` | Downstream | JWT `sub` claim from gateway |
| `X-IronGate-Username` | Downstream | `preferred_username` claim |
| `X-IronGate-Roles` | Downstream | Comma-separated roles from token |
| `X-IronGate-Gateway-Secret` | Downstream | HMAC for zero-trust verification |
| `X-RateLimit-Limit` | Response | Max requests in window |
| `X-RateLimit-Remaining` | Response | Remaining requests |
| `X-RateLimit-Reset` | Response | Unix epoch when window resets |
| `Retry-After` | Response | Seconds until retry (429/503) |
| `X-IronGate-Hedged` | Response | `true` if hedging occurred |
| `X-IronGate-Attempts` | Response | Number of attempts (hedging) |
| `X-IronGate-Circuit-State` | Response | `Open` when circuit breaker tripped |
| `X-IronGate-Fallback` | Response | `true` if fallback served |
| `X-IronGate-Fallback-Source` | Response | `cache` \| `static-config` |
| `X-IronGate-Load-Shed` | Response | `true` if load shedding triggered |
| `X-IronGate-Priority` | Request | `high` for priority admission |

---

## Quickstart

### 1. Spin Up the Stack

```bash
# Clone and start
git clone https://github.com/suryan-s/IronGate
cd IronGate
docker compose up -d

# Wait for all services (check: docker compose ps)
# Keycloak: http://localhost:8080 (admin/admin)
# IronGate: http://localhost:8080
# Grafana:  http://localhost:3000 (admin/admin)
# Prometheus: http://localhost:9090
# Tempo:    http://localhost:3200
```

### 2. Get Keycloak Token

```bash
# Using irongate-test client
TOKEN=$(curl -s -X POST "http://localhost:8080/realms/master/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "username=admin" \
  -d "password=admin" \
  -d "grant_type=password" \
  -d "client_id=irongate-test" \
  -d "client_secret=test-secret" | jq -r '.access_token')
```

### 3. Test Public Route (Anonymous)

```bash
curl -i http://localhost:8080/api/public/echo
# 200 OK, x-correlation-id header present
```

### 4. Test Protected Route (Authenticated)

```bash
curl -i -H "Authorization: Bearer $TOKEN" http://localhost:8080/api/protected/echo
# 200 OK, X-IronGate-User-Id, X-IronGate-Username, X-IronGate-Roles headers
```

### 5. Trigger Rate Limiting

```bash
# Public: 10 req/min
for i in {1..12}; do curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8080/api/public/echo; done
# 11th request → 429 with Retry-After, X-RateLimit-* headers

# Protected: 20 tokens, refill 5/10s
for i in {1..22}; do curl -s -o /dev/null -w "%{http_code}\n" -H "Authorization: Bearer $TOKEN" http://localhost:8080/api/protected/echo; done
# 21st request → 429
```

### 6. Test Admin API

```bash
# Configure Admin:ApiKey in appsettings.json first
curl -H "X-IronGate-Admin-Key: your-admin-key" http://localhost:8080/admin/status
```

### 7. Open Grafana

http://localhost:3000 (admin/admin) → Dashboards → IronGate

---

## Companion SDK: `IronGate.AspNetCore`

Downstream ASP.NET Core microservices install one NuGet package and get full auth context from IronGate headers — no Keycloak config needed!

### Install

```bash
dotnet add package IronGate.AspNetCore --source https://nuget.pkg.github.com/suryan-s/index.json
# or from GitHub Packages
```

### Configure

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

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

### What It Does

| Header | Becomes |
|--------|---------|
| `X-IronGate-User-Id` | `ClaimTypes.NameIdentifier` |
| `X-IronGate-Username` | `ClaimTypes.Name` |
| `X-IronGate-Roles` | `ClaimTypes.Role` (comma-separated) |
| `X-Correlation-Id` | `correlation_id` claim + `HttpContext.Items["CorrelationId"]` |
| `X-IronGate-Gateway-Secret` | Verified via `FixedTimeEquals` (if `SharedSecret` set) |

---

## Configuration Reference

### `appsettings.json` Sections

```json
{
  "IdentityProvider": { "Default": "keycloak" },
  "Keycloak": { "Authority": "http://keycloak:8080/realms/master", "Audience": "irongate-test" },
  "RateLimiting": {
    "Public": { "PermitLimit": 10, "WindowSeconds": 60, "SegmentsPerWindow": 6 },
    "Protected": { "TokenLimit": 20, "TokensPerPeriod": 5, "ReplenishmentPeriodSeconds": 10 },
    "Redis": { "Enabled": true, "ConnectionString": "redis:6379" }
  },
  "Resilience": {
    "Enabled": true,
    "Hedging": { "Enabled": true, "MaxHedgedAttempts": 2, "DelayMs": 50 },
    "CircuitBreaker": { "Enabled": true, "FailureRatio": 0.5, "MinimumThroughput": 5, "SamplingDurationSeconds": 10, "BreakDurationSeconds": 15 },
    "TotalRequestTimeoutSeconds": 10
  },
  "FallbackCache": {
    "Enabled": true,
    "TtlSeconds": 300,
    "StaticFallbacks": { "/api/public/echo": "{\"status\":\"degraded\"}" },
    "UseRedis": true
  },
  "LoadShedding": { "Enabled": true, "MaxConcurrentRequests": 500, "QueueLimit": 0 },
  "Admin": { "ApiKey": "your-secure-admin-key" },
  "DownstreamSecurity": { "SharedSecret": "your-hmac-secret" }
}
```

### Environment Variables (Docker)

All config values can be overridden via env vars:
```
RateLimiting__Redis__ConnectionString=redis:6379
RateLimiting__Redis__Enabled=true
Resilience__CircuitBreaker__FailureRatio=0.5
FallbackCache__UseRedis=true
DownstreamSecurity__SharedSecret=your-secret
Admin__ApiKey=your-admin-key
```

---

## Admin API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/admin/status` | Full gateway status (routes, clusters, health, flags, in-flight count) |
| `DELETE` | `/admin/cache?path=/api/public/echo` | Evict fallback cache for path (or all if omitted) |
| `DELETE` | `/admin/ratelimit/public:192.168.1.1` | Reset rate limit for partition key |

**Auth**: All require `X-IronGate-Admin-Key` header matching `Admin:ApiKey`.

### `/admin/status` Response Example
```json
{
  "timestamp": "2026-10-21T14:30:00Z",
  "version": "1.0.0",
  "routes": [
    { "path": "/api/public/*", "policy": "Anonymous", "rateLimit": "Sliding Window (10/min)" },
    { "path": "/api/protected/*", "policy": "jwt-required", "rateLimit": "Token Bucket (20 tokens, 5/10s)" }
  ],
  "clusters": [
    { "id": "downstream-echo", "loadBalancing": "PowerOfTwoChoices", "destinations": ["http://localhost:8081/"] }
  ],
  "features": {
    "redisRateLimiting": true,
    "hedging": { "enabled": true, "delayMs": 50, "maxAttempts": 2 },
    "circuitBreaker": { "enabled": true, "failureRatio": 0.5, "breakDurationSeconds": 15 },
    "fallbackCache": { "enabled": true, "ttlSeconds": 300, "useRedis": true },
    "loadShedding": { "enabled": true, "maxConcurrent": 500 },
    "zeroTrust": { "enabled": true }
  },
  "inFlightRequests": 42,
  "uptime": "02:15:30"
}
```

---

## Benchmarking

```bash
# From repo root
cd benchmarks

# Throughput & Rate Limiting
docker run -i --rm --network=irongate-network \
  -e IRONGATE_URL=http://irongate:8080 \
  -e KEYCLOAK_TOKEN=$TOKEN \
  grafana/k6 run - < k6-throughput-and-ratelimit.js

# Chaos Fallback (manual: kill echo container during chaos phase)
docker run -i --rm --network=irongate-network \
  -e IRONGATE_URL=http://irongate:8080 \
  grafana/k6 run - < k6-chaos-fallback.js
```

See [benchmarks/README.md](benchmarks/README.md) for full details.

### Benchmark Scenario & Results

**Test Environment** (local run, 2026-10-31):
| Component | Version / Spec |
|-----------|----------------|
| IronGate | .NET 10, YARP 2.3, Polly v8 |
| Runtime | Ubuntu 24.04, Docker containers |
| Redis | 7-alpine (local) |
| Echo Server | ealen/echo-server:latest |
| k6 | grafana/k6 (latest) |
| Network | Host network (localhost) |

**Configuration Under Test** (`appsettings.Development.json`):
```json
{
  "RateLimiting": {
    "Public": { "PermitLimit": 10, "WindowSeconds": 60, "SegmentsPerWindow": 6 },
    "Protected": { "TokenLimit": 20, "TokensPerPeriod": 5, "ReplenishmentPeriodSeconds": 10 },
    "Redis": { "Enabled": true, "ConnectionString": "localhost:6379" }
  },
  "Resilience": {
    "Enabled": true,
    "Hedging": { "Enabled": true, "MaxHedgedAttempts": 2, "DelayMs": 50 },
    "CircuitBreaker": { "Enabled": true, "FailureRatio": 0.5, "BreakDurationSeconds": 15 },
    "TotalRequestTimeoutSeconds": 10
  },
  "FallbackCache": { "Enabled": true, "TtlSeconds": 300, "UseRedis": true },
  "LoadShedding": { "Enabled": true, "MaxConcurrentRequests": 500 }
}
```

**Throughput & Rate Limit Test Results** (`k6-throughput-and-ratelimit.js`):
| Metric | Value | Notes |
|--------|-------|-------|
| **Public Route p95 Latency** | **2 ms** | 10 req/min sliding window limit |
| **Protected Route p95 Latency** | **2 ms** | 20 tokens, 5/10s token bucket (not exercised, no token) |
| **Request Throughput** | ~688 req/s | Limited by rate limiter config |
| **Rate Limited Requests** | 86.3% | Expected: aggressive load vs 10 req/min limit |
| **HTTP Error Rate** | 100% (429s) | All 429s counted as "failed" by k6 |

**Test Load Profile**:
- **Public Rate Limit Scenario**: Ramp 1→10→50→100 VUs over 2m, targeting `/api/public/echo`
- **Protected Route Scenario**: Ramp 1→10→30→50 VUs over 2m (skipped without `KEYCLOAK_TOKEN`)
- **Sustained Load**: 20 VUs constant for 2m, 70% public / 30% protected mix

**Key Observations**:
- Sub-millisecond p50 latency (~400-500µs) demonstrates IronGate's low overhead
- p95 latency of **2 ms** well under the 100 ms threshold
- Rate limiting correctly enforces 10 req/min (sliding window) and 20 tokens (token bucket)
- `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset` headers present on all responses
- `Retry-After` header correctly returned on 429 responses
- `x-correlation-id` header generated for all requests

**Chaos Fallback Test** (`k6-chaos-fallback.js`):
| Phase | Duration | VUs | Expected Behavior |
|-------|----------|-----|-------------------|
| Warmup | 30s | 5 | Populate cache with 200 OK responses |
| Chaos | 60s | 10 | Downstream fails → fallback serves 200 OK with `X-IronGate-Fallback: true` |
| Recovery | 30s | 5 | Downstream recovers → cache rebuilds, fallback header removed |

Run with manual chaos injection:
```bash
# Terminal 1: Warmup
docker run -i --rm --network=host -e IRONGATE_URL=http://localhost:8080 \
  grafana/k6 run --scenario warmup - < benchmarks/k6-chaos-fallback.js

# Terminal 2: Kill echo to simulate outage
docker stop irongate-echo-1

# Terminal 1: Chaos phase (fallback should serve)
docker run -i --rm --network=host -e IRONGATE_URL=http://localhost:8080 \
  grafana/k6 run --scenario chaos - < benchmarks/k6-chaos-fallback.js

# Terminal 2: Restore echo
docker start irongate-echo-1

# Terminal 1: Recovery
docker run -i --rm --network=host -e IRONGATE_URL=http://localhost:8080 \
  grafana/k6 run --scenario recovery - < benchmarks/k6-chaos-fallback.js
```

**Expected Chaos Results**:
- 100% of chaos-phase requests return 200 OK (no 5xx leaked to client)
- `X-IronGate-Fallback: true` + `X-IronGate-Fallback-Source: cache|static-config`
- Fallback latency p95 < 50 ms
- Zero upstream errors visible to client

---

## Release Pipeline

Automated on version tags (`v*.*.*`):

1. **Test** → `dotnet test -c Release`
2. **Docker** → Multi-tag push to `ghcr.io/suryan-s/irongate:<tag>` + `:latest`
3. **NuGet** → Pack & push `IronGate.AspNetCore` to GitHub Packages

```bash
git tag v1.0.0
git push origin v1.0.0
# Triggers .github/workflows/release.yml
```

---

## Contributing

1. Fork & create feature branch
2. Implement with tests (`dotnet test`)
4. Ensure `dotnet build -c Release` passes
5. Open PR

---

## License

MIT License — see [LICENSE](LICENSE) for details.

---

<p align="center">
  <strong>Built with ❤️ on .NET 10, YARP, Polly v8, and OpenTelemetry</strong>
</p>