// Exercise the packaged page through the native bridge using neutral game fixtures.
import { chromium } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const web = path.resolve(import.meta.dirname, '..'), assets = path.join(web, 'dist-maui');
const shots = path.resolve(web, '../../../tests/screenshots');
await fs.mkdir(shots, { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 852, height: 393 }, isMobile: true, hasTouch: true });
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.hostname !== 'game.local') return route.abort();
    if (url.pathname === '/test-title.svg') return route.fulfill({ contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="280"><rect width="1200" height="280" fill="#18354a"/><path d="M0 260L240 80 450 220 700 20 1000 210 1200 100V280H0" fill="#355e6b"/><text x="600" y="150" text-anchor="middle" font-family="sans-serif" font-size="76" fill="#ecdfb4">ERA · JOURNEY</text></svg>' });
    const file = path.resolve(assets, decodeURIComponent(url.pathname).replace(/^\/wwwroot\//, ''));
    if (!file.startsWith(assets + path.sep)) return route.abort();
    try { await route.fulfill({ body: await fs.readFile(file), contentType: file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : file.endsWith('.html') ? 'text/html' : 'application/octet-stream' }); }
    catch { await route.fulfill({ status: 404, body: '' }); }
  });
  await page.addInitScript(() => {
    localStorage.setItem('app.touchLayout', 'on');
    window.testMessages = [];
    window.chrome = { webview: { postMessage: raw => window.testMessages.push(JSON.parse(raw)) } };
  });
  await page.goto('https://game.local/wwwroot/index.html');
  const showTurn = async (generation, count = 2, width = 1280, rowSizes = Array(count).fill(1)) => page.evaluate(({ generation, width, rowSizes }) => {
    window.__onMessage({ type: 'layout', windowWidth: width, fontSize: 24, lineHeight: 28, fontName: 'monospace', state: 'WaitInput' });
    const line = text => ({ entries: [{ segments: [{ text }] }], isLineEnd: true, align: 'center' });
    const lines = [ { entries: [{ segments: [{ text: '', image: { src: 'test-title.svg', width: 1200, height: 280, ypos: 0 } }] }], isLineEnd: true },
      ...Array.from({ length: 10 }, () => line('')), line('旅の始まり · 中日文测试'), line('请选择本回合的行动') ];
    let value = 0;
    for (const size of rowSizes) lines.push({ entries: Array.from({ length: size }, () => {
      const i = value++;
      return { segments: [{ text: i === 0 ? '[0] 初次游玩' : i === 1 ? '[1] 存档再开' : `[${i}] 選択肢 ${i}` }], button: { value: i, isInteger: true, generation } };
    }), isLineEnd: true, align: 'center' });
    window.__onTurn({ protocolVersion: 13, generation, revision: generation, awaiting: { input: { type: 'IntValue' } }, diff: { lineOps: [{ type: 'clear_screen' }, { type: 'append', newLines: lines }], bgColor: '#0c131c' } });
  }, { generation, width, rowSizes });
  const checkFit = async (columns = 1) => {
    await page.waitForFunction(() => {
      const viewport = document.querySelector('.terminal'), content = document.querySelector('.terminal-content');
      return viewport && content && Math.abs(content.getBoundingClientRect().width - viewport.clientWidth) < 1.1;
    });
    const terminal = await page.locator('.terminal').boundingBox(), panel = await page.getByRole('complementary', { name: '当前游戏选项' }).boundingBox();
    const size = page.viewportSize();
    assert.ok(Math.abs(terminal.width - size.width) < 1 && Math.abs(terminal.height - size.height) < 1, 'game fills screen behind overlays');
    assert.ok(Math.abs(terminal.x) < 1 && Math.abs(terminal.y) < 1, 'no reserved top toolbar or side rail');
    assert.ok(panel.x >= 0 && panel.x + panel.width < size.width && panel.y + panel.height < size.height, 'choices overlay the bottom-right game area');
    const expectedWidth = Math.min(size.width * .925, columns * Math.min(170, Math.max(88, size.width * .115)) + (columns - 1) * 4);
    assert.ok(Math.abs(panel.width - expectedWidth) < 1, 'compact buttons expand their overlay only to fit the original row');
    const button = await page.locator('.game-choice').first().evaluate(el => ({ height: el.getBoundingClientRect().height, bg: getComputedStyle(el).backgroundColor, font: getComputedStyle(el).fontSize }));
    assert.ok(button.height >= 32 && button.height < 60, 'compact choice height');
    assert.equal(button.bg, 'rgba(48, 52, 58, 0.55)');
    assert.equal(button.font, '12px');
    for (const original of await page.locator('.terminal .term-btn').evaluateAll(els => els.map(el => ({ bg: getComputedStyle(el).backgroundColor, shadow: getComputedStyle(el).boxShadow })))) {
      assert.equal(original.bg, 'rgba(0, 0, 0, 0)', 'original game choices have no mask');
      assert.equal(original.shadow, 'none', 'original game choices have no outline shadow');
    }
  };
  await showTurn(1);
  await page.locator('.game-choice').first().waitFor();
  await page.evaluate(() => document.fonts.ready);
  await page.waitForFunction(() => Math.abs(document.querySelector('.terminal').getBoundingClientRect().width - innerWidth) < 1);
  await checkFit();
  assert.ok(await page.locator('.terminal .term-btn-touch').count() > 0, 'Android touch targets are exercised');
  const original = page.locator('.terminal .term-btn').first();
  await original.hover();
  assert.equal(await original.evaluate(el => getComputedStyle(el).backgroundColor), 'rgba(0, 0, 0, 0)', 'hover does not restore original button masking');
  const choiceY = async () => page.locator('.terminal .term-btn').evaluateAll(els => els.map(el => el.getBoundingClientRect().y));
  const touchPositions = await choiceY();
  assert.ok(Math.abs((touchPositions[1] - touchPositions[0]) - 28 * 852 / 1280) < 1, 'adjacent original choice rows use the exact game line height');
  await page.getByRole('button', { name: '触控排版', exact: true }).click(); await checkFit();
  const plainPositions = await choiceY();
  assert.deepEqual(plainPositions, touchPositions, 'touch toggle does not add vertical choice spacing');
  await page.getByRole('button', { name: '触控排版', exact: true }).click(); await checkFit();
  await page.screenshot({ path: path.join(shots, 'android-overlay-choices.png') });
  const oldWidth = await page.locator('.terminal-content').evaluate(el => el.getBoundingClientRect().width);
  await page.getByRole('button', { name: '收起选项 · 2', exact: true }).click();
  assert.equal(await page.locator('.choice-list').isVisible(), false);
  assert.ok(Math.abs(await page.locator('.terminal-content').evaluate(el => el.getBoundingClientRect().width) - oldWidth) < 1, 'collapse preserves game scale');
  await page.getByRole('button', { name: '展开选项 · 2', exact: true }).click();
  await checkFit();
  assert.ok(Math.abs(await page.locator('.terminal-content').evaluate(el => el.getBoundingClientRect().width) - oldWidth) < 1, 'expand preserves game scale');
  await page.locator('.game-choice').first().evaluate(el => { el.click(); el.click(); });
  assert.deepEqual(await page.evaluate(() => window.testMessages.filter(m => m.type === 'input' && m.value != null).map(m => m.value)), ['0']);
  assert.equal(await page.locator('.game-choice').first().isDisabled(), true);
  console.log('PASS: exact original game row spacing with touch enabled/disabled, full-screen game, compact translucent overlay choices, unchanged scale on collapse/expand, exact value and duplicate-tap guard');
  await showTurn(2, 40);
  await checkFit();
  await page.locator('.game-choice').last().scrollIntoViewIfNeeded();
  await page.locator('.game-choice').last().click();
  assert.equal(await page.evaluate(() => window.testMessages.filter(m => m.type === 'input' && m.value != null).at(-1).value), '39');
  await showTurn(3);
  for (const viewport of [{ width: 740, height: 360 }, { width: 960, height: 432 }, { width: 640, height: 360 }]) {
    await page.setViewportSize(viewport); await checkFit();
  }
  await page.screenshot({ path: path.join(shots, 'android-overlay-small.png') });
  // Android multi-window can override orientation; the layout must remain usable there too.
  await page.setViewportSize({ width: 393, height: 852 }); await checkFit();
  await page.setViewportSize({ width: 852, height: 393 });
  await showTurn(4, 2, 1920); await checkFit();
  await showTurn(5, 6, 1280, [3, 2, 1]); await checkFit(3);
  const checkRows = async expected => {
    const rows = await page.locator('.choice-row').evaluateAll(els => els.map(el => [...el.querySelectorAll('.game-choice')].map(b => {
      const r = b.getBoundingClientRect(); return { x: r.x, y: r.y, right: r.right, text: b.textContent };
    })));
    assert.deepEqual(rows.map(r => r.length), expected);
    for (const row of rows) for (let i = 1; i < row.length; i++) {
      assert.ok(Math.abs(row[i].y - row[0].y) < 1, 'same source row never wraps its buttons');
      assert.ok(row[i].x >= row[i - 1].right, 'row buttons do not overlap');
    }
  };
  await checkRows([3, 2, 1]);
  await page.screenshot({ path: path.join(shots, 'android-options-rows.png') });
  await page.locator('.game-choice').nth(4).click();
  assert.equal(await page.evaluate(() => window.testMessages.filter(m => m.type === 'input' && m.value != null).at(-1).value), '4');
  await showTurn(6, 15, 1280, [10, 4, 1]);
  await page.setViewportSize({ width: 640, height: 360 }); await checkRows([10, 4, 1]);
  assert.ok(await page.locator('.choice-panel').evaluate(el => el.getBoundingClientRect().right < innerWidth), 'dense rows stay within a small screen');
  await page.screenshot({ path: path.join(shots, 'android-options-dense.png') });
  console.log('PASS: original choices are transparent, source rows 3/2/1 and 10/4/1 preserved, exact multi-column input and no game shrink');
  await page.getByRole('button', { name: '更多操作', exact: true }).click();
  await page.getByText('适应屏幕宽度', { exact: true }).waitFor();
  await page.getByText('返回游戏选择', { exact: true }).click();
  await page.getByRole('button', { name: '返回选择', exact: true }).click();
  await page.evaluate(() => window.__onMessage({ type: 'gameExited' }));
  await page.locator('.choice-panel').waitFor({ state: 'detached' });
  console.log('PASS: 40 scrollable options, 640/740/852/960px widths, portrait fallback, 1920px game and exit cleanup');
  assert.deepEqual(errors, []);
} finally { await browser.close(); }
