<script>
  /** A run's priority as a small chip: nothing for normal, the level's name otherwise (background, low, high, critical). */
  import { priorityLevel } from './util.js';

  let { priority = 0 } = $props();

  const level = $derived(priorityLevel(priority));
</script>

{#if level !== 'normal'}
  <span class="prio" data-level={level} title="Priority {level}: runs queue by it{level === 'critical' ? ', and a critical run takes the instance of a lower run started for you' : level === 'background' ? ', and a background run gives its instance to any higher run that waits' : ''}">{level}</span>
{/if}

<style>
  .prio {
    flex: none;
    font-size: 10px;
    line-height: 14px;
    padding: 0 4px;
    border-radius: 3px;
    background: var(--bg-3);
    color: var(--fg-dim);
  }
  .prio[data-level='high'] {
    color: var(--warn);
  }
  .prio[data-level='critical'] {
    color: var(--err);
  }
</style>
