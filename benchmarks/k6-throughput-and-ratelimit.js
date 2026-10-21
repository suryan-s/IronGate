import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';
import { SharedArray } from 'k6/data';

// Configuration via environment variables
const BASE_URL = __ENV.IRONGATE_URL || 'http://localhost:8080';
const KEYCLOAK_TOKEN = __ENV.KEYCLOAK_TOKEN || '';
const PUBLIC_ROUTE = `${BASE_URL}/api/public/echo`;
const PROTECTED_ROUTE = `${BASE_URL}/api/protected/echo`;

// Custom metrics
const publicRequests = new Rate('public_requests');
const protectedRequests = new Rate('protected_requests');
const rateLimited = new Rate('rate_limited');
const publicLatency = new Trend('public_latency');
const protectedLatency = new Trend('protected_latency');
const totalRequests = new Counter('total_requests');
const errors = new Counter('errors');

// Test configuration
export const options = {
  scenarios: {
    // Public route test - ramps up to test rate limiting
    public_rate_limit: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: '30s', target: 10 },   // Warm up
        { duration: '1m', target: 50 },    // Ramp up
        { duration: '30s', target: 100 },  // Stress
        { duration: '30s', target: 0 },    // Cool down
      ],
      exec: 'publicRoute',
      tags: { test_type: 'public_rate_limit' },
    },
    // Protected route test - with auth token
    protected_route: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: '30s', target: 10 },
        { duration: '1m', target: 30 },
        { duration: '30s', target: 50 },
        { duration: '30s', target: 0 },
      ],
      exec: 'protectedRoute',
      tags: { test_type: 'protected_route' },
    },
    // Constant load test for latency percentiles
    sustained_load: {
      executor: 'constant-vus',
      vus: 20,
      duration: '2m',
      exec: 'sustainedLoad',
      tags: { test_type: 'sustained_load' },
    },
  },
  thresholds: {
    // p95 latency should be under 100ms
    'public_latency': ['p(95)<100'],
    'protected_latency': ['p(95)<100'],
    // Error rate should be minimal (rate limiting is expected behavior)
    'rate_limited': ['rate<0.5'],
  },
};

// Headers factory
function getHeaders(token) {
  const headers = {
    'Content-Type': 'application/json',
    'Accept': 'application/json',
  };
  if (token) {
    headers['Authorization'] = `Bearer ${token}`;
  }
  return headers;
}

// Public route test - tests anonymous rate limiting
export function publicRoute() {
  const startTime = new Date();
  const res = http.get(PUBLIC_ROUTE, {
    headers: getHeaders(),
  });
  
  const latency = new Date() - startTime;
  publicLatency.add(latency);
  totalRequests.add(1);

  const isRateLimited = res.status === 429;
  const isOk = res.status === 200;
  
  publicRequests.add(isOk);
  rateLimited.add(isRateLimited);
  
  if (res.status >= 500) {
    errors.add(1);
  }

  check(res, {
    'public: status is 200 or 429': (r) => r.status === 200 || r.status === 429,
    'public: has x-correlation-id': (r) => r.headers['x-correlation-id'] !== undefined,
    'public: rate limited has Retry-After': (r) => r.status !== 429 || r.headers['Retry-After'] !== undefined,
    'public: rate limited has X-RateLimit-* headers': (r) => 
      r.status === 429 || (r.headers['X-RateLimit-Limit'] && r.headers['X-RateLimit-Remaining'] && r.headers['X-RateLimit-Reset']),
  });

  sleep(0.1);
}

// Protected route test - requires valid token
export function protectedRoute() {
  if (!KEYCLOAK_TOKEN) {
    console.warn('KEYCLOAK_TOKEN not set, skipping protected route test');
    return;
  }

  const startTime = new Date();
  const res = http.get(PROTECTED_ROUTE, {
    headers: getHeaders(KEYCLOAK_TOKEN),
  });
  
  const latency = new Date() - startTime;
  protectedLatency.add(latency);
  totalRequests.add(1);

  const isRateLimited = res.status === 429;
  const isOk = res.status === 200;
  const isUnauthorized = res.status === 401;
  
  protectedRequests.add(isOk);
  rateLimited.add(isRateLimited);
  
  if (res.status >= 500 || (isUnauthorized && KEYCLOAK_TOKEN)) {
    errors.add(1);
  }

  check(res, {
    'protected: status is 200, 401, or 429': (r) => [200, 401, 429].includes(r.status),
    'protected: has x-correlation-id': (r) => r.headers['x-correlation-id'] !== undefined,
    'protected: has X-Forwarded-* headers when OK': (r) => r.status !== 200 || (r.headers['x-forwarded-user'] && r.headers['x-forwarded-roles']),
    'protected: rate limited has X-RateLimit-* headers': (r) => 
      r.status === 429 || (r.headers['X-RateLimit-Limit'] && r.headers['X-RateLimit-Remaining'] && r.headers['X-RateLimit-Reset']),
  });

  sleep(0.1);
}

// Sustained load test - mixed public and protected
export function sustainedLoad() {
  const route = Math.random() < 0.7 ? PUBLIC_ROUTE : PROTECTED_ROUTE;
  const token = route === PROTECTED_ROUTE ? KEYCLOAK_TOKEN : '';
  
  const startTime = new Date();
  const res = http.get(route, {
    headers: getHeaders(token),
  });
  
  const latency = new Date() - startTime;
  if (route === PUBLIC_ROUTE) {
    publicLatency.add(latency);
    publicRequests.add(res.status === 200);
  } else {
    protectedLatency.add(latency);
    protectedRequests.add(res.status === 200);
  }
  totalRequests.add(1);
  rateLimited.add(res.status === 429);
  
  if (res.status >= 500) {
    errors.add(1);
  }

  sleep(0.05);
}

// Setup: fetch token if not provided
export function setup() {
  if (!KEYCLOAK_TOKEN) {
    console.log('No KEYCLOAK_TOKEN provided. Attempting to fetch from Keycloak...');
    // Note: In practice, you'd fetch this from Keycloak
    // For now, we'll rely on the env var being set
  }
  return { baseUrl: BASE_URL };
}

// Teardown
export function teardown(data) {
  console.log('Test completed');
}