import { describe, it, expect } from 'vitest';
import { readPreset, writeEntries } from '../aiPreset';
describe('SillyTavern entry selection', () => {
  it('uses the active order, shows unlisted blocks disabled and preserves other data', () => {
    const raw = JSON.stringify({ extension: { keep: 1 }, prompts: [{ identifier: 'a', name: 'Alpha', content: 'A' }, { identifier: 'b', enabled: false, content: 'B' }, { identifier: 'hidden' }], prompt_order: [{ character_id: 7, order: [] }, { character_id: 100001, extra: 'keep', order: [{ identifier: 'b', enabled: true, custom: 42 }, { identifier: 'a', enabled: true }] }] });
    const { entries } = readPreset(raw);
    expect(entries.map(e => [e.id, e.enabled])).toEqual([['b', false], ['a', true], ['hidden', false]]);
    entries[0]!.enabled = true; entries.reverse();
    const output = JSON.parse(writeEntries(raw, entries));
    expect(output.extension.keep).toBe(1); expect(output.prompt_order[0].order).toEqual([]);
    expect(output.prompt_order[1].extra).toBe('keep'); expect(output.prompt_order[1].order[2].custom).toBe(42);
    expect(output.prompts[1].enabled).toBe(true);
  });
  it('rejects invalid JSON and duplicate identifiers', () => {
    expect(() => readPreset('[]')).toThrow();
    expect(() => readPreset('{"prompts":[{"identifier":"a"},{"identifier":"a"}]}')).toThrow();
  });
});
