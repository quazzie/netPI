// Work tab (netpi.work): what is running right now — the agents, runs, processes and today's usage.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { createTab } from '@netpi/kit';
import WorkTab from './WorkTab.svelte';

export const mount = createTab(WorkTab);