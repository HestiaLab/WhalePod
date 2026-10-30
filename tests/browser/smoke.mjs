import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const BASE_URL = process.env.BASE_URL;
const CHROME_PATH = process.env.CHROME_PATH
  || (process.platform === 'win32'
    ? 'C:/Program Files/Google/Chrome/Application/chrome.exe'
    : 'google-chrome');
const PAGES = (process.env.PAGES || '/,/search?q=test').split(',').map((page) => page.trim());
const RUN_AXE = process.env.AXE === '1';
const VIEWPORTS = [
  { name: '1280', width: 1280, height: 900 },
  { name: '375', width: 375, height: 812 }
];

if (!BASE_URL) {
  console.error('BASE_URL is required, e.g. BASE_URL=http://localhost:5224 node tests/browser/smoke.mjs');
  process.exit(2);
}

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const reportDir = join('tests', 'browser', 'reports', stamp);
await mkdir(reportDir, { recursive: true });
const profileDir = await mkdtemp(join(tmpdir(), 'whalepod-browser-'));
const port = 9300 + Math.floor(Math.random() * 500);
const chrome = spawn(CHROME_PATH, [
  '--headless=new',
  '--disable-gpu',
  '--no-first-run',
  '--no-default-browser-check',
  `--remote-debugging-port=${port}`,
  `--user-data-dir=${profileDir}`,
  'about:blank'
], { stdio: 'ignore' });

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
let target;
for (let attempt = 0; attempt < 50; attempt++) {
  try {
    const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    target = targets.find((item) => item.type === 'page');
    if (target) break;
  } catch {
    // Chrome is still starting.
  }
  await sleep(200);
}

if (!target) {
  chrome.kill();
  console.error('Chrome did not expose a CDP page target.');
  process.exit(2);
}

const socket = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  socket.addEventListener('open', resolve, { once: true });
  socket.addEventListener('error', reject, { once: true });
});

let nextId = 1;
const pending = new Map();
const loadWaiters = [];
let consoleErrors = [];
socket.addEventListener('message', (event) => {
  const message = JSON.parse(event.data);
  if (message.id && pending.has(message.id)) {
    const { resolve, reject } = pending.get(message.id);
    pending.delete(message.id);
    message.error ? reject(new Error(JSON.stringify(message.error))) : resolve(message.result);
    return;
  }
  if (message.method === 'Page.loadEventFired') loadWaiters.shift()?.();
  if (message.method === 'Runtime.exceptionThrown') consoleErrors.push(message.params);
  if (message.method === 'Runtime.consoleAPICalled' && message.params?.type === 'error') {
    consoleErrors.push(message.params);
  }
});

const send = (method, params = {}) =>
  new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    socket.send(JSON.stringify({ id, method, params }));
  });

const waitForLoad = () =>
  new Promise((resolve) => {
    const timer = setTimeout(resolve, 15000);
    loadWaiters.push(() => {
      clearTimeout(timer);
      resolve();
    });
  });

async function navigate(url) {
  const loaded = waitForLoad();
  await send('Page.navigate', { url });
  await loaded;
}

async function evaluate(expression, awaitPromise = false) {
  const result = await send('Runtime.evaluate', {
    expression,
    returnByValue: true,
    awaitPromise
  });
  if (result.exceptionDetails) {
    throw new Error(JSON.stringify(result.exceptionDetails));
  }
  return result.result.value;
}

await send('Page.enable');
await send('Runtime.enable');

let axeSource;
if (RUN_AXE) {
  const response = await fetch('https://cdn.jsdelivr.net/npm/axe-core@4.10.3/axe.min.js');
  if (!response.ok) {
    chrome.kill();
    console.error('Unable to download axe-core.');
    process.exit(2);
  }
  axeSource = await response.text();
}

const results = [];
for (const viewport of VIEWPORTS) {
  await send('Emulation.setDeviceMetricsOverride', {
    width: viewport.width,
    height: viewport.height,
    deviceScaleFactor: 1,
    mobile: viewport.width < 600
  });
  for (const page of PAGES) {
    consoleErrors = [];
    const url = new URL(page, BASE_URL).toString();
    await navigate(url);
    const metrics = JSON.parse(await evaluate(`JSON.stringify({
      title: document.title,
      innerWidth: window.innerWidth,
      scrollWidth: document.documentElement.scrollWidth
    })`));
    const screenshot = await send('Page.captureScreenshot', { format: 'png' });
    const fileName = `${viewport.name}${page.replace(/[^a-z0-9]+/gi, '_') || '_home'}.png`;
    await writeFile(join(reportDir, fileName), Buffer.from(screenshot.data, 'base64'));

    let axeViolations = [];
    if (RUN_AXE) {
      await evaluate(axeSource);
      const axeResult = await evaluate(
        `axe.run(document, { resultTypes: ['violations'] }).then(r => JSON.stringify(r.violations.map(v => ({
          id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.length
        }))))`,
        true);
      axeViolations = JSON.parse(axeResult);
      await writeFile(
        join(reportDir, `${viewport.name}${page.replace(/[^a-z0-9]+/gi, '_') || '_home'}-axe.json`),
        JSON.stringify(axeViolations, null, 2));
    }

    const entry = {
      viewport: viewport.name,
      path: page,
      url,
      title: metrics.title,
      scrollWidth: metrics.scrollWidth,
      overflow: metrics.scrollWidth > metrics.innerWidth,
      consoleErrors: consoleErrors.length,
      axe: axeViolations
    };
    results.push(entry);
    console.log(JSON.stringify(entry));
  }
}

await writeFile(join(reportDir, 'summary.json'), JSON.stringify(results, null, 2));
socket.close();
chrome.kill();

const failed = results.some((entry) =>
  entry.overflow
  || entry.consoleErrors > 0
  || entry.axe.some((violation) => violation.impact === 'serious' || violation.impact === 'critical'));
console.log(`report=${reportDir}`);
console.log(failed ? 'SMOKE_FAILED' : 'SMOKE_OK');
process.exit(failed ? 1 : 0);
