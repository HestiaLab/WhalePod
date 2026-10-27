import http from 'k6/http';
import { check } from 'k6';
import encoding from 'k6/encoding';

const API = __ENV.API_BASE;
const WEB = __ENV.WEB_BASE;
const HANDLE = __ENV.HANDLE || 'bob';
const IMAGE = encoding.b64decode(
  'iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAzSURBVFhH7c4hAQAwCABBwlKJmMswPAHAnHjz6iLr/ctiju0AAAAAAAAAAAAAAAAAAAAadmaUps1BXw4AAAAASUVORK5CYII=',
  'std',
  'b',
);

if (!API || !WEB) {
  throw new Error('API_BASE and WEB_BASE are required.');
}

export const options = {
  scenarios: {
    upload_2rps: {
      executor: 'constant-arrival-rate',
      rate: 2,
      timeUnit: '1s',
      duration: '20s',
      preAllocatedVUs: 4,
      maxVUs: 10,
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<1500'],
  },
  summaryTrendStats: ['min', 'med', 'p(50)', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  const login = http.get(`${WEB}/dev/test-login?handle=${HANDLE}`, { redirects: 0 });
  const session = login.cookies['whalepod_session']?.[0]?.value;
  if (!session) {
    throw new Error(`Login failed with status ${login.status}.`);
  }

  const csrf = http.get(`${API}/api/v1/auth/csrf`, {
    headers: { Cookie: `whalepod_session=${session}` },
  });
  const csrfCookie = csrf.cookies['whalepod_csrf']?.[0]?.value;
  const token = csrf.json('data.token');
  if (!csrfCookie || !token) {
    throw new Error('CSRF token was not issued.');
  }

  return {
    token,
    cookie: `whalepod_session=${session}; whalepod_csrf=${csrfCookie}`,
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
