import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const args = process.argv.slice(2);
const arg = name => { const i = args.indexOf(name); return i < 0 ? null : args[i + 1]; };
const base = new URL(arg('--url') || 'http://127.0.0.1:5099/');
assert(['127.0.0.1', 'localhost', '[::1]'].includes(base.hostname), '只连接本机合成探针');
const modulePath = arg('--playwright-module');
const { chromium } = await import(modulePath ? pathToFileURL(path.join(modulePath, 'index.mjs')).href : 'playwright');
const output = path.resolve(arg('--output') || 'artifacts/tablet/codex-reader-acceptance');
await fs.mkdir(output, { recursive: true });
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const contexts = [], results = [], errors = [];
function pass(name, detail = {}) { results.push({ name, passed: true, ...detail }); console.log('PASS ' + name); }
const readState = page => page.evaluate(() => ({ index: tabletReader.index(), total: tabletReader.total(),
  offset: tabletReader.offset(), text: document.querySelector('.page-window > .page-body:not(.measure)').textContent }));
try {
  const context = await browser.newContext({ viewport: { width: 1360, height: 1000 } }); contexts.push(context);
  const catalog = await (await context.request.get(new URL('/tablet/catalog', base).href)).json();
  assert.equal(catalog.claimedRoles.length, 0, '请先重启合成探针，释放上次验收的角色');
  const page = await context.newPage(); page.on('pageerror', e => errors.push(e.message));
  await page.goto(base.href, { waitUntil: 'domcontentloaded' });
  await page.locator('.role-list button').first().click();
  // 有原稿页的角色本默认显示原稿；先确认原稿模式，再切到文字重排验证排版。
  await page.locator('.reader.source-mode').waitFor();
  await page.getByRole('button', { name: '文字重排', exact: true }).click();
  await page.locator('.reader.text-mode').waitFor();
  await page.waitForFunction(() => window.tabletReader?.total() > 0);
  assert.match(await page.locator('.crumb').innerText(), /合成角色 1/);
  const book = await page.evaluate(() => fetch('/tablet/role/book').then(r => r.json()));
  const expected = book.chapters.map(c => c.title + c.blocks.filter(b => !['ChapterTitle', 'Break'].includes(b.kind))
    .map(b => b.text + (b.kind === 'Figure' ? b.imageCaption || '' : '')).join('')).join('');
  assert.equal(await page.evaluate(() => tabletReader.paginatedText()), expected);
  pass('actual-reader-text-and-role', { characters: expected.length });
  await page.getByRole('button', { name: '下一页', exact: true }).click();
  await page.waitForFunction(() => tabletReader.index() > 0 && tabletReader.offset() > 0);
  const turned = await readState(page);
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForFunction(offset => window.tabletReader?.offset() === offset, turned.offset);
  assert.equal((await readState(page)).text, turned.text);
  pass('turn-and-refresh-restore', { offset: turned.offset });
  await page.getByRole('button', { name: 'Aa', exact: true }).click();
  await page.getByRole('button', { name: '增大字号', exact: true }).click();
  await page.waitForFunction(() => getComputedStyle(document.querySelector('.reader')).getPropertyValue('--body-size').trim() === '20px');
  await page.locator('.settings select').selectOption('2.2');
  await page.waitForFunction(async () => { const p = await fetch('/tablet/role/progress').then(r => r.json());
    return p.settings.fontSize === 20 && p.settings.lineSpacing === '2.2'; });
  const typed = await readState(page);
  assert(typed.offset <= turned.offset && typed.offset + typed.text.length > turned.offset);
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => window.tabletReader?.total() > 0 && getComputedStyle(document.querySelector('.reader')).getPropertyValue('--body-size').trim() === '20px');
  assert.equal(await page.evaluate(() => getComputedStyle(document.querySelector('.reader')).getPropertyValue('--leading').trim()), '2.2');
  pass('type-settings-persist-and-repaginate');
  for (const viewport of [{ width: 1360, height: 1000 }, { width: 834, height: 1112 }, { width: 430, height: 932 }]) {
    await page.setViewportSize(viewport); await page.waitForTimeout(300);
    assert.equal(await page.evaluate(() => tabletReader.paginatedText()), expected);
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({ path: path.join(output, `reader-${viewport.width}.png`), fullPage: true });
    pass('responsive-' + viewport.width, { offset: (await readState(page)).offset });
  }
  await page.getByRole('button', { name: '专注', exact: true }).click();
  await page.locator('.reader.focus').waitFor();
  await page.getByRole('button', { name: '退出专注', exact: true }).click();
  await page.getByRole('button', { name: book.chapters[0].title, exact: true }).click();
  // 点击后要等服务端循环回来再读位置：不等的话会读到跳章前的旧偏移。
  await page.waitForFunction(() => tabletReader.offset() === 0);
  assert.equal((await readState(page)).offset, 0);
  await page.reload({ waitUntil: 'domcontentloaded' }); await page.waitForFunction(() => window.tabletReader?.total() > 0);
  assert.equal((await readState(page)).offset, 0); pass('focus-chapter-zero-anchor');

  const other = await browser.newContext(); contexts.push(other);
  const otherPage = await other.newPage(); await otherPage.goto(base.href, { waitUntil: 'domcontentloaded' });
  await otherPage.locator('.role-list button').first().waitFor();
  assert(await otherPage.locator('.role-list button').first().isDisabled());
  await otherPage.locator('.role-list button').nth(1).click(); await otherPage.locator('.reader.source-mode').waitFor();
  const source = p => p.evaluate(() => fetch('/tablet/role/source/2').then(r => r.arrayBuffer()).then(b => Array.from(new Uint8Array(b))));
  assert.notDeepEqual(await source(page), await source(otherPage));
  assert.equal((await otherPage.evaluate(() => fetch('/tablet/role/progress').then(r => r.json()))).characterOffset, 0);
  await Promise.all([3, 4, 5, 6].map(async role => { const isolated = await browser.newContext(); contexts.push(isolated);
    assert.equal((await isolated.request.post(new URL('/tablet/claim?roleId=role-' + role, base).href)).status(), 200);
    const response = await isolated.request.get(new URL('/tablet/role/book', base).href);
    assert.equal((await response.json()).roleId, 'role-' + role); }));
  pass('six-independent-browser-role-sessions');
  await page.getByRole('button', { name: '全部线索卡', exact: true }).click(); await page.locator('.card-list button').first().click();
  await page.waitForFunction(() => document.querySelector('.shot img')?.naturalWidth === 300);
  await page.getByRole('button', { name: '公共对话', exact: true }).click(); assert.equal(await page.locator('.public input,.public textarea').count(), 0);
  await page.getByRole('button', { name: '我的角色本', exact: true }).click(); await page.waitForFunction(() => window.tabletReader?.total() > 0);
  assert.equal(await page.evaluate(() => tabletReader.paginatedText()), expected); pass('cards-readonly-feed-return');

  const fixture = await context.newPage();
  await fixture.goto(new URL('/reader.css', base).href, { waitUntil: 'domcontentloaded' });
  const css = await (await context.request.get(new URL('/reader.css', base).href)).text();
  await fixture.setContent('<!doctype html><html><head><style>' + css + '</style></head><body><div class="reader" style="--body-size:20px;--leading:1.95;width:420px"><div class="page-window" style="height:280px;min-height:280px"><article class="page-body measure"></article><article class="page-body" id="visible"></article></div></div></body></html>');
  await fixture.addScriptTag({ url: new URL('/reader-pagination.js', base).href });
  const data = await fixture.evaluate(async () => {
    const long = '不改写𠮷字🙂，原文继续。\n'.repeat(90);
    const blocks = [{ kind: 'ChapterTitle', text: '第一章', sourcePageNumber: 1 }, { kind: 'Paragraph', text: long, sourcePageNumber: 1 },
      { kind: 'SectionTitle', text: '原有节题', sourcePageNumber: 2 },
      { kind: 'Figure', text: '地图', caption: '原有图注', sourceUrl: '/tablet/role/source/2', sourcePageNumber: 2 },
      { kind: 'Break', text: '' }, { kind: 'Paragraph', text: '<script>不是代码</script>', sourcePageNumber: 2 },
      ...Array.from({ length: 405 }, () => ({ kind: 'Paragraph', text: '短段。', sourcePageNumber: 3 }))];
    const expected = '第一章' + long + '原有节题地图原有图注<script>不是代码</script>' + '短段。'.repeat(405);
    const body = document.querySelector('#visible'), measure = document.querySelector('.measure');
    tabletReader.mount(body, measure, blocks); await tabletReader.paginate(0);
    const total = tabletReader.total(), anchors = [], reachable = [], wellFormed = [];
    let oversizedFigures = 0;
    for (let i = 0; i < total; i++) { anchors.push(tabletReader.offset());
      const fits = body.scrollHeight <= measure.clientHeight + 1;
      const window = body.parentElement;
      const scrollableFigure = Boolean(body.querySelector('figure')) && getComputedStyle(window).overflowY === 'auto'
        && window.scrollHeight >= body.scrollHeight;
      if (!fits && scrollableFigure) oversizedFigures++;
      reachable.push(fits || scrollableFigure);
      wellFormed.push(body.textContent.isWellFormed()); tabletReader.turn(1); }
    const saved = anchors[Math.floor(total / 3)]; await tabletReader.paginate(saved); const restored = tabletReader.offset();
    tabletReader.jumpChapter(0);
    return { total, allReachable: reachable.every(Boolean), oversizedFigures, wellFormed: wellFormed.every(Boolean), exact: tabletReader.paginatedText() === expected,
      anchorsValid: anchors.every(Number.isFinite), increasing: anchors.every((n, i) => !i || n > anchors[i - 1]),
      saved, restored, zero: tabletReader.offset(), injectedScript: Boolean(body.querySelector('script')) };
  });
  assert(data.total > 10 && data.exact && data.allReachable && data.wellFormed && data.anchorsValid && data.increasing, JSON.stringify(data));
  assert.equal(data.restored, data.saved); assert.equal(data.zero, 0); assert.equal(data.injectedScript, false);
  pass('dom-long-paragraph-unicode-figures-405-blocks', data);
  await page.evaluate(() => { window.acceptancePaginations = 0; const original = tabletReader.paginate;
    tabletReader.paginate = async (...args) => { window.acceptancePaginations++; return original(...args); }; });
  await page.waitForTimeout(500); assert.equal(await page.evaluate(() => acceptancePaginations), 0); assert.deepEqual(errors, []);
  pass('no-render-loop');
  await fs.writeFile(path.join(output, 'results.json'), JSON.stringify({ passed: true, results, realTablet: '未执行' }, null, 2));
} catch (error) {
  await fs.writeFile(path.join(output, 'results.json'), JSON.stringify({ passed: false, results, error: String(error), errors }, null, 2));
  throw error;
} finally {
  await Promise.all(contexts.map(async context => {
    try { await context.request.post(new URL('/tablet/release', base).href); }
    catch { /* 服务中断时仍关闭这个验收浏览器。 */ }
    finally { await context.close(); }
  }));
  await browser.close();
}
