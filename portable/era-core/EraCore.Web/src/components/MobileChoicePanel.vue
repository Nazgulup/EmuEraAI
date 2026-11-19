<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { gameTranslation } from '../lib/translation';
import { useGameStore } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import { shouldSubmitButtonValue } from '../lib/inputRouting';
import type { GameChoice } from '../lib/gameChoices';

const props = defineProps<{ choices: GameChoice[]; expanded: boolean }>();
defineEmits<{ toggle: [] }>();
const game = useGameStore(), conn = useConnectionStore();
const list = ref<HTMLElement | null>(null);
const rows = computed(() => {
  const grouped = new Map<number, GameChoice[]>();
  for (const choice of props.choices) {
    if (!grouped.has(choice.row)) grouped.set(choice.row, []);
    grouped.get(choice.row)!.push(choice);
  }
  return [...grouped].map(([key, choices]) => ({ key, choices }));
});
const columns = computed(() => rows.value.reduce((max, row) => Math.max(max, row.choices.length), 1));
const disabled = computed(() => game.inputInFlight || conn.status !== 'connected' || !conn.canInput);
watch(() => game.currentTurnGeneration, () => { if (list.value) list.value.scrollTop = 0; });
function submit(choice: GameChoice) {
  // Recheck the current state on every click, including a delayed tap on an old DOM node.
  if (!props.choices.some(c => c.key === choice.key) || !shouldSubmitButtonValue({
    connected: conn.status === 'connected', awaiting: game.awaiting,
    buttonGeneration: choice.button.generation, currentTurnGeneration: game.currentTurnGeneration,
    inputInFlight: game.inputInFlight, canInput: conn.canInput,
  })) return;
  game.setInputInFlight();
  void conn.sendInput(String(choice.button.value));
}
</script>

<template>
  <aside v-if="choices.length" class="choice-panel" :class="{ collapsed: !expanded }" :style="{ '--choice-columns': columns }" aria-label="当前游戏选项"
    @pointerdown.stop @pointerup.stop @mousedown.stop @mouseup.stop @click.stop @keydown.stop @keyup.stop>
    <button class="choice-toggle" :aria-expanded="expanded" :aria-label="`${expanded ? '收起选项' : '展开选项'} · ${choices.length}`" @click="$emit('toggle')">
      {{ expanded ? '收起' : '选项' }} · {{ choices.length }}
    </button>
    <div v-show="expanded" ref="list" class="choice-list">
      <div v-for="row in rows" :key="row.key" class="choice-row">
        <button v-for="choice in row.choices" :key="choice.key" class="game-choice" :disabled="disabled" @click="submit(choice)">{{ gameTranslation.text(choice.label) }}</button>
      </div>
    </div>
  </aside>
</template>

<style scoped>
.choice-panel { --choice-width: 88px; --choice-width: clamp(88px, 11.5vw, 170px); position: absolute; right: 2.5%; right: calc(2.5% + env(safe-area-inset-right)); bottom: 5.5%; bottom: calc(5.5% + env(safe-area-inset-bottom)); width: calc(var(--choice-width) * var(--choice-columns) + 4px * (var(--choice-columns) - 1)); max-width: 92.5%; max-height: 75%; display: flex; flex-direction: column; z-index: 30; pointer-events: none; }
.choice-panel.collapsed { width: auto; min-width: 0; }
.choice-toggle, .game-choice { color: #f1f1f1; background: rgba(48, 52, 58, .55); border: 1px solid rgba(230, 236, 242, .18); border-radius: 4px; font: inherit; font-size: 12px; padding: 6px; cursor: pointer; touch-action: manipulation; pointer-events: auto; }
.choice-toggle { align-self: flex-end; flex-shrink: 0; font-size: 11px; min-height: 26px; padding: 4px 6px; }
.choice-list { min-height: 0; overflow-y: auto; overscroll-behavior: contain; pointer-events: auto; }
.choice-row { display: flex; justify-content: flex-end; margin-top: 4px; }
.game-choice { display: block; flex: 0 0 auto; width: calc((100% - 4px * (var(--choice-columns) - 1)) / var(--choice-columns)); min-width: 0; min-height: 40px; min-height: clamp(32px, 11vh, 52px); text-align: center; white-space: pre-wrap; overflow-wrap: break-word; }
.game-choice + .game-choice { margin-left: 4px; }
.game-choice:active:not(:disabled) { background: rgba(66, 97, 118, .7); }
.game-choice:disabled { opacity: .5; }
</style>
