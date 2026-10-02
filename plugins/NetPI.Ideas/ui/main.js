// Ideas tab (netpi.ideas): the global ideas backlog (one file, ideas carry a project), filtered by project.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { createTab } from '@netpi/kit';
import IdeasTab from './IdeasTab.svelte';

export const mount = createTab(IdeasTab);