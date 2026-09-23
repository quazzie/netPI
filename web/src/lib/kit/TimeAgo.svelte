<script>
  import { timeAgo } from '../format.js';
  import { clockNow } from './ticker.svelte.js';
  let { time, class: cls = '' } = $props();
  const text = $derived(timeAgo(time, clockNow()));
  const full = $derived(time ? new Date(time).toLocaleString() : '');
</script>

<time class={cls} datetime={typeof time === 'string' ? time : undefined} title={full}>{text}</time>
