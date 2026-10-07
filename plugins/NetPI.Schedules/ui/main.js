// Schedules tab (netpi.schedules): the prompts that start on their own as a fresh chat.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { createTab } from '@netpi/kit';
import SchedulesTab from './SchedulesTab.svelte';

export const mount = createTab(SchedulesTab);
