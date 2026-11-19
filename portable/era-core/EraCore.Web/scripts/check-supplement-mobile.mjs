// Replay a real lazy engine / production bridge run using neutral controlled replies.
import { chromium } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const web = path.resolve(import.meta.dirname, '..'), root = path.resolve(web, '../../..');
const assets = path.join(web, 'dist-maui');
const trace = JSON.parse(await fs.readFile(path.join(root, 'logs/supplement-host/bridge-replay.json'), 'utf8'));
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 852, height: 393 }, isMobile: true, hasTouch: true });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url()), file = path.resolve(assets, decodeURIComponent(url.pathname).replace(/^\/wwwroot\//, ''));
    if (url.hostname !== 'game.local' || !file.startsWith(assets + path.sep)) return route.abort();
    try { await route.fulfill({ body: await fs.readFile(file), contentType: file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : file.endsWith('.html') ? 'text/html' : 'application/octet-stream' }); }
    catch { await route.fulfill({ status: 404, body: '' }); }
  });
  await page.addInitScript(() => {
    localStorage.setItem('app.touchLayout', 'on');
    window.testMessages = []; window.chrome = { webview: { postMessage: raw => window.testMessages.push(JSON.parse(raw)) } };
  });
  await page.goto('https://game.local/wwwroot/index.html');
  await page.evaluate(s => {
    window.__onMessage({ type: 'layout', windowWidth: 760, fontSize: 18, lineHeight: 19, state: 'WaitInput' });
    window.__onTurn({ protocolVersion: 13, generation: s.generation, revision: s.revision, awaiting: s.awaiting,
      diff: { lineOps: [{ type: 'clear_screen' }, { type: 'append', newLines: s.lines }], bgColor: s.bgColor, bgImages: s.bgImages } });
  }, trace.initial);
  let revision = trace.initial.revision, state = null, pending = 0, accepted = 0, restored = 0;
  const replies = trace.events.filter(e => e.kind === 'message' && e.data.type === 'aiState' && e.data.mode === 'ai').map(e => e.data.text);
  for (const event of trace.events) {
    if (event.kind === 'turn' && event.data.revision <= trace.initial.revision) continue;
    await page.evaluate(event => event.kind === 'turn' ? window.__onTurn(event.data) : window.__onMessage(event.data), event);
    if (event.kind === 'turn') revision = event.data.revision;
    if (event.kind === 'message' && event.data.type === 'aiState') state = event.data;
    if (!state || state.displayRevision !== revision) continue;
    await page.waitForFunction(() => document.querySelectorAll('.terminal .term-btn').length === 2);
    if (state.mode === 'pending') {
      await page.locator('.terminal').getByText('【AI 口上生成中…】', { exact: true }).waitFor(); pending++;
    } else if (state.mode === 'ai') {
      await page.waitForFunction(text => document.querySelector('.terminal').textContent.includes(text), state.text);
      const body = await page.locator('.terminal').textContent();
      assert.ok(body.indexOf(state.text) < body.indexOf('[1]'), 'supplement appears before the menu');
      assert.equal(body.split(state.text).length - 1, 1, 'regeneration has one paragraph, not duplicate insertions');
      if (++accepted === 1) await page.screenshot({ path: path.join(root, 'tests/screenshots/android-supplement.png') });
    } else if (state.mode === 'original' || state.supplementEnabled === false) {
      await page.waitForFunction(replies => replies.every(text => !document.querySelector('.terminal').textContent.includes(text)), replies);
      restored++;
    }
    assert.equal((await page.locator('.terminal').textContent()).includes('LATE_REPLY_MUST_NOT_APPEAR'), false);
    assert.deepEqual(await page.locator('.terminal .term-btn').allTextContents(), ['[1] 等待', '[2] 查看地图']);
  }
  assert.ok(pending >= 2 && accepted >= 3 && restored >= 2, JSON.stringify({ pending, accepted, restored }));
  assert.equal(await page.getByRole('dialog', { name: 'AI 口上' }).count(), 0, 'automatic supplement needs no open AI panel');
  assert.equal(await page.evaluate(() => window.testMessages.some(m => m.type === 'input' && m.value != null)), false);
  await page.locator('.game-choice').first().click();
  assert.deepEqual(await page.evaluate(() => window.testMessages.filter(m => m.type === 'input' && m.value != null).map(m => m.value)), ['1']);
  assert.deepEqual(errors, []);
  console.log(`PASS: actual no-PRINT engine trace, ${pending} pending / ${accepted} accepted / ${restored} restored checkpoints, original options preserved, one paragraph per event, late replies rejected, exact option input`);
} finally { await browser.close(); }
