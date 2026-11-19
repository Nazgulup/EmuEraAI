import { chromium } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const web = path.resolve(import.meta.dirname, '..'), root = path.resolve(web, '../../..'), assets = path.join(web, 'dist-maui');
const trace = JSON.parse(await fs.readFile(path.join(root, 'logs/function-document/bridge-replay.json'), 'utf8'));
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
  await page.locator('.terminal').waitFor();
  for (const event of trace.events) {
    if (event.kind === 'turn' && event.data.revision <= trace.initial.revision) continue;
    await page.evaluate(event => event.kind === 'turn' ? window.__onTurn(event.data) : window.__onMessage(event.data), event);
  }
  await page.locator('.ai-launch').click();
  const panel = page.getByRole('dialog', { name: 'AI 口上' });
  const openDocument = panel.getByRole('button', { name: '函数清单 / 编辑选择', exact: true });
  if (await openDocument.count()) await openDocument.click();
  await panel.getByLabel('优先 AI KOJO_MISC', { exact: true }).waitFor();
  assert.equal(await panel.getByLabel('优先 AI KOJO_MISC', { exact: true }).isChecked(), false);
  await panel.getByLabel('优先 AI KOJO_MISC', { exact: true }).check();
  await panel.getByLabel('备注 KOJO_MISC', { exact: true }).fill('已确认可重复的日常文本');
  await panel.getByRole('button', { name: '保存函数选择（1）', exact: true }).click();
  const message = await page.evaluate(() => window.testMessages.at(-1));
  const nativePage = trace.events.find(e => e.kind === 'message' && e.data.type === 'aiStoryDocument').data;
  assert.equal(message.type, 'aiStoryDocumentSave'); assert.equal(message.documentId, nativePage.documentId);
  assert.deepEqual(message.edits, [{ key: nativePage.rows[0].key, action: 'allow', note: '已确认可重复的日常文本' }]);
  await page.screenshot({ path: path.join(root, 'tests/screenshots/android-function-document.png') });
  await panel.getByRole('button', { name: '导出函数文档', exact: true }).click();
  assert.equal(await page.evaluate(() => window.testMessages.at(-1).type), 'aiStoryDocumentExport');
  assert.equal(await page.evaluate(() => window.testMessages.some(m => m.type === 'input' && (m.value != null || m.keys?.some(k => k.down)))), false);
  assert.deepEqual(errors, []);
  console.log('PASS: actual generated function page renders, unknown stays unchecked, checkbox/note edits save with current document identity, export command is available, no game option/key-down or browser errors');
} finally { await browser.close(); }
