<script>
  import { timeAgo } from '../format.js';
  import { clockNow } from './ticker.svelte.js';
  let { time, class: cls = '' } = $props();
  const now = clockNow();
  const text = $derived(timeAgo(time, now()));
  const full = $derived(time ? new Date(time).toLocaleString() : '');
</script>

<time class={cls} datetime={typeof time === 'string' ? time : undefined} title={full}>{text}</time>
