// Neutral Japanese fixture from the actual engine/production bridge test; no external API.
import { chromium } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const web = path.resolve(import.meta.dirname, '..'), root = path.resolve(web, '../../..');
const assets = path.join(web, 'dist-maui');
const trace = JSON.parse(await fs.readFile(path.join(root, 'logs/translation-host/bridge-replay.json'), 'utf8'));
const settings = trace.events.find(e => e.kind === 'message' && e.data.type === 'aiSettings').data;
const nativeReply = trace.events.find(e => e.kind === 'message' && e.data.requestId === 'body-and-options').data;
const originals = ['朝の風が庭を吹き抜けた。', '[1] 地図を見る', '[2] 待つ'];
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 852, height: 393 }, isMobile: true, hasTouch: true });
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url()), file = path.resolve(assets, decodeURIComponent(url.pathname).replace(/^\/wwwroot\//, ''));
    if (url.hostname !== 'game.local' || !file.startsWith(assets + path.sep)) return route.abort();
    try { await route.fulfill({ body: await fs.readFile(file), contentType: file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : file.endsWith('.html') ? 'text/html' : 'application/octet-stream' }); }
    catch { await route.fulfill({ status: 404, body: '' }); }
  });
  await page.addInitScript(({ originals, translations }) => {
    localStorage.setItem('app.touchLayout', 'on');
    window.testMessages = []; window.translationMap = Object.fromEntries(originals.map((s, i) => [s, translations[i]]));
    window.translationMap['雨が降ってきた。'] = '开始下雨了。';
    window.chrome = { webview: { postMessage: raw => {
      const message = JSON.parse(raw); window.testMessages.push(message);
      if (message.type === 'aiTranslate') setTimeout(() => window.__onMessage({ type: 'aiTranslation', requestId: message.requestId,
        translations: window.failTranslation ? [] : message.texts.map(s => window.translationMap[s] ?? s), error: window.failTranslation ? '测试断网，保留原文' : '' }), 30);
    } } };
  }, { originals, translations: nativeReply.translations });
  await page.goto('https://game.local/wwwroot/index.html');
  await page.evaluate(s => {
    window.__onMessage({ type: 'layout', windowWidth: 760, fontSize: 18, lineHeight: 19, state: 'WaitInput' });
    window.__onTurn({ protocolVersion: 13, generation: s.generation, revision: s.revision, awaiting: s.awaiting,
      diff: { lineOps: [{ type: 'clear_screen' }, { type: 'append', newLines: s.lines }], bgColor: s.bgColor, bgImages: s.bgImages } });
  }, trace.initial);
  await page.locator('.game-choice').first().waitFor();
  await page.evaluate(async () => { await document.fonts.ready; await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))); });
  // The game-page enter animation scales every row briefly; measure after it settles.
  await page.waitForTimeout(500);
  const originalHeights = await page.locator('.terminal .term-line').evaluateAll(elements => elements.map(e => e.getBoundingClientRect().height));
  await page.evaluate(s => window.__onMessage(s), settings);
  await page.waitForFunction(() => document.querySelector('.terminal').textContent.includes('清晨的风吹拂着庭院。'));
  assert.deepEqual(await page.locator('.terminal .term-btn').allTextContents(), ['[1] 查看地图', '[2] 等待']);
  assert.deepEqual(await page.locator('.game-choice').allTextContents(), ['[1] 查看地图', '[2] 等待']);
  const translatedHeights = await page.locator('.terminal .term-line').evaluateAll(elements => elements.map(e => e.getBoundingClientRect().height));
  assert.equal(translatedHeights.length, originalHeights.length);
  assert.ok(translatedHeights.every((h, i) => Math.abs(h - originalHeights[i]) < .1), 'translation preserves game row heights: '+JSON.stringify({ originalHeights, translatedHeights }));
  assert.equal(await page.evaluate(() => window.testMessages.some(m => m.type === 'input' && m.value != null)), false);
  await page.screenshot({ path: path.join(root, 'tests/screenshots/android-translation.png') });
  await page.locator('.ai-launch').click();
  await page.getByRole('button', { name: '显示原文', exact: true }).click();
  await page.getByRole('button', { name: '关闭', exact: true }).click();
  assert.deepEqual(await page.locator('.game-choice').allTextContents(), originals.slice(1));
  const calls = await page.evaluate(() => window.testMessages.filter(m => m.type === 'aiTranslate').length);
  await page.locator('.ai-launch').click(); await page.getByRole('button', { name: '显示译文', exact: true }).click(); await page.getByRole('button', { name: '关闭', exact: true }).click();
  assert.deepEqual(await page.locator('.game-choice').allTextContents(), ['[1] 查看地图', '[2] 等待']);
  assert.equal(await page.evaluate(() => window.testMessages.filter(m => m.type === 'aiTranslate').length), calls);
  await page.evaluate(s => {
    window.failTranslation = true;
    const original = s.lines.find(l => l.entries.some(e => e.segments.some(seg => seg.text === '朝の風が庭を吹き抜けた。')));
    const line = JSON.parse(JSON.stringify(original));
    line.entries = [{ segments: [{ text: '雨が降ってきた。' }] }];
    window.__onTurn({ protocolVersion: 13, generation: s.generation, revision: s.revision + 1, awaiting: s.awaiting, diff: { lineOps: [{ type: 'append', newLines: [line] }] } });
  }, trace.initial);
  await page.waitForFunction(() => window.testMessages.some(m => m.texts?.includes('雨が降ってきた。')));
  await page.locator('.ai-launch').click(); await page.getByText('翻译：测试断网，保留原文', { exact: true }).waitFor();
  await page.getByRole('button', { name: '关闭', exact: true }).click(); assert.ok((await page.locator('.terminal').textContent()).includes('雨が降ってきた。'));
  await page.evaluate(() => { window.failTranslation = false; });
  await page.locator('.ai-launch').click(); await page.getByRole('button', { name: '重试翻译', exact: true }).click(); await page.getByRole('button', { name: '关闭', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('.terminal').textContent.includes('开始下雨了。'));
  await page.locator('.game-choice').first().click();
  assert.deepEqual(await page.evaluate(() => window.testMessages.filter(m => m.type === 'input' && m.value != null).map(m => m.value)), ['1']);
  await page.evaluate(() => window.__onMessage({ type: 'gameExited' }));
  assert.deepEqual(errors, []);
  console.log('PASS: actual Japanese fixture body and original/overlay options translated; unchanged row spacing, original toggle/cache, offline fallback/retry, no automatic input, translated option still submits exact value 1, exit clears view');
} finally { await browser.close(); }
