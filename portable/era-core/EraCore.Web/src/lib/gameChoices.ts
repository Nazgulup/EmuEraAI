import type { AwaitInfo, ButtonRef, DisplayLine } from '../types/protocol';
import { isLiveButton } from './inputRouting';

export interface GameChoice { key: string; row: number; label: string; button: ButtonRef }

/** Only interpreter-supplied buttons are choices; never infer actions from dialogue text. */
export function collectGameChoices(lines: DisplayLine[], awaiting: AwaitInfo, generation: number): GameChoice[] {
  const choices: GameChoice[] = [];
  for (const [row, line] of lines.entries()) for (const [column, entry] of line.entries.entries()) {
    const button = entry.button;
    if (!button || !isLiveButton({ awaiting, buttonGeneration: button.generation, currentTurnGeneration: generation })) continue;
    // Image-map actions depend on the tapped pixel, not the surrounding button value.
    if (entry.segments.some(s => s.image?.srcm)) continue;
    // Keep every actual button and its display row, including equal-valued aliases.
    const key = `${button.generation}:${row}:${column}:${JSON.stringify(button.value)}`;
    const text = entry.segments.filter(s => !s.image && !s.shape).map(s => s.text).join('').trim();
    choices.push({ key, row, button, label: text || `[${String(button.value)}]` });
  }
  return choices;
}
