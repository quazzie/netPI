<script lang="ts">
  import ImageAttachments from "./ImageAttachments.svelte";
  import type { UserBlock } from "../../types";
  let { block }: { block: UserBlock } = $props();
</script>

<div class="msg-user">
  <div class="bubble">
    <div class="who">You</div>
    {block.text}
    <ImageAttachments images={block.images ?? []} />
  </div>
</div>
