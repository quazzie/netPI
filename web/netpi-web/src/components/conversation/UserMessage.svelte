<script lang="ts">
  import type { UserBlock } from "../../types";
  let { block }: { block: UserBlock } = $props();
</script>

<div class="msg-user">
  <div class="bubble">
    <div class="who">You</div>
    {block.text}
  </div>
</div>
