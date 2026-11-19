type Block = Record<string, any>;
export interface PresetEntry { id: string; name: string; role: string; content: string; marker: boolean; enabled: boolean }
export function readPreset(raw: string) {
  const data = raw.trim() ? JSON.parse(raw) : {};
  if (!data || Array.isArray(data) || typeof data !== 'object') throw new Error('预设必须是 JSON 对象。');
  if (Object.keys(data).length && !Array.isArray(data.prompts)) throw new Error('请选择酒馆 Chat Completion 预设（需要 prompts）。');
  const prompts: Block[] = data.prompts ?? [];
  const ids = new Set<string>();
  for (const p of prompts) {
    if (!p || typeof p.identifier !== 'string' || !p.identifier || ids.has(p.identifier)) throw new Error('提示词 identifier 缺失或重复。');
    ids.add(p.identifier);
  }
  const groups: Block[] = Array.isArray(data.prompt_order) ? data.prompt_order : [];
  const group = groups.find(g => g.character_id === 100001) ?? groups.find(g => g.character_id === 100000) ?? groups[0];
  const order: Block[] = group ? (group.order ?? []) : prompts.map(p => ({ identifier: p.identifier, enabled: true }));
  const entries: PresetEntry[] = [];
  const seen = new Set<string>();
  for (const row of [...order, ...prompts.map(p => ({ identifier: p.identifier, enabled: false }))]) {
    if (seen.has(row.identifier)) continue;
    seen.add(row.identifier);
    const p = prompts.find(p => p.identifier === row.identifier);
    if (!p) continue;
    entries.push({ id: p.identifier, name: p.name ?? p.identifier, role: p.role ?? 'system', content: p.content ?? '', marker: !!p.marker, enabled: row.enabled !== false && p.enabled !== false });
  }
  return { data, prompts, group, entries };
}
export function writeEntries(raw: string, entries: PresetEntry[]): string {
  const { data, prompts, group } = readPreset(raw);
  if (entries.length !== prompts.length || new Set(entries.map(e => e.id)).size !== entries.length || entries.some(e => !prompts.some(p => p.identifier === e.id))) throw new Error('预设条目列表不完整，请重新加载。');
  const active = group ?? { character_id: 100001, order: [] };
  if (!group) data.prompt_order = [active];
  active.order = entries.map(e => {
    const row = (active.order ?? []).find((r: Block) => r.identifier === e.id) ?? { identifier: e.id };
    prompts.find(p => p.identifier === e.id)!.enabled = e.enabled;
    return { ...row, enabled: e.enabled };
  });
  return JSON.stringify(data);
}
