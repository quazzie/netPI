<script>
  /**
   * One agent's dialog on its own (the Work tab opens it from an agent's name): it reads the settings document itself
   * and follows it, so what the dialog saves shows at once. The same AgentDialog the Settings → Agents page opens.
   */
  import { untrack } from 'svelte';
  import AgentDialog from './AgentDialog.svelte';
  import { app } from '../../lib/state/app.svelte.js';
  import { rpc } from '../../lib/rpc.svelte.js';

  let { id: startId, onclose } = $props();

  let id = $state(untrack(() => startId));
  let doc = $state.raw({});

  async function load() {
    try {
      doc = (await rpc('settings.get'))?.settings ?? {};
    } catch {}
  }
  $effect(() => {
    app.settingsVersion;
    load();
  });
</script>

<AgentDialog
  {id}
  agent={doc?.agents?.[id]}
  info={app.slots.find((p) => p.configured && p.key === id) ?? null}
  localSlots={doc?.models?.localSlots ?? 2}
  taken={Object.keys(doc?.agents ?? {})}
  onrename={(next) => (id = next)}
  {onclose}
/>
