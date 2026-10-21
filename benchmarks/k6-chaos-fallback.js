import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

// Configuration via environment variables
const BASE_URL = __ENV.IRONGATE_URL || 'http://localhost:8080';
const PUBLIC_ROUTE = `${BASE_URL}/api/public/echo`;
const CHAOS_ROUTE = `${BASE_URL}/api/public/chaos`; // Endpoint that returns 500

// Custom metrics
const fallbackServed = new Rate('fallback_served');
const fallbackFromCache = new Rate('fallback_from_cache');
const fallbackFromStatic = new Rate('fallback_from_static');
const upstreamErrors = new Rate('upstream_errors');
const fallbackLatency = new Trend('fallback_latency');

// Test configuration - short duration chaos test
export const options = {
  scenarios: {
    // Phase 1: Warm up - populate cache with 200 OK responses
    warmup: {
      executor: 'constant-vus',
      vus: 5,
      duration: '30s',
      exec: 'warmupCache',
      tags: { phase: 'warmup' },
    },
    // Phase 2: Chaos - downstream returns 500, verify fallback
    chaos: {
      executor: 'constant-vus',
      vus: 10,
      duration: '60s',
      exec: 'chaosTest',
      startTime: '35s', // Start after warmup
      tags: { phase: 'chaos' },
    },
    // Phase 3: Recovery - downstream recovers, verify cache rebuild
    recovery: {
      executor: 'constant-vus',
      vus: 5,
      duration: '30s',
      exec: 'recoveryTest',
      startTime: '100s', // Start after chaos
      tags: { phase: 'recovery' },
    },
  },
  thresholds: {
    // 100% of requests during chaos should be served by fallback (no 5xx to client)
    'fallback_served': ['rate>=1.0'],
    // Zero failed HTTP requests (all should return 200 via fallback)
    'upstream_errors': ['rate==0'],
    // Fallback latency should be low
    'fallback_latency': ['p(95)<50'],
  },
};

function getHeaders() {
  return {
    'Content-Type': 'application/json',
    'Accept': 'application/json',
  };
}

// Phase 1: Warm up the cache with successful responses
export function warmupCache() {
  const startTime = new Date();
  const res = http.get(PUBLIC_ROUTE, { headers: getHeaders() });
  const latency = new Date() - startTime;
  
  if (res.status === 200) {
    fallbackLatency.add(latency);
  } else {
    upstreamErrors.add(1);
  }
  
  check(res, {
    'warmup: status 200': (r) => r.status === 200,
    'warmup: has x-correlation-id': (r) => r.headers['x-correlation-id'] !== undefined,
  });
  
  sleep(0.2);
}

// Phase 2: Chaos - downstream returns 500 (simulated via /chaos endpoint or manual intervention)
// NOTE: This requires the downstream echo service to have a /chaos endpoint that returns 500
// OR you manually kill the echo container during this phase
export function chaosTest() {
  // Try the main route first - if downstream is down, fallback should kick in
  const startTime = new Date();
  const res = http.get(PUBLIC_ROUTE, { headers: getHeaders() });
  const latency = new Date() - startTime;
  
  const isFallback = res.headers['X-IronGate-Fallback'] === 'true';
  const fromCache = isFallback && res.headers['X-IronGate-Fallback-Source'] === 'cache';
  const fromStatic = isFallback && res.headers['X-IronGate-Fallback-Source'] === 'static-config';
  
  if (isFallback) {
    fallbackServed.add(1);
    fallbackLatency.add(latency);
    if (fromCache) fallbackFromCache.add(1);
    if (fromStatic) fallbackFromStatic.add(1);
  }
  
  if (res.status >= 500) {
    upstreamErrors.add(1);
  }
  
  check(res, {
    'chaos: NO 5xx returned to client': (r) => r.status < 500,
    'chaos: fallback served (200 OK)': (r) => r.status === 200,
    'chaos: X-IronGate-Fallback: true': (r) => r.headers['X-IronGate-Fallback'] === 'true',
    'chaos: has fallback source header': (r) => 
      r.headers['X-IronGate-Fallback'] !== 'true' || 
      (r.headers['X-IronGate-Fallback-Source'] === 'cache' || r.headers['X-IronGate-Fallback-Source'] === 'static-config'),
    'chaos: response body present': (r) => r.body && r.body.length > 0,
  });
  
  sleep(0.1);
}

// Phase 3: Recovery - downstream recovers, verify cache rebuild
export function recoveryTest() {
  const startTime = new Date();
  const res = http.get(PUBLIC_ROUTE, { headers: getHeaders() });
  const latency = new Date() - startTime;
  
  const isFallback = res.headers['X-IronGate-Fallback'] === 'true';
  
  check(res, {
    'recovery: status 200': (r) => r.status === 200,
    'recovery: eventually no fallback header': (r) => r.headers['X-IronGate-Fallback'] !== 'true',
    'recovery: latency reasonable': (r) => latency < 200,
  });
  
  sleep(0.2);
}

// Setup
export function setup() {
  console.log('Starting chaos fallback test...');
  console.log('Phase 1: Warmup (30s) - Populating cache with 200 OK responses');
  console.log('Phase 2: Chaos (60s) - Downstream fails, fallback should serve');
  console.log('Phase 3: Recovery (30s) - Downstream recovers, cache rebuilds');
  return { baseUrl: __ENV.IRONGATE_URL || 'http://localhost:8080' };
}

export function teardown(data) {
  console.log('Chaos fallback test completed');
}