// Files tab (netpi.tools.files, left panel): lazy file tree of the active session's workspace.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { createTab } from '@netpi/kit';
import FilesTab from './FilesTab.svelte';

export const mount = createTab(FilesTab);