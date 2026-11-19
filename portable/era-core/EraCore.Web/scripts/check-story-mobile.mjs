// Replay production native bridge events from the neutral story-policy engine test.
import { chromium } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const web = path.resolve(import.meta.dirname, '..'), root = path.resolve(web, '../../..');
const assets = path.join(web, 'dist-maui');
const trace = JSON.parse(await fs.readFile(path.join(root, 'logs/story-host/bridge-replay.json'), 'utf8'));
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
  await page.locator('.terminal').waitFor();
  let revision = trace.initial.revision, state = null, protectedChecks = 0, acceptedChecks = 0, panelChecked = false;
  const kinds = new Set();
  for (const event of trace.events) {
    if (event.kind === 'turn' && event.data.revision <= trace.initial.revision) continue;
    await page.evaluate(event => event.kind === 'turn' ? window.__onTurn(event.data) : window.__onMessage(event.data), event);
    if (event.kind === 'turn') revision = event.data.revision;
    if (event.kind === 'message' && event.data.type === 'aiState') state = event.data;
    if (!state || state.displayRevision !== revision || !state.storyReady) continue;
    if (!state.canGenerate) {
      assert.equal(state.mode, 'original'); kinds.add(state.storyKind);
      if (state.text.trim()) await page.waitForFunction(text => document.querySelector('.terminal')?.textContent.replace(/\s+/g, '').includes(text.replace(/\s+/g, '')), state.text);
      protectedChecks++;
      if (state.storyKind === 'story' && !panelChecked) {
        await page.locator('.ai-launch').click();
        const panel = page.getByRole('dialog', { name: 'AI 口上' });
        assert.equal(await panel.getByRole('button', { name: '重新生成', exact: true }).isDisabled(), true);
        assert.ok((await panel.getByLabel('剧情判断').textContent()).includes(state.storyReason));
        await panel.getByRole('button', { name: '仅本段允许', exact: true }).click();
        const sent = await page.evaluate(() => window.testMessages.at(-1));
        assert.deepEqual(sent, { type: 'aiStoryRule', scope: 'event', action: 'allow', eventId: state.eventId, storyTurn: state.storyTurn });
        await page.screenshot({ path: path.join(root, 'tests/screenshots/android-story-protection.png') });
        await panel.getByRole('button', { name: '关闭', exact: true }).click(); panelChecked = true;
      }
    } else if (state.mode === 'ai') {
      await page.waitForFunction(text => document.querySelector('.terminal')?.textContent.includes(text), state.text); acceptedChecks++;
    }
  }
  assert.ok(protectedChecks >= 6 && acceptedChecks >= 3 && panelChecked, JSON.stringify({ protectedChecks, acceptedChecks, panelChecked }));
  assert.ok(kinds.has('story') && kinds.has('unknown') && kinds.has('manual-deny'));
  const gameInputs = await page.evaluate(() => window.testMessages.filter(m => m.type === 'input'));
  assert.equal(gameInputs.some(m => m.value != null), false, JSON.stringify(gameInputs));
  assert.equal(gameInputs.some(m => m.keys?.some(k => k.down)), false, JSON.stringify(gameInputs));
  assert.deepEqual(errors, []);
  console.log(`PASS: production story trace: ${protectedChecks} protected / ${acceptedChecks} AI checkpoints; original text retained, story/unknown/manual-deny displayed, explicit event override bound to event+turn, no game option submission or key-down, no browser errors`);
} finally { await browser.close(); }
