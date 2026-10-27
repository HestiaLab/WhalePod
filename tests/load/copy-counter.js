import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE_URL;
const ORIGIN = __ENV.ORIGIN;
const ARTIFACT_ID = __ENV.ARTIFACT_ID;

if (!BASE || !ORIGIN || !ARTIFACT_ID) {
  throw new Error('BASE_URL, ORIGIN and ARTIFACT_ID are required.');
}

export const options = {
  insecureSkipTLSVerify: true,
  scenarios: {
    steady_100: {
      executor: 'constant-arrival-rate',
      rate: 100,
      timeUnit: '1s',
      duration: '30s',
      preAllocatedVUs: 50,
      maxVUs: 100,
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.001'],
    http_req_duration: ['p(95)<200', 'p(99)<500'],
  },
};

export default function () {
  const response = http.post(
    `${BASE}/internal/counters/copy`,
    JSON.stringify({ artifactId: ARTIFACT_ID }),
    {
      headers: {
        'Content-Type': 'application/json',
        Origin: ORIGIN,
      },
    },
  );

  check(response, {
    'status is 204': (res) => res.status === 204,
  });
}
