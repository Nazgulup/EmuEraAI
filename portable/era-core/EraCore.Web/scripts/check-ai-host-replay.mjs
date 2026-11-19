// Replay the actual engine + production AI bridge trace, not a hand-written aiState fixture.
import { chromium } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const web = path.resolve(import.meta.dirname, '..'), assets = path.join(web, 'dist-maui');
const root = path.resolve(web, '../../..');
const trace = JSON.parse(await fs.readFile(path.join(root, 'logs/android-ai-host/bridge-replay.json'), 'utf8'));
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 852, height: 393 }, isMobile: true, hasTouch: true });
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    const file = path.resolve(assets, decodeURIComponent(url.pathname).replace(/^\/wwwroot\//, ''));
    if (url.hostname !== 'game.local' || !file.startsWith(assets + path.sep)) return route.abort();
    try { await route.fulfill({ body: await fs.readFile(file), contentType: file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : file.endsWith('.html') ? 'text/html' : 'application/octet-stream' }); }
    catch { await route.fulfill({ status: 404, body: '' }); }
  });
  await page.addInitScript(() => { window.testMessages = []; window.chrome = { webview: { postMessage: raw => window.testMessages.push(JSON.parse(raw)) } }; });
  await page.goto('https://game.local/wwwroot/index.html');
  await page.evaluate(s => {
    window.__onMessage({ type: 'layout', windowWidth: 760, fontSize: 18, lineHeight: 19, state: 'WaitInput' });
    window.__onTurn({ protocolVersion: 13, generation: s.generation, revision: s.revision, awaiting: s.awaiting,
      diff: { lineOps: [{ type: 'clear_screen' }, { type: 'append', newLines: s.lines }], bgColor: s.bgColor, bgImages: s.bgImages } });
  }, trace.initial);
  let revision = trace.initial.revision, state = null, pending = 0, accepted = 0, fallback = 0;
  for (const event of trace.events) {
    if (event.kind === 'turn' && event.data.revision <= trace.initial.revision) continue;
    await page.evaluate(event => event.kind === 'turn' ? window.__onTurn(event.data) : window.__onMessage(event.data), event);
    if (event.kind === 'turn') revision = event.data.revision;
    if (event.kind === 'message' && event.data.type === 'aiState') state = event.data;
    if (!state || state.displayRevision !== revision) continue;
    if (state.mode === 'pending') {
      await page.locator('.terminal').getByText('【AI 口上生成中…】', { exact: true }).waitFor(); pending++;
    } else if (state.mode === 'ai') {
      await page.waitForFunction(text => document.querySelector('.terminal').textContent.includes(text), state.text);
      accepted++;
    } else if (state.mode === 'original') {
      await page.waitForFunction(text => document.querySelector('.terminal').textContent.includes(text), state.text.split('\n')[0]);
      fallback++;
    }
  }
  assert.ok(pending >= 1 && accepted >= 3 && fallback >= 1, { pending, accepted, fallback });
  assert.equal(await page.getByRole('dialog', { name: 'AI 口上' }).count(), 0);
  assert.equal(await page.evaluate(() => window.testMessages.some(m => m.type === 'aiGet' || m.type === 'aiGenerate')), false);
  await page.screenshot({ path: path.join(root, 'tests/screenshots/android-ai-auto.png') });
  await page.getByRole('button', { name: 'AI 口上', exact: true }).click();
  assert.equal(await page.getByRole('button', { name: '重新生成', exact: true }).isDisabled(), false);
  assert.deepEqual(errors, []);
  console.log(`PASS: real engine/bridge trace, closed AI panel, automatic main-transcript replacement, ${pending} pending / ${accepted} accepted / ${fallback} fallback checkpoints, regeneration enabled`);
} finally { await browser.close(); }
