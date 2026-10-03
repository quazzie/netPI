<script>
  import Modal from './Modal.svelte';
  import { allCommands } from '../../lib/commands.js';
  import { prefs } from '../../lib/state/ui.svelte.js';
  let { onclose } = $props();

  const keys = [
    ['Ctrl+T', 'New session'],
    ['Ctrl+W', 'Close tab'],
    ['Ctrl+Tab / Ctrl+Shift+Tab', 'Next / previous tab'],
    ['Ctrl+1…9', 'Go to tab (9 = last)'],
    ['Ctrl+B', 'Toggle left panel'],
    ['Ctrl+Alt+B', 'Toggle right panel'],
    ['Ctrl+K', 'Command palette'],
    ['Ctrl+I', 'New idea (also /idea with no text)'],
    ['Ctrl+,', 'Settings'],
    [prefs.enterSends ? 'Enter' : 'Ctrl+Enter', 'Send (steer while the agent runs)'],
    ['Alt+Enter', 'Queue for after the current run'],
    ['Shift+Enter', 'New line'],
    ['Esc', 'Stop the running agent'],
    ['/', 'Commands'],
    ['@', 'Mention a file'],
  ];
  const cmds = allCommands();
</script>

<Modal title="Keyboard & commands" width={820} {onclose}>
  <div class="cols">
    <div>
      <div class="np-section-title">Keyboard</div>
      <table class="np-table">
        <tbody>
          {#each keys as [k, d] (k)}
            <tr><td class="k"><span class="np-kbd">{k}</span></td><td>{d}</td></tr>
          {/each}
        </tbody>
      </table>
    </div>
    <div>
      <div class="np-section-title">Commands</div>
      <table class="np-table">
        <tbody>
          {#each cmds as c (c.name)}
            <tr>
              <td class="np-mono k">/{c.name}{#if c.argsHint}<span class="np-dim hint">{c.argsHint}</span>{/if}</td>
              <td>{c.description}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  </div>
</Modal>

<style>
  .cols {
    display: grid;
    grid-template-columns: 1fr 1.2fr;
    gap: 24px;
  }
  .k {
    white-space: nowrap;
  }
  .hint {
    margin-left: 0.6em;
  }
  td {
    color: var(--fg-muted);
  }
</style>
