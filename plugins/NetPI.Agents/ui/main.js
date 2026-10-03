// Usage tab (netpi.agents): what the models cost, per month, day, agent, model and chat.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { createTab } from '@netpi/kit';
import UsageTab from './UsageTab.svelte';

export const mount = createTab(UsageTab);
