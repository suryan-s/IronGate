# IronGate Benchmarks

This directory contains [k6](https://k6.io/) load testing scripts for benchmarking IronGate's performance, rate limiting, and chaos resilience.

## Prerequisites

- [Docker](https://docker.com) installed
- IronGate stack running via `docker compose up -d` (from repo root)
- Keycloak token for protected route tests

## Quick Start

### 1. Start IronGate Stack

```bash
# From repo root
docker compose up -d
```

Wait for all services to be healthy (Keycloak, Redis, Echo, OTEL stack).

### 2. Get Keycloak Token

```bash
# Using admin-cli (if available) or irongate-test client
TOKEN=$(curl -s -X POST "http://localhost:8080/realms/master/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "username=admin" \
  -d "password=admin" \
  -d "grant_type=password" \
  -d "client_id=irongate-test" \
  -d "client_secret=test-secret" | jq -r '.access_token')

export KEYCLOAK_TOKEN=$TOKEN
```

### 3. Run Throughput & Rate Limit Test

```bash
docker run -i --rm \
  --network=irongate-network \
  -e IRONGATE_URL=http://irongate:8080 \
  -e KEYCLOAK_TOKEN=$KEYCLOAK_TOKEN \
  grafana/k6 run - < benchmarks/k6-throughput-and-ratelimit.js
```

**Expected Results:**
- p95 latency < 100ms for both public and protected routes
- Rate limiting kicks in at configured limits (10 req/min public, 20 tokens protected)
- `x-correlation-id` header present on all responses
- `X-RateLimit-*` headers present on 429 responses

### 4. Run Chaos Fallback Test

```bash
# Option A: Let k6 handle chaos simulation (requires downstream /chaos endpoint)
docker run -i --rm \
  --network=irongate-network \
  -e IRONGATE_URL=http://irongate:8080 \
  grafana/k6 run - < benchmarks/k6-chaos-fallback.js

# Option B: Manual chaos - run warmup, then manually kill echo container
# Terminal 1: Run warmup phase only
docker run -i --rm --network=irongate-network -e IRONGATE_URL=http://irongate:8080 \
  grafana/k6 run --scenario warmup - < benchmarks/k6-chaos-fallback.js

# Terminal 2: Kill echo container to simulate outage
docker stop iron-gate-echo-1

# Terminal 1: Run chaos phase (fallback should serve)
docker run -i --rm --network=irongate-network -e IRONGATE_URL=http://irongate:8080 \
  grafana/k6 run --scenario chaos - < benchmarks/k6-chaos-fallback.js

# Terminal 2: Restart echo container
docker start iron-gate-echo-1

# Terminal 1: Run recovery phase
docker run -i --rm --network=irongate-network -e IRONGATE_URL=http://irongate:8080 \
  grafana/k6 run --scenario recovery - < benchmarks/k6-chaos-fallback.js
```

## Viewing Results in Grafana

1. Open Grafana: http://localhost:3000 (admin/admin)
2. Go to **Dashboards** → **Browse** → **k6** (if you have k6 dashboard imported)
3. Or create a new dashboard with Prometheus data source querying:
   - `rate(public_requests_total[1m])` - Request rate
   - `histogram_quantile(0.95, rate(public_latency_bucket[5m]))` - p95 latency
   - `rate(rate_limited_total[1m])` - Rate limit hit rate
   - `fallback_served` - Fallback activation rate

## Custom Metrics Exported

Both scripts export custom k6 metrics that can be sent to Prometheus via [k6 Prometheus output](https://grafana.com/docs/k6/latest/results-output/real-time/prometheus/):

```bash
# Run with Prometheus remote write
docker run -i --rm \
  --network=irongate-network \
  -e IRONGATE_URL=http://irongate:8080 \
  -e KEYCLOAK_TOKEN=$KEYCLOAK_TOKEN \
  grafana/k6 run \
    --out prometheus=http://prometheus:9090/api/v1/write \
    - < benchmarks/k6-throughput-and-ratelimit.js
```

### Key Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `public_requests` | Rate | Successful public route requests/sec |
| `protected_requests` | Rate | Successful protected route requests/sec |
| `rate_limited` | Rate | 429 responses/sec |
| `public_latency` / `protected_latency` | Trend | Response latency ms |
| `total_requests` | Counter | Total requests made |
| `errors` | Counter | 5xx errors |
| `fallback_served` | Rate | Fallback responses served (chaos test) |
| `fallback_from_cache` / `fallback_from_static` | Rate | Fallback source breakdown |
| `fallback_latency` | Trend | Fallback response latency |

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `IRONGATE_URL` | `http://localhost:8080` | IronGate base URL |
| `KEYCLOAK_TOKEN` | (empty) | Bearer token for protected routes |

## Interpreting Results

### Successful Rate Limiting
```
public_requests: 10/min (then 429s)
rate_limited: 1.0 (after limit exceeded)
X-RateLimit-Limit: 10
X-RateLimit-Remaining: 0 → decrements correctly
```

### Successful Hedging (requires slow downstream)
```
protected_latency p95: < 100ms (not 1000ms+)
X-IronGate-Hedged: true
X-IronGate-Attempts: 2
```

### Successful Fallback (Chaos)
```
fallback_served: 1.0 (100% of chaos requests served via fallback)
upstream_errors: 0 (no 5xx leaked to client)
X-IronGate-Fallback: true
X-IronGate-Fallback-Source: cache | static-config
```

## Troubleshooting

| Issue | Solution |
|-------|----------|
| `connection refused` | Ensure `docker compose up -d` completed and services are healthy |
| `401 Unauthorized` on protected | Verify `KEYCLOAK_TOKEN` is valid and not expired |
| `429` immediately | Rate limit window may not have reset; wait 60s or adjust config |
| No fallback headers | Ensure FallbackCache is enabled and TTL not expired |

## Extending Tests

To add custom scenarios:
1. Copy the script
2. Modify `options.scenarios` for new load patterns
3. Add custom `exec` functions
4. Register new metrics with `Rate`, `Trend`, `Counter`

## CI Integration

Run in GitHub Actions:
```yaml
- name: Run k6 Load Tests
  run: |
    docker compose up -d
    sleep 30
    TOKEN=$(curl -s -X POST "http://localhost:8080/realms/master/protocol/openid-connect/token" \
      -d "username=admin&password=admin&grant_type=password&client_id=irongate-test&client_secret=test-secret" | jq -r .access_token)
    docker run -i --rm --network=irongate-network \
      -e IRONGATE_URL=http://irongate:8080 \
      -e KEYCLOAK_TOKEN=$TOKEN \
      grafana/k6 run - < benchmarks/k6-throughput-and-ratelimit.js
```