<script>
  import { rpc } from '../../lib/rpc.svelte.js';
  import { toast } from '../../lib/state/ui.svelte.js';
  let backups = $state([]);
  let busy = $state(false);
  let error = $state('');
  async function load() {
    try { backups = await rpc('backup.list'); error = ''; }
    catch (e) { error = e.message; }
  }
  $effect(() => { load(); });
  async function create() {
    busy = true;
    try { await rpc('backup.create', {}, { timeout: 300000 }); await load(); toast('Backup created'); }
    catch (e) { toast(e.message, 'error'); }
    finally { busy = false; }
  }
  async function verify(id) {
    try { await rpc('backup.verify', { id }, { timeout: 300000 }); toast('Backup checksums verified'); }
    catch (e) { toast(e.message, 'error'); }
  }
</script>
<button class="np-btn" onclick={create} disabled={busy}>{busy ? 'Creating backup…' : 'Back up now'}</button>
<p class="np-dim">Snapshots include conversations, projects, usage and settings. Settings may contain API keys. Project files, skills and plugins need separate backups.</p>
<p class="np-dim">Restore into a new home with <code>node scripts/restore-backup.mjs "snapshot-folder" "new-home"</code>, then start NetPI with <code>--home "new-home"</code>.</p>
{#if error}<p role="alert">{error}</p>{/if}
{#each backups as backup (backup.id)}
  <div class="backup">
    <div>{new Date(backup.createdAt).toLocaleString()} · {backup.automatic ? 'automatic' : 'manual'}</div>
    <code>{backup.path}</code>
    <button class="np-btn" onclick={() => verify(backup.id)}>Verify</button>
  </div>
{/each}
<style>
  .backup { margin: 12px 0; padding: 10px; border: 1px solid var(--border); border-radius: var(--radius); }
  code { overflow-wrap: anywhere; font-size: var(--fs-sm); }
  .backup code { display: block; margin: 6px 0; }
</style>
