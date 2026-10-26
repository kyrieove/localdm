// 无头 Chrome 打开电脑托管的 Flutter 网页版，只汇总 /tablet/* 请求的路径与状态码，不截图、不打印内容。
// 先用 scripts/open-story.ps1 翻开一本剧本，再跑：node scripts/probe-tablet-app.mjs [端口，默认 5188]
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const base = `http://127.0.0.1:${process.argv[2] ?? 5188}`;
const device = randomBytes(16).toString('hex');
const headers = { 'X-LocalDm-Device': device };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// 先用同一个设备编号认领第一个空着的角色、翻开第 1 页：App 打开后应直接进阅读页。
const catalog = await (await fetch(base + '/tablet/catalog', { headers })).json();
const role = catalog.roles.find((r) => !catalog.claimedRoles.includes(r.roleId));
console.log('预认领', (await fetch(`${base}/tablet/claim?roleId=${encodeURIComponent(role.roleId)}`, { method: 'POST', headers })).status,
  '翻开第1页', (await fetch(base + '/tablet/role/open/1', { method: 'POST', headers })).status);

const chrome = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', [
  '--headless=new', '--remote-debugging-port=9333', '--window-size=1180,820',
  `--user-data-dir=${mkdtempSync(join(tmpdir(), 'flutter-probe-'))}`, 'about:blank'], { stdio: 'ignore' });
try {
  let targets;
  for (let i = 0; i < 40 && !targets; i++) {
    await sleep(250);
    targets = await fetch('http://127.0.0.1:9333/json/list').then((r) => r.json()).catch(() => null);
  }
  const page = targets.find((t) => t.type === 'page');
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((r) => ws.addEventListener('open', r));
  let id = 0;
  const pending = new Map();
  const seen = new Map();
  const errors = [];
  ws.addEventListener('message', (e) => {
    const m = JSON.parse(e.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); }
    if (m.method === 'Network.responseReceived' && m.params.response.url.includes('/tablet/')) {
      const path = new URL(m.params.response.url).pathname.replace(/\/cards\/[^/]+\//, '/cards/*/');
      const key = `${path} ${m.params.response.status}`;
      seen.set(key, (seen.get(key) ?? 0) + 1);
    }
    if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.text.slice(0, 160));
    if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') errors.push(String(m.params.args[0]?.value).slice(0, 160));
  });
  const send = (method, params = {}) => new Promise((r) => { pending.set(++id, r); ws.send(JSON.stringify({ id, method, params })); });
  await send('Network.enable');
  await send('Runtime.enable');
  await send('Page.enable');
  // shared_preferences 网页版把值 JSON 编码后存在 localStorage 的 flutter.<键> 下。
  await send('Page.navigate', { url: base + '/app/favicon.png' });
  await sleep(800);
  await send('Runtime.evaluate', { expression: `localStorage.setItem('flutter.device', ${JSON.stringify(JSON.stringify(device))})` });
  for (const section of ['book', 'cards']) {
    seen.clear();
    await send('Page.navigate', { url: `${base}/app/?section=${section}` });
    await sleep(15000);
    console.log(`\n== section=${section}`);
    for (const [k, n] of [...seen].sort()) console.log(`${k} ×${n}`);
  }
  console.log('\n错误', errors.length ? errors : '无');
} finally {
  await fetch(base + '/tablet/release', { method: 'POST', headers });
  chrome.kill();
}
