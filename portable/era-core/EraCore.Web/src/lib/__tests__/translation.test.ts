import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { GameTranslation } from '../translation';

describe('game text translation', () => {
  let tr: GameTranslation;
  let sent: { type: string; requestId: string; texts: string[] }[];
  const settings = { TranslationEnabled: true, TranslationLanguage: 'zh-CN', TranslationTokens: 2048, Model: 'controlled' };
  const flush = () => vi.advanceTimersByTime(60);
  beforeEach(() => { vi.useFakeTimers(); tr = new GameTranslation(); sent = []; tr.configure(settings, 0, json => sent.push(JSON.parse(json))); });
  afterEach(() => { tr.reset(); vi.useRealTimers(); });

  it('deduplicates body/options, preserves source spacing, and never sends input', () => {
    expect(tr.text('  [1] 地図を見る  ')).toBe('  [1] 地図を見る  ');
    tr.text('[1] 地図を見る'); tr.text('······'); tr.text('100'); flush();
    expect(sent).toHaveLength(1); expect(sent[0]!.texts).toEqual(['[1] 地図を見る']);
    expect(sent[0]!.type).toBe('aiTranslate');
    tr.receive({ requestId: sent[0]!.requestId, translations: ['[1] 查看地图'] });
    expect(tr.text('  [1] 地図を見る  ')).toBe('  [1] 查看地图  ');
    tr.text('[1] 地図を見る'); flush(); expect(sent).toHaveLength(1);
    tr.toggleOriginal(); expect(tr.text('[1] 地図を見る')).toBe('[1] 地図を見る');
    tr.toggleOriginal(); expect(tr.text('[1] 地図を見る')).toBe('[1] 查看地图');
  });

  it('serializes bounded batches and caches every response', () => {
    for (let i = 0; i < 36; i++) tr.text(`地図 ${i}`);
    flush(); expect(sent[0]!.texts).toHaveLength(16);
    for (let i = 0; i < 3; i++) {
      const request = sent[i]!;
      expect(request.texts.join('').length).toBeLessThanOrEqual(1200);
      tr.receive({ requestId: request.requestId, translations: request.texts.map(t => t.replace('地図', '地图')) }); flush();
    }
    expect(sent).toHaveLength(3); expect(tr.text('地図 35')).toBe('地图 35');
  });

  it('keeps originals after failure and requires retry for failed visible text', () => {
    tr.text('朝の風'); flush(); tr.text('旅人');
    tr.receive({ requestId: sent[0]!.requestId, error: 'HTTP 503' });
    for (let i = 0; i < 5; i++) { expect(tr.text('朝の風')).toBe('朝の風'); tr.text('旅人'); flush(); }
    expect(sent).toHaveLength(1); expect(tr.state.status).toContain('503');
    tr.retry(); tr.text('朝の風'); flush(); expect(sent).toHaveLength(2);
    tr.receive({ requestId: sent[1]!.requestId, translations: ['清晨的风'] });
    expect(tr.text('朝の風')).toBe('清晨的风');
  });

  it('rejects stale replies after reset, settings change, or retry', () => {
    tr.text('朝の風'); flush(); const old = sent[0]!.requestId;
    tr.configure({ ...settings, TranslationLanguage: 'en' }, 1, json => sent.push(JSON.parse(json)));
    tr.text('朝の風'); flush(); tr.receive({ requestId: old, translations: ['错误语言'] });
    expect(tr.text('朝の風')).toBe('朝の風');
    const pending = sent[1]!.requestId;
    tr.retry(); tr.receive({ requestId: pending, translations: ['过期'] });
    expect(tr.text('朝の風')).toBe('朝の風'); flush();
    tr.reset(); tr.receive({ requestId: sent[2]!.requestId, translations: ['旧游戏'] });
    expect(tr.state.enabled).toBe(false); expect(tr.text('朝の風')).toBe('朝の風');
  });

  it('keeps cache when reopening settings but invalidates it after save', () => {
    tr.text('旅人'); flush(); tr.receive({ requestId: sent[0]!.requestId, translations: ['旅客'] });
    tr.configure(settings, 0, json => sent.push(JSON.parse(json)));
    expect(tr.text('旅人')).toBe('旅客'); flush(); expect(sent).toHaveLength(1);
    tr.configure(settings, 1, json => sent.push(JSON.parse(json)));
    expect(tr.text('旅人')).toBe('旅人'); flush(); expect(sent).toHaveLength(2);
  });

  it('timeouts and malformed responses restore source without automatic repeated charges', () => {
    tr.text('旅人'); flush(); vi.advanceTimersByTime(22000);
    expect(tr.state.busy).toBe(false); expect(tr.state.status).toContain('超时');
    tr.text('旅人'); flush(); expect(sent).toHaveLength(1);
    tr.retry(); tr.text('旅人'); flush();
    tr.receive({ requestId: sent[1]!.requestId, translations: [] });
    expect(tr.text('旅人')).toBe('旅人'); expect(tr.state.status).toContain('不完整');
  });
});
