import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// 平板镜像界面探针：只连本机合成探针（SpikeTabletUiSource），不读任何真实剧本。
const args = process.argv.slice(2);
const arg = name => { const i = args.indexOf(name); return i < 0 ? null : args[i + 1]; };
const base = new URL(arg('--url') || 'http://127.0.0.1:5099/');
assert(['127.0.0.1', 'localhost', '[::1]'].includes(base.hostname), '只连接本机合成探针');
const modulePath = arg('--playwright-module');
const { chromium } = await import(modulePath ? pathToFileURL(path.join(modulePath, 'index.mjs')).href : 'playwright');
const output = path.resolve(arg('--output') || 'artifacts/tablet/ui-mirror');
await fs.mkdir(output, { recursive: true });
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const errors = [];
try {
  const context = await browser.newContext({ viewport: { width: 1360, height: 1000 } });
  const page = await context.newPage();
  page.on('pageerror', e => errors.push(e.message));
  await page.goto(base.href, { waitUntil: 'domcontentloaded' });
  await page.locator('.role-list button:not([disabled])').first().click();
  await page.getByRole('button', { name: '公共对话', exact: true }).click();
  await page.locator('.board').waitFor();

  // 内容：环节三态、当前环节、灰色按钮与说明、对话。
  const phases = await page.locator('.board-phases li').allInnerTexts();
  assert.deepEqual(phases, ['开场', '搜证', '讨论', '揭秘']);
  assert.equal(await page.locator('.board-phases li.current').innerText(), '搜证');
  assert.equal(await page.locator('.board-phases li.done').count(), 1);
  const controls = page.locator('.board-control');
  assert(await controls.count() >= 1);
  for (const c of await controls.all()) assert(await c.isDisabled(), '灰色按钮必须 disabled');
  assert.equal(await page.locator('.board-note').innerText(), '请在电脑上操作');
  assert(await page.locator('.feed li').count() >= 1, '公开对话应显示');
  assert.equal(await page.locator('.public input,.public textarea').count(), 0);
  // 内部状态不出现。
  const text = await page.locator('.public').innerText();
  for (const word of ['备忘', '上下文']) assert(!text.includes(word), `页面不应出现「${word}」`);
  // 发送与对话入口仍在底部条。
  assert(await page.getByRole('button', { name: '发送', exact: true }).isVisible());
  console.log('PASS content');

  for (const [name, viewport, narrow] of [
    ['desktop-1360', { width: 1360, height: 1000 }, false],
    ['tablet-landscape-1112', { width: 1112, height: 834 }, false],
    ['tablet-portrait-834', { width: 834, height: 1112 }, true],
    ['phone-430', { width: 430, height: 932 }, true],
  ]) {
    await page.setViewportSize(viewport); await page.waitForTimeout(300);
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${name} 横向溢出`);
    assert.equal(await page.locator('.board-oneline').isVisible(), narrow, `${name} 折叠状态不对`);
    assert.equal(await page.locator('.board-phases').isVisible(), !narrow, `${name} 环节列表可见性不对`);
    await page.screenshot({ path: path.join(output, `ui-${name}.png`), fullPage: true });
    console.log(`PASS ${name}`);
  }
  assert.deepEqual(errors, [], '页面不应有脚本错误');
} finally {
  await browser.close();
}
