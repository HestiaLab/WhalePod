import http from 'k6/http';
import { check } from 'k6';
import encoding from 'k6/encoding';

const API = __ENV.API_BASE;
const WEB = __ENV.WEB_BASE;
const AUTH_COOKIE = __ENV.AUTH_COOKIE;
const CSRF_TOKEN = __ENV.CSRF_TOKEN;
const DURATION = __ENV.DURATION || '10m';
const IMAGE = encoding.b64decode(
  'iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAzSURBVFhH7c4hAQAwCABBwlKJmMswPAHAnHjz6iLr/ctiju0AAAAAAAAAAAAAAAAAAAAadmaUps1BXw4AAAAASUVORK5CYII=',
  'std',
  'b',
);

if (!API || !WEB) {
  throw new Error('API_BASE and WEB_BASE are required.');
}

if (!AUTH_COOKIE || !CSRF_TOKEN) {
  throw new Error('AUTH_COOKIE and CSRF_TOKEN are required; do not rely on development login.');
}

export const options = {
  scenarios: {
    upload_2rps: {
      executor: 'constant-arrival-rate',
      rate: 2,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 4,
      maxVUs: 10,
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<1500'],
    checks: ['rate>0.99'],
    dropped_iterations: ['count<100'],
  },
  summaryTrendStats: ['min', 'med', 'p(50)', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  return {
    token: CSRF_TOKEN,
    cookie: AUTH_COOKIE,
  };
}

export default function (data) {
  const response = http.post(
    `${API}/api/v1/uploads/images`,
    { file: http.file(IMAGE, 'sample.png', 'image/png') },
    {
      headers: {
        Cookie: data.cookie,
        'X-CSRF-Token': data.token,
      },
    },
  );

  check(response, {
    'status is 201': (res) => res.status === 201,
  });
}
