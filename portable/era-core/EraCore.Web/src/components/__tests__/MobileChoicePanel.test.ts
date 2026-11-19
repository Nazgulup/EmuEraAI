// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { mount } from '@vue/test-utils';
import { useGameStore } from '../../stores/game';
import { useConnectionStore } from '../../stores/connection';
import { collectGameChoices } from '../../lib/gameChoices';
import type { DisplayLine } from '../../types/protocol';
import MobileChoicePanel from '../MobileChoicePanel.vue';

vi.mock('../../lib/mauiBridge', async importOriginal => ({
  ...await importOriginal<typeof import('../../lib/mauiBridge')>(), isMauiEnvironment: () => true,
}));
const lines: DisplayLine[] = [{ isLineEnd: true, entries: [
  { segments: [{ text: '[0] 初次游玩' }], button: { value: 0, isInteger: true, generation: 3 } },
  { segments: [{ text: '過去の選択肢' }], button: { value: 9, isInteger: true, generation: 2 } },
  { segments: [{ text: '[1] 存档再开' }], button: { value: '9007199254740993', isInteger: true, generation: 3 } },
  { segments: [{ text: '重复按钮' }], button: { value: 0, isInteger: true, generation: 3 } },
  { segments: [{ text: '[2] 普通文字不生成按钮' }] },
] }];
function fixture() {
  const game = useGameStore(), conn = useConnectionStore();
  conn.status = 'connected';
  game.applyTurn(JSON.stringify({ protocolVersion: 13, generation: 3, revision: 1,
    awaiting: { input: { type: 'IntValue' } }, diff: { lineOps: [{ type: 'append', newLines: lines }] } }));
  const send = vi.spyOn(conn, 'sendInput').mockResolvedValue();
  const choices = collectGameChoices(game.displayState.lines, game.awaiting, game.currentTurnGeneration);
  return { game, conn, send, choices };
}
beforeEach(() => { localStorage.clear(); setActivePinia(createPinia()); });
describe('mobile game choices', () => {
  it('keeps image-map input on the image while ordinary image buttons retain their value', () => {
    const imageLines: DisplayLine[] = [{ isLineEnd: true, entries: [
      { segments: [{ text: '', image: { src: 'button.png', width: 40, height: 40, ypos: 0 } }], button: { value: '戻る', isInteger: false, generation: 3 } },
      { segments: [{ text: '', image: { src: 'map.png', srcm: 'hit.png', width: 500, height: 300, ypos: 0 } }], button: { value: 7, isInteger: true, generation: 3 } },
    ] }];
    expect(collectGameChoices(imageLines, { input: { type: 'StrValue' } }, 3).map(c => c.label)).toEqual(['[戻る]']);
  });
  it('keeps current interpreter buttons including equal-value aliases and Japanese labels', () => {
    const choices = collectGameChoices(lines, { input: { type: 'IntValue' } }, 3);
    expect(choices.map(c => c.label)).toEqual(['[0] 初次游玩', '[1] 存档再开', '重复按钮']);
    expect(new Set(choices.map(c => c.key)).size).toBe(3);
    expect(collectGameChoices(lines, { input: { type: 'AnyKey' } }, 3)).toEqual([]);
    expect(collectGameChoices(lines, {}, 3)).toEqual([]);
  });
  it('preserves the source row counts and left-to-right order without inventing grid rows', () => {
    fixture();
    const button = (value: number) => ({ segments: [{ text: `[${value}] 选项` }], button: { value, isInteger: true, generation: 3 } });
    const source = [
      { isLineEnd: true, entries: [button(0), button(1), button(2)] },
      { isLineEnd: true, entries: [{ segments: [{ text: '说明文字' }] }] },
      { isLineEnd: false, entries: [button(3), button(4)] },
      { isLineEnd: true, entries: [button(5)] },
    ];
    const choices = collectGameChoices(source, { input: { type: 'IntValue' } }, 3);
    const wrapper = mount(MobileChoicePanel, { props: { choices, expanded: true } });
    expect(wrapper.findAll('.choice-row').map(row => row.findAll('.game-choice').map(b => b.text())))
      .toEqual([['[0] 选项', '[1] 选项', '[2] 选项'], ['[3] 选项', '[4] 选项'], ['[5] 选项']]);
    expect(choices.map(c => c.row)).toEqual([0, 0, 0, 2, 2, 3]);
    wrapper.unmount();
  });
  it('submits zero exactly once even on repeated taps without changing the transcript', async () => {
    const { game, send, choices } = fixture();
    const before = JSON.stringify(game.displayState);
    const wrapper = mount(MobileChoicePanel, { props: { choices, expanded: true } });
    await wrapper.findAll('.game-choice')[0]!.trigger('click');
    await wrapper.findAll('.game-choice')[0]!.trigger('click');
    expect(send.mock.calls).toEqual([['0']]);
    expect(JSON.stringify(game.displayState)).toBe(before);
    wrapper.unmount();
  });
  it('preserves the exact wire value for integers larger than JavaScript safe precision', async () => {
    const { send, choices } = fixture();
    const wrapper = mount(MobileChoicePanel, { props: { choices, expanded: true } });
    await wrapper.findAll('.game-choice')[1]!.trigger('click');
    expect(send).toHaveBeenCalledWith('9007199254740993'); wrapper.unmount();
  });
  it('rejects stale choices after a new turn and clears choices after exit', async () => {
    const { game, send, choices } = fixture();
    const wrapper = mount(MobileChoicePanel, { props: { choices, expanded: true } });
    game.applyTurn(JSON.stringify({ generation: 4, awaiting: { input: { type: 'IntValue' } }, diff: null }));
    await wrapper.findAll('.game-choice')[0]!.trigger('click');
    expect(send).not.toHaveBeenCalled();
    game.reset(); expect(collectGameChoices(game.displayState.lines, game.awaiting, game.currentTurnGeneration)).toEqual([]);
    wrapper.unmount();
  });
  it('collapsing only changes the panel and never submits game input', async () => {
    const { send, choices } = fixture();
    const wrapper = mount(MobileChoicePanel, { props: { choices, expanded: true } });
    await wrapper.get('.choice-toggle').trigger('click');
    expect(wrapper.emitted('toggle')).toHaveLength(1); expect(send).not.toHaveBeenCalled();
    await wrapper.setProps({ expanded: false }); expect(wrapper.get('.choice-list').attributes('style')).toContain('display: none');
    wrapper.unmount();
  });
});
describe('mobile viewport scaling', () => {
  it('fits to viewport and new game width by default, without changing the saved game layout', () => {
    const game = useGameStore(); game.windowWidth = 1600;
    game.setViewportWidth(640); expect(game.effectiveScale).toBe(.4);
    game.setViewportWidth(800); expect(game.effectiveScale).toBe(.5);
    game.windowWidth = 1000; expect(game.effectiveScale).toBe(.8);
    expect(game.windowWidth).toBe(1000);
  });
  it('manual zoom persists until fit is selected again; invalid measurements do not break rendering', () => {
    const game = useGameStore(); game.windowWidth = 1600; game.setViewportWidth(640);
    game.setScale(.3); game.setViewportWidth(800); expect(game.effectiveScale).toBe(.3);
    setActivePinia(createPinia()); const reopened = useGameStore();
    reopened.windowWidth = 1600; reopened.setViewportWidth(800); expect(reopened.effectiveScale).toBe(.3);
    reopened.fitToScreen(); expect(reopened.effectiveScale).toBe(.5);
    reopened.setViewportWidth(0); reopened.setScale(NaN); expect(reopened.effectiveScale).toBe(.5);
  });
});
