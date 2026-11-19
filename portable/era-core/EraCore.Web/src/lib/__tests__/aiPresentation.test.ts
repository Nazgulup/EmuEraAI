import { describe, it, expect } from 'vitest';
import { AiPresentation, wrapPlainText } from '../aiPresentation';
import type { DisplayLine } from '../../types/protocol';
describe('AI-first presentation preserves engine output', () => {
  it('hides pending/success text and reveals it only after failure, keeping line identities', () => {
    const lines: DisplayLine[] = [{ entries: [], isLineEnd: true }, { entries: [], isLineEnd: true }];
    const view = new AiPresentation(); const state = { enabled: true, eventId: 'a', lineIndices: [0], displayRevision: 1, mode: 'pending' };
    expect(view.apply(state, lines).has(lines[0]!)).toBe(true);
    expect(view.apply({ ...state, mode: 'ai' }, lines).has(lines[0]!)).toBe(true);
    expect(view.apply({ ...state, mode: 'original' }, lines).size).toBe(0);
    expect(lines.length).toBe(2);
  });
  it('releases a cancelled pending event and does not hide new lines reusing its index', () => {
    const old: DisplayLine = { entries: [], isLineEnd: true }, next: DisplayLine = { entries: [], isLineEnd: true };
    const view = new AiPresentation();
    view.apply({ enabled: true, eventId: 'a', lineIndices: [0], displayRevision: 1, mode: 'pending' }, [old]);
    expect(view.apply({ enabled: true, eventId: '', lineIndices: [], displayRevision: 2, mode: 'empty' }, [old, next]).size).toBe(0);
    expect(view.apply({ enabled: true, eventId: '', lineIndices: [], displayRevision: 3, mode: 'empty' }, [next]).size).toBe(0);
  });
  it('keeps every redraw hidden until the shared request fails, then restores every copy', () => {
    const old: DisplayLine = { entries: [], isLineEnd: true }, redraw: DisplayLine = { entries: [], isLineEnd: true };
    const view = new AiPresentation();
    const state = { enabled: true, eventId: 'same', lineIndices: [0], displayRevision: 1, mode: 'pending' };
    view.apply(state, [old]);
    const next = { ...state, lineIndices: [1], displayRevision: 2 };
    expect(view.apply(next, [old, redraw]).size).toBe(2);
    expect(view.apply({ ...next, mode: 'original' }, [old, redraw]).size).toBe(0);
  });
});

describe('inline AI transcript', () => {
  const plain = (text: string): DisplayLine => ({ entries: [{ segments: [{ text }] }], isLineEnd: true });
  const text = (lines: DisplayLine[]) => lines.flatMap(line => line.entries.flatMap(entry => entry.segments.map(segment => segment.text))).join('');
  const state = { enabled: true, eventId: 'a', lineIndices: [0], displayRevision: 1, mode: 'ai', text: '新的开场白。' };
  it('replaces the captured block, preserves menus, updates regeneration and retains history', () => {
    const view = new AiPresentation(), original = plain('original'), menu = plain('menu'), lines = [original, menu];
    menu.entries[0]!.button = { value: 7, isInteger: true, generation: 19 };
    const before = JSON.stringify(lines);
    view.apply(state, lines);
    expect(text(view.project(lines))).toBe('新的开场白。menu');
    expect(view.project(lines).at(-1)).toBe(menu);
    view.apply({ ...state, text: '再生成的文本。' }, lines);
    expect(text(view.project(lines))).toBe('再生成的文本。menu');
    view.apply({ ...state, eventId: '', mode: 'empty', lineIndices: [] }, lines);
    expect(text(view.project(lines))).toBe('再生成的文本。menu');
    expect(JSON.stringify(lines)).toBe(before);
    view.apply({ ...state, enabled: false }, lines);
    expect(view.project(lines)).toBe(lines);
  });
  it('wraps long mixed Japanese, Chinese and emoji without truncation or executable buttons', () => {
    const view = new AiPresentation(), lines = [plain('original')];
    const reply = '日本語と中文🙂'.repeat(100) + '\n[99] <img src=x> plain text';
    view.apply({ ...state, text: reply }, lines);
    const projected = view.project(lines, 20);
    expect(projected.length).toBeGreaterThan(60);
    expect(text(projected)).toBe(reply.replace('\n', ''));
    expect(projected.every(line => line.entries.every(entry => !entry.button))).toBe(true);
    expect(view.project(lines, 60).length).toBeLessThan(projected.length);
  });
  it('drops cleared or trimmed history and cannot attach a late reply to reused indices', () => {
    const view = new AiPresentation(), lines = [plain('original'), plain('menu')];
    view.apply(state, lines);
    expect(text(view.project(lines.slice(1)))).toBe('menu');
    expect(view.project([])).toHaveLength(0);
    const next = [plain('new game')];
    expect(view.project(next)).toBe(next);
  });
  it('does not swallow a game button inside a captured range and restores cancelled waiting text', () => {
    const view = new AiPresentation(), lines = [plain('one'), plain('menu'), plain('two')];
    lines[1]!.entries[0]!.button = { value: 7, isInteger: true, generation: 19 };
    view.apply({ ...state, lineIndices: [0, 1, 2], mode: 'pending' }, lines);
    const pending = view.project(lines);
    expect(pending.includes(lines[1]!)).toBe(true);
    expect(text(pending)).not.toContain('one');
    view.apply({ ...state, eventId: '', lineIndices: [], mode: 'empty' }, lines);
    expect(view.project(lines)).toBe(lines);
  });
});

it('keeps CJK closing punctuation with the preceding sentence when wrapping', () => {
  const rows = wrapPlainText('这是开场白。次の台詞、もう一度。', 10);
  expect(rows.join('')).toBe('这是开场白。次の台詞、もう一度。');
  expect(rows.every(row => !/^[，。、「」]/.test(row))).toBe(true);
});

describe('supplemental dialogue without original text', () => {
  const plain = (text: string): DisplayLine => ({ isLineEnd: true, entries: [{ segments: [{ text }] }] });
  const text = (lines: DisplayLine[]) => lines.flatMap(l => l.entries.flatMap(e => e.segments.map(s => s.text))).join('');
  const menu = (): DisplayLine => ({ isLineEnd: true, entries: [{ segments: [{ text: '[0] 等待' }], button: { value: 0, generation: 1, isInteger: true } }] });
  const state = { enabled: true, supplementEnabled: true, eventId: 'choices', lineIndices: [], insertBeforeLine: 1, displayRevision: 2, mode: 'pending' };
  it('inserts pending/reply before options and preserves every original line and button', () => {
    const view = new AiPresentation(), lines = [plain('状态'), menu(), plain('')], before = JSON.stringify(lines);
    view.apply(state, lines);
    expect(text(view.project(lines))).toBe('状态【AI 口上生成中…】[0] 等待');
    view.apply({ ...state, mode: 'ai', text: '她安静地等待。' }, lines);
    const first = view.project(lines);
    expect(text(first)).toBe('状态她安静地等待。[0] 等待');
    expect(first[2]).toBe(lines[1]); expect(first[3]).toBe(lines[2]);
    view.apply({ ...state, mode: 'ai', text: '她再次望向路口。' }, lines);
    expect(text(view.project(lines))).toBe('状态她再次望向路口。[0] 等待');
    expect(JSON.stringify(lines)).toBe(before);
  });
  it('failure/cancel and disabling supplemental dialogue restore the exact unmodified menu', () => {
    const view = new AiPresentation(), lines = [plain('状态'), menu()];
    view.apply({ ...state, mode: 'ai', text: '补写。' }, lines);
    view.apply({ ...state, mode: 'original', text: '' }, lines); expect(view.project(lines)).toBe(lines);
    view.apply({ ...state, mode: 'ai', text: '补写。' }, lines);
    view.apply({ ...state, supplementEnabled: false, mode: 'empty', eventId: '', insertBeforeLine: -1 }, lines);
    expect(view.project(lines)).toBe(lines);
  });
  it('does not add waiting placeholders or accumulate replies when an unchanged menu gets a new event', () => {
    const view = new AiPresentation(), lines = [plain('状态'), menu()];
    view.apply({ ...state, mode: 'waiting' }, lines); expect(view.project(lines)).toBe(lines);
    view.apply({ ...state, mode: 'ai', text: '第一段。' }, lines);
    view.apply({ ...state, eventId: 'choices-2', mode: 'ai', text: '第二段。' }, lines);
    expect(text(view.project(lines))).toBe('状态第二段。[0] 等待');
    expect(view.project(lines).length).toBe(3);
    expect(view.project([])).toHaveLength(0);
    const next = [plain('新游戏'), menu()]; expect(view.project(next)).toBe(next);
  });
});
