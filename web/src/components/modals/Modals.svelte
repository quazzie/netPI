<script>
  import SettingsModal from './SettingsModal.svelte';
  import FolderPickerModal from './FolderPickerModal.svelte';
  import ConfirmModal from './ConfirmModal.svelte';
  import PromptModal from './PromptModal.svelte';
  import HelpModal from './HelpModal.svelte';
  import CommandPalette from './CommandPalette.svelte';
  import Lightbox from './Lightbox.svelte';
  import ProjectPicker from './ProjectPicker.svelte';
  import ProjectsModal from './ProjectsModal.svelte';
  import IdeaDialog from './IdeaDialog.svelte';
  import { modals } from '../../lib/state/ui.svelte.js';
</script>

{#if modals.settings}<SettingsModal page={typeof modals.settings === 'string' ? modals.settings : 'general'} onclose={() => (modals.settings = false)} />{/if}
{#if modals.help}<HelpModal onclose={() => (modals.help = false)} />{/if}
{#if modals.palette}<CommandPalette onclose={() => (modals.palette = false)} />{/if}
{#if modals.projectPicker}<ProjectPicker data={modals.projectPicker} onclose={() => (modals.projectPicker = null)} />{/if}
{#if modals.projects}<ProjectsModal data={modals.projects} onclose={() => (modals.projects = null)} />{/if}
{#if modals.idea}<IdeaDialog data={modals.idea} onclose={() => (modals.idea = null)} />{/if}
{#if modals.folder}<FolderPickerModal data={modals.folder} onclose={() => (modals.folder = null)} />{/if}
{#if modals.prompt}<PromptModal data={modals.prompt} onclose={() => (modals.prompt = null)} />{/if}
{#if modals.confirm}<ConfirmModal data={modals.confirm} onclose={() => (modals.confirm = null)} />{/if}
{#if modals.lightbox}<Lightbox data={modals.lightbox} onclose={() => (modals.lightbox = null)} />{/if}
