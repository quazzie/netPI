<script>
  /**
   * The session's project and its workspace (sessions.setProject / sessions.setWorkspace): which checkout this chat
   * actually works in. The button sits in the composer bar, styled like the profile button; the picker popover
   * (modals/ProjectPicker) opens above it — the anchor is inside [data-composer], so it places top-start.
   *
   * A session bound to a workspace of its own shows that checkout's directory and branch, not the project's folder: they
   * are different directories, and an agent writing in one while the user reads the other is the confusion this prevents.
   */
  import Icon from '../../lib/kit/Icon.svelte';
  import { projectOf, workspaceOf } from '../../lib/state/app.svelte.js';
  import { modals } from '../../lib/state/ui.svelte.js';

  let { session } = $props();
  let btn = $state();

  const project = $derived(projectOf(session));
  const scope = $derived(workspaceOf(session));
  // The workspace a chat is bound to is attached to its meta by the Workspaces plugin.
  const boundId = $derived(session?.meta?.workspaceId);
  const workspace = $derived(boundId ? (scope?.workspaceId === boundId ? scope : null) : null);

  // What the chat works in: the workspace root when it has one, else the project's folder.
  const root = $derived(workspace?.root ?? project?.path ?? null);
  const label = $derived(
    !project && !workspace ? 'No project'
    : workspace?.branch ? `${workspace.branch}`
    : workspace ? (project ? `${project.name} · wt` : 'workspace')
    : (project?.name ?? 'No project'),
  );
  const title = $derived(
    [
      project ? `Project: ${project.name} (${project.path})` : 'No project',
      workspace ? `Workspace ${workspace.workspaceId}${workspace.isolated ? ' (its own checkout)' : ''}: ${workspace.root}` : null,
      root && !workspace ? `Working directory: ${root}` : null,
      workspace?.branch ? `Branch: ${workspace.branch}` : null,
      workspace?.ownerSessionId && workspace.ownerSessionId !== session.id ? `Owned by ${workspace.ownerSessionId}` : null,
      workspace && !workspace.isolated ? 'Not isolated: writes here are visible to every worker of this project.' : null,
    ].filter(Boolean).join('\n'),
  );

  function toggle() {
    modals.projectPicker = modals.projectPicker ? null : { sessionId: session.id, anchor: btn };
  }
</script>

<button
  class="pick"
  class:none={!project}
  bind:this={btn}
  onclick={toggle}
  {title}
  aria-label="Project"
  aria-haspopup="dialog"
>
  <Icon name="folder" size={13} />
  <span class="np-ellipsis">{label}</span>
  {#if workspace?.isolated}<Icon name="branch" size={12} />{/if}
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