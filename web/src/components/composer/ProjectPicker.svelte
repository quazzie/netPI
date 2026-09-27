<script>
  /**
   * The session's project (sessions.setProject): its working folder and instruction files. The button sits in the
   * composer bar, styled like the profile button; the picker popover (modals/ProjectPicker) opens above it — the
   * anchor is inside [data-composer], so it places top-start.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import { projectOf } from '../../lib/state/app.svelte.js';
  import { modals } from '../../lib/state/ui.svelte.js';

  let { session } = $props();
  let btn = $state();

  const project = $derived(projectOf(session));

  function toggle() {
    modals.projectPicker = modals.projectPicker ? null : { sessionId: session.id, anchor: btn };
  }
</script>

<button
  class="pick"
  class:none={!project}
  bind:this={btn}
  onclick={toggle}
  title={project?.path ?? 'Attach a project'}
  aria-label="Project"
  aria-haspopup="dialog"
>
  <Icon name="folder" size={13} />
  <span class="np-ellipsis">{project?.name ?? 'No project'}</span>
</button>

<style>
  .pick {
    display: flex;
    align-items: center;
    gap: 5px;
    max-width: 120px;
    height: 26px;
    padding: 0 8px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--fg-muted);
    font-size: var(--fs-sm);
  }
  .pick:hover {
    background: var(--bg-3);
    color: var(--fg);
  }
  .pick.none span {
    color: var(--fg-dim);
  }
</style>
