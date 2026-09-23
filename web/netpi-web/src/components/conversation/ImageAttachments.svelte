<script lang="ts">
  import type { ImageAttachment } from "../../types";
  let { images = [] }: { images?: ImageAttachment[] } = $props();
</script>

{#if images.length}
  <div class="image-attachments">
    {#each images as image}
      {#if ["image/png", "image/jpeg", "image/gif", "image/webp"].includes(image.mimeType)}
        <img src={`data:${image.mimeType};base64,${image.data}`} alt="Attachment preview" loading="lazy" />
      {/if}
    {/each}
  </div>
{/if}

<style>
  .image-attachments { display:flex; flex-wrap:wrap; gap:8px; margin:8px 0; }
  img { max-width:100%; width:auto; max-height:360px; object-fit:contain; border-radius:6px; }
</style>
