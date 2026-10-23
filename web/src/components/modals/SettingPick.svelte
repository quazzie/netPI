<script>
  /**
   * A picker setting (types `pick` and `pickText`, settingsModel.js pickSpec): the kit's PickList over the candidates its
   * options name, loaded when the field shows - the local models the model list has now, NetPI's tools, the MCP servers'
   * tool names, the plugins, or a method that answers names - and fixed ones. Names it does not offer can be typed (a model
   * of a loadout that is not up, a pattern like ha_get_*).
   */
  import PickList from '../../lib/kit/PickList.svelte';
  import { rpc } from '../../lib/rpc.svelte.js';
  import { app } from '../../lib/state/app.svelte.js';
  import { setSetting } from '../../lib/settings.js';
  import { pickSpec, pickValue, pickStore } from '../../lib/settingsModel.js';

  let { setting: s, value, disabled = false } = $props();

  const spec = $derived(pickSpec(s));
  let loaded = $state.raw([]);

  const names = (r) =>
    (Array.isArray(r) ? r : (r?.models ?? r?.names ?? r?.data ?? []))
      .map((x) => (typeof x === 'string' ? { value: x } : { value: x.id ?? x.name, hint: x.description ?? x.hint }))
      .filter((x) => x.value);

  async function load() {
    const out = [];
    for (const src of spec.sources) {
      try {
        if (src === '@models') continue; // from app.models, live
        if (src === '@tools')
          for (const t of (await rpc('tools.list')) ?? []) out.push({ value: t.name, hint: t.label ?? '', group: `Tools · ${t.pluginId ?? 'host'}` });
        else if (src === '@mcpTools')
          for (const t of (await rpc('mcp.tools', {}))?.tools ?? []) out.push({ value: t.name, hint: (t.description ?? '').split('\n')[0].slice(0, 80), group: `MCP · ${t.serverId}` });
        else if (src === '@plugins')
          for (const p of (await rpc('plugins.list')) ?? []) out.push({ value: `plugin:${p.id}`, hint: p.name, group: 'Every tool of a plugin' });
        else if (src.startsWith('rpc:')) for (const n of names(await rpc(src.slice(4), {}))) out.push({ ...n, group: 'Offered by the server' });
      } catch {
        // a source that is not there (its plugin is off): its names can still be typed
      }
    }
    loaded = out;
  }
  $effect(() => {
    spec.sources.join();
    load();
  });

  const candidates = $derived([
    ...spec.fixed.map((v) => ({ value: v, group: spec.exclusive.length ? 'Choices' : null })),
    ...(spec.sources.includes('@models')
      ? [...new Map(app.models.filter((m) => m.isLocal).map((m) => [m.id, { value: m.id, hint: `${m.provider}${m.status ? ` · ${m.status}` : ''}`, group: 'Models listed now' }])).values()]
      : []),
    ...loaded,
  ]);
  const chosen = $derived(pickValue(s, value ?? s.default));
</script>

<PickList
  value={chosen}
  {candidates}
  exclusive={spec.exclusive}
  max={spec.max}
  {disabled}
  label={s.label ?? s.key}
  placeholder={s.placeholder ? `None (${s.placeholder})` : 'None'}
  customPlaceholder={spec.sources.includes('@models') ? 'Other model id…' : 'Other…'}
  onchange={(next) => setSetting(s.key, JSON.stringify(next) === JSON.stringify(pickValue(s, s.default)) ? null : pickStore(s, next))}
/>
