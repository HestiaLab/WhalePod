import http from 'k6/http';
import { check } from 'k6';

const MODE = __ENV.MODE || 'api';
const SCENARIO = __ENV.SCENARIO || 'feed';
const BASE = __ENV.BASE_URL;
const POST_ID = __ENV.POST_ID;
const QUERY = __ENV.QUERY || '测试';

if (!BASE) {
  throw new Error('BASE_URL is required.');
}

const scenarios = {
  api: {
    feed: { rate: 50, path: () => '/api/v1/feed?sort=latest&limit=20' },
    detail: { rate: 50, path: () => `/api/v1/posts/${POST_ID}` },
    search: { rate: 30, path: () => `/api/v1/search?q=${encodeURIComponent(QUERY)}&type=posts&limit=20` },
  },
  web: {
    feed: { rate: 20, path: () => '/' },
    detail: { rate: 20, path: () => `/posts/${POST_ID}` },
    search: { rate: 10, path: () => `/search?q=${encodeURIComponent(QUERY)}` },
  },
};

const selected = scenarios[MODE]?.[SCENARIO];
if (!selected) {
  throw new Error(`Unknown MODE/SCENARIO: ${MODE}/${SCENARIO}`);
}

if (SCENARIO === 'detail' && !POST_ID) {
  throw new Error('POST_ID is required for detail scenarios.');
}

export const options = {
  scenarios: {
    [`${MODE}_${SCENARIO}`]: {
      executor: 'constant-arrival-rate',
      rate: selected.rate,
      timeUnit: '1s',
      duration: '30s',
      preAllocatedVUs: Math.max(10, Math.min(selected.rate, 100)),
      maxVUs: Math.max(50, selected.rate * 3),
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<1500', 'p(99)<3000'],
  },
  summaryTrendStats: ['min', 'med', 'p(50)', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export default function () {
  const response = http.get(`${BASE}${selected.path()}`, {
    headers: { Accept: MODE === 'api' ? 'application/json' : 'text/html' },
  });

  check(response, {
    'status is 200': (res) => res.status === 200,
  });
}
