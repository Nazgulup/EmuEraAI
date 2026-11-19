import { shallowRef } from 'vue';
import type { DisplayLine } from '../types/protocol';
export interface AiPresentationState { mode: string; enabled: boolean; eventId: string; text?: string; lineIndices: number[]; insertBeforeLine?: number; supplementEnabled?: boolean; displayRevision: number }
export const aiPresentationState = shallowRef<AiPresentationState | null>(null);

interface Block { id: string; originals: Set<DisplayLine>; anchor?: DisplayLine; mode: string; text: string; key?: string; layout?: DisplayLine[] }
// Render a separate transcript; the engine's lines, input buttons and revision stay intact.
export class AiPresentation {
  private blocks: Block[] = [];
  private enabled = false;
  private prune(lines: DisplayLine[]) {
    const visible = new Set(lines);
    for (const block of this.blocks) block.originals = new Set([...block.originals].filter(line => visible.has(line)));
    this.blocks = this.blocks.filter(block => block.anchor ? visible.has(block.anchor) : block.originals.size > 0);
  }
  apply(state: AiPresentationState, lines: DisplayLine[]): Set<DisplayLine> {
    this.prune(lines);
    this.enabled = state.enabled;
    if (state.supplementEnabled === false) this.blocks = this.blocks.filter(block => !block.anchor);
    const registered = new Set(this.blocks.flatMap(block => [...block.originals]));
    const captured = new Set(state.eventId ? state.lineIndices.map(i => lines[i]).filter((line): line is DisplayLine => !!line && !line.entries.some(entry => entry.button)) : []);
    let current: Block | undefined;
    for (const line of lines) {
      if (!captured.has(line) || registered.has(line)) continue;
      if (!current) { current = { id: state.eventId, originals: new Set(), mode: state.mode, text: state.text ?? '' }; this.blocks.push(current); }
      current.originals.add(line);
    }
    const anchor = Number.isInteger(state.insertBeforeLine) && state.insertBeforeLine! >= 0 ? lines[state.insertBeforeLine!] : undefined;
    if (state.supplementEnabled !== false && state.eventId && !captured.size && anchor?.entries.some(entry => entry.button)) {
      // Repeated states/regen replace the same inserted paragraph; a new event using
      // an unchanged menu must not stack multiple paragraphs above that menu.
      this.blocks = this.blocks.filter(block => block.anchor !== anchor || block.id === state.eventId);
      if (!this.blocks.some(block => block.id === state.eventId && block.anchor === anchor))
        this.blocks.push({ id: state.eventId, originals: new Set(), anchor, mode: state.mode, text: state.text ?? '' });
    }
    for (const block of this.blocks) {
      if (block.id === state.eventId) { block.mode = state.mode; block.text = state.text ?? ''; }
      else if (block.mode === 'pending' || block.mode === 'waiting') block.mode = 'original';
    }
    return new Set(this.active().flatMap(block => [...block.originals]));
  }
  private active() { return this.enabled ? this.blocks.filter(block => block.mode !== 'original' && block.mode !== 'empty' && !(block.anchor && block.mode === 'waiting')) : []; }
  project(lines: DisplayLine[], columns = 80): DisplayLine[] {
    this.prune(lines);
    const replacements = new Map<DisplayLine, Block>();
    const insertions = new Map<DisplayLine, Block>();
    for (const block of this.active()) {
      if (block.anchor) insertions.set(block.anchor, block);
      for (const line of block.originals) replacements.set(line, block);
    }
    if (!replacements.size && !insertions.size) return lines;
    const output: DisplayLine[] = [], emitted = new Set<Block>();
    const append = (block: Block) => {
      if (emitted.has(block)) return;
      emitted.add(block);
      const text = block.mode === 'ai' ? block.text : block.mode === 'pending' ? '【AI 口上生成中…】' : '【等待 AI 口上；可打开 AI 口上菜单生成或恢复原文】';
      const key = `${columns}:${text}`;
      if (block.key !== key) {
        block.layout = wrapPlainText(text, columns).map(text => ({ entries: [{ segments: [{ text }] }], isLineEnd: true, align: 'left' }));
        block.key = key;
      }
      output.push(...block.layout!);
    };
    for (const line of lines) {
      const insertion = insertions.get(line);
      if (insertion) append(insertion);
      const block = replacements.get(line);
      if (block) append(block);
      else output.push(line);
    }
    return output;
  }
}

// Physical view rows keep virtual scrolling and following menu hit areas in sync.
// No HTML, button syntax or script evaluation; Unicode code points remain intact.
export function wrapPlainText(text: string, columns: number): string[] {
  const limit = Math.max(2, Math.floor(columns)), rows: string[] = [];
  for (const paragraph of text.replace(/\r\n?/g, '\n').split('\n')) {
    let row = '', width = 0;
    for (const char of paragraph) {
      const cells = /[\u0300-\u036f\u200d\ufe00-\ufe0f]/.test(char) ? 0 : char.codePointAt(0)! > 0xff ? 2 : 1;
      const closing = /[，。！？、；：）》〉」』】,.!?;:]/.test(char) && width <= limit + 2;
      if (width + cells > limit && row && !closing) { rows.push(row); row = ''; width = 0; }
      row += char; width += cells;
    }
    rows.push(row);
  }
  return rows;
}
