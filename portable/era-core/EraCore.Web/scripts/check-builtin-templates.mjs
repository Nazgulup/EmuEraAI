import { chromium } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const web = path.resolve(import.meta.dirname, '..'), root = path.resolve(web, '../../..'), assets = path.join(web, 'dist-maui');
const trace = JSON.parse(await fs.readFile(path.join(root, 'logs/builtin-host/bridge-replay.json'), 'utf8'));
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
    localStorage.setItem('app.touchLayout', 'on'); window.testMessages = [];
    window.chrome = { webview: { postMessage: raw => window.testMessages.push(JSON.parse(raw)) } };
  });
  await page.goto('https://game.local/wwwroot/index.html');
  await page.evaluate(s => {
    window.__onMessage({ type: 'layout', windowWidth: 760, fontSize: 18, lineHeight: 19, state: 'WaitInput' });
    window.__onTurn({ protocolVersion: 13, generation: s.generation, revision: s.revision, awaiting: s.awaiting,
      diff: { lineOps: [{ type: 'clear_screen' }, { type: 'append', newLines: s.lines }], bgColor: s.bgColor, bgImages: s.bgImages } });
  }, trace.initial);
  await page.locator('.terminal').waitFor(); let protectedChecked = false;
  for (const event of trace.events) {
    if (event.kind === 'turn' && event.data.revision <= trace.initial.revision) continue;
    await page.evaluate(event => event.kind === 'turn' ? window.__onTurn(event.data) : window.__onMessage(event.data), event);
    if (!protectedChecked && event.kind === 'message' && event.data.type === 'aiState' && event.data.hasEvent && event.data.storyReady && !event.data.canGenerate && event.data.storyTemplateState === 'matched') {
      await page.locator('.ai-launch').click();
      const panel = page.getByRole('dialog', { name: 'AI 口上' });
      await panel.getByLabel('内置剧情模板').scrollIntoViewIfNeeded();
      assert.ok((await panel.getByLabel('内置剧情模板').textContent()).includes('eraJourney0.92'));
      assert.ok((await panel.getByLabel('内置剧情模板').textContent()).includes('版本 0.92'));
      assert.ok((await panel.getByLabel('剧情判断').textContent()).includes('内置模板'));
      assert.equal(await panel.getByRole('button', { name: '重新生成', exact: true }).isDisabled(), true);
      await page.screenshot({ path: path.join(root, 'tests/screenshots/android-builtin-templates.png') });
      await panel.getByRole('button', { name: '关闭', exact: true }).click(); protectedChecked = true;
    }
  }
  assert.ok(protectedChecked);
  await page.waitForFunction(() => document.querySelector('.terminal')?.textContent.includes('旅人收起地图，等待安排。'));
  const messages = await page.evaluate(() => window.testMessages);
  assert.equal(messages.some(m => m.type === 'aiStoryImport' || (m.type === 'input' && (m.value != null || m.keys?.some(k => k.down)))), false);
  assert.deepEqual(errors, []);
  console.log('PASS: production bridge replay shows automatic built-in name/version match, protection reason and disabled generation; explicit event override still fills game text; no template import or game input required');
} finally { await browser.close(); }
