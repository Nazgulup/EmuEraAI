<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { postInput } from '../lib/mauiBridge';
import { gameTranslation } from '../lib/translation';
import { readPreset, writeEntries } from '../lib/aiPreset';

interface Settings {
  Enabled: boolean; AutoGenerate: boolean; GenerateWithoutOriginal: boolean; Endpoint: string; Model: string; ApiKey: string;
  TimeoutSeconds: number; OutputTokens: number; PresetJson: string;
  CharacterDescription: string; Personality: string; World: string;
  UseGameStyle: boolean; UsePresetOutputTokens: boolean; ThinkingMode: string;
  TranslationEnabled: boolean; TranslationLanguage: string; TranslationTokens: number;
}
const open = ref(false), editing = ref(false), notice = ref(''), preview = ref('');
const generationLog = ref(''), logVisible = ref(false), probing = ref(false);
const gameStyle = ref('');
const storyReason = ref('正在等待游戏文本'), storyLocation = ref('');
const storyTemplateName = ref(''), storyTemplateReason = ref('正在匹配内置模板');
interface FunctionRow { key: string; file: string; function: string; line: number; classification: string; reason: string; action: string; note: string }
const docVisible = ref(false), docId = ref(''), docState = ref('idle'), docReason = ref('未匹配模板时会自动建立函数清单');
const docDone = ref(0), docTotal = ref(0), docCount = ref(0), docPage = ref(0), docQuery = ref(''), docKind = ref('');
const docRows = ref<FunctionRow[]>([]), docEdits = ref<Record<string, {key: string; action: string; note: string}>>({});
function loadDocument(page = 0) { docVisible.value = true; send('aiStoryDocument', { query: docQuery.value, kind: docKind.value, page }); }
function editFunction(row: FunctionRow, action: string, note = docEdits.value[row.key]?.note ?? row.note) { docEdits.value[row.key] = { key: row.key, action, note }; }
function functionAction(row: FunctionRow) { return docEdits.value[row.key]?.action ?? row.action; }
function saveDocument() { send('aiStoryDocumentSave', { documentId: docId.value, edits: Object.values(docEdits.value) }); }
const canGenerate = ref(false), storyReady = ref(false), canSetFunctionRule = ref(false);
const storyEventId = ref(''), storyTurn = ref(0);
function setStoryRule(scope: string, action: string) { send('aiStoryRule', { scope, action, eventId: storyEventId.value, storyTurn: storyTurn.value }); }
const text = ref(''), status = ref('等待游戏口上'), character = ref('');
const busy = ref(false), hasEvent = ref(false), hasOriginal = ref(true), hasKey = ref(false), clearKey = ref(false);
const settings = ref<Settings>({ Enabled: false, AutoGenerate: true, GenerateWithoutOriginal: false, TranslationEnabled: false, TranslationLanguage: 'zh-CN', TranslationTokens: 2048, Endpoint: 'https://api.openai.com/v1', Model: '', ApiKey: '', TimeoutSeconds: 20, OutputTokens: 400, PresetJson: '', CharacterDescription: '', Personality: '', World: '', UseGameStyle: true, UsePresetOutputTokens: false, ThinkingMode: 'auto' });
const models = ref<string[]>([]), modelsBusy = ref(false), modelsStatus = ref(''), modelRequest = ref('');
let modelSequence = 0;
const selectedEntry = ref(0), rawVisible = ref(false);
const presetView = computed(() => {
  try { return { entries: readPreset(settings.value.PresetJson).entries, error: '' }; }
  catch (error) { return { entries: [], error: (error as Error).message }; }
});
const selectedContent = computed(() => presetView.value.entries[selectedEntry.value]);
watch(() => [settings.value.Endpoint, settings.value.ApiKey, clearKey.value], () => { models.value = []; modelRequest.value = ''; modelsBusy.value = false; modelsStatus.value = '连接信息改变后，请刷新模型列表。'; });
function loadModels() {
  modelRequest.value = String(++modelSequence); modelsBusy.value = true; modelsStatus.value = '正在读取 /models…';
  send('aiModels', { requestId: modelRequest.value, settings: { Endpoint: settings.value.Endpoint, ApiKey: settings.value.ApiKey, TimeoutSeconds: settings.value.TimeoutSeconds }, clearKey: clearKey.value });
}
function setEntry(index: number, enabled: boolean) {
  const entries = presetView.value.entries.map(e => ({ ...e })); entries[index]!.enabled = enabled;
  settings.value.PresetJson = writeEntries(settings.value.PresetJson, entries);
}
function setAll(enabled: boolean) { if (presetView.value.entries.length) settings.value.PresetJson = writeEntries(settings.value.PresetJson, presetView.value.entries.map(e => ({ ...e, enabled }))); }
function moveEntry(delta: number) {
  const entries = [...presetView.value.entries], from = selectedEntry.value, to = from + delta;
  if (to < 0 || to >= entries.length) return;
  entries.splice(to, 0, entries.splice(from, 1)[0]!);
  settings.value.PresetJson = writeEntries(settings.value.PresetJson, entries); selectedEntry.value = to;
}
function send(type: string, extra: Record<string, unknown> = {}) { postInput(JSON.stringify({ type, ...extra })); }
function receive(event: Event) {
  const msg = (event as CustomEvent).detail;
  if (msg.type === 'aiState') {
    text.value = msg.text; status.value = msg.status; character.value = msg.character;
    busy.value = msg.busy; hasEvent.value = msg.hasEvent;
    hasOriginal.value = msg.hasOriginal ?? true;
    canGenerate.value = msg.canGenerate === true; storyReady.value = msg.storyReady === true;
    storyReason.value = msg.storyReason || '尚未确认是否为日常口上，保留原文'; storyLocation.value = msg.storyLocation || '';
    canSetFunctionRule.value = msg.canSetFunctionRule === true;
    storyEventId.value = msg.eventId || ''; storyTurn.value = msg.storyTurn || 0;
    storyTemplateName.value = msg.storyTemplateName || ''; storyTemplateReason.value = msg.storyTemplateReason || '尚未匹配内置模板';
  } else if (msg.type === 'aiStoryDocument') {
    if (docId.value && docId.value !== msg.documentId) docEdits.value = {};
    docId.value = msg.documentId; docState.value = msg.state; docReason.value = msg.reason;
    docDone.value = msg.done; docTotal.value = msg.total; docCount.value = msg.count; docPage.value = msg.page; docRows.value = msg.rows;
  } else if (msg.type === 'aiStoryDocumentProgress') {
    docState.value = msg.state; docReason.value = msg.reason; docDone.value = msg.done; docTotal.value = msg.total;
    if (msg.state === 'ready' && docVisible.value) loadDocument(docPage.value);
  } else if (msg.type === 'aiStoryDocumentSaved') {
    docEdits.value = {}; notice.value = msg.text; loadDocument(docPage.value);
  } else if (msg.type === 'aiSettings') {
    settings.value = { ...settings.value, ...msg.settings }; hasKey.value = msg.hasKey; clearKey.value = false;
  } else if (msg.type === 'aiNotice') notice.value = msg.text;
  else if (msg.type === 'aiLog') { generationLog.value = msg.text; logVisible.value = true; probing.value = false; }
  else if (msg.type === 'aiStyle') gameStyle.value = msg.text;
  else if (msg.type === 'aiModels' && msg.requestId === modelRequest.value) { modelsBusy.value = false; models.value = msg.models; modelsStatus.value = msg.error || `已加载 ${msg.models.length} 个模型。`; }
  else if (msg.type === 'aiPreview') preview.value = msg.text;
  else if (msg.type === 'aiPresetImported') { settings.value.PresetJson = msg.text; selectedEntry.value = 0; notice.value = '预设已导入，可勾选条目，点击保存后生效。'; }
}
function show() { open.value = true; send('aiGet'); }
function save() {
  const tokens = settings.value.OutputTokens;
  if (!Number.isInteger(tokens) || tokens < 1 || tokens > 2147483647) { notice.value = '最大输出 tokens 必须是 1～2147483647 的整数。'; return; }
  const translationTokens = settings.value.TranslationTokens;
  if (!Number.isInteger(translationTokens) || translationTokens < 1 || translationTokens > 2147483647) { notice.value = '翻译输出 tokens 必须是 1～2147483647 的整数。'; return; }
  notice.value = ''; send('aiSave', { settings: settings.value, clearKey: clearKey.value });
}
function resetGame() {
  open.value = false; text.value = ''; character.value = ''; status.value = '等待游戏口上';
  busy.value = false; hasEvent.value = false; preview.value = ''; gameStyle.value = '';
  canGenerate.value = false; storyReady.value = false; canSetFunctionRule.value = false; storyReason.value = '正在等待游戏文本'; storyLocation.value = '';
  storyTemplateName.value = ''; storyTemplateReason.value = '正在匹配内置模板';
  docVisible.value = false; docId.value = ''; docState.value = 'idle'; docReason.value = '未匹配模板时会自动建立函数清单'; docRows.value = []; docEdits.value = {}; docQuery.value = ''; docKind.value = ''; docPage.value = 0; docCount.value = 0; docDone.value = 0; docTotal.value = 0;
  generationLog.value = ''; logVisible.value = false; probing.value = false; notice.value = '';
  modelRequest.value = ''; modelsBusy.value = false;
}
onMounted(() => { window.addEventListener('era-ai-message', receive); window.addEventListener('era-ai-reset', resetGame); });
onUnmounted(() => { window.removeEventListener('era-ai-message', receive); window.removeEventListener('era-ai-reset', resetGame); });
</script>

<template>
  <button class="ai-launch" @click.stop="show" @mousedown.stop @mouseup.stop @keydown.stop @keyup.stop>AI 口上</button>
  <section v-if="open" class="ai-overlay" role="dialog" aria-label="AI 口上" @click.stop @keydown.stop @keyup.stop @mousedown.stop @mouseup.stop @pointerdown.stop>
    <header><h2>AI 口上 <small>{{ character }}</small></h2><button @click="open = false">关闭</button></header>
    <nav><button :class="{ chosen: !editing }" @click="editing = false">当前口上</button><button :class="{ chosen: editing }" @click="editing = true">API 与酒馆预设</button></nav>
    <div v-if="!editing" class="ai-content">
      <p class="ai-hint">游戏程序处理数值；重新生成只改这一段文字。</p>
      <p class="ai-dialogue">{{ busy ? (hasOriginal ? 'AI 口上生成中…确认失败后才显示原口上。' : '正在补写口上…本回合没有原文，失败时保留游戏画面。') : text || '等待游戏口上；启用 AI 后优先生成，未触发时可查看日志。' }}</p>
      <p role="status">{{ status }}</p>
      <div v-if="gameTranslation.state.enabled">
        <p role="status">翻译：{{ gameTranslation.state.status }}</p>
        <div class="ai-buttons"><button @click="gameTranslation.toggleOriginal()">{{ gameTranslation.state.translated ? '显示原文' : '显示译文' }}</button><button @click="gameTranslation.retry()">重试翻译</button></div>
      </div>
      <div class="ai-buttons"><button :disabled="!hasEvent || busy || !canGenerate" @click="send('aiGenerate')">重新生成</button><button @click="send('aiRestore')">恢复原口上 / 取消</button><button @click="send('aiPreview')">查看请求</button></div>
      <h3>剧情保护</h3>
      <p aria-label="内置剧情模板">{{ storyTemplateName ? '内置模板：' + storyTemplateName : '内置模板：未套用' }}<br>{{ storyTemplateReason }}</p>
      <button @click="docVisible ? docVisible = false : loadDocument()">{{ docVisible ? '收起函数清单' : '函数清单 / 编辑选择' }}</button>
      <p class="ai-hint" aria-label="函数扫描进度">{{ docReason }}<span v-if="docState === 'scanning'"> · {{ docDone }}/{{ docTotal }} 个脚本</span></p>
      <section v-if="docVisible" aria-label="可编辑函数清单">
        <p class="ai-hint">勾选表示优先 AI；取消勾选表示保留原文。自动模式只允许明确日常，剧情调用仍保护。保存后对当前允许的段落自动生成，不执行游戏指令。备注仅用于整理。</p>
        <input v-model="docQuery" aria-label="搜索函数或文件" placeholder="搜索函数名或文件名" maxlength="200" />
        <div class="ai-buttons"><select v-model="docKind" aria-label="函数分类"><option value="">全部函数</option><option value="routine">明确日常</option><option value="story">剧情</option><option value="unknown">不确定</option></select><button @click="loadDocument()">搜索函数</button><button :disabled="docState !== 'ready'" @click="send('aiStoryDocumentExport')">导出函数文档</button></div>
        <div class="ai-buttons"><button :disabled="docPage === 0" @click="loadDocument(docPage - 1)">上一页</button><span>第 {{ docPage + 1 }} 页 · {{ docCount }} 个函数</span><button :disabled="(docPage + 1) * 30 >= docCount" @click="loadDocument(docPage + 1)">下一页</button></div>
        <article v-for="row in docRows" :key="row.key" class="ai-function">
          <label><input type="checkbox" :aria-label="'优先 AI ' + row.function" :checked="functionAction(row) === 'allow' || (functionAction(row) === 'auto' && row.classification === 'routine')" :disabled="docState !== 'ready'" @change="editFunction(row, ($event.target as HTMLInputElement).checked ? 'allow' : 'deny')" /> {{ row.function }}</label>
          <small>{{ row.file }}:{{ row.line }} · {{ row.classification === 'routine' ? '日常候选' : row.classification === 'story' ? '剧情保护' : '不确定' }}</small>
          <p class="ai-hint">{{ row.reason }}</p>
          <select :value="functionAction(row)" :aria-label="'规则 ' + row.function" @change="editFunction(row, ($event.target as HTMLSelectElement).value)"><option value="auto">自动判断（日常优先 AI）</option><option value="allow">手动优先 AI（剧情保护仍生效）</option><option value="deny">保留原文 / 禁止 AI</option></select>
          <input :value="docEdits[row.key]?.note ?? row.note" :aria-label="'备注 ' + row.function" placeholder="可编辑备注" maxlength="1000" @input="editFunction(row, functionAction(row), ($event.target as HTMLInputElement).value)" />
        </article>
        <button class="ai-save" :disabled="docState !== 'ready' || Object.keys(docEdits).length === 0" @click="saveDocument">保存函数选择（{{ Object.keys(docEdits).length }}）</button>
      </section>
      <p role="status" aria-label="剧情判断">{{ canGenerate ? '允许生成：' : '保留原文：' }}{{ storyReason }}</p>
      <p v-if="storyLocation" class="ai-hint">{{ storyLocation }}</p>
      <div class="ai-buttons">
        <button :disabled="!hasEvent || !storyReady" @click="setStoryRule('event', 'allow')">仅本段允许</button>
        <button :disabled="!hasEvent || !storyReady" @click="setStoryRule('event', 'deny')">仅本段禁止</button>
        <button :disabled="!hasEvent || !storyReady" @click="setStoryRule('event', 'auto')">本段恢复自动判断</button>
      </div>
      <div class="ai-buttons">
        <button :disabled="!canSetFunctionRule" @click="setStoryRule('function', 'allow')">本游戏此函数允许</button>
        <button :disabled="!canSetFunctionRule" @click="setStoryRule('function', 'deny')">本游戏此函数禁止</button>
        <button :disabled="!canSetFunctionRule" @click="setStoryRule('function', 'auto')">清除此函数规则</button>
      </div>
      <p class="ai-hint">默认只生成明确的日常口上；剧情和不确定文本保留原文，无原口上补写也遵守此规则。允许后点击“重新生成”。函数规则只属于当前游戏和脚本版本，剧情调用仍优先保护；本段允许可临时放行剧情，但不会覆盖已保存的禁止规则。</p>
      <div class="ai-buttons"><button @click="send('aiStoryImport')">导入剧情规则模板</button><button @click="send('aiStoryExport')">导出本游戏规则</button></div>
      <div class="ai-buttons"><button @click="send('aiLog')">生成日志</button><button @click="send('aiExportLog')">导出日志</button><button :disabled="probing" @click="probing = true; send('aiProbe')">测试已保存 API</button><button @click="send('aiStyle')">游戏风格</button></div>
      <label v-if="gameStyle">当前游戏风格<textarea :value="gameStyle" readonly rows="7" aria-label="当前游戏风格" /></label>
      <label v-if="logVisible">生成日志（密钥已脱敏）<textarea :value="generationLog" readonly rows="12" aria-label="生成日志" /></label>
      <p v-if="logVisible" class="ai-hint">日志包含原口上、AI 正文及逐字比较。exactOriginal 表示与原文完全相同；exactPrevious 表示与上一版完全相同。长正文按 part 分段。</p>
      <textarea v-if="preview" :value="preview" readonly rows="10" aria-label="实际请求（不含密钥）" />
    </div>
    <div v-else class="ai-content ai-settings">
      <label class="ai-check"><input v-model="settings.Enabled" type="checkbox" />启用 AI</label>
      <label class="ai-check"><input v-model="settings.AutoGenerate" type="checkbox" />新口上自动生成</label>
      <label class="ai-check"><input v-model="settings.GenerateWithoutOriginal" aria-label="无原口上时补写" type="checkbox" />无原口上时补写</label>
      <p class="ai-hint">未捕获原口上、但有当前角色和可选项时，用只读状态与选项补写一段文字。开启“新口上自动生成”后自动补写，也可手动重新生成。失败时保留原画面，不执行选项或改变数值。</p>
      <h3>游戏翻译</h3>
      <label class="ai-check"><input v-model="settings.TranslationEnabled" aria-label="自动翻译正文和选项" type="checkbox" />自动翻译正文和选项</label>
      <div class="ai-grid"><label>译成<select v-model="settings.TranslationLanguage" aria-label="翻译目标语言"><option value="zh-CN">简体中文</option><option value="zh-TW">繁體中文</option><option value="en">English</option><option value="ja">日本語</option></select></label><label>翻译输出 tokens<input v-model.number="settings.TranslationTokens" aria-label="翻译输出 tokens" type="number" min="1" max="2147483647" step="1" /></label></div>
      <p class="ai-hint">独立开关，使用下方同一 API 和模型，将显示的正文与选项分批翻译。不会改写脚本或选项编号，也不使用口上预设。失败时显示原文；“当前口上”页可切换原文、重试翻译，重复文字复用本次游戏缓存。</p>
      <p class="ai-hint">API 模式：OpenAI 兼容 · Chat Completions</p>
      <label>API 基础地址<input v-model="settings.Endpoint" type="url" placeholder="https://服务商/v1" /></label>
      <label>模型名称（也可手动输入）<input v-model="settings.Model" autocomplete="off" /></label>
      <label v-if="models.length">可用模型<select aria-label="可用模型" :value="models.includes(settings.Model) ? settings.Model : ''" @change="settings.Model = ($event.target as HTMLSelectElement).value"><option disabled value="">选择模型</option><option v-for="id in models" :key="id" :value="id">{{ id }}</option></select></label>
      <button :disabled="modelsBusy" @click="loadModels">刷新模型列表</button><p class="ai-hint" role="status">{{ modelsStatus }}</p>
      <label>API Key<input v-model="settings.ApiKey" type="password" autocomplete="new-password" :placeholder="hasKey ? '已安全保存；留空保留原密钥' : '填写你的密钥'" /></label>
      <label v-if="hasKey" class="ai-check"><input v-model="clearKey" type="checkbox" />清除已保存密钥</label>
      <div class="ai-grid"><label>超时（秒）<input v-model.number="settings.TimeoutSeconds" type="number" min="2" max="180" /></label><label>最大输出 tokens<input v-model.number="settings.OutputTokens" aria-label="最大输出 tokens" type="number" min="1" max="2147483647" step="1" /></label></div>
      <p class="ai-hint">按所选额度发送，不再限制为 8192；实际可用额度取决于模型。预设填写了上下文上限时，输入与输出仍须在该预算内。</p>
      <label class="ai-check"><input v-model="settings.UsePresetOutputTokens" type="checkbox" />使用预设中的输出上限（否则以上方设置为准）</label>
      <label>思考模式<select v-model="settings.ThinkingMode" aria-label="思考模式"><option value="auto">自动（DeepSeek 关闭思考）</option><option value="provider">跟随服务商默认</option><option value="disabled">关闭思考（thinking 参数）</option><option value="enabled">开启思考（thinking 参数）</option></select></label>
      <p class="ai-hint">思考可能用完输出额度而没有正文。开启思考时请增大输出 tokens 与超时。自动模式仅为 DeepSeek 官方接口关闭思考。</p>
      <label class="ai-check"><input v-model="settings.UseGameStyle" type="checkbox" />加载时总结游戏风格</label>
      <p class="ai-hint">启用 AI 后，首次向已配置 API 发送有限的游戏口上样本进行文风分析，之后复用缓存。不会读取存档或执行脚本。</p>
      <label>角色描述<textarea v-model="settings.CharacterDescription" rows="3" /></label>
      <label>性格 / 口吻<textarea v-model="settings.Personality" rows="2" /></label>
      <label>世界 / 场景<textarea v-model="settings.World" rows="3" /></label>
      <h3>酒馆预设条目</h3>
      <div class="ai-buttons"><button @click="send('aiImport')">导入 JSON</button><button @click="send('aiExport', { preset: settings.PresetJson })">导出 JSON</button><button @click="send('aiCheck', { preset: settings.PresetJson })">检查兼容性</button></div>
      <p class="ai-hint">支持最大 32 MB 的 JSON。仅勾选的条目参与生成；选择条目可查看内容。</p>
      <p v-if="presetView.error" role="alert">{{ presetView.error }}</p>
      <div v-if="presetView.entries.length" class="ai-buttons"><button @click="setAll(true)">全选</button><button @click="setAll(false)">全不选</button><button :disabled="selectedEntry <= 0" @click="moveEntry(-1)">上移</button><button :disabled="selectedEntry >= presetView.entries.length - 1" @click="moveEntry(1)">下移</button></div>
      <div class="ai-entries">
        <div v-for="(entry, i) in presetView.entries" :key="entry.id" class="ai-entry" :class="{ selected: selectedEntry === i }">
          <input type="checkbox" :aria-label="'启用 ' + entry.name" :checked="entry.enabled" @change="setEntry(i, ($event.target as HTMLInputElement).checked)" />
          <button @click="selectedEntry = i">{{ entry.name }} <small>{{ entry.role }}{{ entry.marker ? ' / 标记槽' : '' }}</small></button>
        </div>
      </div>
      <label v-if="selectedContent">{{ selectedContent.id }}<textarea :value="selectedContent.content || (selectedContent.marker ? '[动态标记槽]' : '')" readonly rows="5" aria-label="条目内容" /></label>
      <button @click="rawVisible = !rawVisible">{{ rawVisible ? '收起 JSON' : '编辑原始 JSON' }}</button>
      <label v-if="rawVisible">酒馆 Chat Completion 预设<textarea v-model="settings.PresetJson" rows="7" placeholder="导入 JSON，或留空使用内置提示" /></label>
      <p class="ai-hint">支持提示词顺序、常用宏、注入深度和模型参数。未支持的宏会回退原口上；不执行酒馆脚本。设置当前为全局一套。</p>
      <button class="ai-save" @click="save">保存设置</button>
    </div>
    <p v-if="notice" class="ai-notice" role="status">{{ notice }}</p>
  </section>
</template>

<style scoped>
.ai-launch { position: fixed; left: 12px; top: max(8px, env(safe-area-inset-top)); z-index: 100; border: 1px solid #607d8b; border-radius: 22px; padding: 10px 18px; background: #233e50; color: #fff; box-shadow: 0 3px 12px #0004; }
.ai-overlay { position: fixed; inset: 0; z-index: 1500; background: #17212b; color: #e6edf3; display: flex; flex-direction: column; overflow: auto; padding: max(12px, env(safe-area-inset-top)) 16px max(18px, env(safe-area-inset-bottom)); font: 15px/1.55 sans-serif; }
header { display: flex; justify-content: space-between; align-items: center; gap: 12px; } h2 { font-size: 21px; margin: 8px 0 16px; } small { font-size: 14px; font-weight: normal; }
nav,.ai-buttons { display: flex; gap: 8px; flex-wrap: wrap; margin: 10px 0; }
.ai-overlay button { min-height: 42px; border: 1px solid #526575; border-radius: 8px; background: #273746; color: #fff; padding: 8px 14px; font: inherit; }
.ai-overlay button.chosen,.ai-save { background: #276c83; }.ai-overlay button:disabled { opacity: .45; }
.ai-content { width: 100%; max-width: 760px; margin: 0 auto; }.ai-dialogue { white-space: pre-wrap; padding: 16px; background: #202e3b; border-radius: 10px; min-height: 160px; }
.ai-settings label { display: flex; flex-direction: column; gap: 6px; margin: 12px 0; }.ai-settings .ai-check { flex-direction: row; align-items: center; }
.ai-overlay input:not([type=checkbox]),.ai-overlay textarea,.ai-overlay select { box-sizing: border-box; width: 100%; border: 1px solid #526575; border-radius: 6px; padding: 10px; background: #0f1922; color: #fff; font: inherit; }
.ai-entries { max-height: 320px; overflow: auto; }.ai-entry { display: flex; align-items: center; gap: 10px; padding: 4px; }.ai-entry.selected { background: #354b5c; }.ai-entry button { flex: 1; text-align: left; min-width: 0; overflow-wrap: anywhere; }.ai-entry small { display: block; color: #b6c7d6; }.ai-entry input { flex-shrink: 0; }
.ai-overlay input[type=checkbox] { width: 20px; height: 20px; }.ai-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
.ai-hint { color: #abbac7; font-size: 13px; }.ai-notice { white-space: pre-wrap; padding: 12px; background: #354354; border-radius: 8px; }.ai-save { width: 100%; margin: 12px 0; }
.ai-function { border: 1px solid #526575; border-radius: 8px; padding: 12px; margin: 10px 0; overflow-wrap: anywhere; }.ai-function small { display: block; }.ai-function input:not([type=checkbox]) { margin-top: 8px; }
</style>
