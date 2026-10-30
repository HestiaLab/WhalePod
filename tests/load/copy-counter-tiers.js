import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE_URL;
const ORIGIN = __ENV.ORIGIN;
const ARTIFACT_ID = __ENV.ARTIFACT_ID;
const TIER = __ENV.TIER || 'target_100';
const DURATION = __ENV.DURATION;

if (!BASE || !ORIGIN || !ARTIFACT_ID) {
  throw new Error('BASE_URL, ORIGIN and ARTIFACT_ID are required.');
}

const tiers = {
  baseline_50: { rate: 50, duration: '60s', preAllocatedVUs: 50, maxVUs: 200 },
  target_100: { rate: 100, duration: '60s', preAllocatedVUs: 100, maxVUs: 300 },
  stress_200: { rate: 200, duration: '60s', preAllocatedVUs: 200, maxVUs: 500 },
  burst_500: { rate: 500, duration: '20s', preAllocatedVUs: 500, maxVUs: 1000 },
  duplicate_heavy: { rate: 100, duration: '60s', preAllocatedVUs: 100, maxVUs: 300 },
  distinct_heavy: { rate: 100, duration: '60s', preAllocatedVUs: 100, maxVUs: 300 },
};

const selected = tiers[TIER];
if (!selected) {
  throw new Error(`Unknown TIER: ${TIER}`);
}

export const options = {
  scenarios: {
    [TIER]: {
      executor: 'constant-arrival-rate',
      rate: selected.rate,
      timeUnit: '1s',
      duration: DURATION || selected.duration,
      preAllocatedVUs: selected.preAllocatedVUs,
      maxVUs: selected.maxVUs,
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<500', 'p(99)<1000'],
    checks: ['rate>0.99'],
    dropped_iterations: ['count<100'],
  },
  summaryTrendStats: ['min', 'med', 'p(50)', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

function clientIp() {
  if (TIER === 'distinct_heavy') {
    const index = Math.floor(Math.random() * 2000);
    return `10.20.${Math.floor(index / 250)}.${(index % 250) + 1}`;
  }

  if (TIER === 'duplicate_heavy') {
    return `10.30.0.${(__ITER % 10) + 1}`;
  }

  return '10.40.0.1';
}

export default function () {
  const response = http.post(
    `${BASE}/internal/counters/copy`,
    JSON.stringify({ artifactId: ARTIFACT_ID }),
    {
      headers: {
        'Content-Type': 'application/json',
        Origin: ORIGIN,
        'X-Forwarded-For': clientIp(),
      },
    },
  );

  check(response, {
    'status is 204': (res) => res.status === 204,
  });
}
