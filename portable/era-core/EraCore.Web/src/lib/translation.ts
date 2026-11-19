import { reactive } from 'vue';

export interface TranslationSettings {
  TranslationEnabled?: boolean;
  TranslationLanguage?: string;
  TranslationTokens?: number;
  Endpoint?: string;
  Model?: string;
  ThinkingMode?: string;
  TimeoutSeconds?: number;
}

// Presentation only: no game store, input value, interpreter or save access.
export class GameTranslation {
  readonly state = reactive({ enabled: false, translated: true, busy: false, status: '自动翻译未开启', revision: 0 });
  private cache = new Map<string, string>();
  private failed = new Set<string>();
  private queue = new Set<string>();
  private timer?: ReturnType<typeof setTimeout>;
  private timeout?: ReturnType<typeof setTimeout>;
  private pending?: { id: string; texts: string[] };
  private epoch = 0;
  private sequence = 0;
  private key = '';
  private budget = 1200;
  private waitMs = 22000;
  private send: (json: string) => void = () => {};

  configure(settings: TranslationSettings, version: number, send: (json: string) => void) {
    const key = JSON.stringify([settings.TranslationEnabled, settings.TranslationLanguage, settings.TranslationTokens,
      settings.Endpoint, settings.Model, settings.ThinkingMode, settings.TimeoutSeconds, version]);
    this.send = send;
    if (this.key === key) return;
    this.reset();
    this.key = key;
    this.state.enabled = settings.TranslationEnabled === true;
    this.budget = Math.min(1200, Math.max(32, Math.floor((settings.TranslationTokens ?? 2048) / 2)));
    this.waitMs = Math.max(2, Math.min(180, settings.TimeoutSeconds ?? 20)) * 1000 + 2000;
    this.state.status = this.state.enabled ? '已开启；显示文字时自动翻译' : '自动翻译未开启';
  }

  reset() {
    clearTimeout(this.timer); clearTimeout(this.timeout);
    this.timer = undefined; this.timeout = undefined;
    this.epoch++; this.pending = undefined; this.key = '';
    this.cache.clear(); this.queue.clear(); this.failed.clear();
    this.state.enabled = false; this.state.translated = true; this.state.busy = false;
    this.state.status = '自动翻译未开启'; this.state.revision++;
  }

  text(original: string): string {
    // Every mounted span subscribes to responses, without mutating reactive state during rendering.
    void this.state.revision;
    if (!this.state.enabled || !this.state.translated) return original;
    const source = original.trim();
    if (!/[A-Za-z\u3040-\u30ff\u3400-\u9fff]/.test(source)) return original;
    const cached = this.cache.get(source);
    if (cached !== undefined) return original.slice(0, original.indexOf(source)) + cached + original.slice(original.indexOf(source) + source.length);
    if (source.length <= this.budget && !this.failed.has(source) && !this.pending?.texts.includes(source)) {
      this.queue.add(source); this.schedule();
    }
    return original;
  }

  private schedule() {
    if (!this.timer && !this.pending && this.queue.size && this.state.enabled && this.state.translated)
      this.timer = setTimeout(() => { this.timer = undefined; this.flush(); }, 60);
  }

  private flush() {
    if (this.pending || !this.state.enabled || !this.state.translated) return;
    const texts: string[] = []; let chars = 0;
    for (const text of this.queue) {
      if (texts.length === 16 || chars + text.length > this.budget) break;
      texts.push(text); chars += text.length; this.queue.delete(text);
    }
    if (!texts.length) return;
    const id = `${this.epoch}-${++this.sequence}`;
    this.pending = { id, texts }; this.state.busy = true;
    this.state.status = '正在翻译；尚未完成的文字保留原文';
    this.timeout = setTimeout(() => this.receive({ requestId: id, error: '翻译超时，保留原文；可重试。' }), this.waitMs);
    try { this.send(JSON.stringify({ type: 'aiTranslate', requestId: id, texts })); }
    catch { this.receive({ requestId: id, error: '翻译连接不可用，保留原文；可重试。' }); }
  }

  receive(message: { requestId?: string; translations?: unknown; error?: string }) {
    const pending = this.pending;
    if (!pending || pending.id !== message.requestId) return;
    clearTimeout(this.timeout); this.timeout = undefined; this.pending = undefined;
    const values = message.translations;
    const valid = !message.error && Array.isArray(values) && values.length === pending.texts.length && values.every(x => typeof x === 'string' && x.trim());
    if (valid) {
      pending.texts.forEach((source, i) => this.cache.set(source, values[i] as string));
      while (this.cache.size > 4096) this.cache.delete(this.cache.keys().next().value!);
      this.state.status = this.failed.size ? '部分文字翻译失败，已保留原文；可重试' : '已显示译文；重复文字复用本次游戏缓存';
    } else {
      pending.texts.forEach(source => this.failed.add(source));
      // Pause queued batches after an API/format failure, avoiding a request storm during an outage.
      for (const source of this.queue) this.failed.add(source);
      this.queue.clear();
      this.state.status = message.error || '翻译回复不完整，保留原文；可重试。';
    }
    this.state.busy = false; this.state.revision++; this.schedule();
  }

  toggleOriginal() {
    this.state.translated = !this.state.translated;
    if (!this.state.translated) { clearTimeout(this.timer); this.timer = undefined; this.queue.clear(); }
    this.state.revision++;
  }

  retry() {
    clearTimeout(this.timer); clearTimeout(this.timeout);
    this.timer = undefined; this.timeout = undefined; this.pending = undefined; this.epoch++;
    this.failed.clear(); this.queue.clear(); this.state.busy = false;
    this.state.translated = true; this.state.status = '重新翻译当前显示的未完成文字'; this.state.revision++;
  }
}

export const gameTranslation = new GameTranslation();
